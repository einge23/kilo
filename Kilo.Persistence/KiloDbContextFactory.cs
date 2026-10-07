using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Kilo.Persistence;

// EF tooling reads environment configuration without starting either executable.
public sealed class KiloDbContextFactory : IDesignTimeDbContextFactory<KiloDbContext>
{
    public KiloDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var connection = DatabaseServiceCollectionExtensions.GetConnectionString(configuration);
        return new KiloDbContext(new DbContextOptionsBuilder<KiloDbContext>()
            .UseNpgsql(connection, postgres => postgres.CommandTimeout(15)
                .MigrationsHistoryTable("__EFMigrationsHistory", "public")).Options);
    }
}
