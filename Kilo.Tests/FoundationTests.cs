using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Asp.Versioning;
using Kilo.Persistence;
using Microsoft.EntityFrameworkCore;
using Kilo.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Kilo.Tests;

public sealed class FoundationTests
{
    private const string UnavailableDatabase =
        "Host=127.0.0.1;Port=1;Database=kilo;Username=test;Password=private-test-value;Timeout=1";

    [Theory]
    [InlineData("")]
    [InlineData("not a connection string;private-test-value")]
    [InlineData("Host=localhost;Password=private-test-value")]
    public void Invalid_configuration_fails_without_exposing_values(string connection)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:Postgres"] = connection }).Build();
        var error = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddKiloPersistence(configuration));
        Assert.DoesNotContain("private-test-value", error.ToString());
    }

    [Fact]
    public async Task Host_has_scoped_contexts_and_only_test_routes_are_versioned()
    {
        await using var factory = CreateApi(UnavailableDatabase);
        using var client = factory.CreateClient();
        using var firstScope = factory.Services.CreateScope();
        using var secondScope = factory.Services.CreateScope();
        var db = firstScope.ServiceProvider.GetRequiredService<KiloDbContext>();
        Assert.Same(db, firstScope.ServiceProvider.GetRequiredService<KiloDbContext>());
        Assert.NotSame(db, secondScope.ServiceProvider.GetRequiredService<KiloDbContext>());
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/probe")).StatusCode);
        foreach (var route in new[] { "/api/probe", "/api/v2/probe" })
        {
            var response = await client.GetAsync(route);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
            Assert.Equal(404, problem!.Status);
        }

        var invalid = await client.PostAsJsonAsync("/api/v1/probe", new { name = "" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.NotEmpty((await invalid.Content.ReadFromJsonAsync<ValidationProblemDetails>())!.Errors);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/openapi/v1.json")).StatusCode);
    }

    [Fact]
    public async Task Unavailable_database_returns_bounded_anonymous_readiness_failure()
    {
        await using var factory = CreateApi(UnavailableDatabase);
        using var client = factory.CreateClient();
        var timer = Stopwatch.StartNew();
        var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("Unhealthy", await response.Content.ReadAsStringAsync());
        Assert.InRange(timer.Elapsed.TotalSeconds, 0, 6);
    }

    [Fact]
    public async Task Migrator_missing_configuration_exits_with_safe_diagnostic()
    {
        var result = await RunMigrator("");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("ConnectionStrings:Postgres is required.", result.Output);
    }

    [PostgresFact]
    public async Task Ef_migrations_repeat_and_transactions_roll_back_failures_and_cancellation()
    {
        await WithDatabase(async connectionString =>
        {
            var first = await RunMigrator(connectionString);
            Assert.True(first.ExitCode == 0, first.Output);
            await using var db = CreateContext(connectionString);
            Assert.False(db.Database.HasPendingModelChanges());
            var fixture = new User { ClerkUserId = "fixture" };
            db.Users.Add(fixture);
            await db.SaveChangesAsync();
            Assert.True(fixture.Id > 0);
            Assert.Equal("UTC", fixture.TimeZone);
            Assert.Equal("imperial", fixture.MeasurementSystem);
            Assert.Equal(DateTimeKind.Utc, fixture.CreatedAt.Kind);
            var migrations = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
            Assert.Single(migrations);
            var second = await RunMigrator(connectionString);
            Assert.True(second.ExitCode == 0, second.Output);
            Assert.Equal(migrations, await db.Database.GetAppliedMigrationsAsync());
            Assert.Equal(fixture.Id, (await db.Users.AsNoTracking().SingleAsync()).Id);

            await using (var write = CreateContext(connectionString))
            await using (var transaction = await write.Database.BeginTransactionAsync())
            {
                write.Users.Add(new User { ClerkUserId = "rollback" });
                await write.SaveChangesAsync();
                await Assert.ThrowsAsync<PostgresException>(() =>
                    write.Database.ExecuteSqlRawAsync("SELECT 1 / 0"));
            }
            Assert.False(await db.Users.AnyAsync(user => user.ClerkUserId == "rollback"));

            await using (var write = CreateContext(connectionString))
            await using (var transaction = await write.Database.BeginTransactionAsync())
            {
                write.Users.Add(new User { ClerkUserId = "cancelled" });
                await write.SaveChangesAsync();
                using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    write.Database.ExecuteSqlRawAsync("SELECT pg_sleep(10)", deadline.Token));
            }
            Assert.False(await db.Users.AnyAsync(user => user.ClerkUserId == "cancelled"));
            await using (var invalid = CreateContext(connectionString))
            {
                invalid.Users.Add(new User { ClerkUserId = "invalid", MeasurementSystem = "unknown" });
                await Assert.ThrowsAsync<DbUpdateException>(() => invalid.SaveChangesAsync());
            }
            Assert.False(await db.Users.AnyAsync(user => user.ClerkUserId == "invalid"));
            await using var factory = CreateApi(connectionString);
            using var client = factory.CreateClient();
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        });
    }

    [PostgresFact]
    public async Task Explicit_legacy_adoption_preserves_rows_and_rejects_schema_drift()
    {
        await WithDatabase(async connectionString =>
        {
            await using var db = CreateContext(connectionString);
            await db.Database.ExecuteSqlRawAsync(LegacyBaseline);
            var fixture = new User { ClerkUserId = "retained" };
            db.Users.Add(fixture);
            await db.SaveChangesAsync();
            var normal = await RunMigrator(connectionString);
            Assert.NotEqual(0, normal.ExitCode); // Never silently adopts an unrecognized existing table.
            var adopted = await RunMigrator(connectionString, "--adopt-legacy-baseline");
            Assert.True(adopted.ExitCode == 0, adopted.Output);
            Assert.Equal(fixture.Id, (await db.Users.AsNoTracking().SingleAsync()).Id);
            Assert.Single(await db.Database.GetAppliedMigrationsAsync());
            Assert.Equal(0, (await RunMigrator(connectionString)).ExitCode);
            Assert.Equal(fixture.Id, (await db.Users.AsNoTracking().SingleAsync()).Id);
        });
        await WithDatabase(async connectionString =>
        {
            await using var db = CreateContext(connectionString);
            await db.Database.ExecuteSqlRawAsync(LegacyBaseline);
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE users ALTER COLUMN time_zone SET DEFAULT 'GMT'");
            db.Users.Add(new User { ClerkUserId = "preserve-on-rejection" });
            await db.SaveChangesAsync();
            var rejected = await RunMigrator(connectionString, "--adopt-legacy-baseline");
            Assert.NotEqual(0, rejected.ExitCode);
            Assert.Empty(await db.Database.GetAppliedMigrationsAsync());
            Assert.Single(await db.Users.AsNoTracking().ToListAsync());
        });
    }

    private static KiloDbContext CreateContext(string connectionString) => new(
        new DbContextOptionsBuilder<KiloDbContext>().UseNpgsql(connectionString,
            postgres => postgres.CommandTimeout(15).MigrationsHistoryTable("__EFMigrationsHistory", "public")).Options);

    private static async Task WithDatabase(Func<string, Task> check)
    {
        var adminSettings = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("KILO_TEST_POSTGRES"));
        var database = "kilo_test_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(adminSettings.ConnectionString);
        await admin.OpenAsync();
        await using (var command = new NpgsqlCommand($"CREATE DATABASE {database}", admin) { CommandTimeout = 15 })
            await command.ExecuteNonQueryAsync();
        try
        {
            var settings = new NpgsqlConnectionStringBuilder(adminSettings.ConnectionString) { Database = database };
            await check(settings.ConnectionString);
        }
        finally
        {
            await using var command = new NpgsqlCommand($"DROP DATABASE {database} WITH (FORCE)", admin) { CommandTimeout = 15 };
            await command.ExecuteNonQueryAsync();
        }
    }

    // A historical test fixture only; production schema changes are EF migrations.
    private const string LegacyBaseline = """
        CREATE TABLE users (
          id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
          clerk_user_id text NOT NULL UNIQUE,
          time_zone text NOT NULL DEFAULT 'UTC',
          measurement_system text NOT NULL DEFAULT 'imperial'
            CHECK (measurement_system IN ('imperial', 'metric')),
          created_at timestamptz NOT NULL DEFAULT now(),
          CHECK (btrim(clerk_user_id) <> '')
        );
        CREATE TABLE schemaversions (scriptname text NOT NULL);
        INSERT INTO schemaversions VALUES ('Kilo.Migrations.Migrations.001_users.sql');
        """;

    private static WebApplicationFactory<Program> CreateApi(string connectionString) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Postgres", connectionString);
            builder.ConfigureServices(services =>
                services.AddControllers().AddApplicationPart(typeof(ProbeController).Assembly));
        });

    private static async Task<(int ExitCode, string Output)> RunMigrator(string connectionString, params string[] arguments)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Kilo.slnx")))
            root = root.Parent;
        Assert.NotNull(root);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var assembly = Path.Combine(root.FullName, "Kilo.Migrations", "bin", configuration,
            "net10.0", "Kilo.Migrations.dll");
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(assembly);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        start.Environment["ConnectionStrings__Postgres"] = connectionString;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output + await error);
    }
}

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KILO_TEST_POSTGRES")))
            Skip = "Set KILO_TEST_POSTGRES to a Postgres admin connection with CREATE DATABASE permission.";
    }
}

// Discovered only through the test assembly; never deployed with the API.
[ApiController]
[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/probe")]
public sealed class ProbeController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok();

    [HttpPost]
    public IActionResult Post(ProbeRequest request) => Ok(request);
}

public sealed record ProbeRequest([Required] string? Name);
