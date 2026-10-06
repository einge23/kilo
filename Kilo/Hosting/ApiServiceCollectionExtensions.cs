using Asp.Versioning;

namespace Kilo.Hosting;

public static class ApiServiceCollectionExtensions
{
    public static IServiceCollection AddKiloApi(
        this IServiceCollection services)
    {
        services.AddControllers();
        services.AddProblemDetails();
        services.AddOpenApi();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddApiVersioning(options =>
        {
            options.DefaultApiVersion = new ApiVersion(1, 0);
            options.AssumeDefaultVersionWhenUnspecified = false;
            options.ReportApiVersions = true;
            options.ApiVersionReader = new UrlSegmentApiVersionReader();
        }).AddMvc();
        return services;
    }
}
