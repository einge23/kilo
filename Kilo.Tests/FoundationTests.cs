using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Asp.Versioning;
using Dapper;
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
    public async Task Host_has_one_pool_and_only_test_routes_are_versioned()
    {
        await using var factory = CreateApi(UnavailableDatabase);
        using var client = factory.CreateClient();
        var pool = factory.Services.GetRequiredService<NpgsqlDataSource>();
        Assert.Same(pool, factory.Services.GetRequiredService<NpgsqlDataSource>());
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
    public async Task Migrations_repeat_and_native_transactions_roll_back_failures_and_cancellation()
    {
        var adminSettings = new NpgsqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("KILO_TEST_POSTGRES"));
        // This generated identifier belongs only to this test; never reuse a user's database.
        var database = "kilo_test_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(adminSettings.ConnectionString);
        await admin.OpenAsync();
        await admin.ExecuteAsync($"CREATE DATABASE {database}");
        try
        {
            var settings = new NpgsqlConnectionStringBuilder(adminSettings.ConnectionString)
                { Database = database };
            var connectionString = settings.ConnectionString;
            var first = await RunMigrator(connectionString);
            Assert.True(first.ExitCode == 0, first.Output);
            await using var source = NpgsqlDataSource.Create(connectionString);
            await using (var connection = await source.OpenConnectionAsync())
            {
                var id = await connection.QuerySingleAsync<int>("""
                    INSERT INTO users(clerk_user_id) VALUES ('fixture') RETURNING id
                    """);
                Assert.True(id > 0);
                Assert.Equal("imperial", await connection.QuerySingleAsync<string>(
                    "SELECT measurement_system FROM users WHERE id = @id", new { id }));
                var scripts = await connection.QuerySingleAsync<int>("SELECT count(*)::int FROM schemaversions");
                Assert.Equal(1, scripts);
                var second = await RunMigrator(connectionString);
                Assert.True(second.ExitCode == 0, second.Output);
                Assert.Equal(id, await connection.QuerySingleAsync<int>(
                    "SELECT id FROM users WHERE clerk_user_id = 'fixture'"));
                Assert.Equal(scripts, await connection.QuerySingleAsync<int>(
                    "SELECT count(*)::int FROM schemaversions"));

                await using (var transaction = await connection.BeginTransactionAsync())
                {
                    await connection.ExecuteAsync(new CommandDefinition(
                        "INSERT INTO users(clerk_user_id) VALUES ('rollback')", transaction: transaction));
                    await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(
                        new CommandDefinition("SELECT 1 / 0", transaction: transaction)));
                }
                Assert.Equal(0, await connection.QuerySingleAsync<int>(
                    "SELECT count(*)::int FROM users WHERE clerk_user_id = 'rollback'"));
            }

            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO users(clerk_user_id) VALUES ('cancelled')", transaction: transaction));
                using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.ExecuteAsync(
                    new CommandDefinition("SELECT pg_sleep(10)", transaction: transaction,
                        commandTimeout: 15, cancellationToken: deadline.Token)));
            }
            await using (var connection = await source.OpenConnectionAsync())
                Assert.Equal(0, await connection.QuerySingleAsync<int>(
                    "SELECT count(*)::int FROM users WHERE clerk_user_id = 'cancelled'"));

            await using var factory = CreateApi(connectionString);
            using var client = factory.CreateClient();
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        }
        finally
        {
            await admin.ExecuteAsync($"DROP DATABASE {database} WITH (FORCE)");
        }
    }

    private static WebApplicationFactory<Program> CreateApi(string connectionString) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Postgres", connectionString);
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
