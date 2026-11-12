using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using ElPaqueto.Security;
using ElPaqueto.Security.Data;
using ElPaqueto.Security.Passwords;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ElPaqueto.Tests;

/// <summary>
/// Creates users with exact permissions in a test host and clients that authenticate as them.
/// </summary>
internal static class TestUserExtensions
{
    /// <summary>Adds an active user holding exactly <paramref name="permissions"/>.</summary>
    public static async Task AddUserAsync(
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

        database.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            UserName = userName,
            PasswordHash = passwords.Hash(password),
            IsActive = isActive,
            Permissions = permissions,
            CreatedAt = now,
            UpdatedAt = now,
            PasswordChangedAt = now,
        });

        await database.SaveChangesAsync();
    }

    /// <summary>Creates a client that sends HTTP Basic credentials for the given user.</summary>
    public static HttpClient CreateClientFor(this WebApplicationFactory<Startup> app, string userName, string password)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{userName}:{password}")));
        return client;
    }
}
