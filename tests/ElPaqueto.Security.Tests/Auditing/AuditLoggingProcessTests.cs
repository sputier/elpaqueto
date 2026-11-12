using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace ElPaqueto.Security.Tests.Auditing;

/// <summary>
/// The shipped configuration (<c>src/ElPaqueto/appsettings.json</c>), through the real, built host: audit events
/// are shown on the console at <c>Information</c>, on standard error, successes are not, and no password is printed.
/// </summary>
public sealed partial class AuditLoggingProcessTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("elpaqueto-audit-").FullName;
    private readonly string _password = "Sentinel-Pw-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

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
    public async Task WithTheShippedConfiguration_AuditEventsGoToStandardErrorAndSecretsNowhere()
    {
        using (var bootstrap = HostProcess.Start(
            ["admin", "bootstrap", "--username", "root-admin", "--password-stdin"], Environment(), _password + "\n"))
        {
            Assert.True(await bootstrap.WaitForExitAsync() == 0, bootstrap.Output);

            // The command's result on standard output, its audit event (Information) on standard error only.
            Assert.Contains("Created administrator 'root-admin'", bootstrap.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("First administrator root-admin", bootstrap.StandardError, StringComparison.Ordinal);
            Assert.DoesNotContain("First administrator", bootstrap.StandardOutput, StringComparison.Ordinal);
            Assert.DoesNotContain(_password, bootstrap.Output, StringComparison.Ordinal);
        }

        using var server = HostProcess.Start([], Environment());
        await server.WaitForOutputAsync("Now listening");
        var address = ListeningAddress().Match(server.Output);
        Assert.True(address.Success, server.Output);
        using var client = new HttpClient { BaseAddress = new Uri(address.Groups[1].Value) };

        using (var right = await SendAsync(client, "root-admin", _password))
        {
            Assert.Equal(HttpStatusCode.OK, right.StatusCode);
        }

        using (var wrong = await SendAsync(client, "root-admin", _password + "-wrong"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        await server.WaitForOutputAsync("bad_password");
        Assert.Contains("Authentication with password failed: bad_password (user root-admin) from 127.0.0.1", server.StandardError, StringComparison.Ordinal);

        // Successes are Debug: one per request would flood the log.
        Assert.DoesNotContain("authenticated with", server.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(_password, server.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes("root-admin:" + _password)), server.Output, StringComparison.Ordinal);
        Assert.Empty(server.StandardOutput);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string userName, string password)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("v3/index.json", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(userName + ":" + password)));
        return await client.SendAsync(request);
    }

    // Only what a test run needs: the logging configuration is the shipped one.
    private Dictionary<string, string> Environment() => new()
    {
        ["ASPNETCORE_ENVIRONMENT"] = "Production",
        ["DOTNET_ENVIRONMENT"] = "Production",
        ["Security__Database__ConnectionString"] = $"Data Source={Path.Combine(_root, "auth.db")}",
        ["Security__Passwords__IterationCount"] = "100000",
        ["Database__ConnectionString"] = $"Data Source={Path.Combine(_root, "packages.db")}",
        ["Storage__Path"] = Path.Combine(_root, "storage"),
    };

    [GeneratedRegex(@"Now listening on: (http://127\.0\.0\.1:\d+)")]
    private static partial Regex ListeningAddress();
}
