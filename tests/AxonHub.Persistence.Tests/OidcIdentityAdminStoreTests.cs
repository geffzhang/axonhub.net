using Dapper;

namespace AxonHub.Persistence.Tests;

[Collection(PostgresCollection.Name)]
public sealed class OidcIdentityAdminStoreTests(PostgresFixture fixture)
{
    private static readonly DateTime CreatedAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime UpdatedAt = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    [Fact]
    public async Task ListPaginatesLiveIdentitiesInIdOrder()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new OidcIdentityAdminStore(context);

        var alice = NewIdentity("https://issuer.example.com", "sub-1", "alice@example.com", 9);
        var bob = NewIdentity("https://issuer.example.com", "sub-2", "bob@example.com", 10);
        var carol = NewIdentity("https://issuer.example.com", "sub-3", "carol@example.com", 9);
        context.OidcIdentities.AddRange(alice, bob, carol);
        await context.SaveChangesAsync();

        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE \"oidc_identities\" SET \"deleted_at\" = @DeletedAt WHERE \"id\" = @Id;",
                new { DeletedAt = 1_700_000_000L, Id = carol.Id });
        }

        // The connection counts and pages over live rows only.
        var first = await store.ListAsync(new OidcIdentityListQuery { First = 1 });

        Assert.Equal(Expected(alice.Id), first.Nodes.Select(row => row.Id));
        Assert.True(first.HasNextPage);
        Assert.False(first.HasPreviousPage);
        Assert.Equal(2, first.TotalCount);

        var second = await store.ListAsync(new OidcIdentityListQuery
        {
            After = new ListCursor(first.Nodes[0].Id, null),
            First = 1,
        });

        Assert.Equal(Expected(bob.Id), second.Nodes.Select(row => row.Id));
        Assert.False(second.HasNextPage);
        Assert.True(second.HasPreviousPage);

        // `last` flips the scan direction and build() restores the id order.
        var last = await store.ListAsync(new OidcIdentityListQuery { Last = 1 });

        Assert.Equal(Expected(bob.Id), last.Nodes.Select(row => row.Id));
        Assert.True(last.HasPreviousPage);
        Assert.False(last.HasNextPage);
    }

    [Fact]
    public async Task ListOrdersByUpdatedAtAndPushesFiltersDown()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new OidcIdentityAdminStore(context);

        var alice = NewIdentity(
            "https://issuer.example.com",
            "sub-1",
            "alice@example.com",
            9,
            UpdatedAt.AddMinutes(2));
        var bob = NewIdentity(
            "https://other.example.com",
            "sub-2",
            "bob@example.com",
            10,
            UpdatedAt.AddMinutes(1));
        var carol = NewIdentity(
            "https://issuer.example.com",
            "sub-3",
            "carol@example.com",
            9,
            UpdatedAt);
        context.OidcIdentities.AddRange(alice, bob, carol);
        await context.SaveChangesAsync();

        var newest = await store.ListAsync(new OidcIdentityListQuery
        {
            SortField = OidcIdentitySortField.UpdatedAt,
            Direction = SortDirection.Desc,
        });

        Assert.Equal(
            Expected(alice.Id, bob.Id, carol.Id),
            newest.Nodes.Select(row => row.Id));

        // The cursor is the keyset (updated_at, id) pair of the last edge.
        var after = await store.ListAsync(new OidcIdentityListQuery
        {
            SortField = OidcIdentitySortField.UpdatedAt,
            Direction = SortDirection.Desc,
            After = new ListCursor(newest.Nodes[0].Id, newest.Nodes[0].UpdatedAt),
            First = 1,
        });

        Assert.Equal(Expected(bob.Id), after.Nodes.Select(row => row.Id));

        // UserOwnedQueryRule evaluates as a user_id predicate, and it composes
        // with the caller's where through AND.
        var owned = await store.ListAsync(new OidcIdentityListQuery
        {
            Where = new OidcIdentityListFilter
            {
                And =
                [
                    new OidcIdentityListFilter { UserId = 9 },
                    new OidcIdentityListFilter
                    {
                        IssuerIn = ["https://issuer.example.com"],
                    },
                ],
            },
        });

        Assert.Equal(Expected(alice.Id, carol.Id), owned.Nodes.Select(row => row.Id));

        var otherIssuer = await store.ListAsync(new OidcIdentityListQuery
        {
            Where = new OidcIdentityListFilter { Issuer = "https://other.example.com" },
        });

        Assert.Equal(Expected(bob.Id), otherIssuer.Nodes.Select(row => row.Id));

        var foldedEmail = await store.ListAsync(new OidcIdentityListQuery
        {
            Where = new OidcIdentityListFilter { EmailContainsFold = "AROL@" },
        });

        Assert.Equal(Expected(carol.Id), foldedEmail.Nodes.Select(row => row.Id));

        var excluded = await store.ListAsync(new OidcIdentityListQuery
        {
            Where = new OidcIdentityListFilter
            {
                Not = new OidcIdentityListFilter { UserIdIn = [9] },
            },
        });

        Assert.Equal(Expected(bob.Id), excluded.Nodes.Select(row => row.Id));
    }

    private static T[] Expected<T>(params T[] values) => values;

    private static OidcIdentityRecord NewIdentity(
        string issuer,
        string subject,
        string? email,
        long userId,
        DateTime? updatedAt = null,
        string? idpName = "okta") => new()
        {
            CreatedAt = CreatedAt,
            UpdatedAt = updatedAt ?? UpdatedAt,
            Issuer = issuer,
            Subject = subject,
            Email = email,
            IdpName = idpName,
            LastLoginAt = UpdatedAt,
            UserId = userId,
        };
}
