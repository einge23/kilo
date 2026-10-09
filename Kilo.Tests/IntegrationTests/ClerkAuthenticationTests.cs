using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Kilo.Features.Me;
using Kilo.Features.Exercises;
using Kilo.Features.Routines;
using Kilo.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Kilo.Tests.IntegrationTests;

public sealed class ClerkAuthenticationTests(ClerkApiFactory factory) : IClassFixture<ClerkApiFactory>
{
    [Fact]
    public async Task Routines_and_placements_use_verified_subject_and_remain_private_from_signed_admins()
    {
        var subject = NewSubject();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token(subject));
        client.DefaultRequestHeaders.Add(KiloApiFactory.SubjectHeader, NewSubject());
        using var created = await client.PostAsJsonAsync("/api/v1/routines", new { name = subject, userId = int.MaxValue });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var routine = (await created.Content.ReadFromJsonAsync<RoutineDto>())!;
        using var exerciseCreated = await client.PostAsJsonAsync("/api/v1/exercises", new { name = subject });
        var exercise = (await exerciseCreated.Content.ReadFromJsonAsync<ExerciseDto>())!;
        using var attached = await client.PostAsJsonAsync($"/api/v1/routines/{routine.Id}/exercises",
            new { exerciseId = exercise.Id, position = 1, defaultRestSeconds = 0, userId = int.MaxValue, exerciseScopeId = 0 });
        Assert.Equal(HttpStatusCode.Created, attached.StatusCode);
        var placement = (await attached.Content.ReadFromJsonAsync<RoutineExerciseDto>())!;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token(NewSubject(), role: "admin"));
        using var hidden = await client.GetAsync($"/api/v1/routines/{routine.Id}");
        using var denied = await client.PutAsJsonAsync($"/api/v1/routines/{routine.Id}", new { name = "Not owned" });
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        using var deniedPlacement = await client.PutAsJsonAsync($"/api/v1/routines/{routine.Id}/exercises/{placement.Id}",
            new { position = 2, defaultRestSeconds = 120 });
        using var deniedAttach = await client.PostAsJsonAsync($"/api/v1/routines/{routine.Id}/exercises",
            new { exerciseId = exercise.Id, position = 2, defaultRestSeconds = 0 });
        Assert.Equal(HttpStatusCode.NotFound, deniedPlacement.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, deniedAttach.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<RoutineSummaryDto[]>("/api/v1/routines?includeArchived=true"))!);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token(subject));
        Assert.Equivalent(routine with { Exercises = [placement] }, await client.GetFromJsonAsync<RoutineDto>($"/api/v1/routines/{routine.Id}"), strict: true);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KiloDbContext>();
        var userId = await db.Users.Where(x => x.ClerkUserId == subject).Select(x => x.Id).SingleAsync();
        Assert.Equal(userId, (await db.Routines.AsNoTracking().SingleAsync(x => x.Id == routine.Id)).UserId);
        var savedPlacement = await db.RoutineExercises.AsNoTracking().SingleAsync(x => x.Id == placement.Id);
        Assert.Equal(userId, savedPlacement.UserId);
        Assert.Equal(userId, savedPlacement.ExerciseScopeId);
        Assert.Equal(0, savedPlacement.DefaultRestSeconds);
    }

    [Theory]
    [InlineData("admin", HttpStatusCode.Created)]
    [InlineData("user", HttpStatusCode.Forbidden)]
    [InlineData(null, HttpStatusCode.Forbidden)]
    [InlineData("Admin", HttpStatusCode.Forbidden)]
    public async Task Global_creation_requires_the_signed_admin_role(string? role, HttpStatusCode expected)
    {
        var name = NewSubject();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token(NewSubject(), role: role));
        client.DefaultRequestHeaders.Add("X-Role", "admin");
        using var response = await client.PostAsJsonAsync("/api/v1/admin/exercises", new
        {
            name, role = "admin", isGlobal = true, userId = 1
        });
        Assert.Equal(expected, response.StatusCode);
        using var update = await client.PutAsJsonAsync("/api/v1/admin/exercises/2147483647", new { name = "Signed role update" });
        Assert.Equal(role == "admin" ? HttpStatusCode.NotFound : HttpStatusCode.Forbidden, update.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KiloDbContext>();
        Assert.Equal(expected == HttpStatusCode.Created ? 1 : 0, await db.Exercises.CountAsync(x => x.Name == name));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("expired")]
    [InlineData("tampered")]
    [InlineData("wrong-issuer")]
    [InlineData("missing-sub")]
    [InlineData("missing-expiration")]
    [InlineData("wrong-algorithm")]
    [InlineData("wrong-audience")]
    [InlineData("wrong-azp")]
    public async Task Invalid_identity_returns_401_without_provisioning(string variant)
    {
        var subject = NewSubject();
        using var client = factory.CreateClient();
        if (variant != "missing")
        {
            var token = factory.Token(subject, variant, role: "admin");
            if (variant == "tampered")
            {
                var parts = token.Split('.');
                parts[2] = Base64UrlEncoder.Encode(new byte[256]);
                token = string.Join('.', parts);
            }
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        using var response = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(401, (await response.Content.ReadFromJsonAsync<ProblemDetails>())!.Status);
        client.DefaultRequestHeaders.Add(KiloApiFactory.RoleHeader, "admin");
        using var deniedAdmin = await client.PostAsJsonAsync("/api/v1/admin/exercises", new { name = subject, role = "admin" });
        Assert.Equal(HttpStatusCode.Unauthorized, deniedAdmin.StatusCode);
        using var deniedRead = await client.GetAsync("/api/v1/exercises");
        Assert.Equal(HttpStatusCode.Unauthorized, deniedRead.StatusCode);
        using var deniedCustom = await client.PostAsJsonAsync("/api/v1/exercises", new { name = subject, role = "admin" });
        Assert.Equal(HttpStatusCode.Unauthorized, deniedCustom.StatusCode);
        using var deniedRoutines = await client.GetAsync("/api/v1/routines");
        Assert.Equal(HttpStatusCode.Unauthorized, deniedRoutines.StatusCode);
        using var deniedRoutineWrite = await client.PostAsJsonAsync("/api/v1/routines", new { name = subject });
        Assert.Equal(HttpStatusCode.Unauthorized, deniedRoutineWrite.StatusCode);
        using var deniedPlacement = await client.PostAsJsonAsync("/api/v1/routines/1/exercises",
            new { exerciseId = 1, position = 1, defaultRestSeconds = 0 });
        using var deniedPlacementUpdate = await client.PutAsJsonAsync("/api/v1/routines/1/exercises/1",
            new { position = 1, defaultRestSeconds = 0 });
        Assert.Equal(HttpStatusCode.Unauthorized, deniedPlacement.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, deniedPlacementUpdate.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KiloDbContext>();
        Assert.False(await db.Users.AnyAsync(user => user.ClerkUserId == subject));
        Assert.False(await db.Exercises.AnyAsync(exercise => exercise.Name == subject));
        Assert.False(await db.Routines.AnyAsync(routine => routine.Name == subject));
    }

    [Fact]
    public async Task Cached_keys_work_during_outage_but_unknown_keys_and_bad_signatures_stay_401()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token(NewSubject()));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/me")).StatusCode);
        factory.Metadata.Unavailable = true;
        try
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token(NewSubject()));
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/me")).StatusCode);
            using var unknown = RSA.Create(2048);
            var key = new RsaSecurityKey(unknown) { KeyId = "unknown" };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token(NewSubject(), key: key));
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/me")).StatusCode);
            key.KeyId = factory.Metadata.Key.KeyId;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token(NewSubject(), key: key));
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/me")).StatusCode);
        }
        finally
        {
            factory.Metadata.Unavailable = false;
        }
    }

    [Fact]
    public async Task Signing_key_rotation_is_loaded_by_native_metadata_refresh()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token(NewSubject()));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/me")).StatusCode);
        factory.Metadata.Rotate();
        await Task.Delay(TimeSpan.FromSeconds(1.1));
        var jwt = factory.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);
        jwt.ConfigurationManager!.RequestRefresh();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token(NewSubject()));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            using var response = await client.GetAsync("/api/v1/me", timeout.Token);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                break;
            }
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            await Task.Delay(100, timeout.Token);
        }
    }

    [Fact]
    public async Task Production_omits_openapi_and_keeps_health_public_and_policy_denials_403()
    {
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/openapi/v1.json")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token(NewSubject()));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/openapi/v1.json")).StatusCode);
        using var denied = await client.GetAsync("/api/v1/test/denied");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(403, (await denied.Content.ReadFromJsonAsync<ProblemDetails>())!.Status);
    }

    [Theory]
    [InlineData(ClerkApiFactory.Origin, true)]
    [InlineData("https://foreign.kilo.test", false)]
    public async Task Cors_preflight_only_grants_configured_origins(string origin, bool allowed)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/me/preferences");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "PUT");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(allowed, response.Headers.Contains("Access-Control-Allow-Origin"));
        if (allowed)
        {
            Assert.Equal(origin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        }
    }

    [Fact]
    public async Task Verified_concurrent_first_requests_resolve_one_account()
    {
        var subject = "user_" + Guid.NewGuid().ToString("N")[..22];
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token(subject));
        var profiles = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => client.GetFromJsonAsync<UserDto>("/api/v1/me")));
        var profile = Assert.IsType<UserDto>(profiles[0]);
        Assert.True(profile.Id > 0);
        Assert.Equal(subject, profile.ClerkUserId);
        Assert.Equal("UTC", profile.TimeZone);
        Assert.Equal("imperial", profile.MeasurementSystem);
        Assert.All(profiles, result => Assert.Equal(profile, result));
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KiloDbContext>();
        Assert.Equal(1, await db.Users.CountAsync(user => user.ClerkUserId == subject));
    }

    [Fact]
    public async Task Cold_metadata_outage_returns_sanitized_503_without_provisioning()
    {
        using var isolated = factory.WithWebHostBuilder(_ => { });
        using var client = isolated.CreateClient();
        var subject = "user_" + Guid.NewGuid().ToString("N")[..22];
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token(subject));
        factory.Metadata.Unavailable = true;
        try
        {
            using var response = await client.GetAsync("/api/v1/me");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
            Assert.Equal(503, problem!.Status);
            Assert.DoesNotContain("Fixture metadata", await response.Content.ReadAsStringAsync());
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<KiloDbContext>();
            Assert.False(await db.Users.AnyAsync(user => user.ClerkUserId == subject));
        }
        finally
        {
            factory.Metadata.Unavailable = false;
        }
    }

    private static string NewSubject() => "user_" + Guid.NewGuid().ToString("N")[..22];
}
