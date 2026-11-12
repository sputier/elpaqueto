using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ElPaqueto.EndToEnd.Tests.Harness;

/// <summary>A user name and its password.</summary>
internal sealed record UserCredential(string UserName, string Password);

/// <summary>An API key. <see cref="ToString"/> never shows the secret.</summary>
internal sealed record ApiKey(string Token)
{
    /// <summary>The public key id: the 12 characters after <c>epq_</c>.</summary>
    public string KeyId => Token.Substring(4, 12);

    /// <summary>The key as the password of source credentials (any user name; <c>apikey</c> by convention).</summary>
    public UserCredential AsSourceCredential() => new("apikey", Token);

    public override string ToString() => "epq_" + KeyId + "_" + Secrets.Mask;
}

/// <summary>
/// Users and API keys, created through the real <c>admin</c> commands of the published server. Passwords
/// are random, generated per run, and passed on standard input, never as arguments.
/// </summary>
internal sealed partial class AdminCli
{
    private readonly IServerUnderTest _server;
    private readonly Action<string> _log;

    public AdminCli(IServerUnderTest server, Action<string> log)
    {
        _server = server;
        _log = log;
    }

    /// <summary>A random password that satisfies the default policy.</summary>
    public static string NewPassword()
        => Secrets.Register("pw-" + RandomNumberGenerator.GetHexString(32, lowercase: true));

    public async Task<UserCredential> BootstrapAsync(string userName)
    {
        var password = NewPassword();
        Secrets.RegisterBasic(userName, password);
        await RunAsync(["bootstrap", "--username", userName, "--password-stdin"], password + "\n");
        return new UserCredential(userName, password);
    }

    public async Task<UserCredential> CreateUserAsync(string userName, string permissions)
    {
        var password = NewPassword();
        Secrets.RegisterBasic(userName, password);
        await RunAsync(["user", "create", userName, "--permissions", permissions, "--password-stdin"], password + "\n");
        return new UserCredential(userName, password);
    }

    public Task DisableUserAsync(string userName) => RunAsync(["user", "disable", userName]);

    public Task SetPermissionsAsync(string userName, string permissions)
        => RunAsync(["user", "set-permissions", userName, "--permissions", permissions]);

    /// <summary>Creates a key; without <paramref name="expiresAt"/> it never expires.</summary>
    public async Task<ApiKey> CreateKeyAsync(string userName, string name, string permissions, DateTimeOffset? expiresAt = null)
    {
        string[] expiry = expiresAt is { } at
            ? ["--expires", at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)]
            : ["--no-expiry"];
        var result = await _server.RunAdminAsync(["key", "create", "--user", userName, "--name", name, "--permissions", permissions, .. expiry]);

        // Register the token before anything is logged.
        var token = result.StandardOutput.Trim();
        if (ApiKeyToken().IsMatch(token))
        {
            Secrets.Register(token);
            Secrets.RegisterBasic("apikey", token);
        }

        _log(result.Describe());
        result.ShouldSucceed();
        Assert.Matches(ApiKeyToken(), token);
        return new ApiKey(token);
    }

    public Task RevokeKeyAsync(ApiKey key) => RunAsync(["key", "revoke", key.KeyId]);

    /// <summary>The status of a key, as <c>admin key show</c> computes it.</summary>
    public async Task<string> KeyStatusAsync(ApiKey key)
    {
        var result = await RunAsync(["key", "show", key.KeyId, "--format", "json"]);
        using var json = JsonDocument.Parse(result.StandardOutput);
        return json.RootElement.GetProperty("status").GetString()!;
    }

    /// <summary>Every user, disabled ones included, as JSON.</summary>
    public async Task<string> ListUsersAsync()
        => (await RunAsync(["user", "list", "--include-disabled", "--format", "json"])).StandardOutput;

    /// <summary>Every key, revoked ones included, as JSON.</summary>
    public async Task<string> ListKeysAsync()
        => (await RunAsync(["key", "list", "--include-revoked", "--format", "json"])).StandardOutput;

    private async Task<CommandResult> RunAsync(IReadOnlyList<string> arguments, string? standardInput = null)
    {
        var result = await _server.RunAdminAsync(arguments, standardInput);
        _log(result.Describe());
        return result.ShouldSucceed();
    }

    [GeneratedRegex(@"\Aepq_[0-9A-Za-z]{12}_[0-9A-Za-z]{43}\z")]
    private static partial Regex ApiKeyToken();
}
