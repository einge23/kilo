using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Kilo.Persistence;

public static class DatabaseServiceCollectionExtensions
{
    public static IServiceCollection AddKiloDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        var connection = GetConnectionString(configuration);
        services.AddDbContext<KiloDbContext>(options => options.UseNpgsql(connection,
            postgres => postgres.CommandTimeout(15).MigrationsHistoryTable("__EFMigrationsHistory", "public")));
        return services;
    }

    internal static string GetConnectionString(IConfiguration configuration)
    {
        var connection = configuration.GetConnectionString("Postgres");
        if (string.IsNullOrWhiteSpace(connection))
        {
            throw new InvalidOperationException("ConnectionStrings:Postgres is required.");
        }

        NpgsqlConnectionStringBuilder settings;
        try
        {
            settings = new NpgsqlConnectionStringBuilder(connection);
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException("ConnectionStrings:Postgres is invalid.");
        }
        if (string.IsNullOrWhiteSpace(settings.Host) || string.IsNullOrWhiteSpace(settings.Database))
        {
            throw new InvalidOperationException("Postgres Host and Database are required.");
        }

        return connection;
    }
}
