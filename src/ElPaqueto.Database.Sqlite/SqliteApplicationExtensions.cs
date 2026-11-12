using System;
using ElPaqueto.Core;
using ElPaqueto.Database.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace ElPaqueto;

public static class SqliteApplicationExtensions
{
    public static ElPaquetoApplication AddSqliteDatabase(this ElPaquetoApplication app)
    {
        app.Services.AddElPaquetoDbContextProvider<SqliteContext>("Sqlite");

        return app;
    }

    public static ElPaquetoApplication AddSqliteDatabase(
        this ElPaquetoApplication app,
        Action<DatabaseOptions> configure)
    {
        app.AddSqliteDatabase();
        app.Services.Configure(configure);
        return app;
    }
}
