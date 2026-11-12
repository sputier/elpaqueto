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
}
