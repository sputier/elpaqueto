using ElPaqueto;
using ElPaqueto.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateBuilder();

// This will add the ElPaqueto services and options to the container.
builder.Services.AddElPaquetoWebApplication(elpaqueto =>
{
    elpaqueto.AddSqliteDatabase();
    elpaqueto.AddFileStorage();
});
var app = builder.Build();

if (builder.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

app.UseStaticFiles();

// Add ElPaqueto's endpoints.
new ElPaquetoEndpointBuilder().MapEndpoints(app);

await app.RunMigrationsAsync();
await app.RunAsync();
