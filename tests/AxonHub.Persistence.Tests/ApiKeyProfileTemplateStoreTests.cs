using Dapper;
using Microsoft.EntityFrameworkCore;

namespace AxonHub.Persistence.Tests;

/// <summary>
/// Locks the api_key_profile_templates store against the reference ent
/// behaviour: the create builder writes jsonb profile JSON or SQL NULL for an
/// explicit nil, the (project_id, name, deleted_at) index surfaces as a name
/// conflict per project, reads honour the access project predicate, updates
/// write only the present fields under the mutation predicate, and deletes are
/// soft.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ApiKeyProfileTemplateStoreTests(PostgresFixture fixture)
{
    private static readonly DateTime CreatedAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime UpdatedAt = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    [Fact]
    public async Task CreateStoresTheProfileJsonOrSqlNullAndReadsTheRowBack()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var project = await AddAsync(context, NewProject("alpha"));
        var other = await AddAsync(context, NewProject("beta"));
        var store = new ApiKeyProfileTemplateStore(context);

        var withProfile = NewTemplate("One", project.Id);
        withProfile.Profile = """{"name":"one","modelMappings":[{"from":"a","to":"b"}]}""";
        var created = await store.CreateAsync(withProfile);
        Assert.True(created.Id > 0);

        var read = await store.GetAsync(created.Id, access: null);
        Assert.NotNull(read);
        Assert.Equal("One", read.Name);
        Assert.Equal("desc", read.Description);
        Assert.Equal(project.Id, read.ProjectId);
        Assert.Equal(CreatedAt, read.CreatedAt);
        Assert.Equal(UpdatedAt, read.UpdatedAt);

        // jsonb normalizes whitespace; compare the stored text and the row.
        await using var verify = db.Factory.CreateDbContext();
        var raw = await verify.Database.GetDbConnection().QuerySingleAsync<string>(
            "SELECT \"profile\"::text FROM \"api_key_profile_templates\" WHERE \"id\" = @Id;",
            new { created.Id });
        RecordAssert.Json(
            """{"name":"one","modelMappings":[{"from":"a","to":"b"}]}""",
            raw);
        RecordAssert.Json(raw, read.Profile);

        // The query policy's project predicate narrows reads.
        Assert.Null(await store.GetAsync(
            created.Id,
            new ApiKeyProfileTemplateAccessFilter { ProjectId = other.Id }));

        // An explicit nil profile suppresses the schema default and stores SQL
        // NULL, like ent's SetProfile(nil).
        var withoutProfile = NewTemplate("Two", project.Id);
        withoutProfile.Profile = null;
        var nullRow = await store.CreateAsync(withoutProfile);

        await using var verifyNull = db.Factory.CreateDbContext();
        var rawNull = await verifyNull.Database.GetDbConnection().QuerySingleOrDefaultAsync<string?>(
            "SELECT \"profile\"::text FROM \"api_key_profile_templates\" WHERE \"id\" = @Id;",
            new { nullRow.Id });
        Assert.Null(rawNull);
        Assert.Null((await store.GetAsync(nullRow.Id, access: null))!.Profile);
    }

    [Fact]
    public async Task DuplicateNamesConflictPerProjectAndSoftDeleteAllowsReuse()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var alpha = await AddAsync(context, NewProject("alpha"));
        var beta = await AddAsync(context, NewProject("beta"));
        var store = new ApiKeyProfileTemplateStore(context);

        await store.CreateAsync(NewTemplate("Shared", alpha.Id));

        // Another project may reuse the name.
        await store.CreateAsync(NewTemplate("Shared", beta.Id));

        await Assert.ThrowsAsync<ApiKeyProfileTemplateNameConflictException>(
            () => store.CreateAsync(NewTemplate("Shared", alpha.Id)));

        // A rename onto the same project's name conflicts too.
        var third = await store.CreateAsync(NewTemplate("Third", alpha.Id));
        await Assert.ThrowsAsync<ApiKeyProfileTemplateNameConflictException>(
            () => store.UpdateAsync(new ApiKeyProfileTemplateUpdate
            {
                Id = third.Id,
                Name = "Shared",
            }));

        // The index includes deleted_at, so a soft-deleted row frees its name.
        Assert.True(await store.DeleteAsync(third.Id, access: null));
        await store.CreateAsync(NewTemplate("Third", alpha.Id));

        Assert.False(await store.DeleteAsync(424242, access: null));

        // The delete honours the mutation project predicate.
        var scoped = await store.CreateAsync(NewTemplate("Scoped", alpha.Id));
        Assert.False(await store.DeleteAsync(
            scoped.Id,
            new ApiKeyProfileTemplateAccessFilter { ProjectId = beta.Id }));
        Assert.NotNull(await store.GetAsync(scoped.Id, access: null));
        Assert.True(await store.DeleteAsync(
            scoped.Id,
            new ApiKeyProfileTemplateAccessFilter { ProjectId = alpha.Id }));
        Assert.Null(await store.GetAsync(scoped.Id, access: null));
    }

    [Fact]
    public async Task ListOrdersByIdAndFiltersByTheProjectEdgeAndAccessScope()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var alpha = await AddAsync(context, NewProject("alpha"));
        var beta = await AddAsync(context, NewProject("beta"));
        var store = new ApiKeyProfileTemplateStore(context);

        var first = await store.CreateAsync(
            NewTemplate("A1", alpha.Id, UpdatedAt.AddMinutes(1)));
        var second = await store.CreateAsync(
            NewTemplate("A2", alpha.Id, UpdatedAt.AddMinutes(2)));
        var third = await store.CreateAsync(
            NewTemplate("A3", alpha.Id, UpdatedAt.AddMinutes(3)));
        var elsewhere = await store.CreateAsync(NewTemplate("B1", beta.Id));

        // ListForProject has no ORDER BY in the reference; the port orders by
        // id (C163).
        var ordered = await store.ListForProjectAsync(alpha.Id, access: null);
        Assert.Equal(
            [first.Id, second.Id, third.Id],
            ordered.Select(static row => row.Id).ToArray());

        // The access predicate is ANDed with the project predicate.
        Assert.Empty(await store.ListForProjectAsync(
            alpha.Id,
            new ApiKeyProfileTemplateAccessFilter { ProjectId = beta.Id }));

        // The reference resolver's hasProjectWith where clause.
        var page = await store.ListAsync(new ApiKeyProfileTemplateListQuery
        {
            Where = new ApiKeyProfileTemplateListFilter
            {
                HasProject = true,
                HasProjectWith =
                [
                    new ApiKeyProfileTemplateProjectFilter { Id = alpha.Id },
                ],
            },
            First = 10,
        });
        Assert.Equal(3, page.TotalCount);
        Assert.Equal(
            [first.Id, second.Id, third.Id],
            page.Nodes.Select(static row => row.Id).ToArray());
        Assert.False(page.HasNextPage);

        // The project-edge predicates beyond the id are supported too.
        var byProjectName = await store.ListAsync(new ApiKeyProfileTemplateListQuery
        {
            Where = new ApiKeyProfileTemplateListFilter
            {
                HasProjectWith =
                [
                    new ApiKeyProfileTemplateProjectFilter { Name = "alpha" },
                ],
            },
            First = 10,
        });
        Assert.Equal(3, byProjectName.TotalCount);

        var byProjectNameFold = await store.ListAsync(new ApiKeyProfileTemplateListQuery
        {
            Where = new ApiKeyProfileTemplateListFilter
            {
                HasProjectWith =
                [
                    new ApiKeyProfileTemplateProjectFilter { NameContainsFold = "ALPH" },
                ],
            },
            First = 10,
        });
        Assert.Equal(3, byProjectNameFold.TotalCount);

        // The query policy predicate alone narrows the connection.
        var scoped = await store.ListAsync(new ApiKeyProfileTemplateListQuery
        {
            Access = new ApiKeyProfileTemplateAccessFilter { ProjectId = beta.Id },
            First = 10,
        });
        Assert.Equal(elsewhere.Id, Assert.Single(scoped.Nodes).Id);

        // UPDATED_AT sorts by the column and the cursor carries the value.
        var descending = await store.ListAsync(new ApiKeyProfileTemplateListQuery
        {
            SortField = ApiKeyProfileTemplateSortField.UpdatedAt,
            Direction = SortDirection.Desc,
            First = 2,
        });
        Assert.Equal(
            [third.Id, second.Id],
            descending.Nodes.Select(static row => row.Id).ToArray());
        Assert.True(descending.HasNextPage);
        Assert.Equal(4, descending.TotalCount);
    }

    [Fact]
    public async Task UpdateWritesOnlyPresentFieldsAndHonoursTheProjectPredicate()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var alpha = await AddAsync(context, NewProject("alpha"));
        var beta = await AddAsync(context, NewProject("beta"));
        var store = new ApiKeyProfileTemplateStore(context);

        var created = await store.CreateAsync(NewTemplate("One", alpha.Id));

        // A description-only update leaves the name and profile alone but
        // bumps updated_at.
        var updated = await store.UpdateAsync(new ApiKeyProfileTemplateUpdate
        {
            Id = created.Id,
            Description = "changed",
        });
        Assert.NotNull(updated);
        Assert.Equal("One", updated.Name);
        Assert.Equal("changed", updated.Description);
        Assert.Equal("""{"name": "p"}""", updated.Profile);
        Assert.True(updated.UpdatedAt > updated.CreatedAt);

        // A profile-only update replaces the jsonb column.
        var profileOnly = await store.UpdateAsync(new ApiKeyProfileTemplateUpdate
        {
            Id = created.Id,
            Profile = """{"name":"two"}""",
        });
        Assert.NotNull(profileOnly);
        Assert.Equal("changed", profileOnly.Description);
        Assert.Equal("""{"name": "two"}""", profileOnly.Profile);

        // The mutation policy predicate scopes the row.
        Assert.Null(await store.UpdateAsync(new ApiKeyProfileTemplateUpdate
        {
            Id = created.Id,
            Name = "blocked",
            ProjectWritePredicate = beta.Id,
        }));
        Assert.Null(await store.UpdateAsync(new ApiKeyProfileTemplateUpdate
        {
            Id = 424242,
            Name = "missing",
        }));

        var renamed = await store.UpdateAsync(new ApiKeyProfileTemplateUpdate
        {
            Id = created.Id,
            Name = "Renamed",
            ProjectWritePredicate = alpha.Id,
        });
        Assert.NotNull(renamed);
        Assert.Equal("Renamed", renamed.Name);
    }

    private static async Task<T> AddAsync<T>(AxonHubDbContext context, T entity)
        where T : class
    {
        context.Add(entity);
        await context.SaveChangesAsync();
        return entity;
    }

    private static ProjectRecord NewProject(string name) => new()
    {
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        Name = name,
        Description = string.Empty,
        Status = "active",
    };

    private static ApiKeyProfileTemplateRecord NewTemplate(
        string name,
        long projectId,
        DateTime? updatedAt = null) => new()
        {
            CreatedAt = CreatedAt,
            UpdatedAt = updatedAt ?? UpdatedAt,
            Name = name,
            Description = "desc",
            Profile = """{"name":"p"}""",
            ProjectId = projectId,
        };
}
