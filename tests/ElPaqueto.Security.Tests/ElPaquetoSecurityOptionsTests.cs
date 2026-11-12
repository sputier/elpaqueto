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
}
