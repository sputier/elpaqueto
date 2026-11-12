using System.Security.Cryptography;
using System.Text;
using ElPaqueto.Core;
using ElPaqueto.Security.ApiKeys;
using ElPaqueto.Security.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ElPaqueto.Security.Tests;

public sealed class ApiKeyServiceTests : IDisposable
{
    private const Permission ReadPublish = Permission.Read | Permission.Publish;

    private readonly AuthDatabaseFixture _database = new();
    private readonly ApiKeyServiceHarness _harness;

    public ApiKeyServiceTests()
    {
        _harness = new ApiKeyServiceHarness(_database);
    }

    public void Dispose() => _database.Dispose();

    // ----- Creation and storage -----

    [Fact]
    public async Task Create_ReturnsAWellFormedTokenOnce_AndStoresOnlyTheHashOfItsSecret()
    {
        var owner = await _harness.AddUserAsync("alice", ReadPublish);

        var created = await _harness.CreateKeyAsync(owner, ReadPublish);

        Assert.True(ApiKeyToken.TryParse(created.Token, out var parts));
        Assert.Equal(created.Key.KeyId, parts.KeyId);
        Assert.DoesNotContain(parts.Secret, created.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(parts.Secret, created.Key.ToString(), StringComparison.Ordinal);

        var stored = await _harness.LoadKeyAsync(parts.KeyId);
        Assert.Equal(SHA256.HashData(Encoding.ASCII.GetBytes(parts.Secret)), stored.SecretHash);
        Assert.Equal(owner, stored.OwnerUserId);
        Assert.Equal(ReadPublish, stored.Permissions);
        Assert.Equal(_harness.Now, stored.CreatedAt);
        Assert.Null(stored.ExpiresAt);
        Assert.Null(stored.RevokedAt);
        Assert.Null(stored.LastUsedAt);
    }

    [Fact]
    public async Task TheDatabaseFiles_NeverContainTheSecretOrTheToken()
    {
        var owner = await _harness.AddUserAsync("alice", ReadPublish);
        var tokens = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            tokens.Add((await _harness.CreateKeyAsync(owner, ReadPublish, name: $"key-{i}")).Token);
        }

        foreach (var token in tokens)
        {
            Assert.True((await _harness.ValidateAsync(token)).Succeeded);
        }

        await using (var context = _harness.CreateContext())
        {
            tokens.Add((await _harness.CreateService(context).RotateAsync(tokens[0][4..16], null)).Token);
        }

        // Read the files twice: while pooled connections still hold the write-ahead log (not yet
        // checkpointed), then after the last connection closed and SQLite checkpointed it.
        var contents = ReadDatabaseFiles();
        SqliteConnection.ClearAllPools();
        contents.AddRange(ReadDatabaseFiles());
        Assert.Contains(contents, bytes => bytes.Length > 0);

        foreach (var token in tokens)
        {
            Assert.True(ApiKeyToken.TryParse(token, out var parts));
            foreach (var bytes in contents)
            {
                Assert.Equal(-1, bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(token)));
                Assert.Equal(-1, bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(parts.Secret)));
                Assert.Equal(-1, bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes(parts.Secret)));

