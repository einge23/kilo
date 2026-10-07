using Kilo.Persistence;

namespace Kilo.Hosting;

public static class PersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddKiloPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddKiloDatabase(configuration);
        services.AddHealthChecks().AddCheck<PostgresReadinessCheck>("postgres", timeout: TimeSpan.FromSeconds(3));
        return services;
    }
}
