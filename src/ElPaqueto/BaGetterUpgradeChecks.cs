using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace ElPaqueto;

/// <summary>
/// Detects the names that the rename from BaGetter changed and that an existing installation may rely on
/// without having set them explicitly. Nothing here changes a file: it only reports.
/// </summary>
public static class BaGetterUpgradeChecks
{
    /// <summary>The environment variable that names the folder of <c>appsettings.json</c>.</summary>
    public const string ConfigRootVariable = "ELPAQUETO_CONFIG_ROOT";

    /// <summary>
    /// BaGetter's name for <see cref="ConfigRootVariable"/>, still read when the new variable is not set.
    /// </summary>
    /// <remarks>
    /// Deprecated fallback: remove it in ElPaqueto 2.0, together with its warning and its documentation.
    /// </remarks>
    public const string LegacyConfigRootVariable = "BAGET_CONFIG_ROOT";

    /// <summary>The default SQLite file name of BaGetter, which ElPaqueto names <c>elpaqueto.db</c>.</summary>
    public const string LegacyDatabaseFileName = "bagetter.db";

    /// <summary>The user documentation page that explains the upgrade.</summary>
    public const string UpgradeGuide = "docs/user/upgrading-from-bagetter.md";

    /// <summary>
    /// Returns the configuration root folder: <see cref="ConfigRootVariable"/> when it is set, otherwise
    /// <see cref="LegacyConfigRootVariable"/>, otherwise <see langword="null"/>.
    /// </summary>
    /// <param name="getEnvironmentVariable">Reads an environment variable.</param>
    /// <param name="fromLegacyVariable">Whether the folder comes from <see cref="LegacyConfigRootVariable"/>.</param>
    public static string ResolveConfigRoot(Func<string, string> getEnvironmentVariable, out bool fromLegacyVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        fromLegacyVariable = false;

        var root = getEnvironmentVariable(ConfigRootVariable);
        if (!string.IsNullOrEmpty(root))
        {
            return root;
        }

        root = getEnvironmentVariable(LegacyConfigRootVariable);
        if (!string.IsNullOrEmpty(root))
        {
            fromLegacyVariable = true;
            return root;
        }

        return null;
    }

    /// <summary>
    /// Finds a BaGetter database that the configured SQLite database would silently replace.
    /// </summary>
    /// <param name="databaseType">The configured <c>Database:Type</c>.</param>
    /// <param name="connectionString">The configured <c>Database:ConnectionString</c>.</param>
    /// <param name="configuredPath">The full path of the configured database file, when a legacy file is found.</param>
    /// <param name="legacyPath">The full path of the legacy database file, when one is found.</param>
    /// <returns>
    /// <see langword="true"/> when the database is SQLite, its file does not exist yet, and a
    /// <c>bagetter.db</c> file exists in the same folder.
    /// </returns>
    /// <remarks>
    /// A relative file name is resolved against the current directory, as SQLite does. In-memory databases,
    /// URI file names and connection strings that cannot be parsed are ignored. Only file paths are returned,
    /// never the connection string, which may contain a password.
    /// </remarks>
    public static bool TryFindLegacyDatabase(string databaseType, string connectionString, out string configuredPath, out string legacyPath)
    {
        configuredPath = null;
        legacyPath = null;

        if (!string.Equals(databaseType, "Sqlite", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(connectionString))
        {
            return false;
        }

        SqliteConnectionStringBuilder builder;
        try
        {
            builder = new SqliteConnectionStringBuilder(connectionString);
        }
        catch (Exception e) when (e is ArgumentException or FormatException or InvalidOperationException)
        {
            return false;
        }

        var dataSource = builder.DataSource;
        if (string.IsNullOrWhiteSpace(dataSource)
            || builder.Mode == SqliteOpenMode.Memory
            || string.Equals(dataSource, ":memory:", StringComparison.OrdinalIgnoreCase)
            || dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string configured;
        try
        {
            configured = Path.GetFullPath(dataSource);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var directory = Path.GetDirectoryName(configured);
        if (directory is null || File.Exists(configured))
        {
            return false;
        }

        var legacy = Path.Combine(directory, LegacyDatabaseFileName);
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(legacy, configured, comparison) || !File.Exists(legacy))
        {
            return false;
        }

        configuredPath = configured;
        legacyPath = legacy;
        return true;
    }
}
