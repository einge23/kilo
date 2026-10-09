using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Kilo.Features.Routines;
using Kilo.Persistence;
using Kilo.Persistence.Entities;
using Kilo.Tests.Fakers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Kilo.Tests.IntegrationTests;

public sealed class RoutinePlacementsControllerTests(KiloApiFactory factory) : IClassFixture<KiloApiFactory>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Repeated_global_or_owned_exercises_have_distinct_ordered_placements(bool global)
    {
        var fixture = await Seed(global);
        using var client = Client(fixture.Subject);
        var created = new List<RoutineExerciseDto>();
        foreach (var position in new[] { 3, 1, 2 })
        {
            var request = new PlacementCreateRequestFaker().UseSeed(701)
                .RuleFor(x => x.ExerciseId, fixture.ExerciseId).RuleFor(x => x.Position, position)
                .RuleFor(x => x.DefaultRestSeconds, position == 1 ? 0 : 120)
                .RuleFor(x => x.Description, $"Instruction {position}\nDéveloppé O'Brien").Generate();
            using var response = await client.PostAsJsonAsync(Route(fixture.RoutineId), request);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var dto = await ReadPlacement(response);
            created.Add(dto);
            Assert.Equal($"/api/v1/routines/{fixture.RoutineId}", response.Headers.Location!.AbsolutePath);
            using var located = await client.GetAsync(response.Headers.Location);
            Assert.Equal(HttpStatusCode.OK, located.StatusCode);
            Assert.Equal(request.Description, dto.Description);
            Assert.Equal(request.DefaultRestSeconds, dto.DefaultRestSeconds);
        }
        Assert.Equal(3, created.Select(x => x.Id).Distinct().Count());
        Assert.All(created, x => Assert.True(x.Id > 0));
        var detail = (await client.GetFromJsonAsync<RoutineDto>($"/api/v1/routines/{fixture.RoutineId}"))!;
        Assert.Equal(created.OrderBy(x => x.Position), detail.Exercises);
        Assert.All(detail.Exercises, x =>
        {
            Assert.Equal(fixture.ExerciseId, x.ExerciseId);
            Assert.Equal("Bench Press", x.ExerciseName);
            Assert.Equal("ACME", x.BrandName);
            Assert.Empty(x.Sets);
        });
        await using var scope = factory.Services.CreateAsyncScope();
        var rows = await Db(scope).RoutineExercises.AsNoTracking().Where(x => x.RoutineId == fixture.RoutineId).ToArrayAsync();
        Assert.All(rows, x => Assert.Equal(global ? 0 : fixture.UserId, x.ExerciseScopeId));
        Assert.All(rows, x => Assert.Equal(fixture.UserId, x.UserId));
        Assert.Equal(0, rows.Single(x => x.Position == 1).DefaultRestSeconds);
    }

    [Fact]
    public async Task Updates_replace_metadata_keep_identity_and_show_current_library_names_and_brands()
    {
        var fixture = await Seed(global: true);
        using var client = Client(fixture.Subject);
        var first = await Create(client, fixture, 1);
        var second = await Create(client, fixture, 2);
        var request = new PlacementUpdateRequestFaker().UseSeed(702)
            .RuleFor(x => x.Position, 4).RuleFor(x => x.Description, "Changed instructions").RuleFor(x => x.DefaultRestSeconds, 0).Generate();
        using var update = await client.PutAsJsonAsync(Route(fixture.RoutineId) + $"/{first.Id}", request);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var changed = await ReadPlacement(update);
        Assert.Equal(first.Id, changed.Id);
        Assert.Equal(first.ExerciseId, changed.ExerciseId);
        Assert.Equal(request.Description, changed.Description);
        Assert.Equal(0, changed.DefaultRestSeconds);
        using var cleared = await client.PutAsJsonAsync(Route(fixture.RoutineId) + $"/{first.Id}", new
        {
            position = 4, defaultRestSeconds = 0, exerciseId = int.MaxValue, userId = int.MaxValue,
            routineId = int.MaxValue, exerciseScopeId = int.MaxValue, archivedAt = DateTime.UtcNow
        });
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Equal("", (await ReadPlacement(cleared)).Description);

        using var admin = Client(fixture.Subject, "admin");
        using var library = await admin.PutAsJsonAsync($"/api/v1/admin/exercises/{fixture.ExerciseId}",
            new { name = "Current bench", description = "Library instructions", brandName = "Updated brand" });
        Assert.Equal(HttpStatusCode.OK, library.StatusCode);
        using var metadata = await client.PutAsJsonAsync($"/api/v1/routines/{fixture.RoutineId}", new { name = "Updated routine" });
        var detail = (await metadata.Content.ReadFromJsonAsync<RoutineDto>())!;
        Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);
        Assert.Equal(new[] { second.Id, first.Id }, detail.Exercises.Select(x => x.Id));
        Assert.All(detail.Exercises, x => Assert.Equal("Updated brand", x.BrandName));
        Assert.All(detail.Exercises, x => Assert.Equal("Current bench", x.ExerciseName));
        Assert.Equal("", detail.Exercises.Single(x => x.Id == first.Id).Description);
        await using var scope = factory.Services.CreateAsyncScope();
        var saved = await Db(scope).RoutineExercises.AsNoTracking().SingleAsync(x => x.Id == first.Id);
        Assert.Equal(fixture.RoutineId, saved.RoutineId);
        Assert.Equal(fixture.UserId, saved.UserId);
        Assert.Equal(0, saved.ExerciseScopeId);
        Assert.Equal(fixture.ExerciseId, saved.ExerciseId);
        Assert.Null(saved.ArchivedAt);
    }

    [Fact]
    public async Task Foreign_accounts_and_admins_cannot_bypass_parent_or_exercise_ownership()
    {
        var own = await Seed();
        var foreign = await Seed();
        var shared = await Seed(global: true);
        using var owner = Client(own.Subject);
        var placement = await Create(owner, own, 1);
        foreach (var role in new string?[] { null, "admin" })
        {
            using var caller = Client(foreign.Subject, role);
            using var wrongRoutine = await caller.PostAsJsonAsync(Route(own.RoutineId), Body(shared.ExerciseId, 1));
            using var wrongExercise = await caller.PostAsJsonAsync(Route(foreign.RoutineId), Body(own.ExerciseId, 1));
            using var wrongPlacement = await caller.PutAsJsonAsync(Route(foreign.RoutineId) + $"/{placement.Id}", Body(own.ExerciseId, 1));
            Assert.Equal(HttpStatusCode.NotFound, wrongRoutine.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, wrongExercise.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, wrongPlacement.StatusCode);
        }
        await using var scope = factory.Services.CreateAsyncScope();
        var otherRoutine = new Routine { UserId = own.UserId, Name = "Another owned parent" };
        Db(scope).Routines.Add(otherRoutine);
        await Db(scope).SaveChangesAsync();
        using var wrongParent = await owner.PutAsJsonAsync(Route(otherRoutine.Id) + $"/{placement.Id}", Body(own.ExerciseId, 1));
        using var missing = await owner.PutAsJsonAsync(Route(own.RoutineId) + "/2147483647", Body(own.ExerciseId, 1));
        Assert.Equal(HttpStatusCode.NotFound, wrongParent.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var globalCaller = Client(foreign.Subject);
        var global = await Create(globalCaller, foreign with { ExerciseId = shared.ExerciseId }, 1);
        Assert.Equal(shared.ExerciseId, global.ExerciseId);
    }

    [Theory]
    [InlineData("routine")]
    [InlineData("global-exercise")]
    [InlineData("custom-exercise")]
    [InlineData("placement")]
    public async Task Archived_write_targets_return_409_without_mutating(string target)
    {
        var fixture = await Seed(global: target == "global-exercise");
        using var client = Client(fixture.Subject);
        var placement = await Create(client, fixture, 1);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = Db(scope);
        if (target == "routine")
            await db.Routines.Where(x => x.Id == fixture.RoutineId).ExecuteUpdateAsync(s => s.SetProperty(x => x.ArchivedAt, DateTime.UtcNow));
        else if (target.EndsWith("exercise", StringComparison.Ordinal))
            await db.Exercises.Where(x => x.Id == fixture.ExerciseId).ExecuteUpdateAsync(s => s.SetProperty(x => x.ArchivedAt, DateTime.UtcNow));
        else
            await db.RoutineExercises.Where(x => x.Id == placement.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.ArchivedAt, DateTime.UtcNow));
        using var response = target == "placement"
            ? await client.PutAsJsonAsync(Route(fixture.RoutineId) + $"/{placement.Id}", Body(fixture.ExerciseId, 2))
            : await client.PostAsJsonAsync(Route(fixture.RoutineId), Body(fixture.ExerciseId, 2));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(409, (await response.Content.ReadFromJsonAsync<ProblemDetails>())!.Status);
        Assert.Single(await db.RoutineExercises.Where(x => x.RoutineId == fixture.RoutineId).ToArrayAsync());
        Assert.Equal(1, (await db.RoutineExercises.AsNoTracking().SingleAsync(x => x.Id == placement.Id)).Position);
    }

    [Fact]
    public async Task Position_conflicts_roll_back_updates_and_archived_positions_can_be_reused()
    {
        var fixture = await Seed();
        using var client = Client(fixture.Subject);
        var first = await Create(client, fixture, 1);
        var second = await Create(client, fixture, 2);
        using var post = await client.PostAsJsonAsync(Route(fixture.RoutineId), Body(fixture.ExerciseId, 1));
        using var put = await client.PutAsJsonAsync(Route(fixture.RoutineId) + $"/{second.Id}", new { position = 1, defaultRestSeconds = 0, description = "Rejected" });
        Assert.Equal(HttpStatusCode.Conflict, post.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
        var detail = (await client.GetFromJsonAsync<RoutineDto>($"/api/v1/routines/{fixture.RoutineId}"))!;
        Assert.Equal(new[] { first, second }, detail.Exercises);
        await using var scope = factory.Services.CreateAsyncScope();
        await Db(scope).RoutineExercises.Where(x => x.Id == first.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.ArchivedAt, DateTime.UtcNow));
        var replacement = await Create(client, fixture, 1);
        Assert.NotEqual(first.Id, replacement.Id);
        detail = (await client.GetFromJsonAsync<RoutineDto>($"/api/v1/routines/{fixture.RoutineId}"))!;
        Assert.Equal(new[] { replacement.Id, second.Id }, detail.Exercises.Select(x => x.Id));
        Assert.Equal(3, await Db(scope).RoutineExercises.CountAsync(x => x.RoutineId == fixture.RoutineId));
    }

    [Theory]
    [InlineData("{}", 422)]
    [InlineData("{\"exerciseId\":1,\"position\":null,\"defaultRestSeconds\":null}", 422)]
    [InlineData("{\"exerciseId\":1,\"position\":0,\"defaultRestSeconds\":-1}", 422)]
    [InlineData("{\"exerciseId\":1,\"position\":-1,\"defaultRestSeconds\":0}", 422)]
    [InlineData("{\"exerciseId\":1,\"position\":1}", 422)]
    [InlineData("{\"exerciseId\":1,\"position\":1.5,\"defaultRestSeconds\":0}", 400)]
    [InlineData("{\"exerciseId\":1,\"position\":1,\"defaultRestSeconds\":1.5}", 400)]
    [InlineData("{\"exerciseId\":1,\"position\":2147483648,\"defaultRestSeconds\":0}", 400)]
    [InlineData("{", 400)]
    [InlineData("null", 400)]
    [InlineData("", 400)]
    public async Task Invalid_requests_do_not_provision_or_change_existing_placements(string json, int status)
    {
        var subject = NewSubject();
        using var first = Client(subject);
        using var post = await first.PostAsync(Route(int.MaxValue), Json(json));
        using var put = await first.PutAsync(Route(int.MaxValue) + "/1", Json(json));
        await AssertValidation(post, status);
        await AssertValidation(put, status);
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.False(await Db(scope).Users.AnyAsync(x => x.ClerkUserId == subject));
        var fixture = await Seed();
        using var owner = Client(fixture.Subject);
        var original = await Create(owner, fixture, 1);
        using var failed = await owner.PutAsync(Route(fixture.RoutineId) + $"/{original.Id}", Json(json));
        await AssertValidation(failed, status);
        var detail = (await owner.GetFromJsonAsync<RoutineDto>($"/api/v1/routines/{fixture.RoutineId}"))!;
        Assert.Equal(original, Assert.Single(detail.Exercises));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Creation_requires_a_positive_library_id(int? exerciseId)
    {
        var subject = NewSubject();
        using var client = Client(subject);
        using var response = await client.PostAsJsonAsync(Route(int.MaxValue), Body(exerciseId, 1));
        var problem = await AssertValidation(response, 422);
        Assert.Equal(new[] { "exerciseId" }, problem.Errors.Keys);
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.False(await Db(scope).Users.AnyAsync(x => x.ClerkUserId == subject));
    }

    [Fact]
    public async Task Anonymous_and_invalid_routes_do_not_reach_placement_writes()
    {
        using var anonymous = factory.CreateClient();
        using var post = await anonymous.PostAsJsonAsync(Route(1), new { });
        using var put = await anonymous.PutAsJsonAsync(Route(1) + "/1", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, post.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, put.StatusCode);
        var subject = NewSubject();
        using var caller = Client(subject);
        foreach (var route in new[] { "/api/routines/1/exercises", "/api/v2/routines/1/exercises", Route(0), Route(-1) })
        {
            using var response = await caller.PostAsJsonAsync(route, Body(1, 1));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        foreach (var route in new[] { Route(1) + "/0", Route(1) + "/1.5" })
        {
            using var response = await caller.PutAsJsonAsync(route, Body(1, 1));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.False(await Db(scope).Users.AnyAsync(x => x.ClerkUserId == subject));
    }

    [Fact]
    public async Task Concurrent_inserts_at_one_position_commit_only_one_placement()
    {
        var fixture = await Seed();
        using var first = Client(fixture.Subject);
        using var second = Client(fixture.Subject);
        var results = await Task.WhenAll(first.PostAsJsonAsync(Route(fixture.RoutineId), Body(fixture.ExerciseId, 1)),
            second.PostAsJsonAsync(Route(fixture.RoutineId), Body(fixture.ExerciseId, 1)));
        try
        {
            Assert.Equal(new[] { HttpStatusCode.Created, HttpStatusCode.Conflict }, results.Select(x => x.StatusCode).Order());
            await using var scope = factory.Services.CreateAsyncScope();
            Assert.Single(await Db(scope).RoutineExercises.Where(x => x.RoutineId == fixture.RoutineId).ToArrayAsync());
        }
        finally { foreach (var result in results) result.Dispose(); }
    }

    [Theory]
    [InlineData("routines")]
    [InlineData("exercises")]
    public async Task Attach_waits_for_the_row_lock_and_observes_committed_archive_state(string table)
    {
        var fixture = await Seed(global: true);
        using var client = Client(fixture.Subject);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = Db(scope);
        await using var transaction = await db.Database.BeginTransactionAsync();
        if (table == "routines")
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE public.routines SET archived_at = now() WHERE id = {fixture.RoutineId}");
        else
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE public.exercises SET archived_at = now() WHERE id = {fixture.ExerciseId}");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = client.PostAsJsonAsync(Route(fixture.RoutineId), Body(fixture.ExerciseId, 1), deadline.Token);
        await WaitForBlockedWrite(table, deadline.Token);
        Assert.False(pending.IsCompleted);
        await transaction.CommitAsync(deadline.Token);
        using var response = await pending;
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.False(await db.RoutineExercises.AnyAsync(x => x.RoutineId == fixture.RoutineId));
    }

    [Fact]
    public async Task Cancellation_during_the_exercise_lock_rolls_back_and_releases_the_routine_lock()
    {
        var fixture = await Seed();
        await using var blocking = factory.Services.CreateAsyncScope();
        await using var transaction = await Db(blocking).Database.BeginTransactionAsync();
        await Db(blocking).Database.ExecuteSqlInterpolatedAsync($"UPDATE public.exercises SET name = name WHERE id = {fixture.ExerciseId}");
        await using var request = factory.Services.CreateAsyncScope();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = request.ServiceProvider.GetRequiredService<RoutinePlacementService>()
            .WriteAsync(fixture.UserId, fixture.RoutineId, new PlacementWrite
            {
                ExerciseId = fixture.ExerciseId,
                Position = 1,
                Description = "Cancelled",
                DefaultRestSeconds = 0
            }, cancellation.Token);
        await WaitForBlockedWrite("exercises", cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        await transaction.CommitAsync();
        using var client = Client(fixture.Subject);
        var created = await Create(client, fixture, 1);
        Assert.NotEqual("Cancelled", created.Description);
        Assert.Single(await Db(blocking).RoutineExercises.Where(x => x.RoutineId == fixture.RoutineId).ToArrayAsync());
    }

    [Theory]
    [InlineData("position", "routine_exercises_position_check")]
    [InlineData("rest", "routine_exercises_default_rest_seconds_check")]
    [InlineData("parent", "routine_exercises_routine_owner_fkey")]
    [InlineData("scope", "routine_exercises_exercise_scope_check")]
    [InlineData("forged-global", "routine_exercises_exercise_scope_fkey")]
    public async Task Database_rejects_invalid_bounds_or_cross_account_relationships(string variant, string constraint)
    {
        var own = await Seed();
        var foreign = await Seed();
        await using var scope = factory.Services.CreateAsyncScope();
        var row = new RoutineExercise
        {
            UserId = own.UserId, RoutineId = variant == "parent" ? foreign.RoutineId : own.RoutineId,
            ExerciseId = variant == "scope" ? foreign.ExerciseId : own.ExerciseId,
            ExerciseScopeId = variant == "scope" ? foreign.UserId : variant == "forged-global" ? 0 : own.UserId,
            Position = variant == "position" ? 0 : 1, DefaultRestSeconds = variant == "rest" ? -1 : 0
        };
        Db(scope).RoutineExercises.Add(row);
        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => Db(scope).SaveChangesAsync());
        Assert.Equal(constraint, Assert.IsType<PostgresException>(failure.InnerException).ConstraintName);
        Assert.False(await Db(scope).RoutineExercises.AsNoTracking().AnyAsync(x => x.RoutineId == own.RoutineId));
    }

    [Fact]
    public async Task Unrelated_database_failure_stays_sanitized_500_and_rolls_back()
    {
        var fixture = await Seed();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = Db(scope);
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE public.routine_exercises ADD CONSTRAINT placement_test_failure CHECK (description <> 'failure-fixture')");
        try
        {
            using var client = Client(fixture.Subject);
            using var response = await client.PostAsJsonAsync(Route(fixture.RoutineId), new
            {
                exerciseId = fixture.ExerciseId, position = 1, description = "failure-fixture", defaultRestSeconds = 0
            });
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Equal(500, (await response.Content.ReadFromJsonAsync<ProblemDetails>())!.Status);
            Assert.DoesNotContain("placement_test_failure", await response.Content.ReadAsStringAsync());
            Assert.False(await db.RoutineExercises.AnyAsync(x => x.RoutineId == fixture.RoutineId));
        }
        finally { await db.Database.ExecuteSqlRawAsync("ALTER TABLE public.routine_exercises DROP CONSTRAINT placement_test_failure"); }
    }

    private async Task WaitForBlockedWrite(string table, CancellationToken cancellationToken)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var pattern = "%public." + table + "%";
        while (await Db(scope).Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM pg_stat_activity
            WHERE datname = current_database() AND wait_event_type = 'Lock' AND query LIKE {pattern}
            """).SingleAsync(cancellationToken) == 0)
            await Task.Delay(25, cancellationToken);
    }

    private async Task<Fixture> Seed(bool global = false)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = Db(scope);
        var subject = NewSubject();
        var user = new User { ClerkUserId = subject };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var routine = new Routine { UserId = user.Id, Name = "Upper A" };
        var exercise = new Exercise { UserId = global ? null : user.Id, Name = "Bench Press", Description = "Library description", BrandName = "ACME" };
        db.Routines.Add(routine);
        db.Exercises.Add(exercise);
        await db.SaveChangesAsync();
        return new(subject, user.Id, routine.Id, exercise.Id);
    }

    private HttpClient Client(string subject, string? role = null)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(KiloApiFactory.SubjectHeader, subject);
        if (role is not null) client.DefaultRequestHeaders.Add(KiloApiFactory.RoleHeader, role);
        return client;
    }

    private static async Task<RoutineExerciseDto> Create(HttpClient client, Fixture fixture, int position)
    {
        using var response = await client.PostAsJsonAsync(Route(fixture.RoutineId), Body(fixture.ExerciseId, position));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadPlacement(response);
    }

    private static object Body(int? exerciseId, int position) => new { exerciseId, position, description = "Placement description", defaultRestSeconds = 120 };
    private static string Route(int id) => $"/api/v1/routines/{id}/exercises";
    private static string NewSubject() => "user_" + Guid.NewGuid().ToString("N")[..22];
    private static KiloDbContext Db(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<KiloDbContext>();
    private static StringContent Json(string text) => new(text, Encoding.UTF8, "application/json");
    private sealed record Fixture(string Subject, int UserId, int RoutineId, int ExerciseId);

    private static async Task<RoutineExerciseDto> ReadPlacement(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(text);
        Assert.Equal(JsonValueKind.Array, json.RootElement.GetProperty("sets").ValueKind);
        Assert.False(json.RootElement.TryGetProperty("userId", out _));
        Assert.False(json.RootElement.TryGetProperty("exerciseScopeId", out _));
        return JsonSerializer.Deserialize<RoutineExerciseDto>(text, JsonSerializerOptions.Web)!;
    }

    private static async Task<ValidationProblemDetails> AssertValidation(HttpResponseMessage response, int status)
    {
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var problem = (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>())!;
        Assert.Equal(status, problem.Status);
        Assert.True(problem.Extensions.ContainsKey("traceId"));
        Assert.NotEmpty(problem.Errors);
        if (status == 422)
        {
            Assert.Equal("https://tools.ietf.org/html/rfc4918#section-11.2", problem.Type);
            Assert.Equal(response.RequestMessage!.RequestUri!.AbsolutePath, problem.Instance);
            Assert.All(problem.Errors.Keys, key => Assert.True(char.IsLower(key[0])));
        }
        return problem;
    }
}
