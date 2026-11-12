using System.Runtime.Versioning;
using ElPaqueto.Security.Data;

namespace ElPaqueto.Security.Tests;

public sealed class AuthDatabaseFileTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("elpaqueto-file-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Prepare_CreatesTheDirectoryAndTheFile()
    {
        var path = Path.Combine(_root, "data", "db", "auth.db");

        AuthDatabaseFile.Prepare($"Data Source={path}");

        Assert.True(File.Exists(path));
    }

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public void Prepare_OnUnix_RestrictsTheDirectoryAndTheFileToTheOwner()
    {
        var path = Path.Combine(_root, "secure", "auth.db");

        AuthDatabaseFile.Prepare($"Data Source={path}");

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public void Prepare_LeavesAnExistingFileAlone()
    {
        var path = Path.Combine(_root, "existing.db");
        File.WriteAllText(path, "existing content");

        AuthDatabaseFile.Prepare($"Data Source={path}");

        Assert.Equal("existing content", File.ReadAllText(path));
    }

    [Fact]
    public void Prepare_ForAnInMemoryDatabase_CreatesNothing()
    {
        AuthDatabaseFile.Prepare("Data Source=:memory:");

        Assert.Empty(Directory.GetFileSystemEntries(_root));
    }

    [Fact]
    public void Prepare_WhenTheLocationIsUnusable_ReportsThePathButNeverTheConnectionString()
    {
        var blocker = Path.Combine(_root, "not-a-directory");
        File.WriteAllText(blocker, string.Empty);
        var path = Path.Combine(blocker, "auth.db");

        var exception = Assert.Throws<InvalidOperationException>(
            () => AuthDatabaseFile.Prepare($"Data Source={path};Password=s3cr3t-marker"));

        Assert.Contains(path, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cr3t-marker", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cr3t-marker", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prepare_CalledConcurrentlyForTheSameFile_NeverFails()
    {
        var path = Path.Combine(_root, "race", "auth.db");

        // The server and a command-line process may start at the same time and prepare the same file.
        var tasks = Enumerable.Range(0, 64)
            .Select(_ => Task.Run(() => AuthDatabaseFile.Prepare($"Data Source={path}")))
            .ToArray();

        await Task.WhenAll(tasks);

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Prepare_WithModeReadWrite_NeverCreatesTheFile()
    {
        var path = Path.Combine(_root, "must-exist", "auth.db");

        AuthDatabaseFile.Prepare($"Data Source={path};Mode=ReadWrite");

        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
    }

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public void RestrictToOwner_NarrowsAFileThatOtherUsersCouldRead()
    {
        var path = Path.Combine(_root, "restored.db");
        File.WriteAllText(path, "restored from a backup");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        File.WriteAllText(path + "-wal", string.Empty);
        File.SetUnixFileMode(path + "-wal", UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);

        var narrowed = AuthDatabaseFile.RestrictToOwner($"Data Source={path}");

        Assert.Equal([path, path + "-wal"], narrowed);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path + "-wal"));
        Assert.Empty(AuthDatabaseFile.RestrictToOwner($"Data Source={path}"));
    }
}
