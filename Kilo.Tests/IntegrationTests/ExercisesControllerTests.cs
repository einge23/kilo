using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Kilo.Features.Exercises;
using Kilo.Features.Exercises.Requests;
using Kilo.Persistence;
using Kilo.Persistence.Entities;
using Kilo.Tests.Fakers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Kilo.Tests.IntegrationTests;

public sealed class ExercisesControllerTests(KiloApiFactory factory) : IClassFixture<KiloApiFactory>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        // Only this class's disposable database: globals otherwise leak between cases.
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<KiloDbContext>().Exercises.ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Create_round_trips_metadata_and_versioned_location(bool global)
    {
        using var client = Client(NewSubject(), "admin");
        var request = new ExerciseWriteRequestFaker().UseSeed(501)
            .RuleFor(x => x.Name, "  Développé O'Brien  ")
            .RuleFor(x => x.Description, "First line\nSecond line: 日本語")
            .RuleFor(x => x.BrandName, "  ACME  ").Generate();
        using var response = await client.PostAsJsonAsync(Route(global), request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = (await response.Content.ReadFromJsonAsync<ExerciseDto>())!;
        Assert.True(dto.Id > 0);
        Assert.Equal(request.Name.Trim(), dto.Name);
        Assert.Equal(request.Description, dto.Description);
        Assert.Equal("ACME", dto.BrandName);
        Assert.Equal(global, dto.IsGlobal);
        Assert.Null(dto.ArchivedAt);
        Assert.Equal($"/api/v1/exercises/{dto.Id}", response.Headers.Location!.AbsolutePath);
        Assert.Equal(dto, await client.GetFromJsonAsync<ExerciseDto>(response.Headers.Location));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(global, json.RootElement.GetProperty("isGlobal").GetBoolean());
        Assert.False(json.RootElement.TryGetProperty("userId", out _));

        await using var scope = factory.Services.CreateAsyncScope();
        var saved = await scope.ServiceProvider.GetRequiredService<KiloDbContext>().Exercises.AsNoTracking().SingleAsync();
        Assert.Equal(dto.Id, saved.Id);
        Assert.Equal(global, saved.UserId is null);
        Assert.Equal(DateTimeKind.Utc, saved.CreatedAt.Kind);
        Assert.True(saved.CreatedAt > DateTime.UtcNow.AddMinutes(-2));
    }

    [Fact]
    public async Task List_combines_globals_and_own_custom_entries_with_stable_order_and_archive_filter()
    {
        using var owner = Client(NewSubject());
        using var foreign = Client(NewSubject());
        using var admin = Client(NewSubject(), "admin");
        Assert.Empty((await owner.GetFromJsonAsync<ExerciseDto[]>(Route(false)))!);
        var shared = await Create(admin, true, "Alpha");
        var own1 = await Create(owner, false, "Beta");
        var own2 = await Create(owner, false, "Beta");
        var hidden = await Create(foreign, false, "Foreign");
        var archivedGlobal = await Create(admin, true, "Old global");
        var archivedOwn = await Create(owner, false, "Old own");
        var archivedForeign = await Create(foreign, false, "Old foreign");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KiloDbContext>();
            await db.Exercises.Where(x => new[] { archivedGlobal.Id, archivedOwn.Id, archivedForeign.Id }.Contains(x.Id))
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.ArchivedAt, DateTime.UtcNow));
        }
        var visible = (await owner.GetFromJsonAsync<ExerciseDto[]>(Route(false)))!;
        Assert.Equal(new[] { shared.Id, own1.Id, own2.Id }, visible.Select(x => x.Id));
        var expanded = (await owner.GetFromJsonAsync<ExerciseDto[]>(Route(false) + "?includeArchived=true"))!;
        Assert.Equal(new[] { shared.Id, own1.Id, own2.Id, archivedGlobal.Id, archivedOwn.Id }, expanded.Select(x => x.Id));
        Assert.DoesNotContain(expanded, x => x.Id == hidden.Id || x.Id == archivedForeign.Id);
        Assert.NotNull((await owner.GetFromJsonAsync<ExerciseDto>($"{Route(false)}/{archivedGlobal.Id}"))!.ArchivedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Update_replaces_metadata_clears_omitted_brand_and_preserves_scope(bool global)
    {
        using var client = Client(NewSubject(), "admin");
        var created = await Create(client, global);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KiloDbContext>();
        var before = await db.Exercises.AsNoTracking().SingleAsync(x => x.Id == created.Id);
        using var response = await client.PutAsJsonAsync($"{Route(global)}/{created.Id}", new { name = "  Updated  " });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = (await response.Content.ReadFromJsonAsync<ExerciseDto>())!;
        Assert.Equal(created.Id, updated.Id);
        Assert.Equal("Updated", updated.Name);
        Assert.Equal("", updated.Description);
        Assert.Null(updated.BrandName);
        Assert.Equal(global, updated.IsGlobal);
        Assert.Equal(updated, await client.GetFromJsonAsync<ExerciseDto>($"{Route(false)}/{created.Id}"));
        var after = await db.Exercises.AsNoTracking().SingleAsync(x => x.Id == created.Id);
        Assert.Equal(before.UserId, after.UserId);
        Assert.Equal(before.CreatedAt, after.CreatedAt);
        Assert.Equal(before.ArchivedAt, after.ArchivedAt);
    }

    [Fact]
    public async Task Admin_and_owner_permissions_never_unlock_foreign_custom_entries()
    {
        using var owner = Client(NewSubject());
        using var foreign = Client(NewSubject());
        using var admin = Client(NewSubject(), "admin");
        var own = await Create(owner, false);
        var shared = await Create(admin, true);
        var adminCustom = await Create(admin, false);
        Assert.False(adminCustom.IsGlobal);
        foreach (var caller in new[] { foreign, admin })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await caller.GetAsync($"{Route(false)}/{own.Id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await caller.PutAsJsonAsync($"{Route(false)}/{own.Id}", Write())).StatusCode);
        }
        foreach (var caller in new[] { owner, admin })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await caller.PutAsJsonAsync($"{Route(false)}/{shared.Id}", Write())).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PutAsJsonAsync($"{Route(true)}/{shared.Id}", Write())).StatusCode);
        foreach (var id in new[] { own.Id, adminCustom.Id, int.MaxValue })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync($"{Route(true)}/{id}", Write())).StatusCode);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"{Route(false)}/{int.MaxValue}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PutAsJsonAsync($"{Route(false)}/{int.MaxValue}", Write())).StatusCode);
        Assert.Equal(own, await owner.GetFromJsonAsync<ExerciseDto>($"{Route(false)}/{own.Id}"));
        Assert.Equal(shared, await owner.GetFromJsonAsync<ExerciseDto>($"{Route(false)}/{shared.Id}"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Request_fields_cannot_set_owner_or_scope(bool global)
    {
        var subject = NewSubject();
        using var client = Client(subject, "admin");
        using var response = await client.PostAsJsonAsync(Route(global), new
        {
            name = "Forged scope", userId = int.MaxValue, isGlobal = !global, role = "admin"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = (await response.Content.ReadFromJsonAsync<ExerciseDto>())!;
        Assert.Equal(global, dto.IsGlobal);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KiloDbContext>();
        var saved = await db.Exercises.AsNoTracking().SingleAsync(x => x.Id == dto.Id);
        Assert.Equal(global ? (int?)null : await db.Users.Where(x => x.ClerkUserId == subject).Select(x => x.Id).SingleAsync(), saved.UserId);
        using var update = await client.PutAsJsonAsync($"{Route(global)}/{dto.Id}", new
        {
            name = "Still same scope", userId = int.MaxValue, isGlobal = !global
        });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal(saved.UserId, (await db.Exercises.AsNoTracking().SingleAsync(x => x.Id == dto.Id)).UserId);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, "  ")]
    [InlineData(true, "  ")]
    [InlineData(false, "  ACME  ")]
    [InlineData(true, "  ACME  ")]
    [InlineData(false, "100")]
    [InlineData(true, "100")]
    [InlineData(false, "101")]
    [InlineData(true, "101")]
    public async Task Both_write_routes_enforce_trimmed_brand_boundaries(bool global, string? brand)
    {
        using var client = Client(NewSubject(), "admin");
        brand = brand is "100" or "101" ? "  " + new string('x', int.Parse(brand)) + "  " : brand;
        var invalid = brand?.Trim().Length > 100;
        var request = new ExerciseWriteRequest { Name = "Brand boundary", BrandName = brand };
        using var post = await client.PostAsJsonAsync(Route(global), request);
        if (invalid)
        {
            await AssertValidation(post);
            await using var scope = factory.Services.CreateAsyncScope();
            Assert.Empty(await scope.ServiceProvider.GetRequiredService<KiloDbContext>().Exercises.ToArrayAsync());
        }
        else
        {
            Assert.Equal(HttpStatusCode.Created, post.StatusCode);
            var dto = (await post.Content.ReadFromJsonAsync<ExerciseDto>())!;
            Assert.Equal(string.IsNullOrWhiteSpace(brand) ? null : brand.Trim(), dto.BrandName);
            Assert.Equal("", dto.Description);
        }
        var created = await Create(client, global);
        using var put = await client.PutAsJsonAsync($"{Route(global)}/{created.Id}", request);
        if (invalid)
        {
            await AssertValidation(put);
            Assert.Equal(created, await client.GetFromJsonAsync<ExerciseDto>($"{Route(false)}/{created.Id}"));
        }
        else
        {
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
            Assert.Equal(string.IsNullOrWhiteSpace(brand) ? null : brand.Trim(), (await put.Content.ReadFromJsonAsync<ExerciseDto>())!.BrandName);
        }
    }

    [Theory]
    [InlineData(false, "{}")]
    [InlineData(true, "{}")]
    [InlineData(false, "{\"name\":\"   \"}")]
    [InlineData(true, "{\"name\":\"   \"}")]
    [InlineData(false, "{\"name\":null}")]
    [InlineData(true, "{\"name\":null}")]
    [InlineData(false, "{")]
    [InlineData(true, "{")]
    [InlineData(false, "null")]
    [InlineData(true, "null")]
    [InlineData(false, "")]
    [InlineData(true, "")]
    [InlineData(false, "{\"name\":42}")]
    [InlineData(true, "{\"name\":42}")]
    public async Task Invalid_requests_return_native_validation_problems_without_writes(bool global, string json)
    {
        var subject = NewSubject();
        using var client = Client(subject, "admin");
        using var post = await client.PostAsync(Route(global), new StringContent(json, Encoding.UTF8, "application/json"));
        var status = json is "{" or "{\"name\":42}" or "null" or ""
            ? HttpStatusCode.BadRequest : HttpStatusCode.UnprocessableEntity;
        await AssertValidation(post, status);
        await using (var rejectedScope = factory.Services.CreateAsyncScope())
        {
            var db = rejectedScope.ServiceProvider.GetRequiredService<KiloDbContext>();
            Assert.False(await db.Users.AnyAsync(user => user.ClerkUserId == subject));
            Assert.False(await db.Exercises.AnyAsync());
        }
        var created = await Create(client, global);
        using var put = await client.PutAsync($"{Route(global)}/{created.Id}", new StringContent(json, Encoding.UTF8, "application/json"));
        await AssertValidation(put, status);
        Assert.Equal(created, await client.GetFromJsonAsync<ExerciseDto>($"{Route(false)}/{created.Id}"));
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.Single(await scope.ServiceProvider.GetRequiredService<KiloDbContext>().Exercises.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Archived_metadata_is_readable_but_cannot_be_updated(bool global)
    {
        using var client = Client(NewSubject(), "admin");
        var dto = await Create(client, global);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KiloDbContext>();
        await db.Exercises.Where(x => x.Id == dto.Id).ExecuteUpdateAsync(set => set.SetProperty(x => x.ArchivedAt, DateTime.UtcNow));
        var archived = (await client.GetFromJsonAsync<ExerciseDto>($"{Route(false)}/{dto.Id}"))!;
        Assert.NotNull(archived.ArchivedAt);
        using var response = await client.PutAsJsonAsync($"{Route(global)}/{dto.Id}", Write());
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(409, (await response.Content.ReadFromJsonAsync<ProblemDetails>())!.Status);
        Assert.Equal(archived, await client.GetFromJsonAsync<ExerciseDto>($"{Route(false)}/{dto.Id}"));
    }

    [Fact]
    public async Task Version_route_and_query_validation_do_not_invoke_wrong_endpoints()
    {
        using var client = Client(NewSubject(), "admin");
        foreach (var route in new[] { "/api/exercises", "/api/v2/exercises", "/api/v1/exercises/0", "/api/v1/exercises/-1", "/api/v1/exercises/1.5" })
        {
            using var response = await client.GetAsync(route);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{route}: {response.StatusCode}");
        }
        using var invalidAdminId = await client.PutAsJsonAsync(Route(true) + "/0", Write());
        Assert.Equal(HttpStatusCode.NotFound, invalidAdminId.StatusCode);
        using var invalid = await client.GetAsync(Route(false) + "?includeArchived=maybe");
        await AssertValidation(invalid, HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_name_and_brand_report_all_errors_with_wire_property_names(bool global)
    {
        using var client = Client(NewSubject(), "admin");
        using var response = await client.PostAsJsonAsync(Route(global),
            new { name = " ", brandName = new string('x', 101) });
        var problem = await AssertValidation(response);
        Assert.Equal(new[] { "brandName", "name" }, problem.Errors.Keys.Order());
        Assert.All(problem.Errors.Values, messages => Assert.Single(messages));
    }

    [Theory]
    [InlineData(false, 401)]
    [InlineData(true, 403)]
    public async Task Authorization_precedes_request_validation(bool authenticated, int status)
    {
        using var client = authenticated ? Client(NewSubject()) : factory.CreateClient();
        using var response = await client.PostAsJsonAsync(Route(true), new { name = "" });
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<KiloDbContext>().Exercises.AnyAsync());
    }

    [Theory]
    [InlineData("name", "exercises_name_check")]
    [InlineData("brand", "exercises_brand_name_check")]
    [InlineData("owner", "exercises_user_id_fkey")]
    [InlineData("positive-owner", "exercises_user_id_check")]
    public async Task Database_constraints_reject_invalid_rows(string variant, string constraint)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KiloDbContext>();
        db.Exercises.Add(new Exercise
        {
            Name = variant == "name" ? " " : "Valid",
            BrandName = variant == "brand" ? new string('x', 101) : null,
            UserId = variant == "owner" ? int.MaxValue : variant == "positive-owner" ? 0 : null
        });
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(constraint, Assert.IsType<PostgresException>(exception.InnerException).ConstraintName);
        Assert.Empty(await db.Exercises.AsNoTracking().ToArrayAsync());
    }

    private HttpClient Client(string subject, string? role = null)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(KiloApiFactory.SubjectHeader, subject);
        if (role is not null) client.DefaultRequestHeaders.Add(KiloApiFactory.RoleHeader, role);
        return client;
    }

    private static ExerciseWriteRequest Write() => new ExerciseWriteRequestFaker().UseSeed(505).Generate();
    private static string NewSubject() => "user_" + Guid.NewGuid().ToString("N")[..22];
    private static string Route(bool global) => global ? "/api/v1/admin/exercises" : "/api/v1/exercises";
    private static async Task<ExerciseDto> Create(HttpClient client, bool global, string? name = null)
    {
        using var response = await client.PostAsJsonAsync(Route(global), new ExerciseWriteRequestFaker().UseSeed(502)
            .RuleFor(x => x.Name, name ?? "Fixture exercise").Generate());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ExerciseDto>())!;
    }

    private static async Task<ValidationProblemDetails> AssertValidation(HttpResponseMessage response,
        HttpStatusCode status = HttpStatusCode.UnprocessableEntity)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var problem = (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>())!;
        Assert.Equal((int)status, problem.Status);
        Assert.Equal("One or more validation errors occurred.", problem.Title);
        Assert.NotEmpty(problem.Type!);
        Assert.True(problem.Extensions.ContainsKey("traceId"));
        if (status == HttpStatusCode.UnprocessableEntity)
        {
            Assert.Equal("https://tools.ietf.org/html/rfc4918#section-11.2", problem.Type);
            Assert.Equal(response.RequestMessage!.RequestUri!.AbsolutePath, problem.Instance);
        }
        Assert.NotEmpty(problem.Errors);
        return problem;
    }
}
