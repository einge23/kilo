using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Kilo.Features.Routines;
using Kilo.Persistence;
using Kilo.Persistence.Entities;
using Kilo.Persistence.Queries;
using Kilo.Tests.Fakers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Kilo.Tests.IntegrationTests;

public sealed class RoutinesControllerTests(KiloApiFactory factory) : IClassFixture<KiloApiFactory>
{
    private const string Route = "/api/v1/routines";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("First line\nO'Brien: 日本語")]
    public async Task Create_persists_owned_empty_routines_and_round_trips_location(string? description)
    {
        var subject = NewSubject();
        using var client = Client(subject);
        var request = new RoutineWriteRequestFaker().UseSeed(601)
            .RuleFor(x => x.Name, "  Upper A  ").RuleFor(x => x.Description, description).Generate();
        using var response = await client.PostAsJsonAsync(Route, request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await ReadRoutine(response);
        Assert.True(dto.Id > 0);
        Assert.Equal("Upper A", dto.Name);
        Assert.Equal(description ?? "", dto.Description);
        Assert.Null(dto.ArchivedAt);
        Assert.Equal($"{Route}/{dto.Id}", response.Headers.Location!.AbsolutePath);
        using var detail = await client.GetAsync(response.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.Equivalent(dto, await ReadRoutine(detail), strict: true);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KiloDbContext>();
        var saved = await db.Routines.AsNoTracking().SingleAsync(x => x.Id == dto.Id);
        Assert.Equal(await db.Users.Where(x => x.ClerkUserId == subject).Select(x => x.Id).SingleAsync(), saved.UserId);
        Assert.Equal(dto.Name, saved.Name);
        Assert.Equal(dto.Description, saved.Description);
        Assert.Equal(DateTimeKind.Utc, saved.CreatedAt.Kind);
        Assert.True(saved.CreatedAt > DateTime.UtcNow.AddMinutes(-2));
    }

    [Fact]
    public async Task List_orders_the_five_templates_and_filters_only_owned_archives()
    {
        using var owner = Client(NewSubject());
        using var foreign = Client(NewSubject());
        Assert.Empty((await owner.GetFromJsonAsync<RoutineSummaryDto[]>(Route))!);
        var active = new List<RoutineDto>();
        foreach (var name in new[] { "Upper A", "Lower A", "Abs and Arms", "Upper B", "Lower B", "Upper A" })
        {
            active.Add(await Create(owner, name));
        }
        Assert.Equal(active.Count, active.Select(x => x.Id).Distinct().Count());
        var archived = await Create(owner, "Archived");
        var hidden = await Create(foreign, "Foreign");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<KiloDbContext>().Routines.Where(x => x.Id == archived.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.ArchivedAt, DateTime.UtcNow));
        }
        var list = (await owner.GetFromJsonAsync<RoutineSummaryDto[]>(Route))!;
        Assert.Equal(active.OrderBy(x => x.Name, StringComparer.Ordinal).ThenBy(x => x.Id).Select(x => x.Id), list.Select(x => x.Id));
        var expanded = (await owner.GetFromJsonAsync<RoutineSummaryDto[]>(Route + "?includeArchived=true"))!;
        Assert.Equal(active.Append(archived).OrderBy(x => x.Name, StringComparer.Ordinal).ThenBy(x => x.Id).Select(x => x.Id),
            expanded.Select(x => x.Id));
        Assert.DoesNotContain(expanded, x => x.Id == hidden.Id);
        Assert.NotNull(expanded.Single(x => x.Id == archived.Id).ArchivedAt);
        Assert.Equal(new[] { hidden.Id }, (await foreign.GetFromJsonAsync<RoutineSummaryDto[]>(Route + "?includeArchived=true"))!.Select(x => x.Id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Changed\nDéveloppé O'Brien")]
    public async Task Update_replaces_metadata_without_changing_identity_or_creation(string? description)
    {
        using var client = Client(NewSubject());
        var original = await Create(client);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KiloDbContext>();
        var before = await db.Routines.AsNoTracking().SingleAsync(x => x.Id == original.Id);
        object request = description is null ? new { name = "  Updated  " } : new { name = "  Updated  ", description };
        using var response = await client.PutAsJsonAsync($"{Route}/{original.Id}", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await ReadRoutine(response);
        Assert.Equal(original.Id, updated.Id);
        Assert.Equal("Updated", updated.Name);
        Assert.Equal(description ?? "", updated.Description);
        using var detail = await client.GetAsync($"{Route}/{original.Id}");
        Assert.Equivalent(updated, await ReadRoutine(detail), strict: true);
        var after = await db.Routines.AsNoTracking().SingleAsync(x => x.Id == original.Id);
        Assert.Equal(before.UserId, after.UserId);
        Assert.Equal(before.CreatedAt, after.CreatedAt);
        Assert.Equal(before.ArchivedAt, after.ArchivedAt);
    }

    [Fact]
    public async Task Foreign_accounts_including_admins_cannot_read_or_edit_private_routines()
    {
        var subject = NewSubject();
        using var owner = Client(subject);
        using var foreign = Client(NewSubject());
        using var admin = Client(NewSubject(), "admin");
        var routine = await Create(owner);
        foreach (var caller in new[] { foreign, admin })
        {
            Assert.Empty((await caller.GetFromJsonAsync<RoutineSummaryDto[]>(Route + "?includeArchived=true"))!);
            foreach (var id in new[] { routine.Id, int.MaxValue })
            {
                using var get = await caller.GetAsync($"{Route}/{id}");
                using var put = await caller.PutAsJsonAsync($"{Route}/{id}", new { name = "Forbidden update" });
                Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
                Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
                Assert.Equal(404, (await put.Content.ReadFromJsonAsync<ProblemDetails>())!.Status);
            }
        }
        using var saved = await owner.GetAsync($"{Route}/{routine.Id}");
        Assert.Equivalent(routine, await ReadRoutine(saved), strict: true);
    }

    [Fact]
    public async Task Request_owner_scope_and_archive_fields_cannot_change_server_controlled_values()
    {
        var subject = NewSubject();
        using var client = Client(subject, "admin");
        using var response = await client.PostAsJsonAsync(Route, new
        {
            name = "Forged owner", userId = int.MaxValue, isGlobal = true,
            archivedAt = DateTime.UtcNow, createdAt = DateTime.UnixEpoch,
            exercises = new[] { new { exerciseId = int.MaxValue } }
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var routine = await ReadRoutine(response);
        Assert.Null(routine.ArchivedAt);
        using var update = await client.PutAsJsonAsync($"{Route}/{routine.Id}", new
        {
            name = "Still private", userId = int.MaxValue, archivedAt = DateTime.UtcNow
        });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KiloDbContext>();
        var saved = await db.Routines.AsNoTracking().SingleAsync(x => x.Id == routine.Id);
        Assert.Equal(await db.Users.Where(x => x.ClerkUserId == subject).Select(x => x.Id).SingleAsync(), saved.UserId);
        Assert.Null(saved.ArchivedAt);
        Assert.True(saved.CreatedAt > DateTime.UtcNow.AddMinutes(-2));
    }

    [Fact]
    public async Task Owned_archived_detail_is_readable_but_metadata_update_returns_409()
    {
        using var client = Client(NewSubject());
        var routine = await Create(client);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KiloDbContext>();
        await db.Routines.Where(x => x.Id == routine.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.ArchivedAt, DateTime.UtcNow));
        using var detail = await client.GetAsync($"{Route}/{routine.Id}");
        var archived = await ReadRoutine(detail);
        Assert.NotNull(archived.ArchivedAt);
        using var update = await client.PutAsJsonAsync($"{Route}/{routine.Id}", new { name = "Not saved" });
        Assert.Equal(HttpStatusCode.Conflict, update.StatusCode);
        Assert.Equal(409, (await update.Content.ReadFromJsonAsync<ProblemDetails>())!.Status);
        using var unchanged = await client.GetAsync($"{Route}/{routine.Id}");
        Assert.Equivalent(archived, await ReadRoutine(unchanged), strict: true);
    }

    [Theory]
    [InlineData("{}", 422)]
    [InlineData("{\"name\":null}", 422)]
    [InlineData("{\"name\":\"   \"}", 422)]
    [InlineData("{", 400)]
    [InlineData("{\"name\":42}", 400)]
    [InlineData("null", 400)]
    [InlineData("", 400)]
    public async Task Invalid_writes_return_standard_problems_and_never_provision_or_mutate(string json, int status)
    {
        var subject = NewSubject();
        using var client = Client(subject);
        using var post = await client.PostAsync(Route, new StringContent(json, Encoding.UTF8, "application/json"));
        await AssertValidation(post, status);
        using var firstPut = await client.PutAsync($"{Route}/{int.MaxValue}", new StringContent(json, Encoding.UTF8, "application/json"));
        await AssertValidation(firstPut, status);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KiloDbContext>();
        Assert.False(await db.Users.AnyAsync(x => x.ClerkUserId == subject));
        var original = await Create(client);
        using var put = await client.PutAsync($"{Route}/{original.Id}", new StringContent(json, Encoding.UTF8, "application/json"));
        await AssertValidation(put, status);
        using var unchanged = await client.GetAsync($"{Route}/{original.Id}");
        Assert.Equivalent(original, await ReadRoutine(unchanged), strict: true);
        var userId = await db.Users.Where(x => x.ClerkUserId == subject).Select(x => x.Id).SingleAsync();
        Assert.Single(await db.Routines.OwnedBy(userId).ToArrayAsync());
    }

    [Fact]
    public async Task Anonymous_requests_are_rejected_before_validation_or_writes()
    {
        using var client = factory.CreateClient();
        foreach (var route in new[] { Route, Route + "/1" })
        {
            using var response = await client.GetAsync(route);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        using var post = await client.PostAsJsonAsync(Route, new { name = "" });
        using var put = await client.PutAsJsonAsync(Route + "/1", new { name = "" });
        Assert.Equal(HttpStatusCode.Unauthorized, post.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, put.StatusCode);
    }

    [Fact]
    public async Task Invalid_routes_and_query_types_do_not_invoke_routine_actions()
    {
        var subject = NewSubject();
        using var client = Client(subject);
        foreach (var route in new[] { "/api/routines", "/api/v2/routines", Route + "/0", Route + "/-1", Route + "/1.5" })
        {
            using var response = await client.GetAsync(route);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        using var query = await client.GetAsync(Route + "?includeArchived=maybe");
        await AssertValidation(query, 400);
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<KiloDbContext>().Users.AnyAsync(x => x.ClerkUserId == subject));
    }

    [Fact]
    public async Task Ownership_query_filters_required_and_nullable_owners_in_postgres()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KiloDbContext>();
        var owner = new User { ClerkUserId = NewSubject() };
        var foreign = new User { ClerkUserId = NewSubject() };
        db.Users.AddRange(owner, foreign);
        await db.SaveChangesAsync();
        var own = new Routine { UserId = owner.Id, Name = "Owned" };
        var hidden = new Routine { UserId = foreign.Id, Name = "Hidden" };
        var custom = new Exercise { UserId = owner.Id, Name = "Owned" };
        var otherCustom = new Exercise { UserId = foreign.Id, Name = "Hidden" };
        var global = new Exercise { Name = "Global" };
        db.Routines.AddRange(own, hidden);
        db.Exercises.AddRange(custom, otherCustom, global);
        await db.SaveChangesAsync();
        var routines = await db.Routines.AsNoTracking().OwnedBy(owner.Id).Where(x => x.Name == "Owned")
            .Select(x => x.Id).ToArrayAsync();
        Assert.Equal(new[] { own.Id }, routines);
        Assert.Equal(new[] { custom.Id }, await db.Exercises.AsNoTracking().OwnedBy(owner.Id).Select(x => x.Id).ToArrayAsync());
        var tracked = await db.Routines.OwnedBy(owner.Id).SingleAsync(x => x.Id == own.Id);
        tracked.Description = "Scoped mutation";
        await db.SaveChangesAsync();
        Assert.Equal("", (await db.Routines.AsNoTracking().SingleAsync(x => x.Id == hidden.Id)).Description);
        Assert.Equal("Scoped mutation", (await db.Routines.AsNoTracking().SingleAsync(x => x.Id == own.Id)).Description);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Ownership_query_rejects_nonpositive_user_ids(int userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KiloDbContext>();
        Assert.Throws<ArgumentOutOfRangeException>(() => db.Routines.OwnedBy(userId));
        Assert.Throws<ArgumentOutOfRangeException>(() => db.Exercises.OwnedBy(userId));
    }

    [Theory]
    [InlineData("name", "routines_name_check")]
    [InlineData("owner", "routines_user_id_fkey")]
    public async Task Database_constraints_reject_invalid_routines(string variant, string constraint)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KiloDbContext>();
        var owner = new User { ClerkUserId = NewSubject() };
        db.Users.Add(owner);
        await db.SaveChangesAsync();
        var routine = new Routine { UserId = variant == "owner" ? int.MaxValue : owner.Id, Name = variant == "name" ? " " : "Valid" };
        db.Routines.Add(routine);
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(constraint, Assert.IsType<PostgresException>(exception.InnerException).ConstraintName);
        Assert.False(await db.Routines.AsNoTracking().AnyAsync(x => x.UserId == owner.Id));
    }

    private HttpClient Client(string subject, string? role = null)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(KiloApiFactory.SubjectHeader, subject);
        if (role is not null)
        {
            client.DefaultRequestHeaders.Add(KiloApiFactory.RoleHeader, role);
        }
        return client;
    }

    private static string NewSubject() => "user_" + Guid.NewGuid().ToString("N")[..22];

    private static async Task<RoutineDto> Create(HttpClient client, string name = "Fixture routine")
    {
        using var response = await client.PostAsJsonAsync(Route,
            new RoutineWriteRequestFaker().UseSeed(602).RuleFor(x => x.Name, name).Generate());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadRoutine(response);
    }

    private static async Task<RoutineDto> ReadRoutine(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(text);
        Assert.Equal(JsonValueKind.Array, json.RootElement.GetProperty("exercises").ValueKind);
        Assert.Equal(0, json.RootElement.GetProperty("exercises").GetArrayLength());
        Assert.False(json.RootElement.TryGetProperty("userId", out _));
        return JsonSerializer.Deserialize<RoutineDto>(text, JsonSerializerOptions.Web)!;
    }

    private static async Task AssertValidation(HttpResponseMessage response, int status)
    {
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var problem = (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>())!;
        Assert.Equal(status, problem.Status);
        Assert.Equal("One or more validation errors occurred.", problem.Title);
        Assert.True(problem.Extensions.ContainsKey("traceId"));
        Assert.NotEmpty(problem.Errors);
        if (status == 422)
        {
            Assert.Equal("https://tools.ietf.org/html/rfc4918#section-11.2", problem.Type);
            Assert.Equal(response.RequestMessage!.RequestUri!.AbsolutePath, problem.Instance);
            Assert.Equal(new[] { "name" }, problem.Errors.Keys);
        }
    }
}
