using ElPaqueto.EndToEnd.Tests.Harness;

namespace ElPaqueto.EndToEnd.Tests.Scenarios;

/// <summary>
/// The refused pushes of #37 scenario 6, shared with rows 6 and 8 of the authorization matrix (#39), so that
/// they are implemented once. Each case prepares its own credentials through the admin command line, pushes
/// with the real <c>dotnet nuget push</c> from a client whose source credentials are a <c>Read</c> user (the
/// service index must load), and checks that nothing changed on the server.
/// </summary>
internal static class RefusedPushes
{
    public const string InvalidKey = "invalid key";
    public const string RevokedKey = "revoked key";
    public const string ExpiredKey = "expired key";
    public const string ReadOnlyKey = "read-only key";
    public const string OwnerWithoutPublish = "owner without publish";
    public const string DisabledOwner = "disabled owner";
    public const string NoKey = "no key";

    /// <summary>Every case and the status code ADR 0002 gives it: 401 for a key that does not authenticate, 403 for a missing permission.</summary>
    public static TheoryData<string, int> Cases => new()
    {
        { InvalidKey, 401 },
        { RevokedKey, 401 },
        { ExpiredKey, 401 },
        { ReadOnlyKey, 403 },
        { OwnerWithoutPublish, 403 },
        { DisabledOwner, 401 },
        // No X-NuGet-ApiKey: the push carries the source's Basic credentials, which never authorize a write.
        { NoKey, 403 },
    };

    /// <summary>Pushes <paramref name="package"/> as the case describes and asserts the refusal and its absence on the server.</summary>
    public static async Task AssertRefusedAsync(ServerFixture fixture, AdminCli admin, NuGetClient client, string caseName, int expectedStatus, TestPackage package)
    {
        Assert.False(await fixture.Api.HasVersionAsync(package), $"{package} must not be on the server before the push.");
        var key = await PrepareKeyAsync(fixture, admin, caseName);

        var result = await client.PushAsync(package, key);

        result.ShouldFail().ShouldContain(expectedStatus == 401 ? "401 (Unauthorized)" : "403 (Forbidden)");
        Assert.False(await fixture.Api.HasVersionAsync(package), $"{package} is registered after a refused push.");
        Assert.False(File.Exists(fixture.Api.StoredPackagePath(package)), $"{package} is in the storage after a refused push.");
    }

    private static async Task<ApiKey?> PrepareKeyAsync(ServerFixture fixture, AdminCli admin, string caseName)
    {
        var owner = "pusher-" + System.Security.Cryptography.RandomNumberGenerator.GetHexString(6, lowercase: true);
        switch (caseName)
        {
            case InvalidKey:
                // The right shape, unknown key id and secret.
                return new ApiKey(Secrets.Register("epq_AAAAAAAAAAAA_" + new string('0', 43)));

            case RevokedKey:
                {
                    var key = await admin.CreateKeyAsync(fixture.Publisher.UserName, "revoked", "publish");
                    await admin.RevokeKeyAsync(key);
                    Assert.Equal("revoked", await admin.KeyStatusAsync(key));
                    return key;
                }

            case ExpiredKey:
                {
                    var expiresAt = DateTimeOffset.UtcNow.AddSeconds(8);
                    var key = await admin.CreateKeyAsync(fixture.Publisher.UserName, "expiring", "publish", expiresAt);
                    var wait = expiresAt.AddSeconds(1) - DateTimeOffset.UtcNow;
                    if (wait > TimeSpan.Zero)
                    {
                        await Task.Delay(wait);
                    }

                    Assert.Equal("expired", await admin.KeyStatusAsync(key));
                    return key;
                }

            case ReadOnlyKey:
                return await admin.CreateKeyAsync(fixture.Publisher.UserName, "read-only", "read");

            case OwnerWithoutPublish:
                {
                    // A Publish key whose owner then loses Publish: the key never exceeds its owner.
                    await admin.CreateUserAsync(owner, "publisher");
                    var key = await admin.CreateKeyAsync(owner, "demoted", "publish");
                    await admin.SetPermissionsAsync(owner, "reader");
                    Assert.Equal("active", await admin.KeyStatusAsync(key));
                    return key;
                }

            case DisabledOwner:
                {
                    await admin.CreateUserAsync(owner, "publisher");
                    var key = await admin.CreateKeyAsync(owner, "disabled", "publish");
                    await admin.DisableUserAsync(owner);
                    Assert.Equal("owner-disabled", await admin.KeyStatusAsync(key));
                    return key;
                }

            case NoKey:
                return null;

            default:
                throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Unknown refused push.");
        }
    }
}
