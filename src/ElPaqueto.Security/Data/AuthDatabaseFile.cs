using Microsoft.Data.Sqlite;

namespace ElPaqueto.Security.Data;

/// <summary>
/// Prepares the SQLite file of the security database before it is opened.
/// </summary>
internal static class AuthDatabaseFile
{
    private const UnixFileMode DirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode DatabaseFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>
    /// Creates the parent directory and the file when they are missing. On Unix the directory is
    /// created with mode 0700 and the file with mode 0600: credentials must not be world-readable.
    /// Nothing that already exists is changed.
    /// </summary>
    /// <param name="connectionString">The SQLite connection string.</param>
    public static void Prepare(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);

        if (IsNotAFile(builder))
        {
            return;
        }

        var path = Path.GetFullPath(builder.DataSource);

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                if (OperatingSystem.IsWindows())
                {
                    Directory.CreateDirectory(directory);
                }
                else
                {
                    Directory.CreateDirectory(directory, DirectoryMode);
                }
            }

            if (!File.Exists(path) && builder.Mode != SqliteOpenMode.ReadOnly)
            {
                var options = new FileStreamOptions
                {
                    Mode = System.IO.FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.ReadWrite,
                };

                if (!OperatingSystem.IsWindows())
                {
                    options.UnixCreateMode = DatabaseFileMode;
                }

                try
                {
                    using var created = new FileStream(path, options);
                }
                catch (IOException) when (File.Exists(path))
                {
                    // Another process or thread created the file between the check and the creation:
                    // the server and a command-line process may start together. That is the expected outcome.
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The path is reported, never the connection string: it could contain a secret.
            throw new InvalidOperationException(
                $"The security database cannot be created or opened at '{path}'. Check that the directory exists and is writable by the server.",
                e);
        }
    }

    // In-memory databases and SQLite URI file names are not plain paths: there is nothing to create.
    private static bool IsNotAFile(SqliteConnectionStringBuilder builder)
    {
        var dataSource = builder.DataSource;

        return builder.Mode == SqliteOpenMode.Memory
            || string.IsNullOrWhiteSpace(dataSource)
            || dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase)
            || dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
    }
}
