using Kilo.Features.Me;
using Kilo.Persistence;
using Kilo.Persistence.Entities;
using Kilo.Tests.Fakers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Xunit;

namespace Kilo.Tests.IntegrationTests;

public sealed class MeControllerTests(KiloApiFactory factory) : IClassFixture<KiloApiFactory>
{
    [Fact]
    public async Task Get_provisions_a_profile_with_defaults_on_first_request()
    {
        var subject = NewSubject();
        using var client = CreateClient(subject);

        var profile = await GetProfile(client);

        Assert.True(profile.Id > 0);
        Assert.Equal(subject, profile.ClerkUserId);
        Assert.Equal("UTC", profile.TimeZone);
        Assert.Equal("imperial", profile.MeasurementSystem);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KiloDbContext>();
        var saved = await db.Users.AsNoTracking().SingleAsync(user => user.ClerkUserId == subject);
        Assert.Equal(profile.Id, saved.Id);
        Assert.Equal(profile.TimeZone, saved.TimeZone);
        Assert.Equal(profile.MeasurementSystem, saved.MeasurementSystem);
    }

    [Fact]
    public async Task Get_returns_the_existing_profile_without_provisioning_another_account()
    {
        var preferences = new PreferencesRequestFaker().UseSeed(1001).Generate();
        var user = new User
        {
            ClerkUserId = NewSubject(),
            TimeZone = preferences.TimeZone,
            MeasurementSystem = preferences.MeasurementSystem
        };
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KiloDbContext>();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        using var client = CreateClient(user.ClerkUserId);

        var profile = await GetProfile(client);
        var repeated = await GetProfile(client);

        Assert.Equal(user.Id, profile.Id);
        Assert.Equal(user.ClerkUserId, profile.ClerkUserId);
        Assert.Equal(preferences.TimeZone, profile.TimeZone);
        Assert.Equal(preferences.MeasurementSystem, profile.MeasurementSystem);
        Assert.Equal(profile, repeated);
        Assert.Equal(1, await db.Users.CountAsync(saved => saved.ClerkUserId == user.ClerkUserId));
    }

    [Theory]
    [InlineData("metric")]
    [InlineData("imperial")]
    public async Task Put_preferences_updates_both_fields_and_persists_them(string measurementSystem)
    {
        var preferences = new PreferencesRequestFaker()
            .UseSeed(2002)
            .RuleFor(request => request.MeasurementSystem, measurementSystem)
            .Generate();
        var user = new User
        {
            ClerkUserId = NewSubject(),
            TimeZone = "UTC",
            MeasurementSystem = measurementSystem == "metric" ? "imperial" : "metric"
        };
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KiloDbContext>();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        using var client = CreateClient(user.ClerkUserId);

        using var response = await client.PutAsJsonAsync("/api/v1/me/preferences", preferences);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<UserDto>();
        Assert.NotNull(updated);
        Assert.Equal(user.Id, updated.Id);
        Assert.Equal(user.ClerkUserId, updated.ClerkUserId);
        Assert.Equal(preferences.TimeZone, updated.TimeZone);
        Assert.Equal(preferences.MeasurementSystem, updated.MeasurementSystem);
        Assert.Equal(updated, await GetProfile(client));
        var saved = await db.Users.AsNoTracking().SingleAsync(saved => saved.Id == user.Id);
        Assert.Equal(preferences.TimeZone, saved.TimeZone);
        Assert.Equal(preferences.MeasurementSystem, saved.MeasurementSystem);
    }

