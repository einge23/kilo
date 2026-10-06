using DbUp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
var connectionString = builder.Configuration
    .GetConnectionString("Postgres");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("ConnectionStrings:Postgres is required.");
    return 1;
}

try
{
    var upgrader = DeployChanges.To
        .PostgresqlDatabase(connectionString)
        .WithScriptsEmbeddedInAssembly(typeof(Program).Assembly)
        .WithTransactionPerScript()
        .Build();
    var result = upgrader.PerformUpgrade();
    if (!result.Successful)
    {
        Console.Error.WriteLine("Migration failed; release not activated.");
        return 1;
    }
}
catch (Exception)
{
    Console.Error.WriteLine("Migrator failed; check protected diagnostics.");
    return 1;
}

Console.WriteLine("Migrations complete.");
return 0;
