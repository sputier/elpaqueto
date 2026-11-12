using System;
using ElPaqueto.Core;
using ElPaqueto.Database.SqlServer;
using Microsoft.Extensions.DependencyInjection;

namespace ElPaqueto;

public static class SqlServerApplicationExtensions
{
    public static ElPaquetoApplication AddSqlServerDatabase(this ElPaquetoApplication app)
    {
        app.Services.AddElPaquetoDbContextProvider<SqlServerContext>("SqlServer");

        return app;
    }

    public static ElPaquetoApplication AddSqlServerDatabase(
        this ElPaquetoApplication app,
        Action<DatabaseOptions> configure)
    {
        app.AddSqlServerDatabase();
        app.Services.Configure(configure);
        return app;
    }
}
