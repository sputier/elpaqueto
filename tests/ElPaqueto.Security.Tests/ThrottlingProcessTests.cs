using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace ElPaqueto.Security.Tests;

/// <summary>
/// Starts the real, built host behind Kestrel and checks what a client sees when it is throttled.
/// </summary>
public sealed partial class ThrottlingProcessTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("elpaqueto-throttling-").FullName;

    public void Dispose()
    {
        try
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A just-killed host may still hold a file for a moment: the temp directory is disposable.
        }
    }

    [Fact]
    public async Task RepeatedFailures_EndIn429WithRetryAfterAndNoChallenge_AndOneLoggedTrip()
    {
        const string password = "process-test-password-marker";
        using var host = HostProcess.Start([], new Dictionary<string, string>
        {
            ["Security__Database__ConnectionString"] = $"Data Source={Path.Combine(_root, "auth.db")}",
            ["Security__Passwords__IterationCount"] = "100000",
            ["Database__ConnectionString"] = $"Data Source={Path.Combine(_root, "packages.db")}",
            ["Storage__Path"] = Path.Combine(_root, "storage"),
            ["Logging__Console__LogLevel__Default"] = "Information",
        });
        await host.WaitForOutputAsync("Now listening");
        var address = ListeningAddress().Match(host.Output);
        Assert.True(address.Success, host.Output);

        using var client = new HttpClient { BaseAddress = new Uri(address.Groups[1].Value) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes("nobody:" + password)));

        for (var i = 0; i < 5; i++)
        {
            using var failure = await client.GetAsync(new Uri("v3/index.json", UriKind.Relative));
            Assert.Equal(HttpStatusCode.Unauthorized, failure.StatusCode);
            Assert.Equal("Basic", failure.Headers.WwwAuthenticate.Single().Scheme);
        }

        using var throttled = await client.GetAsync(new Uri("v3/index.json", UriKind.Relative));

        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        // 60 seconds from the fifth failure, rounded up, minus the time the sixth request took to arrive. That
        // time is normally under a second, but a loaded CI runner was seen taking 5 seconds: the lower bound only
        // proves that the value is the remaining lockout, not a fixed or zero value.
        Assert.InRange(throttled.Headers.RetryAfter?.Delta ?? TimeSpan.Zero, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60));
        Assert.Empty(throttled.Headers.WwwAuthenticate);
        Assert.Equal(string.Empty, await throttled.Content.ReadAsStringAsync());

        await host.WaitForOutputAsync("Sign-in throttling tripped");
        Assert.Single(TripMessage().Matches(host.Output));
        Assert.DoesNotContain(password, host.Output, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"Now listening on: (http://127\.0\.0\.1:\d+)")]
    private static partial Regex ListeningAddress();

    [GeneratedRegex("Sign-in throttling tripped")]
    private static partial Regex TripMessage();
}
