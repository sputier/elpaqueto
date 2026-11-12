using System.Data.Common;
using ElPaqueto.Security.ApiKeys;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ElPaqueto.Security.Tests;

/// <summary>
/// The last-use timestamp must not turn read traffic into write traffic on the security database.
/// </summary>
public sealed class ApiKeyLastUsedTests : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly AuthDatabaseFixture _database = new();
    private readonly ApiKeyServiceHarness _harness;

    public ApiKeyLastUsedTests()
    {
        _harness = new ApiKeyServiceHarness(_database, Interval);
    }

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task ManyUsesWithinTheInterval_WriteOnce()
    {
        var token = await CreateTokenAsync();
        var updates = _harness.Counter.Updates;

        for (var i = 0; i < 100; i++)
        {
            Assert.True((await _harness.ValidateAsync(token.Token)).Succeeded);
            _harness.Clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(1, _harness.Counter.Updates - updates);
        Assert.Equal(ApiKeyServiceHarness.Start.UtcDateTime, (await _harness.LoadKeyAsync(token.Key.KeyId)).LastUsedAt);
    }

    [Fact]
    public async Task AUseAfterTheInterval_WritesAgain()
    {
        var token = await CreateTokenAsync();
        await _harness.ValidateAsync(token.Token);
        var updates = _harness.Counter.Updates;

        _harness.Clock.Advance(Interval - TimeSpan.FromTicks(1));
        await _harness.ValidateAsync(token.Token);
        Assert.Equal(0, _harness.Counter.Updates - updates);

        _harness.Clock.Advance(TimeSpan.FromTicks(1));
        await _harness.ValidateAsync(token.Token);
        Assert.Equal(1, _harness.Counter.Updates - updates);
        Assert.Equal(_harness.Now, (await _harness.LoadKeyAsync(token.Key.KeyId)).LastUsedAt);
    }

    [Fact]
    public async Task ARecentStoredTimestamp_SuppressesTheWrite_InAnotherProcess()
    {
        var token = await CreateTokenAsync();
        await _harness.ValidateAsync(token.Token);

        // A second server process (or a restart) has no in-memory state: the stored value decides.
        _harness.RestartProcess();
        _harness.Clock.Advance(TimeSpan.FromMinutes(1));
        var updates = _harness.Counter.Updates;

        await _harness.ValidateAsync(token.Token);

        Assert.Equal(0, _harness.Counter.Updates - updates);
    }

    [Fact]
    public async Task ConcurrentUsesOfAStaleKey_WriteOnce()
    {
        var token = await CreateTokenAsync();
        var updates = _harness.Counter.Updates;

        // The SQLite provider runs its "async" calls synchronously: one thread per request.
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => _harness.ValidateAsync(token.Token))));

        Assert.All(results, r => Assert.True(r.Succeeded));
        Assert.Equal(1, _harness.Counter.Updates - updates);
    }

    [Fact]
    public async Task AnotherProcessWritingBetweenTheReadAndTheWrite_WinsTheRace()
    {
        var token = await CreateTokenAsync();
        await _harness.ValidateAsync(token.Token);
        _harness.Clock.Advance(Interval);
        _harness.RestartProcess();

        // Right after this process read the (stale) key, another process records a use.
        var otherProcessWroteAt = _harness.Now.AddSeconds(-1);
        var race = new WriteAfterKeyRead(_database.ConnectionString, otherProcessWroteAt);
        await using (var context = _harness.CreateContext(race))
        {
            Assert.True((await _harness.CreateService(context).ValidateAsync(token.Token)).Succeeded);
        }

        Assert.True(race.Fired);
        Assert.Equal(otherProcessWroteAt, (await _harness.LoadKeyAsync(token.Key.KeyId)).LastUsedAt);
    }

    [Fact]
    public async Task AFailedWrite_DoesNotRefuseTheKey()
    {
        var token = await CreateTokenAsync();
        await _harness.ExecuteSqlAsync(
            "CREATE TRIGGER fail_api_key_update BEFORE UPDATE ON ApiKeys BEGIN SELECT RAISE(ABORT, 'injected failure'); END;");

        var result = await _harness.ValidateAsync(token.Token);

        Assert.True(result.Succeeded);
        Assert.Null((await _harness.LoadKeyAsync(token.Key.KeyId)).LastUsedAt);
        Assert.Contains(_harness.Logs.Lines, l => l.StartsWith("Warning", StringComparison.Ordinal) && l.Contains(token.Key.KeyId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailedValidations_NeverWrite()
    {
        var token = await CreateTokenAsync();
        var updates = _harness.Counter.Updates;

        await _harness.ValidateAsync(ApiKeyToken.Format(token.Key.KeyId, ApiKeyGenerator.Default.GenerateSecret()));

        Assert.Equal(0, _harness.Counter.Updates - updates);
        Assert.Null((await _harness.LoadKeyAsync(token.Key.KeyId)).LastUsedAt);
    }

    [Fact]
    public async Task UsingAKey_DoesNotChangeItsConcurrencyStamp()
    {
        var token = await CreateTokenAsync();
        var stamp = (await _harness.LoadKeyAsync(token.Key.KeyId)).ConcurrencyStamp;

        await _harness.ValidateAsync(token.Token);

        var stored = await _harness.LoadKeyAsync(token.Key.KeyId);
        Assert.NotNull(stored.LastUsedAt);
        Assert.Equal(stamp, stored.ConcurrencyStamp);
    }

    [Fact]
    public void Tracker_ClaimsOncePerKeyAndInterval()
    {
        var tracker = ApiKeyServiceHarness.NewTracker(Interval);
        var now = ApiKeyServiceHarness.Start.UtcDateTime;
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        Assert.True(tracker.TryClaimWrite(first, null, now));
        Assert.False(tracker.TryClaimWrite(first, null, now.AddMinutes(4)));
        Assert.True(tracker.TryClaimWrite(second, null, now));
        Assert.True(tracker.TryClaimWrite(first, null, now.AddMinutes(5)));
        Assert.False(tracker.TryClaimWrite(Guid.NewGuid(), now.AddMinutes(-1), now));

        // A clock that went backwards never causes a burst of writes.
        Assert.False(tracker.TryClaimWrite(Guid.NewGuid(), now.AddMinutes(10), now));
    }

    [Fact]
    public void Tracker_DropsExpiredClaimsWhenItGrows()
    {
        var tracker = ApiKeyServiceHarness.NewTracker(Interval);
        var now = ApiKeyServiceHarness.Start.UtcDateTime;
        var early = Guid.NewGuid();
        Assert.True(tracker.TryClaimWrite(early, null, now));

        for (var i = 0; i < 2_000; i++)
        {
            Assert.True(tracker.TryClaimWrite(Guid.NewGuid(), null, now.AddMinutes(10)));
        }

        Assert.True(tracker.TryClaimWrite(early, null, now.AddMinutes(10)));
    }

    private async Task<ApiKeyCreated> CreateTokenAsync()
    {
        var owner = await _harness.AddUserAsync("alice", Permission.Read);
        return await _harness.CreateKeyAsync(owner, Permission.Read);
    }

    private sealed class WriteAfterKeyRead(string connectionString, DateTime lastUsedAt) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
        {
            Fire(command);
            return result;
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            Fire(command);
            return ValueTask.FromResult(result);
        }

        private void Fire(DbCommand command)
        {
            if (Fired || !command.CommandText.Contains("FROM \"ApiKeys\"", StringComparison.Ordinal))
            {
                return;
            }

            Fired = true;
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var update = connection.CreateCommand();
            update.CommandText = "UPDATE ApiKeys SET LastUsedAt = $at";
            update.Parameters.AddWithValue("$at", lastUsedAt);
            update.ExecuteNonQuery();
        }
    }
}
