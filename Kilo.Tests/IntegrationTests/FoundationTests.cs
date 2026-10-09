using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Asp.Versioning;
using Kilo.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Kilo.Hosting;
using Kilo.Persistence.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Kilo.Tests.IntegrationTests;

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

    [Theory]
    [InlineData("OrganizeUserEntity")]
    [InlineData("AddExercises")]
    public async Task Ef_migrations_repeat_and_transactions_roll_back_failures_and_cancellation(string baseline)
    {
        await using var postgres = new PostgreSqlBuilder("postgres:18.6-alpine")
            .WithDatabase("kilo_foundation_" + Guid.NewGuid().ToString("N"))
            .WithPassword(Guid.NewGuid().ToString("N")).Build();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await postgres.StartAsync(timeout.Token);
        var connectionString = new NpgsqlConnectionStringBuilder(postgres.GetConnectionString())
        {
            GssEncryptionMode = GssEncryptionMode.Disable,
            Timeout = 5
        }.ConnectionString;
        Exercise[] upgradeExercises = [];
        await using (var previous = CreateContext(connectionString))
        {
            var previousMigration = previous.Database.GetMigrations().Single(id => id.EndsWith("_" + baseline, StringComparison.Ordinal));
            await previous.GetService<IMigrator>().MigrateAsync(previousMigration, timeout.Token);
            Assert.DoesNotContain(await previous.Database.GetAppliedMigrationsAsync(timeout.Token),
                id => id.EndsWith("_AddRoutines", StringComparison.Ordinal));
            var upgradeUser = new User
            {
                ClerkUserId = "upgrade_fixture", TimeZone = "America/Phoenix", MeasurementSystem = "metric"
            };
            previous.Users.Add(upgradeUser);
            await previous.SaveChangesAsync(timeout.Token);
            if (baseline == "AddExercises")
            {
                upgradeExercises =
                [
                    new Exercise { Name = "Retained global", BrandName = "ACME", Description = "Global metadata" },
                    new Exercise { UserId = upgradeUser.Id, Name = "Retained custom", Description = "Private metadata", ArchivedAt = DateTime.UtcNow }
                ];
                previous.Exercises.AddRange(upgradeExercises);
                await previous.SaveChangesAsync(timeout.Token);
                foreach (var exercise in upgradeExercises)
                {
                    // Capture the stored PostgreSQL microsecond precision before the upgrade.
                    await previous.Entry(exercise).ReloadAsync(timeout.Token);
                }
            }
        }
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
        Assert.Equal(db.Database.GetMigrations(), migrations);
        fixture.TimeZone = "America/Phoenix";
        Validator.ValidateObject(fixture, new ValidationContext(fixture), validateAllProperties: true);
        var routine = new Routine { UserId = fixture.Id, Name = "Replay fixture", Description = "Routine metadata" };
        db.Routines.Add(routine);
        await db.SaveChangesAsync();
        Assert.True(routine.Id > 0);
        Assert.Equal(DateTimeKind.Utc, routine.CreatedAt.Kind);
        var second = await RunMigrator(connectionString);
        Assert.True(second.ExitCode == 0, second.Output);
        Assert.Equal(migrations, await db.Database.GetAppliedMigrationsAsync());
        var retained = await db.Users.AsNoTracking().SingleAsync(user => user.ClerkUserId == "fixture");
        Assert.Equal(fixture.Id, retained.Id);
        var upgraded = await db.Users.AsNoTracking().SingleAsync(user => user.ClerkUserId == "upgrade_fixture");
        Assert.Equal("America/Phoenix", upgraded.TimeZone);
        Assert.Equal("metric", upgraded.MeasurementSystem);
        Assert.Equal("America/Phoenix", retained.TimeZone);
        var retainedRoutine = await db.Routines.AsNoTracking().SingleAsync(x => x.Id == routine.Id);
        Assert.Equal(routine.UserId, retainedRoutine.UserId);
        Assert.Equal(routine.Name, retainedRoutine.Name);
        Assert.Equal(routine.Description, retainedRoutine.Description);
        Assert.Equal(routine.CreatedAt, retainedRoutine.CreatedAt);
        foreach (var original in upgradeExercises)
        {
            var saved = await db.Exercises.AsNoTracking().SingleAsync(x => x.Id == original.Id);
            Assert.Equal(original.UserId, saved.UserId);
            Assert.Equal(original.Name, saved.Name);
            Assert.Equal(original.Description, saved.Description);
            Assert.Equal(original.BrandName, saved.BrandName);
            Assert.Equal(original.ArchivedAt, saved.ArchivedAt);
            Assert.Equal(original.CreatedAt, saved.CreatedAt);
        }

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
    }

    private static KiloDbContext CreateContext(string connectionString) => new(
        new DbContextOptionsBuilder<KiloDbContext>().UseNpgsql(connectionString,
            postgres => postgres.CommandTimeout(15).MigrationsHistoryTable("__EFMigrationsHistory", "public")).Options);

    private static WebApplicationFactory<Program> CreateApi(string connectionString) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Postgres", connectionString);
            builder.UseSetting("Clerk:Issuer", "https://clerk.kilo.test");
            builder.UseSetting("Clerk:AuthorizedParties:0", "https://frontend.kilo.test");
            // Foundation checks isolate routing; Me tests exercise protected requests.
            builder.ConfigureTestServices(services =>
                services.PostConfigure<AuthorizationOptions>(options => options.FallbackPolicy = null));
            builder.ConfigureServices(services =>
                services.AddControllers().AddApplicationPart(typeof(ProbeController).Assembly));
        });

    private static async Task<(int ExitCode, string Output)> RunMigrator(string connectionString)
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
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        start.Environment["ConnectionStrings__Postgres"] = connectionString;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            await process.WaitForExitAsync();
            throw new TimeoutException("The test migrator exceeded its 45-second deadline.");
        }
        return (process.ExitCode, await output + await error);
    }
}

// Discovered only through the test assembly; never deployed with the API.
[ApiController]
[AllowAnonymous]
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
