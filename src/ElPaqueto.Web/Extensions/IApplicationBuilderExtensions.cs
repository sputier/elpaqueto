using System;
using Microsoft.AspNetCore.Builder;

namespace ElPaqueto.Web;

public static class IApplicationBuilderExtensions
{
    public static IApplicationBuilder UseOperationCancelledMiddleware(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.UseMiddleware<OperationCancelledMiddleware>();
    }
}
