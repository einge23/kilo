using Kilo.Hosting.Options;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Kilo.Hosting;

public static class ClerkServiceCollectionExtensions
{
    private static readonly object MetadataUnavailable = new();

    public static IServiceCollection AddKiloClerk(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<ClerkOptions>()
            .Bind(configuration.GetRequiredSection("Clerk"))
            .Validate(
                clerk => Uri.TryCreate(
                    clerk.Issuer, UriKind.Absolute, out var uri)
                    && uri.Scheme == Uri.UriSchemeHttps,
                "Clerk:Issuer must be an absolute HTTPS URL.")
            .Validate(
                clerk => clerk.Audience is null
                    || !string.IsNullOrWhiteSpace(clerk.Audience),
                "Clerk:Audience must be omitted or nonblank.")
            .Validate(
                clerk => clerk.AuthorizedParties.Length > 0
                    && clerk.AuthorizedParties.All(origin =>
                        Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                        && (uri.Scheme is "https" or "http")
                        && origin == uri.GetLeftPart(UriPartial.Authority)),
                "Clerk:AuthorizedParties must contain origins without paths.")
            .ValidateOnStart();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer();

        services.AddOptions<JwtBearerOptions>(
                JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<ClerkOptions>>((jwt, configured) =>
            {
                var clerk = configured.Value;

                jwt.Authority = clerk.Issuer;
                jwt.RequireHttpsMetadata = true;
                jwt.BackchannelTimeout = TimeSpan.FromSeconds(10);
                jwt.MapInboundClaims = false;
                jwt.IncludeErrorDetails = false;

                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = clerk.Issuer,

                    ValidateAudience = clerk.Audience is not null,
                    ValidAudience = clerk.Audience,

                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    ClockSkew = TimeSpan.FromSeconds(5),

                    RequireSignedTokens = true,
                    ValidateIssuerSigningKey = true,
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],

                    NameClaimType = "sub",
                    RoleClaimType = "role"
                };

                jwt.Events = new JwtBearerEvents
                {
                    OnMessageReceived = async context =>
                    {
                        var authorization = context.Request.Headers.Authorization.ToString();
                        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                            || string.IsNullOrWhiteSpace(authorization[7..]))
                        {
                            return;
                        }

                        // Use the native cache before validation: token handlers can suppress
                        // retrieval failures and report them as an unknown signing key.
                        try
                        {
                            await context.Options.ConfigurationManager!
                                .GetConfigurationAsync(context.HttpContext.RequestAborted);
                        }
                        catch (Exception exception) when (
                            !context.HttpContext.RequestAborted.IsCancellationRequested
                            && IsMetadataTransportFailure(exception))
                        {
                            context.HttpContext.Items[MetadataUnavailable] = true;
                            context.Fail("Trusted signing metadata is unavailable.");
                        }
                    },
                    OnChallenge = context =>
                    {
                        if (context.HttpContext.Items.ContainsKey(MetadataUnavailable))
                        {
                            context.HandleResponse();
                            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                        }
                        return Task.CompletedTask;
                    },
                    OnTokenValidated = context =>
                    {
                        var subject = context.Principal?
                            .FindFirst("sub")?.Value;
                        var authorizedParty = context.Principal?
                            .FindFirst("azp")?.Value;

                        if (string.IsNullOrWhiteSpace(subject)
                            || !clerk.AuthorizedParties.Contains(
                                authorizedParty, StringComparer.Ordinal))
                        {
                            context.Fail("Invalid Clerk identity.");
                        }

                        return Task.CompletedTask;
                    }
                };
            });

        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(
                new AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser()
                    .Build());

        services.AddCors();

        services.AddOptions<CorsOptions>()
            .Configure<IOptions<ClerkOptions>>((cors, configured) =>
            {
                cors.AddPolicy("frontend", policy =>
                    policy.WithOrigins(configured.Value.AuthorizedParties)
                        .AllowAnyHeader()
                        .AllowAnyMethod());
            });

        return services;
    }

    private static bool IsMetadataTransportFailure(Exception exception) =>
        exception is HttpRequestException or IOException
        || (exception.InnerException is { } inner && IsMetadataTransportFailure(inner));
}
