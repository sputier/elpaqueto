using ElPaqueto.Security.ApiKeys;
using ElPaqueto.Security.Authentication;
using ElPaqueto.Security.Users;
using McMaster.Extensions.CommandLineUtils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ElPaqueto.Security.Cli;

/// <summary>
/// <c>admin key …</c>: create, list, show, revoke and rotate API keys.
/// </summary>
/// <remarks>
/// The token of a new key is written once, alone and followed by a line feed, to standard output; every
/// other message of <c>create</c> and <c>rotate</c> goes to standard error. It is never logged and never
/// written by any other command.
/// </remarks>
internal static class ApiKeyCommands
{
    private const string UserOption = "--user";
    private const string NameOption = "--name";
    private const string PermissionsOption = "--permissions";
    private const string IncludeRevokedOption = "--include-revoked";

    private const string StoreItNow =
        "Store this key now, for example in your CI secret store: it is shown only once and cannot be displayed again.";

    private static readonly AdminArgument KeyIdArgument = new("keyId", "The key id: the 12 characters after 'epq_' in the key.");

    private static readonly AdminOption PermissionsOptionSpec = new(
        PermissionsOption,
        "list",
        "Required. Comma-separated: read, publish, admin, or a preset: reader, publisher, administrator. The owner must hold every one.");

    public static void Register(AdminCommandBuilder builder, CommandLineApplication admin)
    {
        builder.Group(admin, "key", "Create, list, revoke and rotate API keys.", key =>
        {
            builder.Command(
                key,
                "create",
                "Create an API key for a user. The key is written once to standard output.",
                [],
                [
                    new(UserOption, "username", "Required. The owner of the key."),
                    new(NameOption, "label", "Required. A label, 1 to 100 characters, for example ci-main."),
                    PermissionsOptionSpec,
                    .. ExpiryText.Options,
                    UserOutput.FormatOptionSpec,
                ],
                TokenRemarks,
                CreateAsync);

            builder.Command(
                key,
                "list",
                "List the API keys that are not revoked, or every key with --include-revoked.",
                [],
                [
                    new(UserOption, "username", "Only the keys of this user."),
                    new(IncludeRevokedOption, null, "Include revoked keys."),
                    UserOutput.FormatOptionSpec,
                ],
                null,
                ListAsync);

            builder.Command(
                key,
                "show",
                "Show one API key (never its secret).",
                [KeyIdArgument],
                [UserOutput.FormatOptionSpec],
                null,
                ShowAsync);

            builder.Command(
                key,
                "revoke",
                "Revoke an API key at once. Revoking a revoked key changes nothing.",
                [KeyIdArgument],
                [],
                null,
                RevokeAsync);

            builder.Command(
                key,
                "rotate",
                "Replace an API key by a new one with the same owner, name and permissions, and revoke it.",
                [KeyIdArgument],
                [.. ExpiryText.Options, UserOutput.FormatOptionSpec],
                TokenRemarks + Environment.NewLine
                + "The old key stops working at once. To switch clients over without interruption, create a new key, "
                + "update the clients, then revoke the old key.",
                RotateAsync);
        });
    }

    private static string TokenRemarks =>
        "The key is written alone, followed by a line feed, to standard output; messages go to standard error. "
        + "Redirect standard output to a file or a secret store to keep the key out of the terminal. "
        + "With --format json, standard output is one JSON object that includes the key.";

