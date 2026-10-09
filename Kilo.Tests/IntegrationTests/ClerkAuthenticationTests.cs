using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Kilo.Features.Me;
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
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KiloDbContext>();
        Assert.False(await db.Users.AnyAsync(user => user.ClerkUserId == subject));
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
