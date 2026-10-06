using Npgsql;

namespace Kilo.Hosting;

public static class PersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddKiloPersistence(
        this IServiceCollection services, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("Postgres");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                "ConnectionStrings:Postgres is required.");

        NpgsqlConnectionStringBuilder settings;
        try
        {
            settings = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException(
                "ConnectionStrings:Postgres is invalid.");
        }

        if (string.IsNullOrWhiteSpace(settings.Host)
            || string.IsNullOrWhiteSpace(settings.Database))
            throw new InvalidOperationException(
                "Postgres Host and Database are required.");

        services.AddSingleton<NpgsqlDataSource>(provider =>
        {
            var dataSource = new NpgsqlDataSourceBuilder(connectionString);
            dataSource.UseLoggerFactory(
                provider.GetRequiredService<ILoggerFactory>());
            return dataSource.Build();
        });
        services.AddHealthChecks().AddCheck<PostgresReadinessCheck>(
            "postgres", timeout: TimeSpan.FromSeconds(3));
        return services;
    }
}