    private static async Task<int> CreateAsync(AdminInvocation invocation, CancellationToken cancellationToken)
    {
        var arguments = invocation.Arguments;
        var userName = arguments.Value(UserOption);
        var name = arguments.Value(NameOption);

        if (userName is null)
        {
            return invocation.UsageError($"The option '{UserOption}' is required.");
        }

        if (UserNames.Validate(userName) is { } userNameError)
        {
            return invocation.UsageError(userNameError);
        }

        if (name is null)
        {
            return invocation.UsageError($"The option '{NameOption}' is required.");
        }

        if (ApiKeyService.NameError(name) is { } nameError)
        {
            return invocation.UsageError(nameError);
        }

        if (LooksLikeAKey(name))
        {
            // Refused rather than stored and shown by every list: it may be a key typed in the wrong place.
            return invocation.UsageError("A key name cannot start with 'epq_', the prefix of API keys.");
        }

        if (!TryReadPermissions(invocation, out var permissions, out var failure))
        {
            return failure;
        }

        if (!ExpiryText.TryRead(arguments, Now(invocation), out var expiresAt, out var expiryError)
            || !UserOutput.TryReadFormat(arguments, out var json, out expiryError))
        {
            return invocation.UsageError(expiryError);
        }

        await invocation.MigrateAsync(cancellationToken);

        var owner = await Users(invocation).FindAsync(userName, cancellationToken);
        if (owner is null)
        {
            return UserNotFound(invocation, userName);
        }

        if (!owner.IsActive)
        {
            return invocation.Fail(
                AdminExitCode.PreconditionFailed,
                $"User '{owner.UserName}' is disabled: enable it before creating a key for it. Nothing was changed.");
        }

        var missing = permissions & ~owner.Permissions;
        if (missing != Permission.None)
        {
            return invocation.Fail(
                AdminExitCode.PreconditionFailed,
                $"User '{owner.UserName}' does not hold these permissions: {PermissionText.Format(missing)}. "
                + $"A key can only hold permissions of its owner (it holds: {PermissionText.Format(owner.Permissions)}). Nothing was changed.");
        }

        ApiKeyCreated created;
        try
        {
            created = await Keys(invocation).CreateAsync(owner.Id, name, permissions, expiresAt, cancellationToken);
        }
        catch (ApiKeyRequestException e)
        {
            // The owner changed between the checks above and the creation.
            return invocation.Fail(AdminExitCode.PreconditionFailed, $"Refused: {e.Message} Nothing was changed.");
        }

        // The key is written before anything else can fail: once it is stored, losing its only copy is the worst outcome.
        var view = new ApiKeyView(created.Key, owner.UserName, owner.IsActive, owner.Permissions, Now(invocation));
        if (!TryWriteToken(invocation, created.Token, view, json, replacedKeyId: null))
        {
            return invocation.Fail(
                AdminExitCode.UnexpectedError,
                $"API key {created.Key.KeyId} was created but could not be written to standard output: revoke it with "
                + $"'admin key revoke {created.Key.KeyId}' and create another one.");
        }

        var logger = Log(invocation);
        UserAdministrationLog.ApiKeyCreated(
            logger, created.Key.KeyId, owner.UserName, owner.Id, created.Key.Permissions, created.Key.ExpiresAt);
        invocation.Console.Error.WriteLine(
            $"Created API key {created.Key.KeyId} '{created.Key.Name}' for user '{owner.UserName}' with permissions: "
            + $"{PermissionText.Format(created.Key.Permissions)}; expires: {ExpiryText.Format(created.Key.ExpiresAt)}.");
        invocation.Console.Error.WriteLine(StoreItNow);
        return AdminExitCode.Success;
    }

    private static async Task<int> ListAsync(AdminInvocation invocation, CancellationToken cancellationToken)
    {
        var userName = invocation.Arguments.Value(UserOption);
        if (userName is not null && UserNames.Validate(userName) is { } userNameError)
        {
            return invocation.UsageError(userNameError);
        }

        if (!UserOutput.TryReadFormat(invocation.Arguments, out var json, out var error))
        {
            return invocation.UsageError(error);
        }

        await invocation.MigrateAsync(cancellationToken);

        var owners = await OwnersAsync(invocation, cancellationToken);
        Guid? ownerId = null;
        if (userName is not null)
        {
            var owner = owners.Values.FirstOrDefault(u => string.Equals(u.UserName, userName, StringComparison.OrdinalIgnoreCase));
            if (owner is null)
            {
                return UserNotFound(invocation, userName);
            }

            ownerId = owner.Id;
        }

        var includeRevoked = invocation.Arguments.Has(IncludeRevokedOption);
        var now = Now(invocation);
        var keys = (await Keys(invocation).ListAsync(ownerId, cancellationToken))
            .Where(k => includeRevoked || k.RevokedAt is null)
            .Select(k => View(k, owners, now))
            .ToList();

        invocation.Console.Out.Write(json ? ApiKeyOutput.ListAsJson(keys) : ApiKeyOutput.ListAsText(keys));
        return AdminExitCode.Success;
    }

    private static async Task<int> ShowAsync(AdminInvocation invocation, CancellationToken cancellationToken)
    {
        var keyId = invocation.Arguments.Positionals[0];
        if (!UserOutput.TryReadFormat(invocation.Arguments, out var json, out var error))
        {
            return invocation.UsageError(error);
        }

        if (!TryValidateKeyId(invocation, keyId, out var failure))
        {
            return failure;
        }

        await invocation.MigrateAsync(cancellationToken);
        var key = await Keys(invocation).FindAsync(keyId, cancellationToken);
        if (key is null)
        {
            return KeyNotFound(invocation, keyId);
        }

        var view = View(key, await OwnersAsync(invocation, cancellationToken), Now(invocation));
        invocation.Console.Out.Write(json ? ApiKeyOutput.KeyAsJson(view) : ApiKeyOutput.KeyAsText(view));
        return AdminExitCode.Success;
    }

