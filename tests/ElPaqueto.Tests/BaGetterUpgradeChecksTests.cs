using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace ElPaqueto.Tests;

/// <summary>
/// The detection of BaGetter names that an existing installation may rely on (issue #59).
/// </summary>
public sealed class BaGetterUpgradeChecksTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("elpaqueto-upgrade-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // ---- Configuration root ---------------------------------------------------------------------------------

    [Fact]
    public void ConfigRoot_NewVariableAlone_IsUsedWithoutDeprecation()
    {
        var root = BaGetterUpgradeChecks.ResolveConfigRoot(Environment(("ELPAQUETO_CONFIG_ROOT", "/new")), out var legacy);

        Assert.Equal("/new", root);
        Assert.False(legacy);
    }

    [Fact]
    public void ConfigRoot_LegacyVariableAlone_IsUsedAndFlaggedAsDeprecated()
    {
        var root = BaGetterUpgradeChecks.ResolveConfigRoot(Environment(("BAGET_CONFIG_ROOT", "/old")), out var legacy);

        Assert.Equal("/old", root);
        Assert.True(legacy);
    }

    [Fact]
    public void ConfigRoot_BothVariables_TheNewOneWins()
    {
        var root = BaGetterUpgradeChecks.ResolveConfigRoot(
            Environment(("ELPAQUETO_CONFIG_ROOT", "/new"), ("BAGET_CONFIG_ROOT", "/old")),
            out var legacy);

        Assert.Equal("/new", root);
        Assert.False(legacy);
    }

    [Fact]
    public void ConfigRoot_EmptyNewVariable_FallsBackToTheLegacyOne()
    {
        var root = BaGetterUpgradeChecks.ResolveConfigRoot(
            Environment(("ELPAQUETO_CONFIG_ROOT", ""), ("BAGET_CONFIG_ROOT", "/old")),
            out var legacy);

        Assert.Equal("/old", root);
        Assert.True(legacy);
    }

    [Fact]
    public void ConfigRoot_NoVariable_IsNull()
    {
        Assert.Null(BaGetterUpgradeChecks.ResolveConfigRoot(Environment(), out var legacy));
        Assert.False(legacy);
    }

    // ---- Legacy database ------------------------------------------------------------------------------------

    [Fact]
    public void LegacyDatabase_NextToAMissingConfiguredFile_IsFound()
    {
        var legacy = CreateFile("bagetter.db");
        var configured = Path.Combine(_root, "elpaqueto.db");

        Assert.True(BaGetterUpgradeChecks.TryFindLegacyDatabase("Sqlite", $"Data Source={configured}", out var configuredPath, out var legacyPath));
        Assert.Equal(configured, configuredPath);
        Assert.Equal(legacy, legacyPath);
    }

    [Fact]
    public void LegacyDatabase_ConfiguredFileExists_IsIgnored()
    {
        CreateFile("bagetter.db");
        var configured = CreateFile("elpaqueto.db");

        Assert.False(BaGetterUpgradeChecks.TryFindLegacyDatabase("Sqlite", $"Data Source={configured}", out _, out _));
    }

    [Fact]
    public void LegacyDatabase_NoLegacyFile_NothingIsFound()
    {
        Assert.False(BaGetterUpgradeChecks.TryFindLegacyDatabase("Sqlite", $"Data Source={Path.Combine(_root, "elpaqueto.db")}", out _, out _));
    }

    [Fact]
    public void LegacyDatabase_ConfiguredFileIsTheLegacyFile_IsIgnored()
    {
        Assert.False(BaGetterUpgradeChecks.TryFindLegacyDatabase("Sqlite", $"Data Source={Path.Combine(_root, "bagetter.db")}", out _, out _));

        var legacy = CreateFile("bagetter.db");
        Assert.False(BaGetterUpgradeChecks.TryFindLegacyDatabase("Sqlite", $"Data Source={legacy}", out _, out _));
    }

    [Fact]
    public void LegacyDatabase_InAnotherFolder_IsIgnored()
    {
        CreateFile("bagetter.db");
        var configured = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "db")).FullName, "elpaqueto.db");

        Assert.False(BaGetterUpgradeChecks.TryFindLegacyDatabase("Sqlite", $"Data Source={configured}", out _, out _));
    }

    [Theory]
    [InlineData("PostgreSql")]
    [InlineData("SqlServer")]
    [InlineData(null)]
    public void LegacyDatabase_OtherDatabaseTypes_AreIgnored(string type)
    {
        CreateFile("bagetter.db");

        Assert.False(BaGetterUpgradeChecks.TryFindLegacyDatabase(type, $"Data Source={Path.Combine(_root, "elpaqueto.db")}", out _, out _));
    }

    [Theory]
    [InlineData("Data Source=:memory:")]
    [InlineData("Data Source=shared;Mode=Memory")]
    [InlineData("Data Source=file:elpaqueto.db?mode=ro")]
    [InlineData("not a connection string")]
    [InlineData("")]
    [InlineData(null)]
    public void LegacyDatabase_NoFileDataSource_IsIgnored(string connectionString)
    {
        Assert.False(BaGetterUpgradeChecks.TryFindLegacyDatabase("Sqlite", connectionString, out var configuredPath, out var legacyPath));
        Assert.Null(configuredPath);
        Assert.Null(legacyPath);
    }

    [Fact]
    public void LegacyDatabase_ReturnsOnlyPaths_NeverTheConnectionString()
    {
        var legacy = CreateFile("bagetter.db");
        var configured = Path.Combine(_root, "elpaqueto.db");
        const string secret = "connection-string-secret-marker";

        Assert.True(BaGetterUpgradeChecks.TryFindLegacyDatabase(
            "sqlite",
            $"Data Source={configured};Password={secret};Cache=Shared",
            out var configuredPath,
            out var legacyPath));

        Assert.Equal(configured, configuredPath);
        Assert.Equal(legacy, legacyPath);
        Assert.DoesNotContain(secret, configuredPath, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, legacyPath, StringComparison.Ordinal);
    }

    private string CreateFile(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, []);
        return path;
    }

    private static Func<string, string> Environment(params (string Name, string Value)[] variables)
    {
        var values = new Dictionary<string, string>();
        foreach (var (name, value) in variables)
        {
            values[name] = value;
        }

        return name => values.TryGetValue(name, out var value) ? value : null;
    }
}
