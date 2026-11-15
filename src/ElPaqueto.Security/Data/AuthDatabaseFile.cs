using Microsoft.Data.Sqlite;

namespace ElPaqueto.Security.Data;

/// <summary>
/// Prepares the SQLite file of the security database before it is opened, and keeps it private.
/// </summary>
internal static class AuthDatabaseFile
{
    private const UnixFileMode DirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode DatabaseFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const UnixFileMode OtherUsers =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    /// <summary>
    /// Creates the parent directory and the file when they are missing. On Unix the directory is
    /// created with mode 0700 and the file with mode 0600: credentials must not be world-readable.
    /// Nothing that already exists is changed.
    /// </summary>
    /// <remarks>
    /// The file is created only for <c>Mode=ReadWriteCreate</c>, the default. <c>Mode=ReadWrite</c>
    /// means "open, never create": operators use it so that the server fails when a volume is not
    /// mounted instead of starting on an empty database.
    /// </remarks>
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
            if (builder.Mode == SqliteOpenMode.ReadWriteCreate && !string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
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

            if (builder.Mode == SqliteOpenMode.ReadWriteCreate)
            {
                CreateIfMissing(path);
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

    /// <summary>
    /// Makes sure that, on Unix, the database file and its write-ahead log side files are not
    /// accessible to other users. A file that already exists (restored from a backup, created by
    /// another tool) may have been created with a permissive umask.
    /// </summary>
    /// <param name="connectionString">The SQLite connection string.</param>
    /// <returns>The paths whose permissions were narrowed.</returns>
    public static IReadOnlyList<string> RestrictToOwner(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);

        if (OperatingSystem.IsWindows() || IsNotAFile(builder))
        {
            return [];
        }

        var path = Path.GetFullPath(builder.DataSource);
        var narrowed = new List<string>();

        foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            var mode = File.GetUnixFileMode(candidate);
            if ((mode & OtherUsers) != UnixFileMode.None)
            {
                File.SetUnixFileMode(candidate, mode & ~OtherUsers);
                narrowed.Add(candidate);
            }
        }

        return narrowed;
    }

    // Opening with OpenOrCreate and no prior existence check: the server and a command-line process,
    // or two replicas sharing a volume, may start together on a fresh path, and the loser of a
    // check-then-create race would otherwise fail. UnixCreateMode applies only when the file is
    // actually created, and an existing file is never truncated.
    private static void CreateIfMissing(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = System.IO.FileMode.OpenOrCreate,
            Access = FileAccess.Write,
            Share = FileShare.ReadWrite | FileShare.Delete,
        };

        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = DatabaseFileMode;
        }

        using var stream = new FileStream(path, options);
    }

    // In-memory databases and SQLite URI file names are not plain paths. The URI form is rejected by
    // the options validation, because SQLite would create such a file without the owner-only mode.
    private static bool IsNotAFile(SqliteConnectionStringBuilder builder)
    {
        var dataSource = builder.DataSource;

        return builder.Mode == SqliteOpenMode.Memory
            || string.IsNullOrWhiteSpace(dataSource)
            || dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase)
            || dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
    }
}