    private static async Task<int> RevokeAsync(AdminInvocation invocation, CancellationToken cancellationToken)
    {
        var keyId = invocation.Arguments.Positionals[0];
        if (!TryValidateKeyId(invocation, keyId, out var failure))
        {
            return failure;
        }

        await invocation.MigrateAsync(cancellationToken);
        switch (await Keys(invocation).RevokeAsync(keyId, cancellationToken))
        {
            case ApiKeyRevocation.Revoked:
                var logger = Log(invocation);
                if (logger.IsEnabled(LogLevel.Information))
                {
                    var key = await Keys(invocation).FindAsync(keyId, cancellationToken);
                    var owner = key is null ? null : (await OwnersAsync(invocation, cancellationToken)).GetValueOrDefault(key.OwnerUserId);
                    UserAdministrationLog.ApiKeyRevoked(logger, keyId, owner?.UserName ?? "(unknown)", key?.OwnerUserId ?? Guid.Empty);
                }

                invocation.Console.Out.WriteLine($"Revoked API key {keyId}.");
                return AdminExitCode.Success;
            case ApiKeyRevocation.AlreadyRevoked:
                invocation.Console.Error.WriteLine($"API key {keyId} is already revoked: nothing was changed.");
                return AdminExitCode.Success;
            default:
                return KeyNotFound(invocation, keyId);
        }
    }

    private static async Task<int> RotateAsync(AdminInvocation invocation, CancellationToken cancellationToken)
    {
        var keyId = invocation.Arguments.Positionals[0];
        if (!TryValidateKeyId(invocation, keyId, out var failure))
        {
            return failure;
        }

        if (!ExpiryText.TryRead(invocation.Arguments, Now(invocation), out var expiresAt, out var error)
            || !UserOutput.TryReadFormat(invocation.Arguments, out var json, out error))
        {
            return invocation.UsageError(error);
        }

        await invocation.MigrateAsync(cancellationToken);
        var old = await Keys(invocation).FindAsync(keyId, cancellationToken);
        if (old is null)
        {
            return KeyNotFound(invocation, keyId);
        }

        var owners = await OwnersAsync(invocation, cancellationToken);
        var owner = owners[old.OwnerUserId];

        if (old.RevokedAt is not null)
        {
            return invocation.Fail(
                AdminExitCode.PreconditionFailed,
                $"API key {keyId} is revoked and cannot be rotated: create a new key with 'admin key create'. Nothing was changed.");
        }

        if (!owner.IsActive)
        {
            return invocation.Fail(
                AdminExitCode.PreconditionFailed,
                $"The owner of API key {keyId}, '{owner.UserName}', is disabled: enable it first. Nothing was changed.");
        }

        var missing = old.Permissions & ~owner.Permissions;
        if (missing != Permission.None)
        {
            return invocation.Fail(
                AdminExitCode.PreconditionFailed,
                $"User '{owner.UserName}' no longer holds these permissions of API key {keyId}: {PermissionText.Format(missing)}. "
                + "Rotation never changes what a key may do: revoke the key and create one with the permissions wanted. Nothing was changed.");
        }

        ApiKeyCreated created;
        try
        {
            created = await Keys(invocation).RotateAsync(keyId, expiresAt, cancellationToken);
        }
        catch (ApiKeyRequestException e)
        {
            // The key or its owner changed between the checks above and the rotation.
            return invocation.Fail(AdminExitCode.PreconditionFailed, $"Refused: {e.Message} Nothing was changed.");
        }

        var view = new ApiKeyView(created.Key, owner.UserName, owner.IsActive, owner.Permissions, Now(invocation));
        if (!TryWriteToken(invocation, created.Token, view, json, replacedKeyId: keyId))
        {
            return invocation.Fail(
                AdminExitCode.UnexpectedError,
                $"API key {keyId} was revoked and replaced by {created.Key.KeyId}, but the new key could not be written to standard "
                + $"output: revoke {created.Key.KeyId} with 'admin key revoke' and create another key. The old key cannot be restored.");
        }

        var logger = Log(invocation);
        UserAdministrationLog.ApiKeyRotated(logger, keyId, created.Key.KeyId, owner.UserName, owner.Id, created.Key.ExpiresAt);
        invocation.Console.Error.WriteLine(
            $"Rotated API key {keyId}: it is revoked and replaced by {created.Key.KeyId} '{created.Key.Name}' for user '{owner.UserName}' "
            + $"with permissions: {PermissionText.Format(created.Key.Permissions)}; expires: {ExpiryText.Format(created.Key.ExpiresAt)}.");
        invocation.Console.Error.WriteLine(StoreItNow);
        return AdminExitCode.Success;
    }

