using System.Net;
using ElPaqueto.Security.Configuration;
using ElPaqueto.Security.Throttling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace ElPaqueto.Security.Tests;

/// <summary>
/// The sign-in throttle on its own, with a controlled clock. 203.0.113.0/24, 198.51.100.0/24 and
/// 2001:db8::/32 are documentation ranges.
/// </summary>
public sealed class LoginThrottleTests : IDisposable
{
    private static readonly IPAddress ClientA = IPAddress.Parse("203.0.113.10");
    private static readonly IPAddress ClientB = IPAddress.Parse("198.51.100.20");
    private static readonly int[] ExpectedTripMinutes = [1, 2, 4, 8, 15, 15, 15];

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly TestLogger _logger = new();
    private LoginThrottle? _throttle;

    public void Dispose() => _throttle?.Dispose();

    [Fact]
    public async Task FivePairFailures_ThrottleTheNextAttemptForThatPair_WithoutGrantingAVerification()
    {
        var throttle = Create();

        for (var i = 0; i < 4; i++)
        {
            await FailAsync(throttle, ClientA, "alice");
            Assert.Null(throttle.Check(ClientA, "alice"));
        }

        await FailAsync(throttle, ClientA, "alice");

        var rejection = throttle.Check(ClientA, "alice");
        Assert.NotNull(rejection);
        Assert.Equal(429, rejection.Value.StatusCode);
        Assert.Equal(TimeSpan.FromMinutes(1), rejection.Value.RetryAfter);

        using var slot = await throttle.EnterVerificationAsync(ClientA, "alice", CancellationToken.None);
        Assert.Equal(429, slot.Rejection?.StatusCode);
    }

    [Fact]
    public async Task APairThrottle_DoesNotAffectAnotherClientOrAnotherUserName()
    {
        var throttle = Create();
        await FailAsync(throttle, ClientA, "alice", times: 5);

        Assert.NotNull(throttle.Check(ClientA, "alice"));
        Assert.Null(throttle.Check(ClientB, "alice"));
        Assert.Null(throttle.Check(ClientA, "bob"));
    }

    [Fact]
    public async Task UserNamesAreCountedWithoutRegardToCase()
    {
        var throttle = Create();
        await FailAsync(throttle, ClientA, "alice", times: 3);
        await FailAsync(throttle, ClientA, "ALICE", times: 2);

        Assert.NotNull(throttle.Check(ClientA, "Alice"));
    }

    [Fact]
    public async Task AfterTheThrottleDuration_TheClientMayTryAgain()
    {
        var throttle = Create();
        await FailAsync(throttle, ClientA, "alice", times: 5);

        _time.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(TimeSpan.FromSeconds(1), throttle.Check(ClientA, "alice")?.RetryAfter);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(throttle.Check(ClientA, "alice"));
        using var slot = await throttle.EnterVerificationAsync(ClientA, "alice", CancellationToken.None);
        Assert.Null(slot.Rejection);
    }

    [Fact]
    public async Task RepeatedTrips_DoubleTheDurationUpToTheMaximum()
    {
        var throttle = Create();
        var durations = new List<TimeSpan>();

        for (var trip = 0; trip < 7; trip++)
        {
            await FailAsync(throttle, ClientA, "alice", times: 5);
            var rejection = throttle.Check(ClientA, "alice");
            Assert.NotNull(rejection);
            durations.Add(rejection.Value.RetryAfter);
            _time.Advance(rejection.Value.RetryAfter);
        }

        Assert.Equal(ExpectedTripMinutes.Select(m => TimeSpan.FromMinutes(m)), durations);
    }

    [Fact]
    public async Task TripsAreForgottenOneWindowAfterTheLastThrottleEnds()
    {
        var throttle = Create();
        await FailAsync(throttle, ClientA, "alice", times: 5);
        _time.Advance(TimeSpan.FromMinutes(1));
        await FailAsync(throttle, ClientA, "alice", times: 5);
        Assert.Equal(TimeSpan.FromMinutes(2), throttle.Check(ClientA, "alice")?.RetryAfter);

        // The throttle ends after 2 minutes; the pair window is 15 minutes.
        _time.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromMinutes(15));
        await FailAsync(throttle, ClientA, "alice", times: 5);

