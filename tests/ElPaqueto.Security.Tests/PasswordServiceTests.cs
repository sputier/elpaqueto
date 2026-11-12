using System.Buffers.Binary;
using System.Diagnostics;
using ElPaqueto.Core;
using ElPaqueto.Security.Configuration;
using ElPaqueto.Security.Data;
using ElPaqueto.Security.Passwords;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace ElPaqueto.Security.Tests;

public class PasswordServiceTests(ITestOutputHelper output)
{
    private const string Password = "correct horse battery staple";

    // Hashes of Password made with ASP.NET Core Identity V3 (PBKDF2, HMAC-SHA512). Committed so that a
    // framework update that changed the format would fail here instead of locking users out.
    private const string FixtureHashDefaultCost =
        "AQAAAAIAAzRQAAAAEBjRmW2pbLRQzCb1Bzo8IQtalV0y9mRjYlpEjXgZQxMASJAtT9Y952yGBINtSzALLg==";

    private const string FixtureHashLowCost =
        "AQAAAAIAAYagAAAAEEnt7ve1OUE+sAooIuKTPWEMO58vV+kIiPCEyPblci/sS1AByepmkAq7+vEu9e/NGw==";

    [Fact]
    public void Hash_UsesTheIdentityV3FormatWithHmacSha512AndTheConfiguredIterations()
    {
        var service = Create(iterations: 150_000, out _);

        var bytes = Convert.FromBase64String(service.Hash(Password));

        Assert.Equal(0x01, bytes[0]);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(1, 4))); // 2 = HMAC-SHA512
        Assert.Equal(150_000u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(5, 4)));
        Assert.Equal(16u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(9, 4))); // 128-bit salt
    }

    [Fact]
    public void FixtureHash_MadeWithTheDefaultCost_Verifies()
    {
        var service = Create(iterations: 210_000, out _);

        Assert.Equal(PasswordVerification.Success, service.Verify(UserWith(FixtureHashDefaultCost), Password));
    }

    [Fact]
    public void FixtureHash_MadeWithALowerCost_VerifiesAndAsksForAReHash()
    {
        var service = Create(iterations: 210_000, out _);

        Assert.Equal(PasswordVerification.SuccessRehashNeeded, service.Verify(UserWith(FixtureHashLowCost), Password));
    }

    [Fact]
    public void Hash_OfTheSamePasswordTwice_DiffersBecauseOfTheSalt()
    {
        var service = Create(iterations: 100_000, out _);

        Assert.NotEqual(service.Hash(Password), service.Hash(Password));
    }

    [Fact]
    public void Verify_AfterHash_Succeeds_AndAWrongPasswordFails()
    {
        var service = Create(iterations: 100_000, out _);
        var user = UserWith(service.Hash(Password));

        Assert.Equal(PasswordVerification.Success, service.Verify(user, Password));
        Assert.Equal(PasswordVerification.Failed, service.Verify(user, Password + "x"));
        Assert.Equal(PasswordVerification.Failed, service.Verify(user, string.Empty));
    }

    [Fact]
    public void Verify_ForAnUnknownUser_PerformsAFullVerification()
    {
        var service = Create(iterations: 100_000, out var hasher);
        hasher.VerifyCalls = 0;

        var result = service.Verify(null, Password);

        Assert.Equal(PasswordVerification.Failed, result);
        Assert.Equal(1, hasher.VerifyCalls);
    }

    [Fact]
    public void Verify_ForADisabledUserOrAMissingHash_PerformsAFullVerificationAndFails()
    {
        var service = Create(iterations: 100_000, out var hasher);
        var disabled = UserWith(service.Hash(Password));
        disabled.IsActive = false;
        var withoutHash = UserWith(string.Empty);

        hasher.VerifyCalls = 0;
        Assert.Equal(PasswordVerification.Failed, service.Verify(disabled, Password));
        Assert.Equal(1, hasher.VerifyCalls);

        hasher.VerifyCalls = 0;
        Assert.Equal(PasswordVerification.Failed, service.Verify(withoutHash, Password));
        Assert.Equal(1, hasher.VerifyCalls);
    }

    [Fact]
    public void Verify_ForAKnownUser_PerformsTheSameNumberOfVerificationsAsForAnUnknownOne()
    {
        var service = Create(iterations: 100_000, out var hasher);
        var user = UserWith(service.Hash(Password));

        hasher.VerifyCalls = 0;
        service.Verify(user, "wrong password, but long enough");
        var knownUser = hasher.VerifyCalls;

        hasher.VerifyCalls = 0;
        service.Verify(null, "wrong password, but long enough");

        Assert.Equal(knownUser, hasher.VerifyCalls);
    }

    [Fact]
    public void Verify_ForAnOverLengthPassword_NeverHashesItInFullButStillDoesTheWork()
    {
        var service = Create(iterations: 100_000, out var hasher);
        var user = UserWith(service.Hash(Password));
        var tooLong = new string('a', SecurityPasswordOptions.MaximumLength + 1);

        hasher.VerifyCalls = 0;
        var result = service.Verify(user, tooLong);

        Assert.Equal(PasswordVerification.Failed, result);
        Assert.Equal(1, hasher.VerifyCalls);
        Assert.All(hasher.VerifiedPasswordLengths, length => Assert.True(length <= SecurityPasswordOptions.MaximumLength));
    }

    [Fact]
    public void Verify_ForAnAbsurdlyLongPassword_IsBoundedBeforeNormalization()
    {
        var service = Create(iterations: 100_000, out var hasher);
        var user = UserWith(service.Hash(Password));
        var huge = new string('a', 5_000_000);

        hasher.VerifyCalls = 0;
        var result = service.Verify(user, huge);

        Assert.Equal(PasswordVerification.Failed, result);
        Assert.All(hasher.VerifiedPasswordLengths, length => Assert.True(length <= SecurityPasswordOptions.MaximumLength));
    }

    [Fact]
    public void ComposedAndDecomposedUnicode_VerifyAgainstTheSameHash()
    {
        var service = Create(iterations: 100_000, out _);
        var composed = "café au lait, s'il vous plaît";
        var decomposed = "café au lait, s'il vous plaît";
        var user = UserWith(service.Hash(composed));

        Assert.NotEqual(composed, decomposed);
        Assert.Equal(PasswordVerification.Success, service.Verify(user, decomposed));
    }

    [Fact]
    public void Validate_RejectsAPasswordThatIsTooShort()
    {
        var service = Create(iterations: 100_000, out _);

        var errors = service.Validate(new string('a', 14));

        Assert.Single(errors);
        Assert.Contains("at least 15", errors[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RejectsAPasswordThatIsTooLong()
    {
        var service = Create(iterations: 100_000, out _);

        var errors = service.Validate(new string('a', SecurityPasswordOptions.MaximumLength + 1));

        Assert.Single(errors);
        Assert.Contains("at most 256", errors[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RejectsAPasswordEqualToTheUserNameIgnoringCase()
    {
        var service = Create(iterations: 100_000, out _);

        var errors = service.Validate("Administrator-Account", "administrator-account");

        Assert.Contains(errors, e => e.Contains("user name", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("correct horse battery staple")]
    [InlineData("a passphrase with spaces")]
    [InlineData("exactly 15 chars")]
    public void Validate_AcceptsPassphrasesWithSpaces(string password)
    {
        var service = Create(iterations: 100_000, out _);

        Assert.Empty(service.Validate(password, "alice"));
    }

    [Fact]
    public void Validate_RejectsAPasswordWithTheApiKeyFormat()
    {
        var service = Create(iterations: 100_000, out _);
        var token = "epq_" + new string('A', 12) + "_" + new string('b', 43);

        var errors = service.Validate(token, "alice");

        Assert.Single(errors);
        Assert.Contains("API key", errors[0], StringComparison.Ordinal);
        Assert.DoesNotContain(token, errors[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RejectsAPasswordThatBecomesAnApiKeyAfterNormalization()
    {
        var service = Create(iterations: 100_000, out _);

        // Fullwidth digits and letters are folded to ASCII by NFKC.
        var fullwidth = "epq_" + new string('Ａ', 12) + "_" + new string('１', 43);

        Assert.Contains(service.Validate(fullwidth), e => e.Contains("API key", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("epq_ is how a passphrase can start")]
    [InlineData("epq_AAAAAAAAAAAA_too-short-for-a-key")]
    public void Validate_AcceptsPasswordsThatOnlyResembleTheApiKeyFormat(string password)
    {
        var service = Create(iterations: 100_000, out _);

        Assert.Empty(service.Validate(password));
    }

    [Fact]
    public void Validate_CountsCharactersAfterNormalization()
    {
        var service = Create(iterations: 100_000, out _);

        // 15 characters once composed, 16 code points if left decomposed.
        var decomposed = "ab́cdefghijklmno";

        Assert.Empty(service.Validate(decomposed));
    }

    [Fact]
    public void ThePassword_NeverAppearsInLogsOrMessages()
    {
        const string sentinel = "s3ntinel-p4ssword-value-1234567890";
        var logs = new List<string>();
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(new CapturingProvider(logs));
        });
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Security:Passwords:IterationCount"] = "100000" })
                .Build());
        new ElPaquetoApplication(services).AddElPaquetoSecurity();
        using var provider = services.BuildServiceProvider();
        var passwords = provider.GetRequiredService<IPasswordService>();

        var hash = passwords.Hash(sentinel);
        var user = UserWith(hash);
        var messages = new List<string>();
        messages.AddRange(passwords.Validate(sentinel, sentinel));
        messages.AddRange(passwords.Validate("short"));
        _ = passwords.Verify(user, sentinel);
        _ = passwords.Verify(user, sentinel + "wrong");
        _ = passwords.Verify(null, sentinel);
        var thrown = Assert.Throws<ArgumentNullException>(() => passwords.Verify(user, null!));
        messages.Add(thrown.Message);

        Assert.DoesNotContain(logs, line => line.Contains(sentinel, StringComparison.Ordinal));
        Assert.DoesNotContain(messages, line => line.Contains(sentinel, StringComparison.Ordinal));
        Assert.DoesNotContain(sentinel, hash, StringComparison.Ordinal);
    }

    [Fact]
    public void VerificationCost_AtTheDefaultIterationCount_IsMeasured()
    {
        var service = Create(iterations: 210_000, out _);
        var user = UserWith(service.Hash(Password));

        _ = service.Verify(user, Password); // warm up
        const int runs = 5;
        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < runs; i++)
        {
            _ = service.Verify(user, Password);
        }

        stopwatch.Stop();
        var perVerification = stopwatch.Elapsed.TotalMilliseconds / runs;
        output.WriteLine($"PBKDF2-HMAC-SHA512, 210000 iterations: {perVerification:F1} ms per verification on {Environment.OSVersion}.");

        // Informational: the figure feeds the tuning of the verification cache and the throttling.
        Assert.InRange(perVerification, 1, 5_000);
    }

    private static PasswordService Create(int iterations, out CountingHasher counting)
    {
        var inner = new PasswordHasher<User>(Options.Create(new PasswordHasherOptions
        {
            CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
            IterationCount = iterations,
        }));
        counting = new CountingHasher(inner);

        var options = new ElPaquetoSecurityOptions
        {
            Passwords = new SecurityPasswordOptions { IterationCount = iterations },
        };

        return new PasswordService(counting, Options.Create(options));
    }

    private static User UserWith(string passwordHash) => new()
    {
        Id = Guid.NewGuid(),
        UserName = "alice",
        NormalizedUserName = "ALICE",
        PasswordHash = passwordHash,
        IsActive = true,
        Permissions = Permission.Read,
    };

    private sealed class CountingHasher(IPasswordHasher<User> inner) : IPasswordHasher<User>
    {
        public int VerifyCalls { get; set; }

        public List<int> VerifiedPasswordLengths { get; } = [];

        public string HashPassword(User user, string password) => inner.HashPassword(user, password);

        public PasswordVerificationResult VerifyHashedPassword(User user, string hashedPassword, string providedPassword)
        {
            VerifyCalls++;
            VerifiedPasswordLengths.Add(providedPassword.Length);
            return inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
        }
    }

    private sealed class CapturingProvider(List<string> logs) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(logs);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(List<string> logs) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (logs)
                {
                    logs.Add(formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
                }
            }
        }
    }
}
