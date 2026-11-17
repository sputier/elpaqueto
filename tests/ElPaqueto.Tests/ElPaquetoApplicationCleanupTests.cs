using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
        using (var other = app.WithWebHostBuilder(_ => { }))
        {
            other.CreateClient().Dispose();
        }

        var directories = app.TempDirectories.ToList();
        Assert.Equal(2, directories.Count);

        await app.DisposeAsync();

        Assert.All(directories, directory => Assert.False(Directory.Exists(directory)));
    }

    [Fact]
    public void DeleteQuietlyNeverThrows()
    {
        TestDirectory.DeleteQuietly(Path.Combine(Path.GetTempPath(), "ElPaquetoTests", "does-not-exist"));
    }
}