        Assert.Equal(TimeSpan.FromMinutes(1), throttle.Check(ClientA, "alice")?.RetryAfter);
    }

    [Fact]
    public async Task FailuresOlderThanTheWindow_AreNotCounted()
    {
        var throttle = Create();
        await FailAsync(throttle, ClientA, "alice", times: 4);

        _time.Advance(TimeSpan.FromMinutes(15));
        await FailAsync(throttle, ClientA, "alice");

        Assert.Null(throttle.Check(ClientA, "alice"));
        Assert.Equal(1, throttle.FailureCount(ThrottleScope.Pair, ClientA, "alice"));
    }

    [Fact]
    public async Task ThirtyFailuresAcrossUserNames_ThrottleTheClient()
    {
        var throttle = Create();
        for (var i = 0; i < 29; i++)
        {
            await FailAsync(throttle, ClientA, $"user{i}");
        }

        Assert.Null(throttle.Check(ClientA, "someone-else"));

        await FailAsync(throttle, ClientA, "user29");

        Assert.Equal(429, throttle.Check(ClientA, "someone-else")?.StatusCode);
        Assert.Equal(429, throttle.Check(ClientA, null)?.StatusCode);
        Assert.Null(throttle.Check(ClientB, "someone-else"));
    }

    [Theory]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:2:ffff:ffff:ffff:fffe", true)]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:3::1", false)]
    [InlineData("203.0.113.10", "::ffff:203.0.113.10", true)]
    [InlineData("203.0.113.10", "203.0.113.11", false)]
    [InlineData("::ffff:203.0.113.10", "::ffff:203.0.113.11", false)]
    public async Task ClientAddresses_AreGroupedByIPv4AddressOrIPv6Slash64(string first, string second, bool shared)
    {
        var throttle = Create();
        for (var i = 0; i < 30; i++)
        {
            await FailAsync(throttle, IPAddress.Parse(first), $"user{i}");
        }

        var rejection = throttle.Check(IPAddress.Parse(second), "anyone");

        Assert.Equal(shared, rejection is not null);
    }

    [Fact]
    public async Task ASuccess_DoesNotResetTheClientCounter()
    {
        var throttle = Create();
        for (var i = 0; i < 29; i++)
        {
            await FailAsync(throttle, ClientA, $"user{i}");
        }

        await SucceedAsync(throttle, ClientA, "valid-user");
        throttle.RecordSuccess(ClientA, "valid-user");

        Assert.Equal(29, throttle.FailureCount(ThrottleScope.Client, ClientA, null));
        await FailAsync(throttle, ClientA, "user29");
        Assert.NotNull(throttle.Check(ClientA, "valid-user"));
    }

    [Fact]
    public async Task ASuccess_ResetsOnlyItsOwnPair()
    {
        var throttle = Create();
        await FailAsync(throttle, ClientA, "alice", times: 4);
        await FailAsync(throttle, ClientB, "alice", times: 4);

        await SucceedAsync(throttle, ClientA, "alice");

        Assert.Equal(0, throttle.FailureCount(ThrottleScope.Pair, ClientA, "alice"));
        Assert.Equal(4, throttle.FailureCount(ThrottleScope.Pair, ClientB, "alice"));
        Assert.Equal(8, throttle.FailureCount(ThrottleScope.Account, null, "alice"));
        await FailAsync(throttle, ClientA, "alice", times: 4);
        Assert.Null(throttle.Check(ClientA, "alice"));
    }

    [Fact]
    public async Task ACacheHitSuccess_IsNotAFailureAndResetsThePair()
    {
        var throttle = Create();
        await FailAsync(throttle, ClientA, "alice", times: 4);

        for (var i = 0; i < 100; i++)
        {
            throttle.RecordSuccess(ClientA, "alice");
        }

        Assert.Equal(0, throttle.FailureCount(ThrottleScope.Pair, ClientA, "alice"));
        Assert.Equal(4, throttle.FailureCount(ThrottleScope.Client, ClientA, null));
        Assert.Null(throttle.Check(ClientA, "alice"));
    }

    [Fact]
    public async Task TheAccountCeiling_RefusesOnlyClientsThatFailedForThatAccount()
    {
        var throttle = Create();

        // A distributed guess: one failure from each of 50 addresses.
        for (var i = 0; i < 50; i++)
        {
            await FailAsync(throttle, IPAddress.Parse($"198.51.100.{i + 1}"), "victim");
        }

        // An address that took part is refused for that account, and only for it.
        Assert.Equal(429, throttle.Check(IPAddress.Parse("198.51.100.1"), "victim")?.StatusCode);
        Assert.Null(throttle.Check(IPAddress.Parse("198.51.100.1"), "someone-else"));

        // The owner, who has not failed from its own address, is not affected.
        Assert.Null(throttle.Check(ClientA, "victim"));
        using (var slot = await throttle.EnterVerificationAsync(ClientA, "victim", CancellationToken.None))
        {
            Assert.Null(slot.Rejection);
            slot.Succeeded();
        }

        // A new attacking address gets one guess, then is refused for that account.
        var newcomer = IPAddress.Parse("198.51.100.200");
        Assert.Null(throttle.Check(newcomer, "victim"));
        await FailAsync(throttle, newcomer, "victim");
        Assert.Equal(429, throttle.Check(newcomer, "victim")?.StatusCode);
    }

    [Fact]
    public async Task UnderTheAccountCeiling_FailuresFromManyClientsAreNotRefused()
    {
        var throttle = Create();
        for (var i = 0; i < 49; i++)
        {
            await FailAsync(throttle, IPAddress.Parse($"198.51.100.{i + 1}"), "victim");
        }

        Assert.Null(throttle.Check(IPAddress.Parse("198.51.100.1"), "victim"));
    }

    [Fact]
    public async Task AMillionDistinctUserNames_KeepEveryCounterKindWithinItsBound()
    {
        const int bound = 1_000;
        var throttle = Create(o => o.MaxTrackedEntries = bound);
        var largest = 0;

        for (var i = 0; i < 1_000_000; i++)
        {
            // Rotate addresses so that no client counter trips and every attempt reaches the verification.
            var address = new IPAddress(BitConverter.GetBytes(0x0A000000 + (i % 50_000)));
            await FailAsync(throttle, address, $"random-{i}");

            if (i % 10_000 == 0)
            {
                largest = Math.Max(largest, MaxEntries(throttle));
            }
        }

        largest = Math.Max(largest, MaxEntries(throttle));
        Assert.InRange(largest, 1, bound);
        Assert.InRange(throttle.TrackedEntries(ThrottleScope.Account), bound / 2, bound);
    }

    [Fact]
    public async Task ParallelFailures_AreCountedExactly()
    {
        var throttle = Create(o =>
        {
            o.ClientFailureLimit = 1000;
            o.AccountFailureLimit = 1000;
            o.MaxConcurrentVerifications = 64;
            o.VerificationQueueLimit = 1000;
        });

        await Task.WhenAll(Enumerable.Range(0, 500).Select(i => Task.Run(() => FailAsync(throttle, ClientA, $"user{i}"))));
        await Task.WhenAll(Enumerable.Range(0, 500).Select(i => Task.Run(() => FailAsync(throttle, IPAddress.Parse($"2001:db8:{i:x}::1"), "shared"))));

        Assert.Equal(500, throttle.FailureCount(ThrottleScope.Client, ClientA, null));
        Assert.Equal(500, throttle.FailureCount(ThrottleScope.Account, null, "shared"));
    }

    [Fact]
    public async Task AParallelBurstForOnePair_CannotOvershootThePairLimit()
    {
        var throttle = Create(o =>
        {
            o.MaxConcurrentVerifications = 64;
            o.VerificationQueueLimit = 1000;
        });
        var granted = 0;

        await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(async () =>
        {
            using var slot = await throttle.EnterVerificationAsync(ClientA, "alice", CancellationToken.None);
            if (slot.Rejection is null)
            {
                Interlocked.Increment(ref granted);
                await Task.Yield();
                slot.Failed();
            }
        })));

        Assert.Equal(5, granted);
    }

    [Fact]
    public async Task AParallelBurstAcrossUserNames_CannotOvershootTheClientLimit()
    {
        var throttle = Create(o =>
        {
            o.MaxConcurrentVerifications = 64;
            o.VerificationQueueLimit = 1000;
        });
        var granted = 0;

        await Task.WhenAll(Enumerable.Range(0, 200).Select(i => Task.Run(async () =>
        {
            using var slot = await throttle.EnterVerificationAsync(ClientA, $"user{i}", CancellationToken.None);
            if (slot.Rejection is null)
            {
                Interlocked.Increment(ref granted);
                await Task.Yield();
                slot.Failed();
            }
        })));

        Assert.Equal(30, granted);
    }

    [Fact]
    public async Task AParallelBurstWithTheRightPassword_IsSerializedNotRefused()
    {
        var throttle = Create(o => o.VerificationQueueLimit = 1000);
        var refused = 0;

        await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(async () =>
        {
            using var slot = await throttle.EnterVerificationAsync(ClientA, "alice", CancellationToken.None);
            if (slot.Rejection is not null)
            {
                Interlocked.Increment(ref refused);
                return;
            }

            await Task.Yield();
            slot.Succeeded();
        })));

        Assert.Equal(0, refused);
    }

    [Fact]
    public async Task WhenVerificationCapacityIsExhausted_TheAnswerIs503WithTheConfiguredRetryAfter()
    {
        var throttle = Create(o =>
        {
            o.MaxConcurrentVerifications = 1;
            o.VerificationQueueLimit = 0;
            o.BusyRetryAfter = TimeSpan.FromSeconds(7);
        });

        using var running = await throttle.EnterVerificationAsync(ClientA, "alice", CancellationToken.None);
        Assert.Null(running.Rejection);

        using var refused = await throttle.EnterVerificationAsync(ClientB, "bob", CancellationToken.None);
        Assert.Equal(new ThrottleRejection(503, TimeSpan.FromSeconds(7)), refused.Rejection);

        // Nothing was counted for the refused attempt, and its reservation was released.
        Assert.Equal(0, throttle.FailureCount(ThrottleScope.Client, ClientB, null));
        running.Dispose();
        using var next = await throttle.EnterVerificationAsync(ClientB, "bob", CancellationToken.None);
        Assert.Null(next.Rejection);
    }

    [Fact]
    public async Task WaitingAttemptsBeyondTheQueueLimit_Get503()
    {
        var throttle = Create(o => o.VerificationQueueLimit = 1);

        using var running = await throttle.EnterVerificationAsync(ClientA, "alice", CancellationToken.None);
        var waiting = throttle.EnterVerificationAsync(ClientA, "alice", CancellationToken.None).AsTask();
        using var refused = await throttle.EnterVerificationAsync(ClientA, "alice", CancellationToken.None);

        Assert.Equal(503, refused.Rejection?.StatusCode);
        Assert.False(waiting.IsCompleted);

        running.Failed();
        using var released = await waiting;
        Assert.Null(released.Rejection);
    }

    [Fact]
    public async Task ACancelledWaiter_ReleasesItsPlaceInTheQueue()
    {
        var throttle = Create(o => o.VerificationQueueLimit = 1);
        using var running = await throttle.EnterVerificationAsync(ClientA, "alice", CancellationToken.None);

        using (var cancellation = new CancellationTokenSource())
        {
            var waiting = throttle.EnterVerificationAsync(ClientA, "alice", cancellation.Token).AsTask();
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        }

        var second = throttle.EnterVerificationAsync(ClientA, "alice", CancellationToken.None).AsTask();
        Assert.False(second.IsCompleted);
        running.Dispose();
        using var slot = await second;
        Assert.Null(slot.Rejection);
    }

    [Fact]
    public async Task ASlotDisposedWithoutAnOutcome_CountsNothing()
    {
        var throttle = Create();

        for (var i = 0; i < 10; i++)
        {
            using var slot = await throttle.EnterVerificationAsync(ClientA, "alice", CancellationToken.None);
            Assert.Null(slot.Rejection);
        }

        Assert.Equal(0, throttle.FailureCount(ThrottleScope.Pair, ClientA, "alice"));
        Assert.Equal(0, throttle.TrackedEntries(ThrottleScope.Pair));
        Assert.Equal(0, throttle.TrackedEntries(ThrottleScope.Client));
    }

    [Fact]
    public async Task ATrip_IsLoggedOnceWithTheClientAndKindButNoUserNameOrPassword()
    {
        var throttle = Create();
        await FailAsync(throttle, ClientA, "secret-looking-user-name", times: 5);

        for (var i = 0; i < 10; i++)
        {
            Assert.NotNull(throttle.Check(ClientA, "secret-looking-user-name"));
            using var slot = await throttle.EnterVerificationAsync(ClientA, "secret-looking-user-name", CancellationToken.None);
            Assert.NotNull(slot.Rejection);
        }

        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("Pair", entry.Message, StringComparison.Ordinal);
        Assert.Contains("203.0.113.10", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-looking-user-name", entry.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnIPv6Trip_IsLoggedWithItsNetwork()
    {
        var throttle = Create(o => o.ClientFailureLimit = 1);
        await FailAsync(throttle, IPAddress.Parse("2001:db8:1:2:3:4:5:6"), "alice");

        Assert.Contains(_logger.Entries, e => e.Message.Contains("2001:db8:1:2::/64", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0.2, "1")]
    [InlineData(1, "1")]
    [InlineData(59.1, "60")]
    [InlineData(900, "900")]
    public void RetryAfter_IsWholeSecondsRoundedUp(double seconds, string expected)
    {
        Assert.Equal(expected, new ThrottleRejection(429, TimeSpan.FromSeconds(seconds)).RetryAfterHeaderValue);
    }

    private static int MaxEntries(LoginThrottle throttle)
        => Math.Max(
            throttle.TrackedEntries(ThrottleScope.Client),
            Math.Max(throttle.TrackedEntries(ThrottleScope.Pair), throttle.TrackedEntries(ThrottleScope.Account)));

    private static async Task FailAsync(LoginThrottle throttle, IPAddress client, string userName, int times = 1)
    {
        for (var i = 0; i < times; i++)
        {
            using var slot = await throttle.EnterVerificationAsync(client, userName, CancellationToken.None);
            Assert.Null(slot.Rejection);
            slot.Failed();
        }
    }

    private static async Task SucceedAsync(LoginThrottle throttle, IPAddress client, string userName)
    {
        using var slot = await throttle.EnterVerificationAsync(client, userName, CancellationToken.None);
        Assert.Null(slot.Rejection);
        slot.Succeeded();
    }

    private LoginThrottle Create(Action<SecurityThrottlingOptions>? configure = null)
    {
        var options = new ElPaquetoSecurityOptions();
        options.Throttling.MaxConcurrentVerifications = 4;
        configure?.Invoke(options.Throttling);
        _throttle = new LoginThrottle(Options.Create(options), _time, _logger);
        return _throttle;
    }

    private sealed class TestLogger : ILogger<LoginThrottle>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get
            {
                lock (_entries)
                {
                    return [.. _entries];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}
