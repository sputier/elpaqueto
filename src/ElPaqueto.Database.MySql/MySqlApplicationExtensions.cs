using System;
using ElPaqueto.Core;
using ElPaqueto.Database.MySql;
using Microsoft.Extensions.DependencyInjection;

namespace ElPaqueto;

public static class MySqlApplicationExtensions
{
    public static ElPaquetoApplication AddMySqlDatabase(this ElPaquetoApplication app)
    {
        app.Services.AddElPaquetoDbContextProvider<MySqlContext>("MySql");

        return app;
    }

    public static ElPaquetoApplication AddMySqlDatabase(
        this ElPaquetoApplication app,
        Action<DatabaseOptions> configure)
    {
        app.AddMySqlDatabase();
        app.Services.Configure(configure);
        return app;
    }
}
