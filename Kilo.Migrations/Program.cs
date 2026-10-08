using Kilo.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
if (string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("Postgres")))
{
    Console.Error.WriteLine("ConnectionStrings:Postgres is required.");
    return 1;
}

try
{
    builder.Services.AddKiloDatabase(builder.Configuration);
    using var host = builder.Build();
    await host.StartAsync();
    try
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KiloDbContext>();
        var ct = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
        await db.Database.MigrateAsync(ct);
    }
    finally { await host.StopAsync(); }
}
catch (Exception)
{
    Console.Error.WriteLine("Migration failed; release not activated.");
    return 1;
}

Console.WriteLine("Migrations complete.");
return 0;