                // Any part of the secret: a store of a truncated secret is a leak too.
                for (var start = 0; start + 16 <= parts.Secret.Length; start += 9)
                {
                    Assert.Equal(-1, bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(parts.Secret.Substring(start, 16))));
                }
            }

            // The key id is public and is stored: the scan would see a stored secret the same way.
            Assert.Contains(contents, bytes => bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(parts.KeyId)) >= 0);
        }
    }

    [Fact]
    public async Task Create_WithAPermissionTheOwnerLacks_Fails()
    {
        var owner = await _harness.AddUserAsync("reader", Permission.Read);

        var exception = await Assert.ThrowsAsync<ApiKeyRequestException>(() => _harness.CreateKeyAsync(owner, ReadPublish));

        Assert.Contains("Publish", exception.Message, StringComparison.Ordinal);
        Assert.Empty(await ListAsync(owner));
    }

    [Fact]
    public async Task Create_AnAdminKey_RequiresAnAdminOwner()
    {
        var publisher = await _harness.AddUserAsync("publisher", ReadPublish);
        var administrator = await _harness.AddUserAsync("administrator", ReadPublish | Permission.Admin);

        await Assert.ThrowsAsync<ApiKeyRequestException>(() => _harness.CreateKeyAsync(publisher, Permission.Admin));
        var created = await _harness.CreateKeyAsync(administrator, Permission.Admin);

        Assert.Equal(Permission.Admin, (await _harness.ValidateAsync(created.Token)).EffectivePermissions);
    }

    [Fact]
    public async Task AKeyOfAnAdminOwner_CarriesOnlyTheRequestedPermissions()
    {
        var administrator = await _harness.AddUserAsync("administrator", ReadPublish | Permission.Admin);

        var created = await _harness.CreateKeyAsync(administrator, Permission.Read);

        Assert.Equal(Permission.Read, (await _harness.ValidateAsync(created.Token)).EffectivePermissions);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(-1)]
    public async Task Create_WithNoOrUndefinedPermissions_Fails(int permissions)
    {
        var owner = await _harness.AddUserAsync("alice", ReadPublish | Permission.Admin);

        await Assert.ThrowsAsync<ApiKeyRequestException>(() => _harness.CreateKeyAsync(owner, (Permission)permissions));
    }

    [Fact]
    public async Task Create_ForAnUnknownOrDisabledOwner_Fails()
    {
        var disabled = await _harness.AddUserAsync("disabled", ReadPublish, active: false);

        await Assert.ThrowsAsync<ApiKeyRequestException>(() => _harness.CreateKeyAsync(Guid.NewGuid(), Permission.Read));
        await Assert.ThrowsAsync<ApiKeyRequestException>(() => _harness.CreateKeyAsync(disabled, Permission.Read));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("line\nbreak")]
    [InlineData("tab\there")]
    public async Task Create_WithAnInvalidName_Fails(string name)
    {
        var owner = await _harness.AddUserAsync("alice", ReadPublish);

        await Assert.ThrowsAsync<ApiKeyRequestException>(() => _harness.CreateKeyAsync(owner, Permission.Read, name: name));
    }

    [Fact]
    public async Task Create_WithANameAtTheLimit_Succeeds_AndOneCharacterMoreFails()
    {
        var owner = await _harness.AddUserAsync("alice", ReadPublish);

        await _harness.CreateKeyAsync(owner, Permission.Read, name: new string('n', ApiKey.MaxNameLength));
        await Assert.ThrowsAsync<ApiKeyRequestException>(
            () => _harness.CreateKeyAsync(owner, Permission.Read, name: new string('n', ApiKey.MaxNameLength + 1)));
    }

    [Fact]
    public async Task Create_WithAnExpiryThatIsNotInTheFuture_Fails()
    {
        var owner = await _harness.AddUserAsync("alice", ReadPublish);

        await Assert.ThrowsAsync<ApiKeyRequestException>(() => _harness.CreateKeyAsync(owner, Permission.Read, _harness.Now));
        await Assert.ThrowsAsync<ApiKeyRequestException>(() => _harness.CreateKeyAsync(owner, Permission.Read, _harness.Now.AddDays(-1)));
    }

    [Fact]
    public async Task List_FiltersByOwner_AndNeverExposesTheHash()
    {
        var alice = await _harness.AddUserAsync("alice", ReadPublish);
        var bob = await _harness.AddUserAsync("bob", ReadPublish);
        await _harness.CreateKeyAsync(alice, Permission.Read, name: "a1");
        _harness.Clock.Advance(TimeSpan.FromSeconds(1));
        await _harness.CreateKeyAsync(alice, Permission.Publish, name: "a2");
        await _harness.CreateKeyAsync(bob, Permission.Read, name: "b1");

        Assert.Equal(["a1", "a2"], (await ListAsync(alice)).Select(k => k.Name));
        Assert.Equal(["b1"], (await ListAsync(bob)).Select(k => k.Name));
        Assert.Equal(3, (await ListAsync(null)).Count);
        Assert.DoesNotContain(
            typeof(ApiKeyInfo).GetProperties(),
            p => p.Name.Contains("Hash", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase));
    }

    // ----- Validation -----

    [Fact]
    public async Task Validate_AValidToken_SucceedsWithTheOwnerAndTheEffectivePermissions()
    {
        var owner = await _harness.AddUserAsync("alice", ReadPublish);
        var created = await _harness.CreateKeyAsync(owner, ReadPublish);

        var result = await _harness.ValidateAsync(created.Token);

        Assert.True(result.Succeeded);
        Assert.Equal(ApiKeyValidationFailure.None, result.Failure);
        Assert.Equal(created.Key.Id, result.ApiKeyId);
        Assert.Equal(created.Key.KeyId, result.KeyId);
        Assert.Equal(owner, result.OwnerUserId);
        Assert.Equal("alice", result.OwnerUserName);
        Assert.Equal(ReadPublish, result.EffectivePermissions);
    }

    [Fact]
    public async Task Validate_AnUnknownKeyId_Fails_AfterTheSameComparisonWork()
    {
        var owner = await _harness.AddUserAsync("alice", ReadPublish);
        var created = await _harness.CreateKeyAsync(owner, Permission.Read);
        var unknown = ApiKeyToken.Format(ApiKeyGenerator.Default.GenerateKeyId(), created.Token[^43..]);

        var before = _harness.Comparer.Calls;
        var result = await _harness.ValidateAsync(unknown);

        Assert.Equal(ApiKeyValidationFailure.UnknownKey, result.Failure);
        Assert.False(result.Succeeded);
        Assert.Equal(Permission.None, result.EffectivePermissions);
        Assert.Equal(1, _harness.Comparer.Calls - before);
    }

    [Fact]
    public async Task Validate_AWrongSecret_Fails()
    {
        var owner = await _harness.AddUserAsync("alice", ReadPublish);
        var created = await _harness.CreateKeyAsync(owner, Permission.Read);
        var wrong = ApiKeyToken.Format(created.Key.KeyId, ApiKeyGenerator.Default.GenerateSecret());

        var result = await _harness.ValidateAsync(wrong);

        Assert.Equal(ApiKeyValidationFailure.WrongSecret, result.Failure);
        Assert.Equal(Permission.None, result.EffectivePermissions);
        Assert.Equal(Guid.Empty, result.OwnerUserId);
    }

    [Fact]
    public async Task EveryValidationThatReachesTheDatabase_GoesThroughTheConstantTimeComparer()
    {
        var owner = await _harness.AddUserAsync("alice", ReadPublish);
        var created = await _harness.CreateKeyAsync(owner, Permission.Read);
        var wrong = ApiKeyToken.Format(created.Key.KeyId, ApiKeyGenerator.Default.GenerateSecret());

        var before = _harness.Comparer.Calls;
        await _harness.ValidateAsync(created.Token);
        await _harness.ValidateAsync(wrong);
        Assert.Equal(2, _harness.Comparer.Calls - before);

        using var provider = BuildProvider(new CapturingLoggerProvider());
        Assert.IsType<FixedTimeApiKeyHashComparer>(provider.GetRequiredService<IApiKeyHashComparer>());
    }

    [Fact]
    public async Task Validate_ARevokedKey_Fails()
    {
        var owner = await _harness.AddUserAsync("alice", ReadPublish);
        var created = await _harness.CreateKeyAsync(owner, Permission.Read);

        Assert.Equal(ApiKeyRevocation.Revoked, await RevokeAsync(created.Key.KeyId));

        Assert.Equal(ApiKeyValidationFailure.Revoked, (await _harness.ValidateAsync(created.Token)).Failure);
    }

    [Fact]
    public async Task Validate_AnExpiredKey_FailsFromTheExpiryInstantOn()
    {
        var owner = await _harness.AddUserAsync("alice", ReadPublish);
        var created = await _harness.CreateKeyAsync(owner, Permission.Read, _harness.Now.AddHours(1));

        _harness.Clock.Advance(TimeSpan.FromHours(1) - TimeSpan.FromTicks(10));
        Assert.True((await _harness.ValidateAsync(created.Token)).Succeeded);

        _harness.Clock.Advance(TimeSpan.FromTicks(10));
        Assert.Equal(ApiKeyValidationFailure.Expired, (await _harness.ValidateAsync(created.Token)).Failure);
    }

    [Fact]
    public async Task Validate_WhenTheOwnerIsDisabled_Fails()
    {
        var owner = await _harness.AddUserAsync("alice", ReadPublish);
        var created = await _harness.CreateKeyAsync(owner, Permission.Read);

        await _harness.UpdateUserAsync(owner, u => u.IsActive = false);

        Assert.Equal(ApiKeyValidationFailure.OwnerDisabled, (await _harness.ValidateAsync(created.Token)).Failure);
    }

    [Fact]
    public async Task DemotingTheOwner_RemovesThePermissionFromTheKeyAtTheNextUse_WithoutTouchingTheKey()
    {
        var owner = await _harness.AddUserAsync("alice", ReadPublish);
        var created = await _harness.CreateKeyAsync(owner, ReadPublish);
        Assert.Equal(ReadPublish, (await _harness.ValidateAsync(created.Token)).EffectivePermissions);

        await _harness.UpdateUserAsync(owner, u => u.Permissions = Permission.Read);

        var result = await _harness.ValidateAsync(created.Token);
        Assert.True(result.Succeeded);
        Assert.Equal(Permission.Read, result.EffectivePermissions);
        Assert.Equal(ReadPublish, (await _harness.LoadKeyAsync(created.Key.KeyId)).Permissions);

        await _harness.UpdateUserAsync(owner, u => u.Permissions = Permission.None);
        result = await _harness.ValidateAsync(created.Token);
        Assert.True(result.Succeeded);
        Assert.Equal(Permission.None, result.EffectivePermissions);

        // Restoring the owner restores the key, which never lost its own permissions.
        await _harness.UpdateUserAsync(owner, u => u.Permissions = ReadPublish | Permission.Admin);
        Assert.Equal(ReadPublish, (await _harness.ValidateAsync(created.Token)).EffectivePermissions);
    }

    [Fact]
    public async Task TamperedTokens_AreRefused()
    {
        var owner = await _harness.AddUserAsync("alice", ReadPublish);
        var token = (await _harness.CreateKeyAsync(owner, Permission.Read)).Token;
        var keyIdStart = 4;
        var secretStart = 17;

        Assert.Equal(ApiKeyValidationFailure.UnknownKey, (await _harness.ValidateAsync(FlipCase(token, keyIdStart, 12))).Failure);
        Assert.Equal(ApiKeyValidationFailure.WrongSecret, (await _harness.ValidateAsync(FlipCase(token, secretStart, 43))).Failure);
        Assert.Equal(ApiKeyValidationFailure.WrongSecret, (await _harness.ValidateAsync(ChangeLast(token))).Failure);
        Assert.Equal(ApiKeyValidationFailure.Malformed, (await _harness.ValidateAsync("EPQ_" + token[4..])).Failure);
        Assert.Equal(ApiKeyValidationFailure.Malformed, (await _harness.ValidateAsync(" " + token)).Failure);
        Assert.Equal(ApiKeyValidationFailure.Malformed, (await _harness.ValidateAsync(token + " ")).Failure);
        Assert.Equal(ApiKeyValidationFailure.Malformed, (await _harness.ValidateAsync(token + "\n")).Failure);
        Assert.Equal(ApiKeyValidationFailure.Malformed, (await _harness.ValidateAsync(token[..^1])).Failure);
        Assert.Equal(ApiKeyValidationFailure.Malformed, (await _harness.ValidateAsync(token + "A")).Failure);
        Assert.Equal(ApiKeyValidationFailure.Malformed, (await _harness.ValidateAsync(token.Replace('_', '-'))).Failure);

        // Untouched, it still works: the refusals above were about the tampering.
        Assert.True((await _harness.ValidateAsync(token)).Succeeded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a token")]
    [InlineData("Basic YWxpY2U6cGFzc3dvcmQ=")]
    public async Task MalformedValues_AreRefusedWithoutTouchingTheDatabase(string? value)
    {
        await _harness.AddUserAsync("alice", ReadPublish);
        var commands = _harness.Counter.Commands;
        var comparisons = _harness.Comparer.Calls;

        var result = await _harness.ValidateAsync(value);

        Assert.Equal(ApiKeyValidationFailure.Malformed, result.Failure);
        Assert.Null(result.KeyId);
        Assert.Equal(commands, _harness.Counter.Commands);
        Assert.Equal(comparisons, _harness.Comparer.Calls);
    }

    [Theory]
    [InlineData(ApiKeyToken.MaximumInputLength + 1)]
    [InlineData(5_000_000)]
    public async Task OversizedValues_AreRefusedWithoutTouchingTheDatabase(int length)
    {
        await _harness.AddUserAsync("alice", ReadPublish);
        var commands = _harness.Counter.Commands;

        var result = await _harness.ValidateAsync("epq_" + new string('a', length - 4));

        Assert.Equal(ApiKeyValidationFailure.Oversized, result.Failure);
        Assert.Equal(commands, _harness.Counter.Commands);
    }

    [Fact]
    public async Task AValueOfTheMaximumLength_IsMalformedNotOversized()
    {
        var result = await _harness.ValidateAsync(new string('a', ApiKeyToken.MaximumInputLength));

        Assert.Equal(ApiKeyValidationFailure.Malformed, result.Failure);
    }

    // ----- Revocation -----

    [Fact]
    public async Task Revoke_IsIdempotent_AndKeepsTheFirstRevocationTime()
    {
        var owner = await _harness.AddUserAsync("alice", ReadPublish);
        var created = await _harness.CreateKeyAsync(owner, Permission.Read);

        Assert.Equal(ApiKeyRevocation.Revoked, await RevokeAsync(created.Key.KeyId));
        var firstRevocation = _harness.Now;
        _harness.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(ApiKeyRevocation.AlreadyRevoked, await RevokeAsync(created.Key.KeyId));

        Assert.Equal(firstRevocation, (await _harness.LoadKeyAsync(created.Key.KeyId)).RevokedAt);
    }

    [Theory]
    [InlineData("AAAAAAAAAAAA")]
    [InlineData("short")]
    [InlineData("' OR 1=1 --x")]
    [InlineData("")]
    public async Task Revoke_AnUnknownKey_ReportsNotFound(string keyId)
    {
        var owner = await _harness.AddUserAsync("alice", ReadPublish);
        await _harness.CreateKeyAsync(owner, Permission.Read);

        Assert.Equal(ApiKeyRevocation.NotFound, await RevokeAsync(keyId));
        Assert.All(await ListAsync(owner), k => Assert.Null(k.RevokedAt));
    }

    // ----- Rotation -----

    [Fact]
    public async Task Rotate_CreatesAnEquivalentKey_AndRevokesTheOldOne()
    {
        var owner = await _harness.AddUserAsync("alice", ReadPublish);
        var old = await _harness.CreateKeyAsync(owner, ReadPublish, name: "ci-main");

        ApiKeyCreated rotated;
        await using (var context = _harness.CreateContext())
        {
            rotated = await _harness.CreateService(context).RotateAsync(old.Key.KeyId, _harness.Now.AddDays(30));
        }

        Assert.NotEqual(old.Key.KeyId, rotated.Key.KeyId);
        Assert.Equal("ci-main", rotated.Key.Name);
        Assert.Equal(ReadPublish, rotated.Key.Permissions);
        Assert.Equal(_harness.Now.AddDays(30), rotated.Key.ExpiresAt);
        Assert.Equal(ApiKeyValidationFailure.Revoked, (await _harness.ValidateAsync(old.Token)).Failure);
        Assert.True((await _harness.ValidateAsync(rotated.Token)).Succeeded);
    }

    [Fact]
    public async Task Rotate_WhenTheNewKeyCannotBeStored_LeavesTheOldKeyValid()
    {
        var owner = await _harness.AddUserAsync("alice", ReadPublish);
        var old = await _harness.CreateKeyAsync(owner, ReadPublish);

        // A real storage failure inside the transaction, after the old key has been marked revoked in memory.
        await _harness.ExecuteSqlAsync(
            "CREATE TRIGGER fail_api_key_insert BEFORE INSERT ON ApiKeys BEGIN SELECT RAISE(ABORT, 'injected failure'); END;");

        await using (var context = _harness.CreateContext())
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => _harness.CreateService(context).RotateAsync(old.Key.KeyId, null));
        }

        Assert.True((await _harness.ValidateAsync(old.Token)).Succeeded);
        var keys = await ListAsync(owner);
        Assert.Single(keys);
        Assert.Null(keys[0].RevokedAt);
    }

    [Fact]
    public async Task Rotate_WhenTheOldKeyCannotBeRevoked_StoresNoNewKey()
    {
        var owner = await _harness.AddUserAsync("alice", ReadPublish);
        var old = await _harness.CreateKeyAsync(owner, ReadPublish);

        await _harness.ExecuteSqlAsync(
            "CREATE TRIGGER fail_api_key_update BEFORE UPDATE ON ApiKeys BEGIN SELECT RAISE(ABORT, 'injected failure'); END;");

        await using (var context = _harness.CreateContext())
        {
            await Assert.ThrowsAsync<SqliteException>(() => _harness.CreateService(context).RotateAsync(old.Key.KeyId, null));
        }

        Assert.Single(await ListAsync(owner));
    }

    [Fact]
    public async Task ConcurrentRotationsOfTheSameKey_ProduceExactlyOneNewKey()
    {
        var owner = await _harness.AddUserAsync("alice", ReadPublish);
        var old = await _harness.CreateKeyAsync(owner, ReadPublish);

        async Task<ApiKeyCreated?> RotateAsync()
        {
            await using var context = _harness.CreateContext();
            try
            {
                return await _harness.CreateService(context).RotateAsync(old.Key.KeyId, null);
            }
            catch (ApiKeyRequestException)
            {
                return null;
            }
        }

        // The SQLite provider runs its "async" calls synchronously: one thread per rotation.
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(RotateAsync)));

        var winner = Assert.Single(results, r => r is not null);
        var keys = await ListAsync(owner);
        Assert.Equal(2, keys.Count);
        Assert.Single(keys, k => k.RevokedAt is null);
        Assert.True((await _harness.ValidateAsync(winner!.Token)).Succeeded);
        Assert.Equal(ApiKeyValidationFailure.Revoked, (await _harness.ValidateAsync(old.Token)).Failure);
    }

    [Fact]
    public void ThePermissionFlags_FitTheDatabaseCheckConstraint()
    {
        // CK_Users_Permissions and CK_ApiKeys_Permissions accept 0..7: a new flag needs a migration.
        var all = Enum.GetValues<Permission>().Aggregate(Permission.None, (acc, p) => acc | p);

        Assert.Equal(7, (int)all);
    }

    [Fact]
    public async Task Rotate_ARevokedOrUnknownKey_Fails()
    {
        var owner = await _harness.AddUserAsync("alice", ReadPublish);
        var old = await _harness.CreateKeyAsync(owner, ReadPublish);
        await RevokeAsync(old.Key.KeyId);

        await using var context = _harness.CreateContext();
        var service = _harness.CreateService(context);
        await Assert.ThrowsAsync<ApiKeyRequestException>(() => service.RotateAsync(old.Key.KeyId, null));
        await Assert.ThrowsAsync<ApiKeyRequestException>(() => service.RotateAsync("AAAAAAAAAAAA", null));
        await Assert.ThrowsAsync<ApiKeyRequestException>(() => service.RotateAsync("bad", null));
        Assert.Single(await ListAsync(owner));
    }

    [Fact]
    public async Task Rotate_AfterTheOwnerLostAPermissionOfTheKey_Fails_AndKeepsTheOldKey()
    {
        var owner = await _harness.AddUserAsync("alice", ReadPublish);
        var old = await _harness.CreateKeyAsync(owner, ReadPublish);
        await _harness.UpdateUserAsync(owner, u => u.Permissions = Permission.Read);

        await using (var context = _harness.CreateContext())
        {
            await Assert.ThrowsAsync<ApiKeyRequestException>(() => _harness.CreateService(context).RotateAsync(old.Key.KeyId, null));
        }

        Assert.True((await _harness.ValidateAsync(old.Token)).Succeeded);
    }

    // ----- Owner deletion -----

    [Fact]
    public async Task DeletingAUserWhoOwnsKeys_IsRestricted()
    {
        var owner = await _harness.AddUserAsync("alice", ReadPublish);
        await _harness.CreateKeyAsync(owner, Permission.Read);

        await using var context = _harness.CreateContext();
        context.Users.Remove(await context.Users.SingleAsync(u => u.Id == owner));

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    // ----- Logging -----

    [Fact]
    public async Task SecretsAndTokens_NeverAppearInLogsOrMessages()
    {
        var logs = new CapturingLoggerProvider();
        await using var provider = BuildProvider(logs);
        await provider.GetRequiredService<IAuthDatabaseMigrator>().MigrateAsync();

        var owner = await _harness.AddUserAsync("alice", ReadPublish);
        var tokens = new List<string>();
        var messages = new List<string>();

        await using (var scope = provider.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IApiKeyService>();
            var created = await service.CreateAsync(owner, "ci-main", ReadPublish, null);
            tokens.Add(created.Token);
            messages.Add(created.ToString());

            var wrong = ApiKeyToken.Format(created.Key.KeyId, ApiKeyGenerator.Default.GenerateSecret());
            tokens.Add(wrong);
            var unknown = ApiKeyToken.Format(ApiKeyGenerator.Default.GenerateKeyId(), ApiKeyGenerator.Default.GenerateSecret());
            tokens.Add(unknown);
            const string Sentinel = "s3ntinel-api-key-value-0123456789";
            tokens.Add(Sentinel);

            await service.ValidateAsync(created.Token);
            await service.ValidateAsync(wrong);
            await service.ValidateAsync(unknown);
            await service.ValidateAsync(Sentinel);
            await service.ValidateAsync(created.Token + Sentinel);
            await service.ValidateAsync(new string('x', 200) + Sentinel);

            var rotated = await service.RotateAsync(created.Key.KeyId, null);
            tokens.Add(rotated.Token);
            await service.ValidateAsync(created.Token);
            await service.RevokeAsync(rotated.Key.KeyId);
            await service.ValidateAsync(rotated.Token);

            messages.Add((await Assert.ThrowsAsync<ApiKeyRequestException>(() => service.RotateAsync(rotated.Key.KeyId, null))).Message);
        }

        Assert.NotEmpty(logs.Lines);
        Assert.Contains(logs.Lines, l => l.Contains("rotated", StringComparison.Ordinal));
        foreach (var token in tokens)
        {
            var secret = ApiKeyToken.TryParse(token, out var parts) ? parts.Secret : token;
            Assert.DoesNotContain(logs.Lines, l => l.Contains(secret, StringComparison.Ordinal));
            Assert.DoesNotContain(messages, m => m.Contains(secret, StringComparison.Ordinal));
        }
    }

    private List<byte[]> ReadDatabaseFiles()
    {
        var contents = new List<byte[]>();
        foreach (var path in new[] { _database.DatabasePath, _database.DatabasePath + "-wal" })
        {
            if (!File.Exists(path))
            {
                continue;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            contents.Add(copy.ToArray());
        }

        return contents;
    }

    private static string FlipCase(string token, int start, int length)
    {
        for (var i = start; i < start + length; i++)
        {
            if (char.IsLetter(token[i]))
            {
                var flipped = char.IsUpper(token[i]) ? char.ToLowerInvariant(token[i]) : char.ToUpperInvariant(token[i]);
                return string.Concat(token.AsSpan(0, i), flipped.ToString(), token.AsSpan(i + 1));
            }
        }

        throw new InvalidOperationException("No letter to flip (probability below 1e-9).");
    }

    private static string ChangeLast(string token)
    {
        var last = token[^1];
        var replacement = ApiKeyGenerator.Alphabet[(ApiKeyGenerator.Alphabet.IndexOf(last, StringComparison.Ordinal) + 1) % 62];
        return token[..^1] + replacement;
    }

    private ServiceProvider BuildProvider(CapturingLoggerProvider logs)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(logs);
        });
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:Database:ConnectionString"] = _database.ConnectionString,
                ["Security:Passwords:IterationCount"] = "100000",
            })
            .Build());
        new ElPaquetoApplication(services).AddElPaquetoSecurity();
        return services.BuildServiceProvider();
    }

    private async Task<IReadOnlyList<ApiKeyInfo>> ListAsync(Guid? owner)
    {
        await using var context = _harness.CreateContext();
        return await _harness.CreateService(context).ListAsync(owner);
    }

    private async Task<ApiKeyRevocation> RevokeAsync(string keyId)
    {
        await using var context = _harness.CreateContext();
        return await _harness.CreateService(context).RevokeAsync(keyId);
    }
}