    [Fact]
    public async Task Put_preferences_provisions_an_account_when_it_is_the_first_request()
    {
        var subject = NewSubject();
        var preferences = new PreferencesRequestFaker().UseSeed(3003).Generate();
        using var client = CreateClient(subject);

        using var response = await client.PutAsJsonAsync("/api/v1/me/preferences", preferences);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var profile = await response.Content.ReadFromJsonAsync<UserDto>();
        Assert.NotNull(profile);
        Assert.True(profile.Id > 0);
        Assert.Equal(subject, profile.ClerkUserId);
        Assert.Equal(preferences.TimeZone, profile.TimeZone);
        Assert.Equal(preferences.MeasurementSystem, profile.MeasurementSystem);
        Assert.Equal(profile, await GetProfile(client));

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KiloDbContext>();
        var saved = await db.Users.AsNoTracking().SingleAsync(user => user.ClerkUserId == subject);
        Assert.Equal(profile.Id, saved.Id);
        Assert.Equal(preferences.TimeZone, saved.TimeZone);
        Assert.Equal(preferences.MeasurementSystem, saved.MeasurementSystem);
    }

    [Theory]
    [InlineData(null, "metric")]
    [InlineData("", "metric")]
    [InlineData("Unknown/Timezone", "metric")]
    [InlineData("Eastern Standard Time", "metric")]
    [InlineData("America/Phoenix", null)]
    [InlineData("America/Phoenix", "unknown")]
    public async Task Invalid_preferences_return_422_and_preserve_the_profile(string? timeZone, string? measurementSystem)
    {
        using var client = CreateClient(NewSubject());
        var original = await GetProfile(client);
        using var response = await client.PutAsJsonAsync("/api/v1/me/preferences", new { timeZone, measurementSystem });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.NotEmpty(problem!.Errors);
        Assert.Equal(original, await GetProfile(client));
    }

    [Theory]
    [InlineData("{}", 422)]
    [InlineData("{\"timeZone\":null,\"measurementSystem\":null}", 422)]
    [InlineData("{\"timeZone\":\"  \",\"measurementSystem\":\"Metric\"}", 422)]
    [InlineData("{", 400)]
    [InlineData("", 400)]
    [InlineData("null", 400)]
    [InlineData("{\"timeZone\":42,\"measurementSystem\":\"metric\"}", 400)]
    public async Task Invalid_first_request_has_standard_problem_details_and_never_provisions(string json, int status)
    {
        var subject = NewSubject();
        using var client = CreateClient(subject);
        using var response = await client.PutAsync("/api/v1/me/preferences",
            new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var problem = (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>())!;
        Assert.Equal(status, problem.Status);
        Assert.Equal("One or more validation errors occurred.", problem.Title);
        Assert.NotEmpty(problem.Type!);
        Assert.True(problem.Extensions.ContainsKey("traceId"));
        if (status == 422)
        {
            Assert.Equal("https://tools.ietf.org/html/rfc4918#section-11.2", problem.Type);
            Assert.Equal("/api/v1/me/preferences", problem.Instance);
            Assert.Equal(new[] { "measurementSystem", "timeZone" }, problem.Errors.Keys.Order());
            Assert.All(problem.Errors.Values, messages => Assert.Single(messages));
        }
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KiloDbContext>();
        Assert.False(await db.Users.AnyAsync(user => user.ClerkUserId == subject));
    }

    [Fact]
    public async Task Preference_changes_remain_private_to_each_subject()
    {
        using var first = CreateClient(NewSubject());
        using var second = CreateClient(NewSubject());
        var firstProfile = await GetProfile(first);
        var secondProfile = await GetProfile(second);
        Assert.NotEqual(firstProfile.Id, secondProfile.Id);
        using var response = await first.PutAsJsonAsync("/api/v1/me/preferences",
            new { timeZone = "America/Phoenix", measurementSystem = "metric" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(secondProfile, await GetProfile(second));
        Assert.Equal("metric", (await GetProfile(first)).MeasurementSystem);
    }

    private HttpClient CreateClient(string subject)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(KiloApiFactory.SubjectHeader, subject);
        return client;
    }

    private static async Task<UserDto> GetProfile(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var profile = await response.Content.ReadFromJsonAsync<UserDto>();
        Assert.NotNull(profile);
        return profile;
    }

    private static string NewSubject() => "user_" + Guid.NewGuid().ToString("N")[..22];
}
