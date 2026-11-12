using ElPaqueto.Security.Authentication;
using ElPaqueto.Security.Data;
using ElPaqueto.Security.Passwords;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ElPaqueto.Security.Users;

/// <summary>
/// Creates and changes users in the security database. Used by the <c>admin</c> commands.
/// </summary>
/// <remarks>
/// Every operation that depends on the current state (no administrator yet, the user name is free,
/// another active administrator remains) checks that state and writes inside one SQLite write
/// transaction (<c>BEGIN IMMEDIATE</c>). SQLite admits one writer at a time, so a second process
/// waits until the first one commits and then sees its change: two concurrent bootstraps cannot both
/// succeed. The unique index on the normalized user name is the backstop for duplicate names.
/// Passwords are validated against the policy and hashed before the transaction starts, so the write
/// lock is never held during hashing.
/// </remarks>
internal sealed class UserService
{
    // SQLITE_CONSTRAINT_UNIQUE.
    private const int SqliteConstraintUnique = 2067;

    private readonly AuthDbContext _db;
    private readonly IPasswordService _passwords;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public UserService(AuthDbContext db, IPasswordService passwords, TimeProvider time, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _db = db;
        _passwords = passwords;
        _time = time;
        _logger = loggerFactory.CreateLogger(UserAdministrationLog.Category);
    }

    /// <summary>
    /// Creates the first administrator: a user holding every permission, only if no user holding
    /// <see cref="Permission.Admin"/> exists, active or disabled.
    /// </summary>
    public async Task<UserOperationResult> BootstrapAsync(string userName, string password, CancellationToken cancellationToken)
    {
        if (!TryPrepareNewUser(userName, password, PermissionPresets.Administrator, out var user, out var invalid))
        {
            return invalid;
        }

        var result = await InWriteTransactionAsync(
            async () =>
            {
                if (await _db.Users.AnyAsync(u => (u.Permissions & Permission.Admin) == Permission.Admin, cancellationToken))
                {
                    return UserOperationResult.Of(UserOperationStatus.AdministratorExists);
                }

                if (await NameIsTakenAsync(user.UserName, cancellationToken))
                {
                    return UserOperationResult.Of(UserOperationStatus.UserNameTaken);
                }

                _db.Users.Add(user);
                await _db.SaveChangesAsync(cancellationToken);
                return UserOperationResult.Of(UserOperationStatus.Succeeded, UserSummary.From(user));
            },
            cancellationToken);

        switch (result.Status)
        {
            case UserOperationStatus.Succeeded:
                UserAdministrationLog.AdministratorBootstrapped(_logger, user.UserName, user.Id);
                break;
            case UserOperationStatus.AdministratorExists:
                UserAdministrationLog.BootstrapRefused(_logger, user.UserName, "an administrator already exists");
                break;
            case UserOperationStatus.UserNameTaken:
                UserAdministrationLog.BootstrapRefused(_logger, user.UserName, "the user name is taken");
                break;
            default:
                break;
        }

        return result;
    }

    private bool TryPrepareNewUser(
        string userName,
        string password,
        Permission permissions,
        out User user,
        out UserOperationResult invalid)
    {
        user = new User();
        invalid = UserOperationResult.Of(UserOperationStatus.Succeeded);

        var nameError = UserNames.Validate(userName);
        if (nameError is not null)
        {
            invalid = UserOperationResult.Invalid(nameError);
            return false;
        }

        var policyErrors = _passwords.Validate(password, userName);
        if (policyErrors.Count > 0)
        {
            invalid = UserOperationResult.Invalid(policyErrors);
            return false;
        }

        var now = _time.GetUtcNow().UtcDateTime;
        user = new User
        {
            Id = Guid.NewGuid(),
            UserName = userName,
            NormalizedUserName = User.Normalize(userName),
            PasswordHash = _passwords.Hash(password),
            IsActive = true,
            Permissions = permissions,
            CreatedAt = now,
            UpdatedAt = now,
            PasswordChangedAt = now,
        };

        return true;
    }

    private Task<bool> NameIsTakenAsync(string userName, CancellationToken cancellationToken)
    {
        var normalized = User.Normalize(userName);
        return _db.Users.AnyAsync(u => u.NormalizedUserName == normalized, cancellationToken);
    }

    /// <summary>
    /// Runs <paramref name="work"/> in a SQLite write transaction and commits it only when the work
    /// succeeded. A unique-index violation (a concurrent writer that did not use this method) is
    /// reported as a taken user name; a concurrency-token mismatch as a conflict.
    /// </summary>
    private async Task<UserOperationResult> InWriteTransactionAsync(
        Func<Task<UserOperationResult>> work,
        CancellationToken cancellationToken)
    {
        await _db.Database.OpenConnectionAsync(cancellationToken);

        try
        {
            var connection = (SqliteConnection)_db.Database.GetDbConnection();

            // Synchronous: Microsoft.Data.Sqlite has no asynchronous BeginTransaction. It waits for another
            // writer up to the busy timeout, which is acceptable for a one-shot command.
            // deferred: false issues BEGIN IMMEDIATE: the write lock is taken before the first read,
            // so the state checked below cannot change before the write.
            await using var transaction = connection.BeginTransaction(deferred: false);
            await using var efTransaction = await _db.Database.UseTransactionAsync(transaction, cancellationToken);

            UserOperationResult result;
            try
            {
                result = await work();
            }
            catch (DbUpdateConcurrencyException)
            {
                result = UserOperationResult.Of(UserOperationStatus.ConcurrencyConflict);
            }
            catch (DbUpdateException e) when (e.InnerException is SqliteException { SqliteExtendedErrorCode: SqliteConstraintUnique })
            {
                // The Users table has one unique index, on the normalized user name. Revisit this
                // mapping if another unique index is added to a table written here.
                result = UserOperationResult.Of(UserOperationStatus.UserNameTaken);
            }

            if (result.Status == UserOperationStatus.Succeeded)
            {
                await transaction.CommitAsync(cancellationToken);
            }
            else
            {
                await transaction.RollbackAsync(cancellationToken);
                _db.ChangeTracker.Clear();
            }

            return result;
        }
        finally
        {
            await _db.Database.CloseConnectionAsync();
        }
    }
}
