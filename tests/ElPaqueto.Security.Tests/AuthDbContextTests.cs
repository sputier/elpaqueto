using System.Globalization;
using ElPaqueto.Core;
using ElPaqueto.Security.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ElPaqueto.Security.Tests;

public sealed class AuthDbContextTests : IDisposable
{
    private readonly AuthDatabaseFixture _database = new();

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task Migrate_FromEmpty_CreatesTheSchema()
    {
        await using var context = await _database.CreateMigratedContextAsync();

        var tables = await QueryNamesAsync("SELECT name FROM sqlite_master WHERE type = 'table'");
        Assert.Contains("Users", tables);
        Assert.Contains(AuthDbContext.MigrationsHistoryTable, tables);

        var uniqueIndexes = await QueryNamesAsync(
            "SELECT name FROM sqlite_master WHERE type = 'index' AND sql LIKE 'CREATE UNIQUE INDEX%'");
        Assert.Contains("IX_Users_NormalizedUserName", uniqueIndexes);
        Assert.Contains("ApiKeys", tables);
        Assert.Contains("IX_ApiKeys_KeyId", uniqueIndexes);
    }

    [Fact]
    public async Task Migrate_FromTheInitialSchemaWithUsers_AddsApiKeysAndKeepsTheUsers()
    {
        await using (var initial = _database.CreateContext())
        {
            await initial.GetService<IMigrator>().MigrateAsync("20261112031723_InitialAuthSchema");
            Assert.DoesNotContain("ApiKeys", await QueryNamesAsync("SELECT name FROM sqlite_master WHERE type = 'table'"));

            initial.Users.Add(AuthDatabaseFixture.NewUser("alice"));
            initial.Users.Add(AuthDatabaseFixture.NewUser("bob"));
            await initial.SaveChangesAsync();
        }

        await using var upgraded = _database.CreateContext();
        Assert.Equal(["20261112033957_AddApiKeys"], await upgraded.Database.GetPendingMigrationsAsync());
        await upgraded.Database.MigrateAsync();

        Assert.Equal(2, await upgraded.Users.CountAsync());
        var alice = await upgraded.Users.SingleAsync(u => u.UserName == "alice");
        upgraded.ApiKeys.Add(NewApiKey(alice.Id));
        await upgraded.SaveChangesAsync();
        Assert.Equal(1, await upgraded.ApiKeys.CountAsync());
    }

