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
        Assert.Equal(0, throttle.OutstandingReservations());
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
        Assert.Equal(0, throttle.OutstandingReservations());
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
        Assert.Equal(0, throttle.OutstandingReservations());
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
        Assert.Equal(0, throttle.OutstandingReservations());
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
        using (var released = await waiting.WaitAsync(Timeout))
        {
            Assert.Null(released.Rejection);
        }

        Assert.Equal(0, throttle.OutstandingReservations());
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
        using (var slot = await second.WaitAsync(Timeout))
        {
            Assert.Null(slot.Rejection);
        }

        Assert.Equal(0, throttle.OutstandingReservations());
    }

    [Fact]
    public async Task AnAttemptCancelledWhileQueuedForCapacity_ReleasesItsReservation()
    {
        var throttle = Create(o =>
        {
            o.MaxConcurrentVerifications = 1;
            o.VerificationQueueLimit = 1;
        });
        using var running = await throttle.EnterVerificationAsync(ClientA, "alice", CancellationToken.None);

        using (var cancellation = new CancellationTokenSource())
        {
            var queued = throttle.EnterVerificationAsync(ClientB, "bob", cancellation.Token).AsTask();
            Assert.False(queued.IsCompleted);
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(Timeout));
        }

        running.Dispose();
        Assert.Equal(0, throttle.OutstandingReservations());
        Assert.Equal(0, throttle.FailureCount(ThrottleScope.Client, ClientB, null));
    }

    [Fact]
    public async Task ManyIPv6Slash64InOneSlash48_ShareTheNetworkBudget()
    {
        var throttle = Create();

        // 100 /64 networks of one /48, three failures each: no /64 reaches its own limit.
        for (var i = 0; i < 100; i++)
        {
            for (var j = 0; j < 3; j++)
            {
                await FailAsync(throttle, IPAddress.Parse($"2001:db8:aa:{i:x}::1"), $"user{j}");
            }
        }

        Assert.Equal(429, throttle.Check(IPAddress.Parse("2001:db8:aa:ffff::1"), "anyone")?.StatusCode);
        Assert.Null(throttle.Check(IPAddress.Parse("2001:db8:ab::1"), "anyone"));
        Assert.Null(throttle.Check(ClientA, "anyone"));
        Assert.Contains(_logger.Entries, e => e.Message.Contains("Network", StringComparison.Ordinal)
            && e.Message.Contains("2001:db8:aa::/48", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AWallClockChange_NeitherExtendsNorEndsAThrottle()
    {
        var time = new SteppingClock();
        var options = new ElPaquetoSecurityOptions();
        using var throttle = new LoginThrottle(Options.Create(options), time, _logger);
        await FailAsync(throttle, ClientA, "alice", times: 5);

        time.WallClockOffset = TimeSpan.FromDays(-1);
        Assert.Equal(TimeSpan.FromMinutes(1), throttle.Check(ClientA, "alice")?.RetryAfter);

        time.WallClockOffset = TimeSpan.FromDays(1);
        Assert.Equal(TimeSpan.FromMinutes(1), throttle.Check(ClientA, "alice")?.RetryAfter);

        time.Elapsed = TimeSpan.FromMinutes(1);
        Assert.Null(throttle.Check(ClientA, "alice"));
    }

    [Fact]
    public async Task AFloodOfNewCounters_EvictsNeitherATrippedCounterNorOneInFlight()
    {
        var throttle = Create(o => o.MaxTrackedEntries = 100);
        await FailAsync(throttle, ClientA, "alice", times: 5);
        using var inFlight = await throttle.EnterVerificationAsync(ClientB, "bob", CancellationToken.None);
        Assert.Null(inFlight.Rejection);

        for (var i = 0; i < 5_000; i++)
        {
            await FailAsync(throttle, new IPAddress(BitConverter.GetBytes(0x0A000000 + i)), $"flood-{i}");
        }

        Assert.Equal(429, throttle.Check(ClientA, "alice")?.StatusCode);
        Assert.InRange(throttle.TrackedEntries(ThrottleScope.Pair), 1, 100);
        inFlight.Failed();
        Assert.Equal(1, throttle.FailureCount(ThrottleScope.Pair, ClientB, "bob"));
        Assert.Equal(0, throttle.OutstandingReservations());
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

    [Fact]
    public async Task AnIdenticalFailedAttempt_IsCountedOnceOnEveryCounter()
    {
        var throttle = Create();
        var stale = Attempt("stale password");

        // What one `dotnet restore` with a stale password sends: six identical credentialed attempts, and more.
        var counted = new List<bool>();
        for (var i = 0; i < 12; i++)
        {
            counted.Add(await FailAsync(throttle, ClientA, "alice", stale));
        }

        Assert.Equal([true, .. Enumerable.Repeat(false, 11)], counted);
        Assert.Null(throttle.Check(ClientA, "alice"));
        Assert.Equal(1, throttle.FailureCount(ThrottleScope.Pair, ClientA, "alice"));
        Assert.Equal(1, throttle.FailureCount(ThrottleScope.Client, ClientA, null));
        Assert.Equal(1, throttle.FailureCount(ThrottleScope.Account, null, "alice"));

        var ipv6 = IPAddress.Parse("2001:db8:5:6::1");
        await FailAsync(throttle, ipv6, "alice", stale);
        await FailAsync(throttle, ipv6, "alice", stale);
        Assert.Equal(1, throttle.FailureCount(ThrottleScope.Network, ipv6, null));
        Assert.Equal(0, throttle.OutstandingReservations());
    }

    [Fact]
    public async Task ARepeatedAttempt_IsFlaggedOnTheSlot_OnlyAfterTheFirstFailureIsCounted()
    {
        var throttle = Create();

        using (var first = await throttle.EnterVerificationAsync(ClientA, "alice", Attempt("wrong"), CancellationToken.None))
        {
            Assert.False(first.RepeatsACountedFailure);
            Assert.True(first.Failed());
        }

        using (var second = await throttle.EnterVerificationAsync(ClientA, "alice", Attempt("wrong"), CancellationToken.None))
        {
            Assert.True(second.RepeatsACountedFailure);
            Assert.False(second.Failed());
        }

        // A success, or an aborted attempt, records nothing.
        using (var success = await throttle.EnterVerificationAsync(ClientA, "alice", Attempt("right"), CancellationToken.None))
        {
            success.Succeeded();
        }

        using (var aborted = await throttle.EnterVerificationAsync(ClientA, "alice", Attempt("aborted"), CancellationToken.None))
        {
            Assert.False(aborted.RepeatsACountedFailure);
        }

        using var again = await throttle.EnterVerificationAsync(ClientA, "alice", Attempt("right"), CancellationToken.None);
        Assert.False(again.RepeatsACountedFailure);
        using var abortedAgain = await throttle.EnterVerificationAsync(ClientA, "bob", Attempt("aborted"), CancellationToken.None);
        Assert.False(abortedAgain.RepeatsACountedFailure);
        Assert.Equal(1, throttle.RememberedFailedAttempts());
    }

    [Fact]
    public async Task DistinctWrongPasswords_StillTripThePairAtItsLimit_WhateverIsRepeatedInBetween()
    {
        var throttle = Create();

        for (var i = 0; i < 4; i++)
        {
            Assert.True(await FailAsync(throttle, ClientA, "alice", Attempt($"guess {i}")));

            // Repeating earlier guesses gives nothing and costs nothing.
            for (var j = 0; j <= i; j++)
            {
                Assert.False(await FailAsync(throttle, ClientA, "alice", Attempt($"guess {j}")));
            }
        }

        Assert.Null(throttle.Check(ClientA, "alice"));
        Assert.True(await FailAsync(throttle, ClientA, "alice", Attempt("guess 4")));
        Assert.Equal(429, throttle.Check(ClientA, "alice")?.StatusCode);
    }

    [Fact]
    public async Task TheSamePasswordForDifferentUserNames_CountsForEachOfThem()
    {
        var throttle = Create();

        // Password spraying: one candidate against many user names, known or not.
        for (var i = 0; i < 30; i++)
        {
            Assert.True(await FailAsync(throttle, ClientA, $"user{i}", Attempt("Winter2030!")));
        }

        Assert.Equal(429, throttle.Check(ClientA, "someone-else")?.StatusCode);
    }

    [Fact]
    public async Task TheSameAttemptFromAnotherClient_IsCountedAgain()
    {
        var throttle = Create();

        Assert.True(await FailAsync(throttle, ClientA, "alice", Attempt("wrong")));
        Assert.True(await FailAsync(throttle, ClientB, "alice", Attempt("wrong")));
        Assert.True(await FailAsync(throttle, IPAddress.Parse("2001:db8:1:1::1"), "alice", Attempt("wrong")));
        Assert.True(await FailAsync(throttle, IPAddress.Parse("2001:db8:1:2::1"), "alice", Attempt("wrong")));

        // The same /64 is the same client.
        Assert.False(await FailAsync(throttle, IPAddress.Parse("2001:db8:1:2::ffff"), "alice", Attempt("wrong")));
        Assert.Equal(4, throttle.FailureCount(ThrottleScope.Account, null, "alice"));
    }

    [Fact]
    public async Task UserNamesDifferingOnlyByCase_AreTheSameAttempt()
    {
        var throttle = Create();

        Assert.True(await FailAsync(throttle, ClientA, "alice", Attempt("wrong")));
        Assert.False(await FailAsync(throttle, ClientA, "ALICE", Attempt("wrong")));

        // The password is compared exactly.
        Assert.True(await FailAsync(throttle, ClientA, "alice", Attempt("WRONG")));
    }

    [Fact]
    public async Task AChangedCredentialState_MakesTheSamePasswordANewAttempt()
    {
        var throttle = Create();

        Assert.True(await FailAsync(throttle, ClientA, "alice", Attempt("the password", state: "hash before reset")));
        Assert.False(await FailAsync(throttle, ClientA, "alice", Attempt("the password", state: "hash before reset")));

        // An administrator reset the password, or enabled the user: the same password must be verified again.
        using var slot = await throttle.EnterVerificationAsync(ClientA, "alice", Attempt("the password", state: "hash after reset"), CancellationToken.None);
        Assert.False(slot.RepeatsACountedFailure);
    }

    [Fact]
    public async Task IdenticalApiKeyAttempts_AreCountedOnce_DistinctKeysEach()
    {
        var throttle = Create();

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(i == 0, await FailAsync(throttle, ClientA, null, Attempt("epq_revoked-key", state: "")));
        }

        for (var i = 0; i < 29; i++)
        {
            Assert.True(await FailAsync(throttle, ClientA, null, Attempt($"epq_guess-{i}", state: "")));
        }

        Assert.Equal(429, throttle.Check(ClientA, null)?.StatusCode);
    }

    [Fact]
    public async Task AKeyAndAPasswordWithTheSameValue_AreDifferentAttempts()
    {
        var throttle = Create();

        Assert.True(await FailAsync(throttle, ClientA, null, Attempt("same value", state: "")));
        Assert.True(await FailAsync(throttle, ClientA, "alice", Attempt("same value", state: "")));
        Assert.Equal(2, throttle.FailureCount(ThrottleScope.Client, ClientA, null));
    }

    [Fact]
    public async Task ParallelIdenticalFailures_AreCountedExactlyOnce()
    {
        var throttle = Create(o =>
        {
            o.MaxConcurrentVerifications = 64;
            o.VerificationQueueLimit = 1000;
        });
        var counted = 0;
        var flagged = 0;

        // Key attempts are not serialized per pair: several identical ones run at once and race to report.
        using var start = new ManualResetEventSlim();
        var attempts = Enumerable.Range(0, 200).Select(i => Task.Run(async () =>
        {
            start.Wait();
            var userName = i % 2 == 0 ? null : "alice";
            using var slot = await throttle.EnterVerificationAsync(ClientA, userName, Attempt("same wrong secret"), CancellationToken.None);
            Assert.Null(slot.Rejection);
            if (slot.RepeatsACountedFailure)
            {
                Interlocked.Increment(ref flagged);
            }

            await Task.Yield();
            if (slot.Failed())
            {
                Interlocked.Increment(ref counted);
            }
        })).ToArray();
        start.Set();
        await Task.WhenAll(attempts).WaitAsync(Timeout);

        // One for the key attempts, one for the password attempts.
        Assert.Equal(2, counted);
        Assert.Equal(2, throttle.FailureCount(ThrottleScope.Client, ClientA, null));
        Assert.Equal(1, throttle.FailureCount(ThrottleScope.Pair, ClientA, "alice"));

        // The password attempts run one at a time, so every one after the first knows it repeats.
        Assert.InRange(flagged, 99, 198);
        Assert.Equal(0, throttle.OutstandingReservations());
    }

    [Fact]
    public async Task AnIdenticalFailure_CountsAgainOnlyAfterTheLongestWindow()
    {
        var throttle = Create();
        Assert.True(await FailAsync(throttle, ClientA, "alice", Attempt("wrong")));

        // The account window (1 hour) is the longest: until it ends, the first failure may still count somewhere.
        _time.Advance(TimeSpan.FromMinutes(59));
        Assert.False(await FailAsync(throttle, ClientA, "alice", Attempt("wrong")));

        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(await FailAsync(throttle, ClientA, "alice", Attempt("wrong")));
        Assert.False(await FailAsync(throttle, ClientA, "alice", Attempt("wrong")));
    }

    [Fact]
    public async Task WhenTheFingerprintStoreIsFull_NewFailuresAreCountedEveryTime()
    {
        const int bound = 100;
        var throttle = Create(o => o.MaxTrackedEntries = bound);

        // Fill the store from other clients, one failure each, so that no counter trips.
        for (var i = 0; i < bound; i++)
        {
            await FailAsync(throttle, new IPAddress(BitConverter.GetBytes(0x0A000000 + i)), $"filler-{i}", Attempt($"filler {i}"));
        }

        Assert.Equal(bound, throttle.RememberedFailedAttempts());

        // Fail closed: a failure that cannot be remembered is counted, and so is each of its repetitions.
        for (var i = 0; i < 5; i++)
        {
            using var slot = await throttle.EnterVerificationAsync(ClientA, "alice", Attempt("stale"), CancellationToken.None);
            Assert.Null(slot.Rejection);
            Assert.False(slot.RepeatsACountedFailure);
            Assert.True(slot.Failed());
        }

        Assert.Equal(429, throttle.Check(ClientA, "alice")?.StatusCode);

        // Remembered failures still count once while the store is full.
        Assert.False(await FailAsync(throttle, new IPAddress(BitConverter.GetBytes(0x0A000000)), "filler-0", Attempt("filler 0")));
        Assert.Equal(bound, throttle.RememberedFailedAttempts());

        // Once the fingerprints expire, there is room again.
        _time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, throttle.RememberedFailedAttempts());
        Assert.True(await FailAsync(throttle, ClientB, "bob", Attempt("stale")));
        Assert.False(await FailAsync(throttle, ClientB, "bob", Attempt("stale")));
    }

    [Fact]
    public async Task AFloodOfDistinctAttempts_KeepsTheFingerprintStoreWithinItsBound()
    {
        const int bound = 1_000;
        var throttle = Create(o => o.MaxTrackedEntries = bound);

        for (var i = 0; i < 100_000; i++)
        {
            var address = new IPAddress(BitConverter.GetBytes(0x0A000000 + (i % 5_000)));
            await FailAsync(throttle, address, $"random-{i}", Attempt($"password-{i}"));
            if (i % 1_000 == 0)
            {
                Assert.InRange(throttle.RememberedFailedAttempts(), 1, bound);
            }
        }

        Assert.Equal(bound, throttle.RememberedFailedAttempts());
    }

    [Fact]
    public async Task AFingerprint_IsNeitherTheSecretNorStableAcrossProcesses()
    {
        // Two throttles stand for two processes: the same attempt is a new one for the second.
        var first = Create();
        Assert.True(await FailAsync(first, ClientA, "alice", Attempt("wrong")));
        using var second = new LoginThrottle(Options.Create(new ElPaquetoSecurityOptions()), _time, _logger);
        Assert.True(await FailAsync(second, ClientA, "alice", Attempt("wrong")));

        // Nothing the throttle logs carries the secret.
        await FailAsync(first, ClientA, "alice", Attempt("marker-secret-1"));
        await FailAsync(first, ClientA, "alice", Attempt("marker-secret-2"));
        await FailAsync(first, ClientA, "alice", Attempt("marker-secret-3"));
        await FailAsync(first, ClientA, "alice", Attempt("marker-secret-4"));
        Assert.NotEmpty(_logger.Entries);
        Assert.DoesNotContain(_logger.Entries, e => e.Message.Contains("marker-secret", StringComparison.Ordinal));
        Assert.Equal(nameof(CredentialAttempt), Attempt("marker-secret-5").ToString()?.Split('.')[^1]);
    }

    [Fact]
    public async Task TwoIdenticalKeyAttemptsInFlightAtOnce_CountExactlyOnce()
    {
        var throttle = Create();

        // Key attempts are not serialized: both slots are granted before either reports.
        using var first = await throttle.EnterVerificationAsync(ClientA, null, Attempt("epq_same", state: ""), CancellationToken.None);
        using var second = await throttle.EnterVerificationAsync(ClientA, null, Attempt("epq_same", state: ""), CancellationToken.None);
        Assert.Null(first.Rejection);
        Assert.Null(second.Rejection);
        Assert.False(first.RepeatsACountedFailure);
        Assert.False(second.RepeatsACountedFailure);

        Assert.True(second.Failed());
        Assert.False(first.Failed());
        Assert.Equal(1, throttle.FailureCount(ThrottleScope.Client, ClientA, null));
    }

    [Fact]
    public async Task RepetitionsBeyondTheRepeatLimit_ThrottleTheClient()
    {
        var throttle = Create(o => o.ClientFailureLimit = 3);
        var limit = LoginThrottle.RepeatLimit(new SecurityThrottlingOptions { ClientFailureLimit = 3 });
        Assert.Equal(30, limit);

        Assert.True(await FailAsync(throttle, ClientA, "alice", Attempt("stale")));
        for (var i = 0; i < limit - 1; i++)
        {
            Assert.False(await FailAsync(throttle, ClientA, "alice", Attempt("stale")));
        }

        Assert.Null(throttle.Check(ClientA, "alice"));
        Assert.Equal(limit - 1, throttle.FailureCount(ThrottleScope.Repeat, ClientA, null));
        Assert.Equal(1, throttle.FailureCount(ThrottleScope.Client, ClientA, null));

        Assert.False(await FailAsync(throttle, ClientA, "alice", Attempt("stale")));

        // The whole client is throttled, logged once as a Repeat trip; another client is not.
        Assert.Equal(429, throttle.Check(ClientA, "anyone")?.StatusCode);
        Assert.Equal(429, throttle.Check(ClientA, null)?.StatusCode);
        Assert.Null(throttle.Check(ClientB, "alice"));
        Assert.Contains(_logger.Entries, e => e.Message.Contains("Repeat", StringComparison.Ordinal));
    }

    [Fact]
    public void VerificationTiming_IsAMovingAverage_SeededByTheFirstSample_AndClamped()
    {
        var timing = new ElPaqueto.Security.Authentication.VerificationTiming();
        Assert.Equal(TimeSpan.Zero, timing.Typical);

        timing.Record(TimeSpan.FromMilliseconds(800));
        Assert.Equal(TimeSpan.FromMilliseconds(800), timing.Typical);

        // Weight 1/8 for a new sample.
        timing.Record(TimeSpan.FromMilliseconds(0));
        Assert.Equal(TimeSpan.FromMilliseconds(700), timing.Typical);

        // A pause of a minute counts as two seconds at most.
        var clamped = new ElPaqueto.Security.Authentication.VerificationTiming();
        clamped.Record(TimeSpan.FromMinutes(1));
        Assert.Equal(TimeSpan.FromSeconds(2), clamped.Typical);
        clamped.Record(TimeSpan.FromSeconds(-5));
        Assert.Equal(TimeSpan.FromSeconds(1.75), clamped.Typical);
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

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

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

    // Returns whether the failure was counted.
    private static async Task<bool> FailAsync(LoginThrottle throttle, IPAddress client, string? userName, CredentialAttempt attempt)
    {
        using var slot = await throttle.EnterVerificationAsync(client, userName, attempt, CancellationToken.None);
        Assert.Null(slot.Rejection);
        return slot.Failed();
    }

    private static CredentialAttempt Attempt(string secret, string state = "stored hash")
        => new(secret, state);

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

    /// <summary>A clock whose wall time can jump independently of its monotonic timestamp.</summary>
    private sealed class SteppingClock : TimeProvider
    {
        private static readonly DateTimeOffset Start = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public TimeSpan Elapsed { get; set; }

        public TimeSpan WallClockOffset { get; set; }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => Start + Elapsed + WallClockOffset;

        public override long GetTimestamp() => TimeSpan.FromHours(1).Ticks + Elapsed.Ticks;
    }

    private sealed class TestLogger : ILogger<Audit>
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

/// <summary>Attempts without a credential: every failure counts, as before repetitions were recognised.</summary>
internal static class LoginThrottleTestExtensions
{
    public static ValueTask<VerificationSlot> EnterVerificationAsync(
        this LoginThrottle throttle, IPAddress? client, string? userName, CancellationToken cancellationToken)
        => throttle.EnterVerificationAsync(client, userName, attempt: null, cancellationToken);
}
