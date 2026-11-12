using ElPaqueto.Security.Data;

namespace ElPaqueto.Security.Users;

/// <summary>
/// The outcome of a user administration operation.
/// </summary>
internal enum UserOperationStatus
{
    /// <summary>The change was written.</summary>
    Succeeded,

    /// <summary>The user was already in the requested state: nothing was written.</summary>
    Unchanged,

    /// <summary>The input is invalid (user name, password policy, permissions): nothing was written.</summary>
    InvalidInput,

    /// <summary>A user holding <see cref="Permission.Admin"/> already exists: bootstrap refused.</summary>
    AdministratorExists,

    /// <summary>A user with the same name, ignoring case, already exists.</summary>
    UserNameTaken,

    /// <summary>The user does not exist.</summary>
    NotFound,

    /// <summary>The change would leave no active user holding <see cref="Permission.Admin"/>.</summary>
    LastActiveAdministrator,

    /// <summary>The user was changed by someone else meanwhile: nothing was written, retry.</summary>
    ConcurrencyConflict,
}

/// <summary>
/// A user as shown by the administration commands. It deliberately has no password hash.
/// </summary>
internal sealed record UserSummary(
    Guid Id,
    string UserName,
    bool IsActive,
    Permission Permissions,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime PasswordChangedAt)
{
    public static UserSummary From(User user)
    {
        ArgumentNullException.ThrowIfNull(user);

        return new UserSummary(
            user.Id,
            user.UserName,
            user.IsActive,
            user.Permissions,
            user.CreatedAt,
            user.UpdatedAt,
            user.PasswordChangedAt);
    }
}

/// <summary>
/// The outcome of a user administration operation, with the user it applies to when there is one.
/// </summary>
internal sealed record UserOperationResult(UserOperationStatus Status, UserSummary? User, IReadOnlyList<string> Errors)
{
    public static UserOperationResult Of(UserOperationStatus status, UserSummary? user = null) => new(status, user, []);

    public static UserOperationResult Invalid(IReadOnlyList<string> errors) => new(UserOperationStatus.InvalidInput, null, errors);

    public static UserOperationResult Invalid(string error) => new(UserOperationStatus.InvalidInput, null, [error]);
}