    /// <summary>The only place a token is written: standard output, once.</summary>
    private static bool TryWriteToken(AdminInvocation invocation, string token, ApiKeyView key, bool json, string? replacedKeyId)
    {
        try
        {
            // A line feed, not Environment.NewLine: the same bytes on every platform, for files and pipes.
            invocation.Console.Out.Write(json ? ApiKeyOutput.CreatedAsJson(token, key, replacedKeyId) : token + "\n");
            invocation.Console.Out.Flush();
            return true;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // For example a closed pipe. The exception is not shown: nothing about the token belongs in an error.
            return false;
        }
    }

    private static bool TryValidateKeyId(AdminInvocation invocation, string keyId, out int failure)
    {
        if (ApiKeyService.IsKeyIdFormat(keyId))
        {
            failure = AdminExitCode.Success;
            return true;
        }

        // The value is not repeated: it may be a complete key typed in the wrong place.
        failure = LooksLikeAKey(keyId)
            ? invocation.UsageError(
                "Pass the key id, not the complete key: the 12 characters after 'epq_'. A key given on a command line can be read by "
                + "other users and stays in the shell history: if it was a real key, rotate or revoke it.")
            : invocation.UsageError("A key id has exactly 12 characters from A-Z a-z 0-9 ('admin key list' shows them).");
        return false;
    }

    private static bool TryReadPermissions(AdminInvocation invocation, out Permission permissions, out int failure)
    {
        permissions = Permission.None;
        var value = invocation.Arguments.Value(PermissionsOption);
        if (value is null)
        {
            failure = invocation.UsageError(
                $"The option '{PermissionsOption}' is required: a key gets no permission by default. Valid values: {PermissionText.ValidValues}.");
            return false;
        }

        if (!PermissionText.TryParse(value, out permissions, out var error))
        {
            failure = invocation.UsageError(error);
            return false;
        }

        failure = AdminExitCode.Success;
        return true;
    }

    private static async Task<Dictionary<Guid, UserSummary>> OwnersAsync(AdminInvocation invocation, CancellationToken cancellationToken)
        => (await Users(invocation).ListAsync(includeDisabled: true, cancellationToken)).ToDictionary(u => u.Id);

    private static ApiKeyView View(ApiKeyInfo key, Dictionary<Guid, UserSummary> owners, DateTime now)
    {
        // Keys reference their owner with ON DELETE RESTRICT: the owner always exists.
        var owner = owners[key.OwnerUserId];
        return new ApiKeyView(key, owner.UserName, owner.IsActive, owner.Permissions, now);
    }

    private static int UserNotFound(AdminInvocation invocation, string userName)
        => invocation.Fail(
            AdminExitCode.NotFound,
            LooksLikeAKey(userName)
                ? "The user does not exist. The value given looks like an API key: if it was a real key, rotate or revoke it."
                : $"User '{userName}' does not exist.");

    /// <summary>Whether a value may be a complete key typed in the wrong place: it is then never repeated.</summary>
    private static bool LooksLikeAKey(string value)
        => value.StartsWith(ApiKeyToken.Prefix, StringComparison.OrdinalIgnoreCase);

    private static int KeyNotFound(AdminInvocation invocation, string keyId)
        => invocation.Fail(AdminExitCode.NotFound, $"API key {keyId} does not exist.");

    private static DateTime Now(AdminInvocation invocation)
        => invocation.Services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;

    private static UserService Users(AdminInvocation invocation) => invocation.Services.GetRequiredService<UserService>();

    private static IApiKeyService Keys(AdminInvocation invocation) => invocation.Services.GetRequiredService<IApiKeyService>();

    private static ILogger Log(AdminInvocation invocation)
        => invocation.Services.GetRequiredService<ILoggerFactory>().CreateLogger(UserAdministrationLog.Category);
}
