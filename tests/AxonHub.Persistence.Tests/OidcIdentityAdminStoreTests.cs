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

    [Fact]
    public async Task ListAppliesEveryScalarAndUserEdgePredicate()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new OidcIdentityAdminStore(context);

        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO "users" ("id", "created_at", "updated_at", "email", "status", "password")
                VALUES (101, @CreatedAt, @UpdatedAt, 'alice@example.com', 'activated', 'x'),
                       (102, @CreatedAt, @UpdatedAt, 'bob@example.com', 'activated', 'x');
                UPDATE "users" SET "deleted_at" = 99 WHERE "id" = 102;
                """,
                new { CreatedAt, UpdatedAt });
        }

        var alice = new OidcIdentityRecord
        {
            CreatedAt = CreatedAt,
            UpdatedAt = CreatedAt.AddHours(1),
            Issuer = "https://issuer.example.com",
            Subject = "sub-1",
            Email = "alice@example.com",
            IdpName = "okta",
            LastLoginAt = UpdatedAt,
            UserId = 101,
        };
        var bob = new OidcIdentityRecord
        {
            CreatedAt = CreatedAt.AddMinutes(1),
            UpdatedAt = CreatedAt.AddHours(2),
            Issuer = "https://other.example.com",
            Subject = "sub-2",
            Email = null,
            IdpName = null,
            LastLoginAt = null,
            UserId = 102,
        };
        var carol = new OidcIdentityRecord
        {
            CreatedAt = CreatedAt.AddMinutes(2),
            UpdatedAt = CreatedAt.AddHours(3),
            Issuer = "https://issuer.example.com",
            Subject = "sub-3",
            Email = "carol@example.com",
            IdpName = "auth0",
            LastLoginAt = CreatedAt,
            UserId = 101,
        };
        context.OidcIdentities.AddRange(alice, bob, carol);
        await context.SaveChangesAsync();

        async Task<long[]> Ids(OidcIdentityListFilter where)
        {
            var page = await store.ListAsync(new OidcIdentityListQuery
            {
                Where = where,
                First = 10,
            });
            return page.Nodes.Select(static row => row.Id).ToArray();
        }

        Assert.Equal(
            Expected(alice.Id),
            await Ids(new OidcIdentityListFilter { Id = alice.Id }));
        Assert.Equal(
            Expected(bob.Id, carol.Id),
            await Ids(new OidcIdentityListFilter { IdNEQ = alice.Id }));
        Assert.Equal(
            Expected(alice.Id, carol.Id),
            await Ids(new OidcIdentityListFilter { IdIn = [alice.Id, carol.Id] }));
        Assert.Equal(
            Expected(bob.Id, carol.Id),
            await Ids(new OidcIdentityListFilter { IdNotIn = [alice.Id] }));
        Assert.Equal(
            Expected(bob.Id, carol.Id),
            await Ids(new OidcIdentityListFilter { IdGT = alice.Id }));
        Assert.Equal(
            Expected(bob.Id, carol.Id),
            await Ids(new OidcIdentityListFilter { IdGTE = bob.Id }));
        Assert.Equal(
            Expected(alice.Id),
            await Ids(new OidcIdentityListFilter { IdLT = bob.Id }));
        Assert.Equal(
            Expected(alice.Id, bob.Id),
            await Ids(new OidcIdentityListFilter { IdLTE = bob.Id }));

        Assert.Equal(
            Expected(alice.Id),
            await Ids(new OidcIdentityListFilter { CreatedAt = CreatedAt }));
        Assert.Equal(
            Expected(bob.Id, carol.Id),
            await Ids(new OidcIdentityListFilter { CreatedAtNEQ = CreatedAt }));
        Assert.Equal(
            Expected(alice.Id, carol.Id),
            await Ids(new OidcIdentityListFilter
            {
                CreatedAtIn = [CreatedAt, CreatedAt.AddMinutes(2)],
            }));
        Assert.Equal(
            Expected(bob.Id, carol.Id),
            await Ids(new OidcIdentityListFilter { CreatedAtNotIn = [CreatedAt] }));
        Assert.Equal(
            Expected(bob.Id, carol.Id),
            await Ids(new OidcIdentityListFilter { CreatedAtGT = CreatedAt }));
        Assert.Equal(
            Expected(bob.Id, carol.Id),
            await Ids(new OidcIdentityListFilter
            {
                CreatedAtGTE = CreatedAt.AddMinutes(1),
            }));
        Assert.Equal(
            Expected(alice.Id),
            await Ids(new OidcIdentityListFilter
            {
                CreatedAtLT = CreatedAt.AddMinutes(1),
            }));
        Assert.Equal(
            Expected(alice.Id, bob.Id),
            await Ids(new OidcIdentityListFilter
            {
                CreatedAtLTE = CreatedAt.AddMinutes(1),
            }));

        Assert.Equal(
            Expected(alice.Id),
            await Ids(new OidcIdentityListFilter { UpdatedAt = CreatedAt.AddHours(1) }));
        Assert.Equal(
            Expected(bob.Id),
            await Ids(new OidcIdentityListFilter
            {
                UpdatedAtGT = CreatedAt.AddHours(1),
                UpdatedAtLT = CreatedAt.AddHours(3),
            }));
        Assert.Equal(
            Expected(alice.Id, carol.Id),
            await Ids(new OidcIdentityListFilter
            {
                UpdatedAtNotIn = [CreatedAt.AddHours(2)],
            }));

        Assert.Equal(
            Expected(alice.Id, carol.Id),
            await Ids(new OidcIdentityListFilter { Issuer = "https://issuer.example.com" }));
        Assert.Equal(
            Expected(bob.Id),
            await Ids(new OidcIdentityListFilter
            {
                IssuerNEQ = "https://issuer.example.com",
            }));
        Assert.Equal(
            Expected(alice.Id, carol.Id),
            await Ids(new OidcIdentityListFilter
            {
                IssuerIn = ["https://issuer.example.com", "https://third.example.com"],
            }));
        Assert.Equal(
            Expected(alice.Id, carol.Id),
            await Ids(new OidcIdentityListFilter { IssuerHasPrefix = "https://issuer" }));
        Assert.Equal(
            Expected(alice.Id, carol.Id),
            await Ids(new OidcIdentityListFilter
            {
                IssuerEqualFold = "HTTPS://ISSUER.EXAMPLE.COM",
            }));
        Assert.Equal(
            Expected(bob.Id),
            await Ids(new OidcIdentityListFilter { IssuerContainsFold = "OTHER" }));

        Assert.Equal(
            Expected(bob.Id),
            await Ids(new OidcIdentityListFilter { Subject = "sub-2" }));
        Assert.Equal(
            Expected(carol.Id),
            await Ids(new OidcIdentityListFilter { SubjectContainsFold = "SUB-3" }));

        Assert.Equal(
            Expected(alice.Id),
            await Ids(new OidcIdentityListFilter { Email = "alice@example.com" }));
        // The NULL email is excluded from <> and matches only IsNil.
        Assert.Equal(
            Expected(carol.Id),
            await Ids(new OidcIdentityListFilter { EmailNEQ = "alice@example.com" }));
        Assert.Equal(
            Expected(alice.Id, carol.Id),
            await Ids(new OidcIdentityListFilter
            {
                EmailContains = "@example.com",
            }));
        Assert.Equal(
            Expected(bob.Id),
            await Ids(new OidcIdentityListFilter { EmailIsNil = true }));
        Assert.Equal(
            Expected(alice.Id, carol.Id),
            await Ids(new OidcIdentityListFilter { EmailNotNil = true }));

        Assert.Equal(
            Expected(alice.Id),
            await Ids(new OidcIdentityListFilter { IdpName = "okta" }));
        Assert.Equal(
            Expected(alice.Id, carol.Id),
            await Ids(new OidcIdentityListFilter { IdpNameNotIn = ["none"] }));
        Assert.Equal(
            Expected(bob.Id),
            await Ids(new OidcIdentityListFilter { IdpNameIsNil = true }));
        Assert.Equal(
            Expected(alice.Id, carol.Id),
            await Ids(new OidcIdentityListFilter { IdpNameNotNil = true }));

        Assert.Equal(
            Expected(alice.Id),
            await Ids(new OidcIdentityListFilter { LastLoginAt = UpdatedAt }));
        // "last_login_at <> X" excludes the NULL row, like SQL and ent.
        Assert.Equal(
            Expected(carol.Id),
            await Ids(new OidcIdentityListFilter { LastLoginAtNEQ = UpdatedAt }));
        Assert.Equal(
            Expected(carol.Id),
            await Ids(new OidcIdentityListFilter { LastLoginAtLT = UpdatedAt }));
        Assert.Equal(
            Expected(bob.Id),
            await Ids(new OidcIdentityListFilter { LastLoginAtIsNil = true }));
        Assert.Equal(
            Expected(alice.Id, carol.Id),
            await Ids(new OidcIdentityListFilter { LastLoginAtNotNil = true }));

        Assert.Equal(
            Expected(alice.Id, carol.Id),
            await Ids(new OidcIdentityListFilter { UserId = 101 }));
        Assert.Equal(
            Expected(bob.Id),
            await Ids(new OidcIdentityListFilter { UserIdNEQ = 101 }));
        Assert.Equal(
            Expected(alice.Id, bob.Id, carol.Id),
            await Ids(new OidcIdentityListFilter { UserIdIn = [101, 102] }));
        Assert.Equal(
            Expected(bob.Id),
            await Ids(new OidcIdentityListFilter { UserIdNotIn = [101] }));

        Assert.Equal(
            Expected(alice.Id, bob.Id, carol.Id),
            await Ids(new OidcIdentityListFilter { HasUser = true }));
        // The soft-deleted user 102 still satisfies the edge predicate.
        Assert.Equal(
            Expected(alice.Id, carol.Id),
            await Ids(new OidcIdentityListFilter
            {
                HasUserWith = [new UserListFilter { Email = "alice@example.com" }],
            }));
        Assert.Equal(
            Expected(bob.Id),
            await Ids(new OidcIdentityListFilter
            {
                HasUserWith = [new UserListFilter { Email = "bob@example.com" }],
            }));
        Assert.Equal(
            Expected(alice.Id, bob.Id, carol.Id),
            await Ids(new OidcIdentityListFilter { HasUserWith = [] }));

        Assert.Equal(
            Expected(bob.Id),
            await Ids(new OidcIdentityListFilter
            {
                Not = new OidcIdentityListFilter { Issuer = "https://issuer.example.com" },
            }));
        Assert.Equal(
            Expected(alice.Id),
            await Ids(new OidcIdentityListFilter
            {
                And =
                [
                    new OidcIdentityListFilter { UserId = 101 },
                    new OidcIdentityListFilter { IdpName = "okta" },
                ],
            }));
        Assert.Equal(
            Expected(bob.Id, carol.Id),
            await Ids(new OidcIdentityListFilter
            {
                Or =
                [
                    new OidcIdentityListFilter { Subject = "sub-2" },
                    new OidcIdentityListFilter { Subject = "sub-3" },
                ],
            }));
        Assert.Empty(await Ids(new OidcIdentityListFilter { Or = [] }));
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
