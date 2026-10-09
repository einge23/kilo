using System.Security.Claims;
using System.Text.Encodings.Web;
using Kilo.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Kilo.Tests.IntegrationTests;

public class KiloApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string SubjectHeader = "X-Test-Subject";
    public const string RoleHeader = "X-Test-Role";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18.6-alpine")
        .WithDatabase("kilo_api_" + Guid.NewGuid().ToString("N"))
        .WithPassword(Guid.NewGuid().ToString("N"))
        .Build();

    public async Task InitializeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _postgres.StartAsync(timeout.Token);

        // Test setup applies the real migrations; the serving API never does.
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KiloDbContext>();
        await db.Database.MigrateAsync(timeout.Token);
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        try
        {
            await base.DisposeAsync();
        }
        finally
        {
            await _postgres.DisposeAsync();
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        var connection = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString())
        {
            GssEncryptionMode = GssEncryptionMode.Disable,
            Timeout = 5
        };
        builder.UseSetting("ConnectionStrings:Postgres", connection.ConnectionString);
        builder.UseSetting("Clerk:Issuer", "https://clerk.kilo.test");
        builder.UseSetting("Clerk:Audience", "kilo-tests");
        builder.UseSetting("Clerk:AuthorizedParties:0", "https://frontend.kilo.test");
        builder.UseSetting("Logging:LogLevel:Default", "Warning");
        builder.ConfigureTestServices(services =>
            services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = "KiloTests";
                    options.DefaultChallengeScheme = "KiloTests";
                    options.DefaultForbidScheme = "KiloTests";
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("KiloTests", _ => { }));
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var subject = Request.Headers[SubjectHeader].ToString();
            if (string.IsNullOrWhiteSpace(subject))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity([new Claim("sub", subject)], Scheme.Name, "sub", "role");
            var role = Request.Headers[RoleHeader].ToString();
            if (!string.IsNullOrWhiteSpace(role))
            {
                identity.AddClaim(new Claim("role", role));
            }
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
