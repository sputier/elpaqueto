using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace ElPaqueto.Tests;

public class ElPaquetoApplicationCleanupTests(ITestOutputHelper output)
{
    [Fact]
    public async Task DisposeRemovesTheTemporaryDirectory()
    {
        var app = new ElPaquetoApplication(output);
        using (var client = app.CreateClient())
        {
            using var response = await client.GetAsync("/v3/index.json");
            response.EnsureSuccessStatusCode();
        }

        var directory = Assert.Single(app.TempDirectories);
        Assert.True(Directory.Exists(directory));
        Assert.True(File.Exists(Path.Combine(directory, "auth.db")));

        app.Dispose();

        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task DisposeAsyncRemovesTheDirectoriesOfEveryHost()
    {
        var app = new ElPaquetoApplication(output);
        app.CreateClient().Dispose();

        // WithWebHostBuilder builds a second host, with its own directory.
        await using (var other = app.WithWebHostBuilder(_ => { }))
        {
            other.CreateClient().Dispose();
        }

        var directories = app.TempDirectories.ToList();
        Assert.Equal(2, directories.Count);

        await app.DisposeAsync();

        Assert.All(directories, directory => Assert.False(Directory.Exists(directory)));
    }

    [Fact]
    public void DeleteQuietlyIgnoresAMissingDirectory()
    {
        TestDirectory.DeleteQuietly(Path.Combine(Path.GetTempPath(), "ElPaquetoTests", "does-not-exist"));
    }

    [Fact]
    public void DeleteQuietlyRemovesADirectoryWithAPooledSqliteDatabase()
    {
        var directory = NewDirectory();
        var connectionString = $"Data Source={Path.Combine(directory, "test.db")}";
        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();
        }

        // The pooled connection still holds the file open on Windows.
        TestDirectory.DeleteQuietly(directory);

        Assert.False(Directory.Exists(directory));
    }

    [WindowsFact]
    public void DeleteQuietlyRetriesWhileAFileIsLocked()
    {
        var directory = NewDirectory();
        var locked = new FileStream(Path.Combine(directory, "locked.txt"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);

        // A dedicated thread: a busy thread pool must not delay the release past the retries.
        var release = new Thread(() =>
        {
            Thread.Sleep(120);
            locked.Dispose();
        });
        release.Start();

        TestDirectory.DeleteQuietly(directory);
        release.Join();

        Assert.False(Directory.Exists(directory));
    }

    [WindowsFact]
    public void DeleteQuietlyDoesNotThrowWhileAFileStaysLocked()
    {
        var directory = NewDirectory();
        using (new FileStream(Path.Combine(directory, "locked.txt"), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            var exception = Record.Exception(() => TestDirectory.DeleteQuietly(directory));

            Assert.Null(exception);
            Assert.True(Directory.Exists(directory));
        }

        TestDirectory.DeleteQuietly(directory);
        Assert.False(Directory.Exists(directory));
    }

    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ElPaquetoTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>A fact that only runs on Windows, where an open file cannot be deleted.</summary>
    private sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute()
        {
            if (!OperatingSystem.IsWindows())
            {
                Skip = "An open file can only be locked against deletion on Windows.";
            }
        }
    }
}
