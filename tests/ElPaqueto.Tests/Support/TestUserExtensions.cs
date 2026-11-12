using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using ElPaqueto.Security;
using ElPaqueto.Security.ApiKeys;
using ElPaqueto.Security.Data;
using ElPaqueto.Security.Passwords;
using ElPaqueto.Web;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ElPaqueto.Tests;

/// <summary>
/// Creates users and API keys with exact permissions in a test host, and clients that authenticate with exactly
/// one credential.
/// </summary>
/// <remarks>
/// The default client of <see cref="ElPaquetoApplication"/> sends the seeded test user's API key in
/// <c>X-NuGet-ApiKey</c>, like <c>dotnet nuget push</c>. Every client created here removes it first, so that a
/// test of a password or of a key sent as a Basic password is never decided by the default key.
/// </remarks>
internal static class TestUserExtensions
{
    /// <summary>The header NuGet clients send the API key in.</summary>
    public const string ApiKeyHeader = HttpRequestExtensions.ApiKeyHeader;

    /// <summary>Adds an active user holding exactly <paramref name="permissions"/>.</summary>
    /// <returns>The id of the new user.</returns>
    public static async Task<Guid> AddUserAsync(
        this WebApplicationFactory<Startup> app,
        string userName,
        string password,
        Permission permissions,
        bool isActive = true)
    {
        using var scope = app.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var passwords = scope.ServiceProvider.GetRequiredService<IPasswordService>();
        var now = DateTime.UtcNow;
        var id = Guid.NewGuid();

        database.Users.Add(new User
        {
            Id = id,
            UserName = userName,
            PasswordHash = passwords.Hash(password),
            IsActive = isActive,
            Permissions = permissions,
            CreatedAt = now,
            UpdatedAt = now,
            PasswordChangedAt = now,
        });

        await database.SaveChangesAsync();
        return id;
    }

    /// <summary>Creates an API key for an existing user through the real service.</summary>
    public static async Task<ApiKeyCreated> AddApiKeyAsync(
        this WebApplicationFactory<Startup> app,
        string userName,
        Permission permissions,
        DateTime? expiresAt = null)
    {
        using var scope = app.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var normalized = User.Normalize(userName);
        var ownerId = database.Users.Where(u => u.NormalizedUserName == normalized).Select(u => u.Id).Single();

        return await scope.ServiceProvider.GetRequiredService<IApiKeyService>()
            .CreateAsync(ownerId, "test key", permissions, expiresAt);
    }

    /// <summary>Creates a client that sends no credential at all.</summary>
    public static HttpClient CreateAnonymousClient(this WebApplicationFactory<Startup> app)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = null;
        client.DefaultRequestHeaders.Remove(ApiKeyHeader);
        return client;
    }

    /// <summary>Creates a client that sends HTTP Basic credentials for the given user, and nothing else.</summary>
    public static HttpClient CreateClientFor(this WebApplicationFactory<Startup> app, string userName, string password)
    {
        var client = app.CreateAnonymousClient();
        client.DefaultRequestHeaders.Authorization = Basic(userName, password);
        return client;
    }

    /// <summary>Creates a client that sends <paramref name="token"/> in <c>X-NuGet-ApiKey</c>, and nothing else.</summary>
    public static HttpClient CreateClientWithApiKey(this WebApplicationFactory<Startup> app, string token)
    {
        var client = app.CreateAnonymousClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(ApiKeyHeader, token);
        return client;
    }

    /// <summary>Creates a client that sends <paramref name="token"/> as the password of HTTP Basic credentials, and nothing else.</summary>
    public static HttpClient CreateClientWithApiKeyAsPassword(this WebApplicationFactory<Startup> app, string token, string userName = "apikey")
        => app.CreateClientFor(userName, token);

    /// <summary>HTTP Basic credentials.</summary>
    public static AuthenticationHeaderValue Basic(string userName, string password)
        => new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{userName}:{password}")));
}
