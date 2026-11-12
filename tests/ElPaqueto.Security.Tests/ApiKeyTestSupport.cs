using System.Data.Common;
using ElPaqueto.Security.ApiKeys;
using ElPaqueto.Security.Configuration;
using ElPaqueto.Security.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ElPaqueto.Security.Tests;

/// <summary>A clock that only moves when the test says so.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Counts the comparisons, then delegates to the production comparer.</summary>
internal sealed class CountingHashComparer : IApiKeyHashComparer
{
    private readonly FixedTimeApiKeyHashComparer _inner = new();
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public bool HashesEqual(ReadOnlySpan<byte> computed, ReadOnlySpan<byte> stored)
    {
        Interlocked.Increment(ref _calls);
        return _inner.HashesEqual(computed, stored);
    }
}

/// <summary>Counts the SQL commands sent through EF Core, and the UPDATE statements among them.</summary>
internal sealed class CommandCounter : DbCommandInterceptor
{
    private int _commands;
    private int _updates;

    public int Commands => Volatile.Read(ref _commands);

    public int Updates => Volatile.Read(ref _updates);

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Count(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Count(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Count(command);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Count(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Count(command);
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        Count(command);
        return ValueTask.FromResult(result);
    }

    private void Count(DbCommand command)
    {
        Interlocked.Increment(ref _commands);
        if (command.CommandText.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Increment(ref _updates);
        }
    }
}

/// <summary>Collects every log line, with exceptions.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lines)
            {
                return _lines.ToArray();
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new Logger(_lines);

    public void Dispose()
    {
    }

    private sealed class Logger(List<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var text = $"{logLevel}: {formatter(state, exception)}" + (exception is null ? string.Empty : " " + exception);
            lock (lines)
            {
                lines.Add(text);
            }
        }
    }
}

/// <summary>
/// Builds <see cref="ApiKeyService"/> instances over a real SQLite file, with a controllable clock and
/// counting seams. Every service gets its own context, as two requests or two processes would.
/// </summary>
internal sealed class ApiKeyServiceHarness(AuthDatabaseFixture database, TimeSpan? interval = null)
{
    public static readonly DateTimeOffset Start = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    public ManualTimeProvider Clock { get; } = new(Start);

    public CountingHashComparer Comparer { get; } = new();

    public CommandCounter Counter { get; } = new();

    public CapturingLoggerProvider Logs { get; } = new();

    public ApiKeyUsageTracker Tracker { get; private set; } = NewTracker(interval);

    public DateTime Now => Clock.GetUtcNow().UtcDateTime;

    public static ApiKeyUsageTracker NewTracker(TimeSpan? interval) => new(Options.Create(new ElPaquetoSecurityOptions
    {
        ApiKeys = new SecurityApiKeyOptions { LastUsedUpdateInterval = interval ?? TimeSpan.FromMinutes(5) },
    }));

    /// <summary>Forgets the in-memory debounce state, as a restart or another process would.</summary>
    public void RestartProcess(TimeSpan? newInterval = null) => Tracker = NewTracker(newInterval ?? interval);

    public AuthDbContext CreateContext(IInterceptor? extra = null)
    {
        AuthDatabaseFile.Prepare(database.ConnectionString);

        var builder = new DbContextOptionsBuilder<AuthDbContext>();
        AuthDbContextConfiguration.ConfigureCore(builder, database.ConnectionString);
        builder.AddInterceptors(Counter);
        if (extra is not null)
        {
            builder.AddInterceptors(extra);
        }

        return new AuthDbContext(builder.Options);
    }

    public ApiKeyService CreateService(AuthDbContext context)
    {
        using var factory = LoggerFactory.Create(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(Logs);
        });

        return new ApiKeyService(context, Clock, Comparer, Tracker, factory.CreateLogger<ApiKeyService>(), factory.CreateLogger<Audit>());
    }

    public async Task<Guid> AddUserAsync(string userName, Permission permissions, bool active = true)
    {
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
        var user = AuthDatabaseFixture.NewUser(userName, Now);
        user.Permissions = permissions;
        user.IsActive = active;
        context.Users.Add(user);
        await context.SaveChangesAsync();
        return user.Id;
    }

    public async Task UpdateUserAsync(Guid id, Action<User> change)
    {
        await using var context = CreateContext();
        var user = await context.Users.SingleAsync(u => u.Id == id);
        change(user);
        await context.SaveChangesAsync();
    }

    public async Task<ApiKeyCreated> CreateKeyAsync(Guid owner, Permission permissions, DateTime? expiresAt = null, string name = "ci-main")
    {
        await using var context = CreateContext();
        return await CreateService(context).CreateAsync(owner, name, permissions, expiresAt);
    }

    public async Task<ApiKeyValidationResult> ValidateAsync(string? token)
    {
        await using var context = CreateContext();
        return await CreateService(context).ValidateAsync(token);
    }

    public async Task<ApiKey> LoadKeyAsync(string keyId)
    {
        await using var context = CreateContext();
        return await context.ApiKeys.AsNoTracking().SingleAsync(k => k.KeyId == keyId);
    }

    public async Task ExecuteSqlAsync(string sql)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
