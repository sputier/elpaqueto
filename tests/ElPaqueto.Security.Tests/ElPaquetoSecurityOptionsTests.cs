using ElPaqueto.Core;
using ElPaqueto.Security.Configuration;

namespace ElPaqueto.Security.Tests;

public class ElPaquetoSecurityOptionsTests
{
    private static readonly ValidateElPaquetoOptions<ElPaquetoSecurityOptions> Validator = new("Security");

    [Fact]
    public void Defaults_UseALocalAuthDatabase()
    {
        var options = new ElPaquetoSecurityOptions();

        Assert.Equal("Data Source=auth.db", options.Database.ConnectionString);
    }

    [Fact]
    public void Defaults_AreValid()
    {
        var result = Validator.Validate(name: null, new ElPaquetoSecurityOptions());

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void MissingConnectionString_IsRejectedAndNamesTheSetting(string? connectionString)
    {
        var options = new ElPaquetoSecurityOptions
        {
            Database = new SecurityDatabaseOptions { ConnectionString = connectionString! },
        };

        var result = Validator.Validate(name: null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("Security:Database:ConnectionString", StringComparison.Ordinal));
    }

    [Fact]
    public void TooLongConnectionString_IsRejectedWithoutEchoingItsValue()
    {
        const string secretMarker = "s3cr3t-marker";
        var tooLong = secretMarker + new string('x', SecurityDatabaseOptions.MaxConnectionStringLength);
        var options = new ElPaquetoSecurityOptions
        {
            Database = new SecurityDatabaseOptions { ConnectionString = tooLong },
        };

        var result = Validator.Validate(name: null, options);

        Assert.True(result.Failed);
        Assert.All(result.Failures!, failure => Assert.DoesNotContain(secretMarker, failure, StringComparison.Ordinal));
        Assert.Contains(result.Failures!, f => f.Contains("Security:Database:ConnectionString", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingDatabaseSection_IsRejected()
    {
        var options = new ElPaquetoSecurityOptions { Database = null! };

        var result = Validator.Validate(name: null, options);

        Assert.True(result.Failed);
    }

    [Theory]
    [InlineData("foo")]
    [InlineData("Data Source=")]
    [InlineData("Mode=ReadWriteCreate")]
    [InlineData("NotAKeyword=1;Data Source=x.db")]
    [InlineData("Data Source=x.db;Default Timeout=s3cr3tvalue")]
    [InlineData("Data Source=x.db;Pooling=s3cr3tbool")]
    [InlineData("Data Source=x.db;Default Timeout=99999999999")]
    public void MalformedConnectionString_IsRejectedWithAFixedMessage(string connectionString)
    {
        var options = new ElPaquetoSecurityOptions
        {
            Database = new SecurityDatabaseOptions { ConnectionString = connectionString },
        };

        var result = Validator.Validate(name: null, options);

        Assert.True(result.Failed);
        Assert.All(result.Failures!, failure => Assert.DoesNotContain(connectionString, failure, StringComparison.Ordinal));
        Assert.Contains(result.Failures!, f => f.Contains("Security:Database:ConnectionString", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Data Source=/data/db/auth.db")]
    [InlineData("Data Source=auth.db;Cache=Shared")]
    public void WellFormedConnectionString_IsAccepted(string connectionString)
    {
        var options = new ElPaquetoSecurityOptions
        {
            Database = new SecurityDatabaseOptions { ConnectionString = connectionString },
        };

        Assert.True(Validator.Validate(name: null, options).Succeeded);
    }

    [Theory]
    [InlineData("Data Source=:memory:")]
    [InlineData("Mode=Memory;Data Source=auth.db")]
    [InlineData("Data Source=file:/data/auth.db")]
    [InlineData("Data Source=FILE:auth.db?mode=rwc")]
    public void InMemoryDatabasesAndFileUris_AreRejected(string connectionString)
    {
        var options = new ElPaquetoSecurityOptions
        {
            Database = new SecurityDatabaseOptions { ConnectionString = connectionString },
        };

        var result = Validator.Validate(name: null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("Security:Database:ConnectionString", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(99_999)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10_000_001)]
    public void IterationCountOutsideTheAcceptedRange_IsRejected(int iterations)
    {
        var options = new ElPaquetoSecurityOptions
        {
            Passwords = new SecurityPasswordOptions { IterationCount = iterations },
        };

        var result = Validator.Validate(name: null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("Security:Passwords:IterationCount", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(11)]
    [InlineData(257)]
    public void MinimumLengthOutsideTheAcceptedRange_IsRejected(int minimumLength)
    {
        var options = new ElPaquetoSecurityOptions
        {
            Passwords = new SecurityPasswordOptions { MinimumLength = minimumLength },
        };

        var result = Validator.Validate(name: null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("Security:Passwords:MinimumLength", StringComparison.Ordinal));
    }

    [Fact]
    public void PasswordDefaults_FollowTheDocumentedValues()
    {
        var options = new ElPaquetoSecurityOptions();

        Assert.Equal(210_000, options.Passwords.IterationCount);
        Assert.Equal(15, options.Passwords.MinimumLength);
        Assert.True(Validator.Validate(name: null, options).Succeeded);
    }
    [Fact]
    public void ApiKeyDefaults_FollowTheDocumentedValues()
    {
        var options = new ElPaquetoSecurityOptions();

        Assert.Equal(TimeSpan.FromMinutes(5), options.ApiKeys.LastUsedUpdateInterval);
        Assert.True(Validator.Validate(name: null, options).Succeeded);
    }

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("00:00:00.5")]
    [InlineData("-00:01:00")]
    [InlineData("1.00:00:01")]
    public void LastUsedUpdateIntervalOutsideTheAcceptedRange_IsRejected(string interval)
    {
        var options = new ElPaquetoSecurityOptions
        {
            ApiKeys = new SecurityApiKeyOptions { LastUsedUpdateInterval = TimeSpan.Parse(interval, System.Globalization.CultureInfo.InvariantCulture) },
        };

        var result = Validator.Validate(name: null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("Security:ApiKeys:LastUsedUpdateInterval", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("00:00:01")]
    [InlineData("01:00:00")]
    [InlineData("1.00:00:00")]
    public void LastUsedUpdateIntervalWithinTheAcceptedRange_IsAccepted(string interval)
    {
        var options = new ElPaquetoSecurityOptions
        {
            ApiKeys = new SecurityApiKeyOptions { LastUsedUpdateInterval = TimeSpan.Parse(interval, System.Globalization.CultureInfo.InvariantCulture) },
        };

        Assert.True(Validator.Validate(name: null, options).Succeeded);
    }

    [Fact]
    public void ThrottlingDefaults_FollowTheDocumentedValues()
    {
        var options = new ElPaquetoSecurityOptions();
        var throttling = options.Throttling;

        Assert.Equal(30, throttling.ClientFailureLimit);
        Assert.Equal(TimeSpan.FromMinutes(10), throttling.ClientWindow);
        Assert.Equal(300, throttling.NetworkFailureLimit);
        Assert.Equal(5, throttling.PairFailureLimit);
        Assert.Equal(TimeSpan.FromMinutes(15), throttling.PairWindow);
        Assert.Equal(50, throttling.AccountFailureLimit);
        Assert.Equal(TimeSpan.FromHours(1), throttling.AccountWindow);
        Assert.Equal(TimeSpan.FromMinutes(1), throttling.InitialThrottleDuration);
        Assert.Equal(TimeSpan.FromMinutes(15), throttling.MaximumThrottleDuration);
        Assert.Equal(50_000, throttling.MaxTrackedEntries);
        Assert.Equal(0, throttling.MaxConcurrentVerifications);
        Assert.Equal(Environment.ProcessorCount, throttling.EffectiveMaxConcurrentVerifications);
        Assert.Equal(100, throttling.VerificationQueueLimit);
        Assert.Equal(TimeSpan.FromSeconds(5), throttling.BusyRetryAfter);
        Assert.True(Validator.Validate(name: null, options).Succeeded);
    }

    [Theory]
    [InlineData(nameof(SecurityThrottlingOptions.ClientFailureLimit), 0)]
    [InlineData(nameof(SecurityThrottlingOptions.ClientFailureLimit), 1001)]
    [InlineData(nameof(SecurityThrottlingOptions.NetworkFailureLimit), 0)]
    [InlineData(nameof(SecurityThrottlingOptions.NetworkFailureLimit), 10_001)]
    [InlineData(nameof(SecurityThrottlingOptions.PairFailureLimit), 0)]
    [InlineData(nameof(SecurityThrottlingOptions.AccountFailureLimit), 0)]
    [InlineData(nameof(SecurityThrottlingOptions.MaxTrackedEntries), 99)]
    [InlineData(nameof(SecurityThrottlingOptions.MaxConcurrentVerifications), -1)]
    [InlineData(nameof(SecurityThrottlingOptions.MaxConcurrentVerifications), 1025)]
    [InlineData(nameof(SecurityThrottlingOptions.VerificationQueueLimit), -1)]
    public void ThrottlingNumberOutsideTheAcceptedRange_IsRejectedAndNamesTheSetting(string setting, int value)
    {
        var options = new ElPaquetoSecurityOptions();
        typeof(SecurityThrottlingOptions).GetProperty(setting)!.SetValue(options.Throttling, value);

        var result = Validator.Validate(name: null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("Security:Throttling:" + setting, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(nameof(SecurityThrottlingOptions.ClientWindow), "00:00:00")]
    [InlineData(nameof(SecurityThrottlingOptions.PairWindow), "1.00:00:01")]
    [InlineData(nameof(SecurityThrottlingOptions.AccountWindow), "-00:01:00")]
    [InlineData(nameof(SecurityThrottlingOptions.InitialThrottleDuration), "00:00:00")]
    [InlineData(nameof(SecurityThrottlingOptions.MaximumThrottleDuration), "2.00:00:00")]
    [InlineData(nameof(SecurityThrottlingOptions.BusyRetryAfter), "00:10:01")]
    public void ThrottlingDurationOutsideTheAcceptedRange_IsRejectedAndNamesTheSetting(string setting, string value)
    {
        var options = new ElPaquetoSecurityOptions();
        typeof(SecurityThrottlingOptions).GetProperty(setting)!.SetValue(
            options.Throttling,
            TimeSpan.Parse(value, System.Globalization.CultureInfo.InvariantCulture));

        var result = Validator.Validate(name: null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("Security:Throttling:" + setting, StringComparison.Ordinal));
    }

    [Fact]
    public void AnInitialThrottleDurationAboveTheMaximum_IsRejected()
    {
        var options = new ElPaquetoSecurityOptions();
        options.Throttling.InitialThrottleDuration = TimeSpan.FromMinutes(20);

        var result = Validator.Validate(name: null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("Security:Throttling:InitialThrottleDuration", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingThrottlingSection_IsRejected()
    {
        var options = new ElPaquetoSecurityOptions { Throttling = null! };

        Assert.True(Validator.Validate(name: null, options).Failed);
    }
}
