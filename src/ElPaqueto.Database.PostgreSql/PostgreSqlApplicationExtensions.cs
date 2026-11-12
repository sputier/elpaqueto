using System;
using ElPaqueto.Core;
using ElPaqueto.Database.PostgreSql;
using Microsoft.Extensions.DependencyInjection;

namespace ElPaqueto;

public static class PostgreSqlApplicationExtensions
{
    public static ElPaquetoApplication AddPostgreSqlDatabase(this ElPaquetoApplication app)
    {
        app.Services.AddElPaquetoDbContextProvider<PostgreSqlContext>("PostgreSql");

        return app;
    }

    public static ElPaquetoApplication AddPostgreSqlDatabase(
        this ElPaquetoApplication app,
        Action<DatabaseOptions> configure)
    {
        app.AddPostgreSqlDatabase();
        app.Services.Configure(configure);
        return app;
    }
}