    [Theory]
    [InlineData(8)]
    [InlineData(-1)]
    public async Task UndefinedApiKeyPermissionBits_AreRejectedByTheDatabase(int permissions)
    {
        await using var context = await _database.CreateMigratedContextAsync();
        var user = AuthDatabaseFixture.NewUser("alice");
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var key = NewApiKey(user.Id);
        key.Permissions = (Permission)permissions;
        context.ApiKeys.Add(key);

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task ApiKeys_WithAWrongHashOrKeyIdLength_OrADuplicateKeyId_OrNoOwner_AreRejectedByTheDatabase()
    {
        await using (var seed = await _database.CreateMigratedContextAsync())
        {
            var user = AuthDatabaseFixture.NewUser("alice");
            seed.Users.Add(user);
            seed.ApiKeys.Add(NewApiKey(user.Id, "AAAAAAAAAAAA"));
            await seed.SaveChangesAsync();
        }

        var userId = await ScalarTextAsync("SELECT Id FROM Users");

        async Task AssertRejectedAsync(Action<ApiKey> change)
        {
            await using var context = _database.CreateContext();
            var key = NewApiKey(Guid.Parse(userId));
            change(key);
            context.ApiKeys.Add(key);
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }

        await AssertRejectedAsync(k => k.SecretHash = new byte[31]);
        await AssertRejectedAsync(k => k.KeyId = "SHORT");
        await AssertRejectedAsync(k => k.KeyId = "AAAAAAAAAAAA");
        await AssertRejectedAsync(k => k.OwnerUserId = Guid.NewGuid());
    }

    [Fact]
    public async Task Migrate_UsesWriteAheadLogging()
    {
        await using var context = await _database.CreateMigratedContextAsync();

        var mode = await QueryNamesAsync("PRAGMA journal_mode");

        Assert.Equal(["wal"], mode);
    }

    [Fact]
    public async Task Migrate_Twice_AppliesNothingAndLosesNoRow()
    {
        await using (var first = await _database.CreateMigratedContextAsync())
        {
            first.Users.Add(AuthDatabaseFixture.NewUser("alice"));
            await first.SaveChangesAsync();
        }

        await using var second = _database.CreateContext();
        Assert.Empty(await second.Database.GetPendingMigrationsAsync());

        await second.Database.MigrateAsync();

        Assert.Equal(1, await second.Users.CountAsync());
    }

    [Fact]
    public async Task UserNames_DifferingOnlyByCase_ViolateTheUniqueIndex()
    {
        await using var context = await _database.CreateMigratedContextAsync();
        context.Users.Add(AuthDatabaseFixture.NewUser("Alice"));
        await context.SaveChangesAsync();

        context.Users.Add(AuthDatabaseFixture.NewUser("aLiCe"));

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task ConcurrentUpdates_RaiseAConcurrencyException()
    {
        await using (var seed = await _database.CreateMigratedContextAsync())
        {
            seed.Users.Add(AuthDatabaseFixture.NewUser("alice"));
            await seed.SaveChangesAsync();
        }

        await using var first = _database.CreateContext();
        await using var second = _database.CreateContext();
        var userInFirst = await first.Users.SingleAsync();
        var userInSecond = await second.Users.SingleAsync();

        userInFirst.IsActive = false;
        await first.SaveChangesAsync();

        userInSecond.Permissions = Permission.Admin;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    [Fact]
    public async Task EveryUpdate_RegeneratesTheConcurrencyStamp()
    {
        await using var context = await _database.CreateMigratedContextAsync();
        var user = AuthDatabaseFixture.NewUser("alice");
        context.Users.Add(user);
        await context.SaveChangesAsync();
        var stampAfterInsert = user.ConcurrencyStamp;

        user.IsActive = false;
        await context.SaveChangesAsync();

        Assert.NotEqual(stampAfterInsert, user.ConcurrencyStamp);
    }

    [Fact]
    public async Task Timestamps_RoundTripAsUtcAndOrderCorrectly()
    {
        var baseTime = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        await using (var context = await _database.CreateMigratedContextAsync())
        {
            context.Users.Add(AuthDatabaseFixture.NewUser("third", baseTime.AddHours(3)));
            context.Users.Add(AuthDatabaseFixture.NewUser("first", baseTime.AddHours(1)));
            context.Users.Add(AuthDatabaseFixture.NewUser("second", baseTime.AddHours(2)));
            await context.SaveChangesAsync();
        }

        await using var reader = _database.CreateContext();
        var ordered = await reader.Users.OrderBy(u => u.CreatedAt).ToListAsync();

        Assert.Equal(["first", "second", "third"], ordered.Select(u => u.UserName));
        Assert.All(ordered, u => Assert.Equal(DateTimeKind.Utc, u.CreatedAt.Kind));
        Assert.Equal(baseTime.AddHours(1), ordered[0].CreatedAt);
    }

    [Fact]
    public async Task Permissions_AreStoredAsFlags()
    {
        await using (var context = await _database.CreateMigratedContextAsync())
        {
            var user = AuthDatabaseFixture.NewUser("alice");
            user.Permissions = Permission.Read | Permission.Publish;
            context.Users.Add(user);
            await context.SaveChangesAsync();
        }

        await using var reader = _database.CreateContext();
        var stored = await reader.Users.SingleAsync();

        Assert.Equal(Permission.Read | Permission.Publish, stored.Permissions);
        Assert.Equal(3, await ScalarAsync("SELECT Permissions FROM Users"));
    }

    [Fact]
    public async Task Model_HasNoPendingChanges()
    {
        await using var context = _database.CreateContext();

        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task ASecondContext_CanWriteWhileTheFirstIsStillReading()
    {
        await using var first = await _database.CreateMigratedContextAsync();
        first.Users.Add(AuthDatabaseFixture.NewUser("one"));
        first.Users.Add(AuthDatabaseFixture.NewUser("two"));
        await first.SaveChangesAsync();

        // An open cursor keeps a read snapshot on the first connection, like a server request in flight.
        using var cursor = first.Users.AsNoTracking().AsEnumerable().GetEnumerator();
        Assert.True(cursor.MoveNext());

        await using var second = _database.CreateContext();
        second.Users.Add(AuthDatabaseFixture.NewUser("written-by-the-cli"));

        // The SQLite provider runs its "async" calls synchronously: the concurrent writer needs its own thread.
        var write = Task.Run(() => second.SaveChangesAsync());
        var finished = await Task.WhenAny(write, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.Same(write, finished);
        await write;
        Assert.True(cursor.MoveNext());
    }

    [Fact]
    public void AuthDbContext_IsNotResolvableAsTheInheritedContext()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>());

        Assert.Null(provider.GetService<IContext>());
        Assert.False(typeof(IContext).IsAssignableFrom(typeof(AuthDbContext)));
    }

    [Fact]
    public async Task Migrator_CreatesTheDatabaseAndAppliesPendingMigrations()
    {
        await using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Security:Database:ConnectionString"] = _database.ConnectionString,
        });

        await provider.GetRequiredService<IAuthDatabaseMigrator>().MigrateAsync();

        Assert.True(File.Exists(_database.DatabasePath));
        Assert.Contains("Users", await QueryNamesAsync("SELECT name FROM sqlite_master WHERE type = 'table'"));
    }

    private static ServiceProvider BuildProvider(Dictionary<string, string?> configuration)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(configuration).Build());
        new ElPaquetoApplication(services).AddElPaquetoSecurity();
        return services.BuildServiceProvider();
    }

    private async Task<List<string>> QueryNamesAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={_database.DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static ApiKey NewApiKey(Guid ownerUserId, string keyId = "BBBBBBBBBBBB") => new()
    {
        Id = Guid.NewGuid(),
        KeyId = keyId,
        SecretHash = new byte[ApiKey.SecretHashLength],
        OwnerUserId = ownerUserId,
        Name = "ci-main",
        Permissions = Permission.Read,
        CreatedAt = DateTime.UtcNow,
    };

    private async Task<string> ScalarTextAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={_database.DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture)!;
    }

    private async Task<long> ScalarAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={_database.DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task UnspecifiedKindTimestamps_AreTakenAsUtcWhateverTheServerTimeZone()
    {
        var unspecified = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Unspecified);
        await using (var context = await _database.CreateMigratedContextAsync())
        {
            context.Users.Add(AuthDatabaseFixture.NewUser("alice", unspecified));
            await context.SaveChangesAsync();
        }

        await using var reader = _database.CreateContext();
        var stored = await reader.Users.SingleAsync();

        Assert.Equal(new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc), stored.CreatedAt);
        Assert.Equal(DateTimeKind.Utc, stored.CreatedAt.Kind);
    }

    [Fact]
    public async Task NormalizedUserName_IsAlwaysDerivedFromTheUserName()
    {
        await using var context = await _database.CreateMigratedContextAsync();
        var user = AuthDatabaseFixture.NewUser("Alice");
        user.NormalizedUserName = "SOMETHING-ELSE";
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var second = AuthDatabaseFixture.NewUser("ALICE");
        second.NormalizedUserName = "A-DIFFERENT-VALUE";
        context.Users.Add(second);

        Assert.Equal("ALICE", user.NormalizedUserName);
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task UndefinedPermissionBits_AreRejectedByTheDatabase()
    {
        await using var context = await _database.CreateMigratedContextAsync();
        var user = AuthDatabaseFixture.NewUser("alice");
        user.Permissions = (Permission)8;
        context.Users.Add(user);

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task EveryDefinedPermissionCombination_IsAccepted()
    {
        await using var context = await _database.CreateMigratedContextAsync();
        for (var flags = 0; flags <= 7; flags++)
        {
            var user = AuthDatabaseFixture.NewUser($"user{flags}");
            user.Permissions = (Permission)flags;
            context.Users.Add(user);
        }

        await context.SaveChangesAsync();

        Assert.Equal(8, await context.Users.CountAsync());
    }
}
