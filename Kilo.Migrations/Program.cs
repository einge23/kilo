using Kilo.Migrations;
using Kilo.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var adopt = args.Contains("--adopt-legacy-baseline", StringComparer.Ordinal);
var builder = Host.CreateApplicationBuilder(args.Where(arg => arg != "--adopt-legacy-baseline").ToArray());
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
        if (adopt)
            await LegacyBaselineAdoption.AdoptAsync(db, ct);
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
