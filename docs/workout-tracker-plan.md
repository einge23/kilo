# Kilo / Workout tracker

## A cumulative .NET 10 implementation workbook

Controllers / EF Core 10 / PostgreSQL / Clerk / Docker Compose / Linux

Revised October 8, 2026 for EF Core, FluentValidation, and a global exercise catalog with private custom exercises. Integer IDs. Imperial defaults with explicit lb/kg weights. Routine templates stay separate from workout snapshots.

This is a revised implementation plan, not a completed application. The original PDF supplies the product requirements; its code recipes are reference material, not instructions to execute. This revision supersedes those recipes.

## How to use this guide

Build slices 01-20 in order. Each slice states what already exists, the new work, the implementation sequence, and a concrete verification gate. Earlier code remains in place unless a later slice explicitly changes it. No N-number lookup is required.

Slice 01 explains the foundation and its repository implementation. Snippets are labeled when partial; later notes extend earlier work. The API catalog and schema contracts describe the final product; they are not a request to implement future slices early.

Use each slice's fillable progress panel for implementation, verification, status, review date, and evidence. Mark Done only when its gate passes. Save the filled PDF in a reader that supports AcroForms.

## What changed

- Program.cs composes the host; focused IServiceCollection extensions own API, persistence, and later Clerk registrations.
- Asp.Versioning.Mvc supplies URL-segment versioning. It replaces the hand-built ApiV1Controller routing base.
- Each request has a FluentValidation validator discovered through DI. Async request-rule validation returns standardized 422 ValidationProblemDetails; malformed JSON and type binding return native 400.
- Native health checks use KiloDbContext.Database.CanConnectAsync. EF Core owns mapping, scoped contexts, pooled connections, and transactions.
- Generated EF migrations and their model snapshot grow with the slices. The separate migrator applies them through MigrateAsync. All runtime schema changes use native EF migrations.
- Global exercises are shared and managed by admins; custom exercises stay private. Native role policies authorize global writes.
- Feature folders grow inside one API project. Kilo.Persistence shares the model and migrations between the API and separate migrator; Kilo.Tests verifies their behavior. No further architecture layers are needed.

---page---
# Development order

| Slice | Outcome | Builds on |
| --- | --- | --- |
| 01 | API foundation and migration pipeline | Existing Kilo projects |
| 02 | API and migrator release images | 01 |
| 03 | Repeatable local Compose stack | 02 |
| 04 | Clerk identity and preferences | 01-03 |
| 05 | Global catalog, custom exercises, admin policy | 04 |
| 06 | Routine templates | 05 |
| 07 | Ordered exercise placements | 06 |
| 08 | Planned sets and explicit weights | 07 |
| 09 | Weekly schedule | 08 |
| 10 | Atomic, idempotent workout snapshots | 09 |
| 11 | Performed sets and corrections | 10 |
| 12 | Finalization and paged history | 11 |
| 13 | Previous performance | 12 |
| 14 | Persistent rest deadline | 11-13 |
| 15 | Reordering and archive rules | 14 |
| 16 | Linux host and restricted DB roles | 15 |
| 17 | HTTPS proxy and browser access | 16 |
| 18 | Repeatable deployment | 17 |
| 19 | Backup and restore | 18 |
| 20 | Recovery, diagnostics, release gate | 19 |

## Milestones

After 03: runnable development stack. After 08: complete templates. After 12: usable workout logging API. After 15: complete workout API. After 20: verified single-host deployment.

The frontend is a separate deliverable. The guide retains client behaviors for units and countdowns, but does not claim to build a browser UI. Client QA must use a real client when one is available.

## Original weekly plan

Wednesday (3): Upper A. Thursday (4): Lower A. Friday (5): Abs and Arms. Saturday (6): Upper B. Sunday (7): Lower B. Monday and Tuesday may remain unassigned. Starting a workout is never restricted to its scheduled weekday.

---page---
# Architecture and shared standards

## One API, feature folders, explicit dependencies

Keep the existing Kilo and Kilo.Migrations project names. Start with Hosting for service registration and readiness. Add Features/Me, Exercises, Routines, Schedule, and Sessions only when their slice begins. Keep request/response contracts and real workflow services beside their API feature. Add entities under Kilo.Persistence/Entities and Fluent mappings only when used. Move a genuinely shared WeightDto into Shared when a second feature needs it.

Controllers bind requests and translate outcomes into HTTP. Inject KiloDbContext directly for simple CRUD; extract a concrete workflow service for a real multi-step transaction. EF already supplies change tracking and a unit of work. Do not wrap it in repositories, a custom unit of work, AutoMapper, MediatR, a universal Result type, or four Clean Architecture projects.

## HTTP and validation

Business routes use api/v{version:apiVersion}/..., with [ApiController], [ApiVersion(1.0)], and relative action routes. GET /health is anonymous and unversioned. Explicit routes avoid inheritance surprises. From slice 04 onward, a fallback authorization policy protects business routes by default.

Use camelCase JSON and int IDs. A route uses {id:int:min(1)}; invalid route shapes do not match (404). Each request type has a FluentValidation validator registered by assembly scanning. Await ValidateAsync before provisioning or writes. Rule failures return 422 ValidationProblemDetails with camelCase error keys; malformed JSON/type binding stays native 400. Disable MVC implicit required annotations and omit request DataAnnotations/IValidatableObject. Validate ownership and state in the transaction.

Created: 201 with Location. Read/update: 200. Archive/delete: 204. Missing/invalid token: 401. Authenticated policy denial: 403. Missing/foreign/nested mismatch: 404. Lifecycle, archive, or position conflict: 409. Known dependency outage: 503. Unexpected fault: sanitized 500. Use ProblemDetails for errors; status-code middleware fills otherwise empty error responses. Do not treat every database exception as an outage.

## Database and time

Register KiloDbContext with AddDbContext: one scoped context per request or migrator scope, never a singleton. Await operations sequentially; a context is not thread-safe. Pass CancellationToken to queries, SaveChangesAsync, transactions, and migrations. Use AsNoTracking and DTO projections for reads, tracked entities for writes, and a bounded provider command timeout. Private resource predicates include verified local userId; exercise reads additionally allow global rows (user_id IS NULL); never use unscoped FindAsync for tenant resources. Composite foreign keys enforce ownership as a second defense.

Postgres identity columns generate positive integer keys; gaps are normal. Clerk subjects and retry keys remain text. Use timestamptz for UTC instants and date for optional scheduled dates. The Npgsql EF provider maps timestamptz to UTC DateTime and date to DateOnly; map both explicitly in the model. Use TimeProvider for application-generated timestamps, including deterministic timer tests.

SaveChanges is atomic for one batch. Begin an explicit transaction when locks, reads, or several saves must be atomic together. Use EF parameterized SQL only for a concrete PostgreSQL feature such as FOR UPDATE; keep ordinary CRUD in LINQ. Inspect DbUpdateException.InnerException for a known PostgreSQL SQLSTATE and constraint name before translating a conflict. Do not enable sensitive-data logging. Standards basis: controller behavior [1], the Npgsql EF provider [4], and EF queries/transactions [5, 15].

---page---
# Request validation and 422 responses

## Add rules as their request types arrive

Pin FluentValidation.DependencyInjectionExtensions 12.1.1 [23, 24]. AddKiloApi scans the API assembly for public validators with AddValidatorsFromAssemblyContaining<Program>(). The default lifetime is scoped. No validator is needed before its feature exists; preferences arrive in 04 and exercise writes in 05.

Keep one AbstractValidator<TRequest> beside each request, named PreferencesRequestValidator or ExerciseWriteRequestValidator. Share the exercise validator between private/admin writes. Use NotEmpty for required text and stop a field's cascade before rules requiring a present value. Explicitly use camelCase wire names with OverridePropertyName. Nested input objects use child validators when introduced.

## Explicit asynchronous execution

Inject IValidator<TRequest> into the consuming controller. Await validation with the request cancellation token before CurrentUser provisioning, queries with side effects, or writes. Keep authorization in the native middleware and ownership/lifecycle checks in their workflow. Do not use the unsupported FluentValidation.AspNetCore synchronous auto-validation pipeline or exception-driven validation middleware [23].

```csharp
// At the start of a request-consuming action:
var validation = await validator.ValidateAsync(request, ct);
if (!validation.IsValid)
{
    return this.RequestValidationProblem(validation);
}
// Only valid requests continue to identity resolution and persistence.
```

RequestValidationProblem is the shared Hosting extension. Add each failure to controller.ModelState, then call native ValidationProblem(statusCode: 422, instance: HttpContext.Request.Path). ASP.NET Core supplies the validation title, RFC type, and traceId; no custom response envelope is needed. Advertise 422 ValidationProblemDetails on write actions in OpenAPI.

## Stable response contract

```json
{
  "type": "https://tools.ietf.org/html/rfc4918#section-11.2",
  "title": "One or more validation errors occurred.",
  "status": 422,
  "instance": "/api/v1/me/preferences",
  "errors": { "timeZone": ["Use a supported IANA time zone."] },
  "traceId": "request trace identifier"
}
```

Required/empty/null values, unsupported choices and rule violations return 422. Malformed JSON, an absent body, numeric overflow and incompatible bound types remain native 400 ValidationProblemDetails; route mismatch stays 404. A supplied nested weight object needs both value and unit; omission is distinct from valid zero.

## Verification carried into every slice

Exercise validators through HTTP: valid requests, aggregated camelCase errors, the 422 problem shape, malformed/type-binding 400, unchanged rows and no first-request provisioning on failure. Verify authentication/admin denial occurs before validation. Existing PostgreSQL Testcontainers and Bogus fakers remain the test infrastructure. See docs/request-validation-verification.md for this revision's evidence.

---page---
# Global catalog and private custom exercises

## One library, two fixed scopes

An Exercise has a nullable UserId. Null means a global entry shared by all authenticated users; a positive local UserId means a private custom entry. Do not invent a system user. Scope is fixed after creation; promotion, transfer, cloning, and a seeded catalog are not requested.

GET /exercises returns active globals plus the caller's active custom entries, ordered by name then id. An empty catalog is valid. GET /exercises/{id} permits globals and the caller's entries, including archived detail. Foreign private IDs return 404, including for admins. includeArchived expands only the caller's visible list. ExerciseDto exposes isGlobal, never the owner's ID.

## Writes follow the route and verified identity

| Route family | Creates / changes | Permission |
| --- | --- | --- |
| /exercises | Caller-owned custom entries | Authenticated owner |
| /admin/exercises | Global entries only | Admin policy |

Personal POST always creates a custom entry, even for an admin. Personal PUT/DELETE on a visible global returns 403; global edits use the admin route. Admin writes against a private or missing ID return 404. Admin status never unlocks another account's private data. Requests contain no owner, role, or scope switches.

## Carry this model through later slices

05 implements read/create/update and the native admin policy. Archive endpoints and their locking/usage rules arrive in 15. Routine attachment in 07 accepts an active global or caller-owned custom exercise; routine/session ownership stays private. Snapshot creation in 10 copies either scope's metadata and original weights; later admin edits cannot rewrite history.

Native composite foreign keys and checks enforce exercise access in 07 and 10. A nullable owner alone cannot serve as the global side of a checked composite foreign key. Add the derived access key only when placements need it; see the slice-07 schema contract. This key is internal, not an API ID or new authorization framework.

---page---
# Growth map and migration policy

## Add only files the current slice uses

```text
Kilo.slnx / global.json / .config/dotnet-tools.json
Kilo/
  Program.cs
  Hosting/                  # API registrations and readiness
  Features/                 # added feature by feature
Kilo.Persistence/
  Entities/User.cs
  KiloDbContext.cs
  DatabaseServiceCollectionExtensions.cs
  KiloDbContextFactory.cs    # design-time EF tooling
  Migrations/               # generated C#, designers, model snapshot
Kilo.Migrations/
  Program.cs                # separate MigrateAsync executable
Kilo.Tests/                 # real PostgreSQL and HTTP checks
```

## One ordered, additive EF schema path

| First used | EF migration name | Adds |
| --- | --- | --- |
| 01 / 04 | CreateUsers | Identity and preference storage |
| 05 | AddExercises | Global/private exercises, optional brands |
| 06 | AddRoutines | Routine metadata |
| 07 | AddRoutineExercises | Owned placements and exercise access keys |
| 08 | AddRoutineSets | Planned sets and weight pairs |
| 09 | AddRoutineSchedule | One routine per user/weekday |
| 10 | AddSessions | Sessions and complete snapshots |
| 12 | AddHistoryIndexes | History and previous-session indexes |
| 14 | AddRestTimer | Timer columns, checks, child reference |

EF prefixes generated files with a timestamp. Add entities/mappings, run dotnet ef migrations add, and review the generated migration, designer, and model snapshot together. Do not hand-maintain the snapshot, change an applied migration, or recreate retained data with EnsureCreated. The SQL appendix expresses schema contracts, not executable migration files.

Archive columns and active-position indexes belong to the original table migrations. Session result/lifecycle fields belong to AddSessions; result writes arrive in 11. Timer fields arrive in 14. Add only User in 01; future tables remain future work.

The separate executable calls Database.MigrateAsync and records public.__EFMigrationsHistory. Repeat runs apply only pending migrations. EF owns locking and normal migration transactions; do not wrap MigrateAsync in an application transaction or promise the whole release rolls back. Review generated operations that suppress transactions. Run one migrator per deployment and activate the API only after success [8].

Only native EF migration history is supported. An existing database created by another migration system needs a backup, inventory, and separate conversion plan. Never infer a baseline, fabricate history, or delete retained volumes to make startup pass.

---page---
# Slice 01 - Foundation scope and setup

## Outcome

A .NET 10 controller host, native API versioning, EF persistence, sanitized errors, public readiness, and a separate repeatable EF migrator. No Clerk or workout feature is implemented yet.

## Starting point in this workspace

Slices 01-03 already exist. This revision converts their persistence to EF while preserving integer keys, column names/defaults/checks, and existing local volumes. Kilo.Persistence is shared by the two executables; it is not an extra application layer.

## Build sequence

1. Keep net10.0, nullable reference types, implicit usings, and the existing valid SDK selection. Commit package locks and restore in locked mode.
2. Retain Asp.Versioning.Mvc and Microsoft.AspNetCore.OpenApi; pin FluentValidation.DependencyInjectionExtensions 12.1.1 in Kilo. Reference Kilo.Persistence from the API and migrator.
3. In Persistence pin Microsoft.EntityFrameworkCore.Relational 10.0.12, Microsoft.EntityFrameworkCore.Design 10.0.12 (PrivateAssets=all), Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3, and configuration environment support 10.0.12. Keep Hosting 10.0.12 in the migrator. The provider brings Npgsql; remove the former data-access packages.
4. Add User, KiloDbContext, shared database registration, and a design-time factory. Keep API hosting extensions and slim Program.cs.
5. Pin dotnet-ef 10.0.12 in the local tool manifest. Generate CreateUsers in Persistence/Migrations; commit its designer and model snapshot. Never generate future tables in this baseline.
6. Run the separate migrator twice against a disposable real Postgres database, check readiness, and run the foundation suite. Use an empty database or one already managed by these EF migrations. No legacy migration compatibility code remains.

## SDK selection

```json
{
  "sdk": {
    "version": "10.0.100",
    "rollForward": "latestFeature",
    "allowPrerelease": false
  }
}
```

10.0.100 is a valid minimum installed SDK. Release images use approved pinned digests; review SDK and dependency updates deliberately. Use an exact approved SDK with rollForward=disable in reproducible CI [6].

---page---
# Slice 01 - Program.cs and API services

## Kilo/Program.cs - complete slice-one composition

```csharp
using Kilo.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddKiloApi();
builder.Services.AddKiloPersistence(builder.Configuration);

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.MapHealthChecks("/health").AllowAnonymous();
app.MapControllers();
app.Run();

public partial class Program { }
```

## Hosting/ApiServiceCollectionExtensions.cs - complete registration

```csharp
using Asp.Versioning;
using FluentValidation;

namespace Kilo.Hosting;

public static class ApiServiceCollectionExtensions
{
    public static IServiceCollection AddKiloApi(
        this IServiceCollection services)
    {
        services.AddControllers(options => options
            .SuppressImplicitRequiredAttributeForNonNullableReferenceTypes
                = true);
        services.AddValidatorsFromAssemblyContaining<Program>();
        services.AddProblemDetails();
        services.AddOpenApi();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddApiVersioning(options =>
        {
            options.DefaultApiVersion = new ApiVersion(1, 0);
            options.AssumeDefaultVersionWhenUnspecified = false;
            options.ReportApiVersions = true;
            options.ApiVersionReader = new UrlSegmentApiVersionReader();
        }).AddMvc();
        return services;
    }
}
```

The package owns version matching; URL segments still require an explicit version. DefaultApiVersion does not make /api/exercises valid. Asp.Versioning.Mvc is the current .NET Foundation project descended from the Microsoft-named library [2, 3]. Add its ApiExplorer companion only when separate per-version documents are needed. Development OpenAPI is the built-in Microsoft implementation; no UI package is required for 01.

Keep middleware ordering visible. Do not add a second app.Build(), a self-check branch to production startup, or one extension per registration line.

---page---
# Slice 01 - Persistence registration

## Kilo.Persistence / shared native registration

AddKiloDatabase validates ConnectionStrings:Postgres once, then registers the scoped context for either executable. It rejects blank/malformed settings or missing Host/Database with a key-only diagnostic. No database I/O occurs during registration.

```csharp
services.AddDbContext<KiloDbContext>(options =>
    options.UseNpgsql(connection, postgres => postgres
        .CommandTimeout(15)
        .MigrationsHistoryTable("__EFMigrationsHistory", "public")));
```

This is the registration body, with connection supplied by the shared validator. Use the same options in the design-time factory. The provider manages pooled connections; do not register a singleton context or add a connection wrapper.

## Kilo/Hosting/PersistenceServiceCollectionExtensions.cs

```csharp
using Kilo.Persistence;

namespace Kilo.Hosting;

public static class PersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddKiloPersistence(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddKiloDatabase(configuration);
        services.AddHealthChecks().AddCheck<PostgresReadinessCheck>(
            "postgres", timeout: TimeSpan.FromSeconds(3));
        return services;
    }
}
```

Supply Timeout=3;Command Timeout=15 in local connection configuration. Never expose its value in logs or exceptions, enable sensitive-data logging, or mutate schema while registering services. A well-formed connection string is configuration validity; an unreachable server is readiness failure.

---page---
# Slice 01 - Native readiness and versioned controllers

## Hosting/PostgresReadinessCheck.cs

```csharp
using Kilo.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Kilo.Hosting;

public sealed class PostgresReadinessCheck(KiloDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource
            .CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            return await db.Database.CanConnectAsync(deadline.Token)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Database unavailable.");
        }
        catch (OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("Database check timed out.");
        }
    }
}
```

Health checks resolve the context in their own scope. CanConnectAsync checks connectivity, not schema compatibility. GET /health returns native Healthy/Unhealthy text and 200/503. No custom health controller or third-party package is required [7]. Migration success separately gates release activation.

## Controller pattern - introduced with the first business controller in 04

```csharp
[ApiController]
[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/me")]
public sealed class MeController : ControllerBase
{
    // Slice 04 supplies actual dependencies and actions.
}
```

This is partial, not a slice-one endpoint. Use a test-only v1 controller for foundation routing checks. Positive ID constraints arrive with real resource actions.

---page---
# Slice 01 - Separate migration executable

## Kilo.Migrations/Program.cs - native EF runner

The production entry point validates configuration and clears data-bearing log providers. This is its core lifecycle; see the repository's complete Program.cs for those guards.

```csharp
builder.Services.AddKiloDatabase(builder.Configuration);
using var host = builder.Build();
await host.StartAsync();
try
{
    await using var scope = host.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<KiloDbContext>();
    var ct = host.Services.GetRequiredService<IHostApplicationLifetime>()
        .ApplicationStopping;
    await db.Database.MigrateAsync(ct);
}
finally { await host.StopAsync(); }
```

Use Host.CreateApplicationBuilder for separate executable configuration and cancellation on SIGTERM. The executable boundary returns 1 with a sanitized diagnostic on failure, 0 with Migrations complete on success. Do not print provider exceptions, credentials, or SQL values. Unexpected migration failures must block release activation.

The API never calls MigrateAsync, EnsureCreated, or tooling Database.Update. Production uses an application role for the API and a DDL role for this executable. Provision the database separately; do not make serving processes create it.

## Generated baseline and tooling

CreateUsers is generated from the User model below. EF discovers migrations compiled into Kilo.Persistence.dll, with their metadata and model snapshot. There are no embedded SQL resources or custom migration journal.

KiloDbContextFactory implements IDesignTimeDbContextFactory<KiloDbContext>. It reads environment configuration and reuses the shared connection validator/options. The library generates its runtime configuration for CLI tooling and privately references EF Design. Tooling must not start the API or migrator. The factory requires ConnectionStrings__Postgres; it never guesses a database or inherits another executable's user secrets.

---page---
# Slice 01 - User model and schema mapping

## Kilo.Persistence/Entities/User.cs

```csharp
using System.ComponentModel.DataAnnotations;

namespace Kilo.Persistence.Entities;

public sealed class User
{
    public int Id { get; set; }
    [MaxLength(27)]
    public required string ClerkUserId { get; set; }
    public string TimeZone { get; set; } = "UTC";
    [MaxLength(8)]
    public string MeasurementSystem { get; set; } = "imperial";
    public DateTime CreatedAt { get; set; }
}
```

## KiloDbContext / OnModelCreating core

```csharp
modelBuilder.HasDefaultSchema("public");
var user = modelBuilder.Entity<User>();
user.ToTable("users", table =>
{
    table.HasCheckConstraint("users_clerk_user_id_check",
        "btrim(clerk_user_id) <> ''");
    table.HasCheckConstraint("users_measurement_system_check",
        "measurement_system IN ('imperial', 'metric')");
});
user.HasKey(x => x.Id).HasName("users_pkey");
user.HasAlternateKey(x => x.ClerkUserId)
    .HasName("users_clerk_user_id_key");
user.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
user.Property(x => x.ClerkUserId)
    .HasColumnName("clerk_user_id").HasColumnType("text");
user.Property(x => x.TimeZone).HasColumnName("time_zone")
    .HasColumnType("text").HasDefaultValue("UTC");
user.Property(x => x.MeasurementSystem)
    .HasColumnName("measurement_system")
    .HasColumnType("text").HasDefaultValue("imperial");
user.Property(x => x.CreatedAt).HasColumnName("created_at")
    .HasColumnType("timestamp with time zone").HasDefaultValueSql("now()");
```

KiloDbContext inherits DbContext, accepts DbContextOptions<KiloDbContext>, and exposes DbSet<User> Users. User lives in the Entities namespace. TimeZone stores IANA names without a three-character limit. Length annotations remain model/validation metadata; explicit PostgreSQL text mappings do not enforce these lengths. Keep this small mapping together. Split feature entity configurations with IEntityTypeConfiguration only when real growth warrants it. Explicit names retain the schema contract without a naming-convention dependency.

---page---
# Slice 01 - Migration lifecycle

## Generate, review, then run the separate migrator

```powershell
dotnet tool restore
# Supply a local connection securely via environment configuration.
# The factory requires ConnectionStrings__Postgres for tooling.
dotnet ef migrations add AddExercises --project Kilo.Persistence
dotnet ef migrations has-pending-model-changes --project Kilo.Persistence
dotnet ef migrations script --idempotent --project Kilo.Persistence
dotnet run --project Kilo.Migrations
```

AddExercises is the example for slice 05, not a command to run now. Review both generated C# and provider SQL for loss, defaults, constraints, indexes, and lock cost. After generating the intended migration, pending-model checking must pass. Commit migration, designer, and snapshot together. Only the separate executable applies deployment migrations; script output is for review. Never use EnsureCreated alongside migrations [8].

## EF-only database setup

Use an empty database or one carrying the native history for this application's EF migrations. The former SQL migrator and its compatibility bridge are removed. The serving API never creates or adopts database schema.

An existing non-EF database is not an empty installation. Back it up and inventory it before planning a separate conversion, or select a new empty database for development. Do not insert history entries by hand, remove retained volumes, or change an applied EF migration to force success. Removing old migration code does not delete existing data.

## Keep entity organization separate from schema changes

Place persistence entities in Kilo.Persistence/Entities. The User namespace and approved length annotations are captured in the additive OrganizeUserEntity migration, designer, and snapshot. Its generated Up/Down contain no schema operations. The applied CreateUsers files remain unchanged; pending-model checks must pass. Future model changes still generate additive EF migrations through the same pinned tool.

---page---
# Slice 01 - Configuration and verification gate

## Configure each executable independently

Both projects already have distinct UserSecretsId values. Configure the same key for each; a console migrator does not inherit the API's secret store. These commands use placeholders, not committed credentials.

```powershell
$env:DOTNET_ENVIRONMENT = "Development"
dotnet user-secrets set "ConnectionStrings:Postgres" "<connection>" --project Kilo
dotnet user-secrets set "ConnectionStrings:Postgres" "<connection>" --project Kilo.Migrations
dotnet restore Kilo.slnx --locked-mode
dotnet build Kilo.slnx --no-restore
dotnet run --project Kilo.Migrations
dotnet run --project Kilo.Migrations
dotnet run --project Kilo --no-launch-profile --urls http://127.0.0.1:8080
Invoke-WebRequest http://127.0.0.1:8080/health
```

For the native Windows development process, Host=localhost targets an explicitly available local DB. In containers, use Host=db. Environment override: ConnectionStrings__Postgres. User secrets are development storage, not an encrypted production vault. Supply production credentials through restricted host configuration.

The console migrator has no web launch profile: explicitly selecting Development is what enables its development user-secret loading. Production images use Production configuration with externally supplied settings.

## Package pins for the checked foundation

```powershell
dotnet add Kilo package Asp.Versioning.Mvc --version 10.2.1
dotnet add Kilo package Microsoft.AspNetCore.OpenApi --version 10.0.12
dotnet add Kilo package FluentValidation.DependencyInjectionExtensions --version 12.1.1
dotnet add Kilo.Persistence package Npgsql.EntityFrameworkCore.PostgreSQL --version 10.0.3
dotnet add Kilo.Persistence package Microsoft.EntityFrameworkCore.Relational --version 10.0.12
# EF Design 10.0.12 is a private tooling reference; local dotnet-ef is pinned too.
dotnet add Kilo.Migrations package Microsoft.Extensions.Hosting --version 10.0.12
```

These are verified package pins for recipe checks, not automatic update promises. Pin a patched OpenAPI package rather than keeping the template's 10.0.0 transitive dependency warning. Review package audit output during restore and update the lock file deliberately. Current package identities/targets: [14].

---page---
# Slice 01 - Foundation verification

## One focused runnable integration suite

Create Kilo.Tests with the chosen stable xUnit runner and Microsoft.AspNetCore.Mvc.Testing 10.x. Use WebApplicationFactory<Program> and an isolated PostgreSQL Testcontainer. Reuse that small harness for future slices. Docker is required; database checks must not silently skip. Bound migrator subprocess checks with a deadline and terminate their own process on timeout. Use the real Npgsql EF provider; neither EF InMemory nor SQLite verifies PostgreSQL identity, constraints, and locking.

- Migrate an empty database; assert users and native EF migration history exist. Insert a fixture and rerun; assert the fixture and journal row count are unchanged.
- Start with valid settings; GET /health returns 200. Stop the test DB or point to an unreachable endpoint; it returns 503 within the bounded check deadline plus transport overhead. Bring DB back; readiness recovers.
- Missing/invalid connection configuration fails with a key-only diagnostic. Capture logs and assert test credentials are absent.
- Add a test-assembly v1 probe controller through MVC ApplicationPart. /api/v1/probe resolves; unversioned and unsupported-version paths do not reach the v1 action. Assert the package's documented 404 for unmatched URL versions. Keep this probe out of production.
- Force the second command of a test transaction to fail; disposal rolls back the first. Exercise cancellation while a query is active and show a later scoped context still succeeds.
- Run the migrator with invalid configuration/a failing unapplied EF migration; assert nonzero exit. Confirm the serving executable never runs migrations.

Also assert scoped context lifetime, no pending model changes, database constraints through SaveChanges, and persistence of full IANA time zone names after migration replay.

## Done means

Build passes; migration repeatability, sanitized failure, rollback/cancellation, readiness, and package routing checks pass. Health is the only application endpoint. Do not mark future schema, Clerk, Docker, or feature CRUD complete.

@progress 01

---page---
# Slice 02 - Build release images

## Already exists

01 supplies two executables, compiled EF migrations in their shared library, package locks, and valid SDK selection. Keep separate API and migrator images; use the existing two Dockerfiles as a starting point.

## Add and implement

1. Use a .NET 10 SDK build stage to restore in locked mode and publish Release with --no-restore. Copy solution, SDK selection, both executable project files, shared Persistence project, and their lock files before sources for caching.
2. API final stage uses aspnet:10.0; migrator uses runtime:10.0. Run both as the image's unprivileged APP_UID. The API listens on 8080 through ASPNETCORE_HTTP_PORTS.
3. EF migrations are compiled into Kilo.Persistence.dll and published with the migrator and EF runtime assemblies; no script-folder copy or API migration command is required. Both images carry one release identifier.
4. Keep secrets, .git, .idea, bin, obj, tmp, and output artifacts out of the Docker build context. Use .dockerignore. Build requires package restore access but no running database.
5. Pin approved release base-image digests and build for the host CPU architecture. Preserve runtime CA certificates and globalization/timezone data needed for Clerk and IANA IDs.

## Minimal publish shape - illustrative Dockerfile body

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore Kilo/Kilo.csproj --locked-mode
RUN dotnet publish Kilo/Kilo.csproj -c Release --no-restore \
    -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
USER $APP_UID
COPY --from=build /out .
ENTRYPOINT ["dotnet", "Kilo.dll"]
```

Use the analogous project path, runtime base, and Kilo.Migrations.dll for the migrator. The above favors clarity; the existing project-copy restore cache can stay. No orchestrator or registry abstraction is required.

---page---
# Slice 02 - Verify images

## Gate

- Build both images from a clean source checkout without bin/obj or a database. Verify compiled EF migration discovery from Kilo.Persistence.dll inside the migrator image.
- Run on the intended Linux architecture; inspect UID, listening port, and environment-only settings.
- Scan layers/build context for secret files. Verify outbound trusted HTTPS works from the runtime used by the API.
- Send SIGTERM during a real transactional write after that feature exists; require complete commit or rollback. For now verify host shutdown and container exit.

## Carry forward

Record image tags/digests and the build command. Later releases use the same image pair, not a new packaging strategy. The migration executable exits once; the API runs until stopped.

## Acceptance

02.1 Clean image builds succeed. 02.2 Images run on target architecture. 02.3 Runtime is non-root. 02.4 Trusted HTTPS/globalization dependencies remain available. 02.5 SIGTERM stops the host cleanly; data-integrity shutdown is rechecked in 20.

No Dockerfile-only unit tests. Verify the actual images.

@progress 02

---page---
# Slice 03 - Local Docker Compose

## Already exists

02 supplies API/migrator images; 01 supplies the readiness route and one shared connection-string key. No application code change is needed.

## Add and implement

1. Keep services db, migrations, api. Postgres health uses pg_isready. Migrations waits for service_healthy; API waits for service_completed_successfully. Migrations has no restart loop; API/db use appropriate long-running restart policies.
2. Use POSTGRES_DB=kilo, POSTGRES_USER, and a locally supplied password. The current POSTGRES_DATABASE setting is ineffective. For Postgres 18, mount the persistent volume at /var/lib/postgresql, matching its image layout [9].
3. Use Host=db;Database=kilo in container connection strings. Inject credentials from ignored development configuration. Configure each executable separately.
4. Put development port publishing in an override: API 127.0.0.1:8080:8080; publish DB to loopback only if Rider/native API tests need it. Production has neither direct API nor DB port publishing.
5. Start the full stack; migrations are a fresh one-shot run when explicitly requested. Do not assume an old completed migration container applied migrations from a new image; deployment handles this in 18.

## Compose dependency shape - partial snippet

```yaml
migrations:
  depends_on:
    db:
      condition: service_healthy
api:
  depends_on:
    migrations:
      condition: service_completed_successfully
```

This is the dependency fragment, not a full compose.yaml. Include the established images/builds, environment, volume, and DB health check when applying it.

## Persistent-state rule

Container recreation is routine. Removing the named database volume is a data reset and must be intentional. Changing POSTGRES_DB or POSTGRES_PASSWORD after initialization does not rewrite an existing database or its roles [9].

---page---
# Slice 03 - Verify the local stack

## Gate

- Start with a disposable empty volume; observe db healthy, migrations exits 0, then API becomes ready.
- Create a users fixture through test SQL, recreate containers without deleting volumes, and assert it survives.
- Make a new unapplied test migration fail. API startup is blocked; failure is visible. Repair only the unapplied fixture migration in the disposable source snapshot and run a fresh migrator. Assert failed DDL and its EF history entry rolled back; committed earlier history remains.
- Confirm container Host=db networking and loopback-only development ports.
- Inspect the base production Compose configuration: no published DB/API ports and no hardcoded development password.

## Acceptance

03.1 Fresh startup succeeds in dependency order. 03.2 Container service networking works. 03.3 Data survives recreation. 03.4 Migration failure blocks API start. 03.5 Development access is local.

No Compose-only unit suite. Store one reproducible startup/failure check with the run instructions; reuse it for releases.

## Next change

04 extends API registration with identity and authorization. It does not rewrite persistence, the health route, or the migration host.

@progress 03

---page---
# Slice 04 - Clerk identity and preferences

## Already exists

01's users table already holds Clerk subject, timezone, and measurement preference. Reuse the scoped DbContext, validator DI registration, and v1 versioning. Health stays anonymous.

## Add and implement

1. Add Hosting/ClerkServiceCollectionExtensions.cs with AddKiloClerk(configuration). Bind ClerkOptions (Issuer, optional Audience, AuthorizedParties), validate HTTPS issuer and nonempty allowed origins with ValidateOnStart, and register JwtBearer. Do not fetch or parse JWTs yourself.
2. Let the handler retrieve/cache trusted signing metadata and rotate keys. Set MapInboundClaims=false; verify issuer, lifetime, signing key, and intended algorithm. Validate audience when your Clerk token setup defines it. Require nonblank verified sub and allowed azp in OnTokenValidated [10]. No Clerk secret API key is needed just for signature verification.
3. Register a fallback policy requiring authenticated users, and a named frontend CORS policy with explicit configured origins. CORS permission and token authorized-party validation are separate checks even if their configured origins match.
4. Add Features/Me with CurrentUser, preferences requests/responses, and MeController. Resolve sub from the authenticated principal only. Query Users by verified subject, add a User when absent, then SaveChangesAsync. On the named subject unique-key race, detach the failed Added entity and requery the winning row; do not swallow other DbUpdateException failures. Cache the resolved ID in the scoped resolver for that request.
5. GET /me returns the local profile. PUT /me/preferences replaces both fields. Add PreferencesRequestValidator beside its request. Validate imperial/metric and an actual resolvable IANA timezone (UTC accepted); verify Linux support. Await validation before CurrentUser provisions an account. Never change stored workout weights on preference updates.
6. Supply the API issuer and authorized-party origins in native and Compose configuration. The migrator needs no Clerk settings. Keep public placeholders in .env.example; credentials remain local. Audience is optional and must match the token setup when enabled.

## Program.cs delta after the two existing registrations

```csharp
builder.Services.AddKiloClerk(builder.Configuration);
// After Build and exception/status-code middleware:
app.UseRouting();
app.UseCors("frontend");
app.UseAuthentication();
app.UseAuthorization();
```

The delta has two insertion points, not a block to paste at the bottom. Keep registration before Build and middleware before endpoint mapping. OpenAPI in development and /health must opt out with AllowAnonymous when the fallback policy is enabled.

Routes: GET /api/v1/me; PUT /api/v1/me/preferences. New users default to UTC and imperial; client weight-input default is lb. No new migration; use the existing User mapping.

---page---
# Slice 04 - Verification and auth outage policy

## Gate

- Valid test JWT provisions one local integer ID; concurrent first requests produce exactly one users row.
- Missing, expired, tampered, wrong-issuer, missing-sub, disallowed-algorithm, wrong audience (when configured), and wrong azp return 401 and create no account.
- Valid identity plus unmet authorization policy returns 403. Foreign-data tests begin in 05.
- Rotate fixture signing keys and verify metadata refresh. An outage with usable cached metadata still verifies valid cached-key tokens.
- No usable trusted metadata: deny the request. Return 503 only when metadata retrieval failure is positively identified as the cause. Unknown key or invalid signature alone remains 401. Preserve this distinction in one auth-specific failure hook; do not broadly convert authentication errors to 503.
- PUT America/Phoenix + metric, then imperial; verify persistence. Missing/unknown preference or unknown timezone returns 422 and changes nothing; malformed JSON/type binding returns 400. Neither rejection provisions a new account.
- Anonymous /health and allowed CORS preflight still work. A disallowed browser origin receives no CORS permission.

## Test shape

Use WebApplicationFactory with PostgreSQL Testcontainers. Feature tests use a test authentication handler; native JWT tests use fixture discovery/public JWKS responses. Production OpenAPI returns 404 for authenticated requests and 401 for anonymous requests.

## Acceptance

04.1 Verified identity resolves a local account. 04.2 Concurrent provisioning is race-safe. 04.3 Invalid tokens fail. 04.4 Claim restrictions hold. 04.5 Rotation and diagnosed outages behave safely. 04.6 Imperial is the default. 04.7 Preference changes persist without rewriting values.

## Carry forward

Resolve local userId through CurrentUser, never a request body. Keep explicit v1 controller routes and include version when generating CreatedAtAction links.

@progress 04

---page---
# Slice 05 - Native admin authorization

## Extend the existing Clerk registration

Slice 04 already verifies JWT signature, issuer, lifetime and authorized party. Keep those checks. In AddKiloClerk, set TokenValidationParameters.RoleClaimType = "role" alongside NameClaimType = "sub" and MapInboundClaims = false. ASP.NET Core can then use its native role requirement [17, 18].

```csharp
services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser().Build())
    .AddPolicy("Admin", policy => policy
        .RequireAuthenticatedUser()
        .RequireRole("admin"));
```

Keep authentication and authorization in Hosting, not Program.cs. Protect AdminExercisesController with [Authorize(Policy = "Admin")], [ApiController], [ApiVersion(1.0)], and [Route("api/v{version:apiVersion}/admin/exercises")]. An explicit policy requires authentication itself; the fallback policy is not added automatically. No custom handler, ASP.NET Identity store, or new package is needed.

## Configure the signed claim in Clerk

Use Clerk Dashboard or a trusted backend to set an administrator's public metadata to {"role":"admin"}. Browser clients can read public metadata but cannot set it. Never use unsafe metadata, request headers, or request bodies to grant roles [19]. Under Sessions / Customize session token, merge this compact top-level claim into the existing configuration [20]:

```json
{ "role": "{{user.public_metadata.role}}" }
```

Ordinary users have no admin role. Require the exact value admin; missing, null or differently cased values do not grant access. Changing metadata takes effect in newly refreshed tokens; an already issued valid token retains its claims until expiry. Refresh after role changes and document this limit; do not promise immediate revocation.

## Verify before enabling global writes

Exercise the real policy with native JwtBearer and fixture-signed tokens: valid admin succeeds, valid ordinary/missing/wrong-case role returns 403, and absent/invalid/forged bearer returns 401. A body or header claiming admin cannot change that result. Retain the slice-04 identity, rotation and outage regressions.

---page---
# Slice 05 - Exercise library

## Already exists

Authenticated local users, scoped CurrentUser, EF persistence, native JWT fixtures, and PostgreSQL Testcontainers. Extend Clerk authorization as on the preceding page, then generate only AddExercises.

## Add and implement in this order

1. Reuse the HTTP fixture; rename MeApiFactory to KiloApiFactory as Exercises becomes its second feature. Retain ClerkApiFactory for signed-token policy tests. Add a focused failing admin-policy check before wiring RoleClaimType and the named policy.
2. Add Persistence/Entities/Exercise and mapping: nullable UserId, shared metadata, archived/created instants. Null owner is global; private creation derives its owner through CurrentUser. Keep delete behavior restrictive. Generate and review migration, designer and snapshot together.
3. Add Features/Exercises with one ExerciseWriteRequest, ExerciseDto, ExercisesController and AdminExercisesController. Simple CRUD uses scoped KiloDbContext directly. Share request validation and DTO shape; no forwarding service or repository.
4. Require a nonblank trimmed name. Omitted/null description becomes empty; preserve Unicode and multiline text. Use ExerciseWriteRequestValidator on both write controllers; validate trimmed brand: blank becomes null, nonempty maximum 100 characters. PUT omission clears brand. Brand remains free-form equipment text.
5. Implement shared list/detail reads, private POST/PUT and admin global POST/PUT. Order lists by name then id; exclude archives unless includeArchived. Own/global archived metadata writes return 409. Foreign private/missing IDs return 404. Personal edits of globals return 403. Scope cannot change through a write request.
6. Return 201 with shared detail Location for either creation route, passing version="1" (a string) and id to CreatedAtAction. The versioning URL-generation constraint requires a string. Cross-controller admin creation targets ExercisesController's detail action. Archive writes arrive in 15.

## Read projection - partial body using slice-05 types

```csharp
return await db.Exercises.AsNoTracking()
    .Where(x => x.Id == id &&
        (x.UserId == null || x.UserId == userId))
    .Select(x => new ExerciseDto(x.Id, x.Name, x.Description,
        x.BrandName, x.UserId == null, x.ArchivedAt))
    .SingleOrDefaultAsync(ct);
```

Load private mutations by Id and verified owner; admin mutations by Id and UserId == null. SaveChangesAsync(ct) retrieves generated IDs/defaults. Never return tracked entities or reuse the read predicate as unrestricted write authorization.

---page---
# Slice 05 - Verify exercise CRUD

## Gate

- Admin creates globals; two ordinary accounts see them. Each account creates custom entries visible only to itself. An admin cannot read/update another account's custom entry.
- Native signed-token tests prove admin success, ordinary/missing/wrong-case role 403, and absent/invalid/forged token 401. Body/header owner, scope and role values cannot grant access or change creation scope.
- Personal POST by an admin still creates a private entry. Personal PUT of a global returns 403. Admin PUT of a private/missing ID returns 404. Rejected writes leave rows unchanged.
- Exercise both write routes with Unicode/apostrophes/multiline descriptions, positive IDs, camelCase/isGlobal DTOs, and versioned Location round trips.
- Test null/blank/padded/100/101-character brands; verify normalization, 422 rule boundaries, and PUT omission clearing brand.
- Test empty catalogs, name/id ordering, archived fixtures, default hiding and includeArchived visibility. Visible archived metadata updates return 409; foreign private detail/update stays 404.
- Malformed JSON/type binding returns 400; blank/missing names return 422 ValidationProblemDetails with camelCase errors and traceId. Invalid first writes never provision users. Unversioned/v2 routes do not invoke v1. Verify additive migration replay and no pending model changes.

## Acceptance

05.1 Shared catalog reads. 05.2 Verified private ownership. 05.3 Metadata round trips. 05.4 Private isolation, including admins. 05.5 Brand limits/clearing. 05.6 Native admin policy. 05.7 Fixed scope and guarded writes. 05.8 Archived filtering and deterministic order.

## Carry forward

Reuse DTO validation for private routines and the shared Testcontainers/Bogus patterns. The slice-05 gate passed October 8 with 72 tests, migration upgrade/replay, images and isolated Compose; see docs/slice-05-verification.md. Re-run meaningful gates after changes; PDF regeneration alone does not prove completion.

@progress 05

---page---
# Slice 06 - Routine templates

## Already exists

Exercise feature patterns and user isolation. Generate AddRoutines; keep the established startup and authentication code.

## Add and implement

1. Add Features/Routines with requests/responses, controller, and Routine entity/mapping. Use direct scoped EF CRUD; extract a named workflow only when operations become transactional.
2. Implement create, private list, detail, and metadata replacement. Require a nonblank trimmed name; description is optional and normalizes to empty string.
3. Detail returns exercises=[] until 07 adds placements. Avoid querying a table that does not exist yet. When children arrive, extend the same detail assembler rather than duplicating the endpoint.
4. Filter archived routines from default lists, allow includeArchived explicitly, and reject metadata edits of owned archived routines with 409. Foreign/missing routine is 404.
5. Direct DbContext CRUD needs no additional DI registration. When a real workflow service appears, register it as scoped; group cohesive registrations only when useful. No service-location or reflection scan is needed.

Routes: GET/POST /routines; GET/PUT /routines/{id}. Creating an empty routine is valid. It cannot start a workout until it contains active planned sets.

## Gate

- Create Upper A, Lower A, Abs and Arms, Upper B, Lower B with distinct generated IDs.
- Create empty routines with/without descriptions; verify detail collections are empty rather than null.
- Add RoutineWriteRequestValidator; reject blank names with 422 and no row. Exercise standard missing/foreign detail/update 404 and archived update 409.
- A second account lists none of the first account's routines. Created Location works with the API version.

## Acceptance

06.1 Routine create/list works. 06.2 Empty templates are valid. 06.3 Names are validated. 06.4 Routine ownership is enforced.

@progress 06

---page---
# Slice 07 - Ordered exercise placements

## Already exists

Global/private exercises and owned routines. Generate AddRoutineExercises with the derived library access key, composite foreign keys/checks, and an active-position unique index. Extend routine details to read ordered placements.

## Add and implement

1. Add POST/PUT nested placement actions to the routine feature. Use route name placementId instead of ambiguous exerciseId; the wire path shape stays compatible. Body exerciseId is the library exercise ID.
2. Create distinct placement IDs, allowing the same library exercise more than once. Store position>0, placement-specific description, and defaultRestSeconds>=0. Existing placement exerciseId is immutable; replacement will archive and create a new placement.
3. For each placement write, begin an explicit EF ReadCommitted transaction. Lock the owned routine first, then the visible global or caller-owned library exercise when attaching. Require both active. Store ExerciseScopeId from the selected row (UserId ?? 0), never the request; the scope FK/check prevents cross-user references. Nested update must prove Placement.RoutineId equals the route routineId and UserId equals the caller.
4. Return current library exercise name/brand through the routine detail join; store no placement brand column. Sets remain [] until 08.
5. Use the same scoped context, transaction, and token for every operation. Translate only DbUpdateException wrapping the named active-position unique violation to 409; unrelated database faults remain errors.

## Transaction shape - partial workflow

```csharp
await using var transaction = await db.Database.BeginTransactionAsync(
    IsolationLevel.ReadCommitted, ct);
var routine = await db.Routines.FromSqlInterpolated($"""
    SELECT * FROM public.routines
    WHERE id = {routineId} AND user_id = {userId} FOR UPDATE
    """).AsTracking().SingleOrDefaultAsync(ct);
// Validate routine, then lock/validate a global or owned exercise.
// Add the owned placement only after those checks.
await db.SaveChangesAsync(ct);
await transaction.CommitAsync(ct);
```

This partial workflow uses the slice-06 Routine mapping and System.Data isolation enum. FromSqlInterpolated parameterizes values; never concatenate client SQL. EF has no LINQ FOR UPDATE operator, so this bounded provider query is intentional. Do not nest another context inside the transaction. Extract a small concrete lock helper only when several real callers need it.

Lock ordering starts here: routine parent before library exercise; all code that needs both follows this order. Exercise-only archive will lock just the exercise. Future session mutations use their own parent session lock.

---page---
# Slice 07 - Verify placements

## Gate

- Add positions 1-3 and verify ascending detail order. Give repeated Bench Press placements distinct instructions and IDs.
- Reject nonpositive/fractional positions, negative/noninteger rest, and duplicate active positions. A unique collision is 409; request-rule validation is 422 and fractional/noninteger binding is 400.
- Attach global and own custom entries successfully. Foreign custom/routine or wrong-parent IDs return 404. Direct invalid scope/owner inserts fail database constraints.
- Attach an archived global/own custom exercise or edit an archived routine; return 409. The lock/state check must not only rely on a stale read.
- Change a library brand; both repeated routine placements show the current brand, without writing the placement rows.
- Race two inserts at one position; one commits and one returns 409, with no partial work.

## Acceptance

07.1 Configured order is stable. 07.2 Instructions belong to placement. 07.3 Repeated movements retain distinct identity. 07.4 Position/rest bounds hold. 07.5 Parent/owner checks hold. 07.6 Routine details show current brands.

## Carry forward

Template child writes lock the routine parent. This makes reorder and archive atomic later. Snapshot creation will use RepeatableRead so multi-query copies see one committed template state.

@progress 07

---page---
# Slice 08 - Planned sets and units

## Already exists

Owned placements and exercise access keys and the routine transaction convention. Generate AddRoutineSets; extend routine details with ordered sets. Introduce shared WeightDto when both templates and future results use it.

## Add and implement

1. Add POST/PUT sets under a route routineId/placementId. Prove the complete routine->placement->set chain, not only ownership of the final ID. Lock the routine before writing.
2. Validate setType warmup/working, position>0, targetRepsMin>0, targetRepsMax>=min. Integer binding rejects fractional reps. Use a request validator for required fields and the range relationship; await validation before writes.
3. A weight is null or a complete {value,unit} object. Use nullable decimal?/string input members and a child weight validator with NotNull/NotEmpty so an omitted value cannot silently become zero; response WeightDto contains decimal/string. Validate value>=0 and unit lb/kg. Reject JSON outside decimal range. No unit inference from a profile.
4. Map value/unit as separate nullable entity properties; the DB check enforces both absent or both present. Store exact original decimal values. Use unconstrained numeric (no fixed-scale rounding), bounded at the API by decimal.
5. Nullable restSeconds inherits the placement default; explicit zero means zero. Resolve this only when a session or extra set is created. Keep the template override nullable.

## Wire examples

```json
{
  "position": 1,
  "setType": "working",
  "targetRepsMin": 6,
  "targetRepsMax": 8,
  "targetWeight": { "value": 135.5, "unit": "lb" },
  "restSeconds": null
}
```

Map TargetWeightValue and TargetWeightUnit explicitly. Use HasColumnType("numeric") without fixed precision/scale, and named Fluent check constraints for the pair, valid units, and finite nonnegative values. Project to nullable WeightDto with an explicit conditional expression; the entity remains flat and the API response nested.

---page---
# Slice 08 - Verify planned sets

## Gate

- Two warmup plus three working sets produce five ordered rows.
- Fixed 10-10 and ranged 6-8 targets round-trip. Inverted/nonpositive ranges return 422; fractional integer binding returns 400.
- Null, zero, 2.5, 45, 135.5 lb and 60 kg round-trip exactly through JSON and SQL.
- Weight object {} or missing value/unit, unknown unit, negative value, invalid set type, and negative rest return 422 without writes. Malformed numeric binding/decimal overflow returns 400. Direct SQL with a half-present value/unit pair is rejected too.
- A null rest override stays null; zero stays zero; 120 stays 120. 10 verifies resolved copies.
- Wrong routine/placement/set chain and foreign rows return 404. Position collisions return 409, atomically.

## Acceptance

08.1 One row per ordered set. 08.2 Fixed/ranged targets. 08.3 Null and decimal weights. 08.4 Invalid values rejected. 08.5 Rest inheritance preserved. 08.6 Complete parent relationships enforced. 08.7 Every present weight has an explicit unit.

## Carry forward

Client input defaults follow measurementSystem; requests always send the chosen unit. Preference changes affect presentation, never stored values. Do not add conversion logic to save handlers.

@progress 08

---page---
# Slice 09 - Weekday schedule

## Already exists

Owned templates and profile timezone. Generate AddRoutineSchedule with primary key (user_id,weekday) and owned routine foreign key.

## Add and implement

1. Add Features/Schedule with GET /schedule and PUT/DELETE /schedule/{weekday}. Weekday is an integer 1-7, Monday=1 and Sunday=7.
2. PUT locks owned User then active Routine in one EF transaction. Find (UserId, Weekday), add/update, SaveChangesAsync, commit. This lock order serializes same-user assignments across routines. A routine may occupy several days.
3. DELETE locks User and clears only their selected weekday; repeat returns 204. GET returns assigned entries ordered by weekday.
4. Compute local day from TimeProvider UTC and the user's timezone. Map .NET Sunday=0 to ISO 7; never use server-local time.
5. Starting a workout on any day is valid; schedule assignments do not restrict scheduledDate.

## Gate

- Assign the original routines Wednesday-Sunday (3-7). Replace Wednesday twice; one row remains.
- Assign Upper A to Monday and Wednesday; both exist. Clear Wednesday twice; Monday and other users stay unchanged.
- Keep {weekday:int}; bind the weekday into the slice request input and validate 1-7 with FluentValidation. A bound invalid weekday returns 422; a noninteger route is 404. Verify both.
- Foreign routine returns 404; owned archived routine returns 409. No schedule row is written.
- Test UTC day boundaries for America/Phoenix and a DST-observing zone.

## Acceptance

09.1 ISO mapping is correct. 09.2 One row per user/day. 09.3 Weekday bounds hold. 09.4 Repeated routine assignments work. 09.5 Private reads and owned writes hold.

@progress 09

---page---
# Slice 10 - Start a workout snapshot

## Already exists

Complete templates and transactional EF workflows. Generate AddSessions and its three entities/mappings. Create Features/Sessions with SessionDto assembly and a concrete StartSession workflow. All operations use one scoped context and transaction.

## Add and implement

1. POST /routines/{id}/sessions receives optional scheduledDate and an Idempotency-Key: 1-128 visible ASCII characters. Validate before opening a transaction. The key is text, not a generated resource ID.
2. In a RepeatableRead transaction, look up (user_id,key). If present, compare original routineId and scheduledDate: same payload returns existing snapshot with 200; different payload returns 409. This replay remains valid even if the routine was subsequently archived.
3. If new, require the owned active routine and at least one active planned set. Copy routine metadata, active ordered placements, current global/custom names/brands, validated exercise access keys, ordered targets, both weight columns, and resolved rest. SessionExercise scope FK/check preserves global-or-own access. Actual reps/weights/completion timestamps start null.
4. Keep sourceRoutineExerciseId for placement matching; never substitute library exerciseId. Return 201 with session Location for a new snapshot. GET /sessions/{id} reads the snapshot only.
5. Let errors escape to roll back all rows. Unique-key races require a fresh transaction/snapshot to resolve the persisted key. Use a fresh context as well as a fresh transaction per retry, because rolled-back tracked objects are not reset automatically. Bounded retry (for example, at most 3 attempts) applies only to the named start-key unique violation, serialization failure, or deadlock for this atomic workflow. Never retry arbitrary writes automatically.

## Why RepeatableRead here

Several SELECT/INSERT statements must see one committed template state, including concurrent global admin or private owner brand edits. The routine parent lock convention alone does not serialize independent library metadata updates. RepeatableRead supplies a consistent snapshot; transaction conflicts restart the whole copy, not just the last statement [11].

Project templates with AsNoTracking within the RepeatableRead transaction. Build a new tracked session graph and SaveChangesAsync; EF propagates generated parent keys to children. Rest resolves with set.RestSeconds ?? placement.DefaultRestSeconds. Copy weight value and unit without conversion. Workouts may start on any day; scheduledDate is optional contextual data.

---page---
# Slice 10 - Verify snapshots and retries

## Gate

- Snapshot global and custom placements: copied names, brand, instructions, ordering, set types, reps, weight pairs, resolved rest. Invalid private exercise scope references fail DB constraints. Verify all actual fields are null.
- Copy 135.5 lb and 60 kg; switch preference; copied original numbers/units remain identical.
- Same key/payload sequentially and concurrently returns one session. First new response is 201; replay is 200. Changed routine/date with that key is 409.
- The same key from another user is independent. Invalid key fails the slice input validator with 422. Foreign routine/session is 404.
- Owned archived routine or no active planned sets returns 409 and no snapshot. Replaying a previously successful start still returns it.
- Fail halfway through copy; no parent/child rows survive. Race template edits and brand changes against start; every snapshot matches one committed state.
- Force unique/serialization race; verify a fresh bounded attempt and no duplicate data. Exhausted confirmed transient retry is 503 with safe retry guidance.

## Acceptance

10.1 Complete snapshot. 10.2 Exact targets. 10.3 Unperformed actuals. 10.4 Atomic consistent creation. 10.5 Idempotent retries. 10.6 Off-schedule start. 10.7 Unusable template rejection. 10.8 Brand snapshot. 10.9 Original units retained.

## Carry forward

Every later session mutation begins by SELECT ... FOR UPDATE on the owned workout_sessions row. This serializes set logging, finalization, extra sets, and timers. Read-only DTO assembly does not acquire a write lock.

@progress 10

---page---
# Slice 11 - Record and correct sets

## Already exists

AddSessions already has result columns and session-state constraints. Reuse SessionDto assembly, explicit weight input, and the parent session transaction convention. No new migration.

## Add and implement

1. PUT /sessions/{sessionId}/sets/{setId}/result locks the owned session. Missing/foreign/mismatched chain is 404; non-in_progress session is 409. Validate actualReps>=0, setType, and optional complete weight pair before mutation.
2. Null actualReps means untouched. Recording zero reps still performs the set. On the first result, set completed_at using TimeProvider; corrections preserve that original instant and copied targets.
3. POST /sessions/{sessionId}/exercises/{sessionExerciseId}/sets appends an extra set. Under the same parent lock allocate max(position)+1; targets/results are null; default rest resolves from the copied session exercise unless explicitly overridden.
4. DELETE /sessions/{sessionId}/sets/{setId} removes only an unperformed set. Performed set deletion is 409. Do not renumber retained sets or change their IDs.
5. Keep a firstCompletion boolean in this workflow. 14 will add timer changes in this exact transaction; do not add an event bus or an empty timer service now.

---page---
# Slice 11 - Performed-set verification

## Gate

- Log 8 reps at 135.5 lb, correct values/type, and assert first completion time and targets are unchanged.
- Compare zero reps with untouched null reps; verify null weight versus explicit zero and both units.
- Reject negative/fractional reps, incomplete weights, unknown types, foreign IDs, and wrong session chains without writes.
- Append concurrently; positions are distinct. Extra targets are null and rest is resolved correctly.
- Delete an untouched set; retained IDs stay stable. Performed deletion is 409.
- Seed a final state; all writes return 409. Repeat logging and race against finalization again after 12 exists.

## Acceptance

11.1 Recording performs a set. 11.2 Decimal/null weights work. 11.3 Zero differs from untouched. 11.4 Corrections preserve first time/targets. 11.5 Bounds and chain checks hold. 11.6 Final sessions are immutable. 11.7 Explicit units survive concurrent preference changes.

@progress 11

---page---
# Slice 12 - Finish and browse history

## Already exists

Performed sets, parent session locking, and state columns. Generate AddHistoryIndexes. Extend the sessions feature, not startup.

## Add and implement

1. Complete/abandon actions lock the owned session. Completion requires at least one performed set; abandonment accepts an empty workout. Finalize status and finished_at once from TimeProvider.
2. Same final-state retry returns the original snapshot/time. Switching completed<->abandoned returns 409. All result/extra-set/delete mutations check this same state under the same lock.
3. Count performed sets by nonnull actual_reps/completed_at, including zero. Untouched sets stay null; partial completion does not fabricate results.
4. GET /sessions uses keyset paging by started_at DESC,id DESC. Limit defaults to 20, valid range 1-100. Validate status and a versioned base64 cursor containing the last UTC instant and int ID. Treat cursor data as untrusted and parameterize it; no cursor signing is needed for private filtered reads.
5. Apply user/status filters on every page and request limit+1 to determine nextCursor. Document paging over concurrent inserts; the cursor does not promise a frozen database-wide snapshot.

## Keyset predicate - partial LINQ using slice-10 entities

```csharp
var query = db.WorkoutSessions.AsNoTracking()
    .Where(x => x.UserId == userId);
if (cursor is not null)
    query = query.Where(x => x.StartedAt < cursor.StartedAt
        || (x.StartedAt == cursor.StartedAt && x.Id < cursor.Id));
var rows = await query.OrderByDescending(x => x.StartedAt)
    .ThenByDescending(x => x.Id)
    .Take(limit + 1).Select(x => new SessionSummaryDto(/* fields */))
    .ToListAsync(ct);
```

The projection constructor is intentionally partial. Add status filtering before paging and the actual DTO projection when implementing 12.

For the first page omit the cursor predicate. Validate bounds and serialization precision so ties paginate correctly. A finalization after 14 also clears both timer fields; until then those fields do not exist.

---page---
# Slice 12 - Verify lifecycle and paging

## Gate

- Complete two of five sets; count two performed, preserve three null results. Zero-rep results count as performed.
- Empty completion returns 409; empty abandonment succeeds.
- Repeat each finalization and verify the original finishedAt. Switching final states and later mutations return 409.
- Race result writes, complete, and abandon; one serialized valid outcome persists. No write lands after finalization.
- Traverse several pages with tied startedAt values; no duplicates/omissions in a static fixture. Foreign history never appears.
- Bound invalid limit, status, or cursor returns 422 from the slice query validator; type binding returns 400. Never interpolate untrusted SQL. Verify nextCursor=null on the last page.
- Read 135.5 lb history after metadata/preferences change; snapshot targets, results, brands and units are intact.

## Acceptance

12.1 Final status/time. 12.2 Partial completion. 12.3 Empty completion/abandon rules. 12.4 Same-state replay. 12.5 Stable private paging. 12.6 Immutable snapshots.

## Carry forward

Previous performance in 13 reads these snapshots. Timer in 14 adds a small mutation to the existing first-result and finalization transactions; it does not create a parallel write pathway.

@progress 12

---page---
# Slice 13 - Previous performance

## Already exists

Completed sessions, snapshot DTOs, and the previous-completed index introduced with AddHistoryIndexes. No new table or stored previous-reps field.

## Add and implement

1. GET /routines/{id}/previous-session first verifies the owned routine exists, then finds its latest completed session by finished_at DESC,id DESC. Active and abandoned sessions are excluded.
2. Reuse snapshot read assembly and return {previousSession:null} when no completed session exists. A foreign routine remains 404, not a null response.
3. Match repeated movements by sourceRoutineExerciseId. A new placement has no match even when it uses the same library exercise. Keep individual sets and original brands/weights.
4. Display conversion belongs at the presentation boundary: kilograms = pounds * 0.45359237m; pounds = kilograms / 0.45359237m. Convert from the stored original each time, then round display to two decimals AwayFromZero.
5. Preserve null/zero and fall back to original value/unit if decimal conversion overflows. The API continues returning original pairs; avoid an ambiguous converted value without its unit.

---page---
# Slice 13 - Previous-performance verification

## Gate

- Two completed workouts select the last finished, with ID tie-breaker. A newer abandoned/active session does not replace it.
- Repeated Bench Press placements with different results match their own source IDs. A newly created replacement placement gets no old match.
- No previous workout returns previousSession:null; foreign routine returns 404.
- Verify individual warmup/working results, zero reps, unperformed nulls, copied brands, and exact 135.5 lb.
- Presentation check: 100 lb shows 45.36 kg, then switching back still starts from stored 100 lb. Test null/zero and decimal overflow fallback.

## Acceptance

13.1 Latest completed selection. 13.2 Active/abandoned excluded. 13.3 Placement identity matching. 13.4 Individual result fidelity. 13.5 Null history is normal. 13.6 Display never rewrites history.

@progress 13

---page---
# Slice 14 - Persistent rest deadline

## Already exists

First-completion detection in 11, parent session locks, TimeProvider, and finalization in 12. Generate AddRestTimer and extend SessionDto with serverNow/restTimer.

## Add and implement

1. On first result only, set rest_ends_at=now+resolved seconds and rest_after_set_id=setId in the same result transaction. Zero rest clears both. Another first-completed set replaces the timer; corrections do not restart it.
2. PUT /sessions/{id}/rest-timer stores the supplied UTC EndsAt and clears after-set attribution. Require a bound instant compatible with storage; a past deadline is valid and displays zero. Repeating an absolute deadline is idempotent.
3. DELETE clears both fields. Every timer change locks/checks the same owned in_progress session. Complete/abandon now clear the timer in their existing transactions.
4. Return serverNow from TimeProvider and the persisted deadline; return a restTimer only when rest_ends_at is present. Expiration needs no database write, background job, SignalR server, or server-side ticking loop.
5. Client computes max(0,deadline-adjustedNow), using serverNow to estimate clock offset. Browser refresh/backgrounding must reconstruct from the deadline, not a decrementing counter saved each second.

---page---
# Slice 14 - Timer verification

## Gate

- Fixed-clock first result +120 seconds persists one deadline/afterSetId; zero clears it.
- Refresh shows the same deadline; correcting a result preserves it; another set replaces it.
- Manual absolute update/retry and cancel behave correctly. Manual update has no afterSetId.
- Expired deadlines remain readable and display zero. Test clock offset and background/resume on a real client when available.
- Finalization clears both fields; further timer writes return 409. Foreign session is 404.
- Direct SQL cannot reference a set from another session. Race result/finalization/timer writes and assert consistent final state.

## Acceptance

14.1 Automatic timer. 14.2 Persistent deadline. 14.3 Correct correction/replacement. 14.4 Manual edit/cancel. 14.5 No expiry worker. 14.6 Finalization clears it.

@progress 14

---page---
# Slice 15 - Reorder and archive templates

## Already exists

Archive columns/indexes, owned parent locks, immutable snapshots, and full routine reads. No new schema is needed unless implementation reveals a concrete missing constraint.

## Add and implement

1. Reorder takes exactly the active child IDs, once each. Lock routine parent and validate the complete owned list. Allocate temporary positive positions above the current maximum, then SaveChangesAsync before assigning final 1..N positions and saving again, all inside one explicit transaction. Two saves avoid immediate unique-index collisions; rollback preserves the original order. Check int overflow before any update; reject safely rather than colliding.
2. DELETE planned set/placement archives the source row. Do not physically delete source rows referenced by historical sessions. New starts exclude archived children; old active/completed sessions retain copied values.
3. DELETE routine locks it, archives it, and clears schedule entries in one transaction. Same owned archive returns 204. Metadata changes/start/schedule writes reject the archived state.
4. Personal DELETE /exercises/{id} locks the owned custom row; visible globals return 403. Admin DELETE /admin/exercises/{id} requires the Admin policy and locks only global rows; private/missing IDs return 404. Same authorized archive returns 204. Reject active usage with 409; global usage is checked across all accounts without exposing their data. Placement creation locks the same row; keep routine-before-exercise ordering.
5. To replace a movement, archive old placement and create a new one. Extend slice-05 metadata writes to lock/recheck the exercise before updates now that archive can race them. Brand edits affect routine reads and future starts; they never update session snapshot rows.

## Archive guard - after an authorized exercise row lock

```csharp
var used = await db.RoutineExercises.AnyAsync(x =>
    x.ExerciseId == exerciseId
    && x.ArchivedAt == null && x.Routine.ArchivedAt == null, ct);
```

The authorized row determines scope first. This guard intentionally checks all usages, so a global cannot archive while any account actively uses it. Return a generic 409, never private account/routine details. Execute after taking the exercise lock. A standalone NOT EXISTS followed by an update can race a new placement. Keep errors before writes when possible; rollback any rejected multi-command operation.

---page---
# Slice 15 - Verify editing and historical fidelity

## Gate

- Change name/description, 135 lb target to 140 lb, reps, types, rest, and brand. New starts see changes; previous active/completed sessions keep originals.
- Reorder placements and sets. Duplicate, missing, extra, foreign IDs, and overflow-risk temporary positions are rejected without changing order.
- Archive planned set/placement; future starts omit it, previous snapshots keep it. IDs are never reused.
- Archive a scheduled routine; assignments clear atomically. History stays readable; starts/metadata/schedule changes reject it.
- Global active usage in either of two accounts blocks admin archive with a generic 409. Custom archive remains owner-only. Ordinary global archive returns 403; admin routes cannot archive private IDs. Removing all active usage permits archive without changing snapshots.
- Race exercise archive/placement attach, routine archive/schedule assignment, and reorder/start. Assert one valid serialized result and no mixed snapshot or duplicate position.

## Acceptance

15.1 Template edits work. 15.2 Weight edits preserve snapshots. 15.3 Atomic reordering. 15.4 Removed source content stays historical. 15.5 Archive clears schedule. 15.6 Active snapshots also remain stable. 15.7 Concurrent writes/starts are consistent. 15.8 Brand history remains true. 15.9 Archive/attach race is closed.

## Product milestone

The complete workout API is now implemented. A frontend can create templates, schedule routines, start/log/finish sessions, show prior results, and reconstruct timers. Infrastructure slices make this existing behavior operable on the Linux host.

@progress 15

---page---
# Slice 16 - Prepare Linux production

## Already exists

Local images, Compose network/volumes, configuration validation, and a complete tested API. Keep a single-host deployment; no Kubernetes or cloud service migration is implied.

## Add and implement

1. Verify host CPU, supported Docker Engine/Compose, storage, and system service startup. Create a protected deployment directory with an explicit owner.
2. Supply production connection strings, Clerk issuer/claims, allowed origins, hostname, and image identities outside the repo/images. Restrict file permissions; do not emit resolved secrets through diagnostic commands.
3. Provision separate application and migrator DB roles. Application gets database connect/schema usage, table DML, and sequence privileges needed for identities; migrator owns schema/DDL. Configure grants/default privileges under the actual role creating new tables. API must not have CREATE/DROP/ALTER rights.
4. Choose a supported Postgres major and approved release digest. Keep Postgres state on its named volume and record the matching restore-tool version.
5. Confirm runtime globalization/timezone support and trusted Clerk HTTPS from this host. Required invalid configuration fails startup; DB availability remains a readiness signal.

## Gate

- Bring up the production configuration and verify architecture/versions and persistent data.
- Inspect permissions and tracked/build artifacts; verify secrets are absent from source/layers/logs.
- Application role can perform actual CRUD/identity inserts but cannot create/alter/drop a test table. Migrator role can apply a new EF migration and grants continue to work.
- Invalid required configuration fails clearly without values. User timezone validation works in Linux.

## Acceptance

16.1 Host is compatible. 16.2 Secrets are external/protected. 16.3 Configuration fails safely. 16.4 Roles have intended privileges. 16.5 Versions are controlled.

@progress 16

---page---
# Slice 17 - HTTPS and browser access

## Already exists

Internal API/db services and explicit frontend CORS/Clerk authorized-party validation. Add Caddy and trusted proxy settings for the actual host.

## Add and implement

1. Point a trusted hostname to the host, configure Caddy's TLS/reverse proxy to api:8080, and publish only 80/443. Persist its certificate state.
2. Keep business paths /api/v1 unchanged. Health may be reached by an internal probe; do not expose diagnostic detail endpoints publicly.
3. Configure ASP.NET ForwardedHeaders before middleware that uses scheme/client IP. Trust only the actual proxy address/network and intended forwarded headers. Never clear trusted-proxy restrictions to accept any client [12].
4. Let the edge proxy own HTTPS redirect/certificate renewal. Internal API remains HTTP; do not introduce an internal redirect loop. Test the generated Location scheme through the proxy.
5. Test browser preflight and bearer requests with exact allowed origins. No wildcard credentialed CORS. Keep tokens/headers out of Caddy logs.

## Gate

- An intended device trusts the hostname/certificate and can authenticate through HTTPS.
- HTTP redirects to HTTPS; direct API/DB ports are unreachable externally.
- Allowed-origin preflight succeeds; disallowed origin receives no permission. Authentication is still enforced regardless of Origin.
- Recreate proxy; certificate state remains and renewal is enabled.
- Spoofed forwarded headers from an untrusted path do not change trusted scheme/IP; generated Location works.

## Acceptance

17.1 Trusted HTTPS. 17.2 HTTP redirect. 17.3 Private internal services. 17.4 Explicit browser origins. 17.5 Persistent certificates.

@progress 17

---page---
# Slice 18 - Repeatable deployment

## Already exists

Production host, image pair, migrator role, migration journal, readiness and HTTPS. Add one concrete runbook or small shell script for this host.

## Add and implement

1. Lock deployments with native flock on one protected host lock file. Identify both images by one release ID and pinned digest. Pull/build before activation.
2. Take/verify the required backup and select a compatible migration plan. For breaking schema changes use a documented maintenance window; do not imply an automatic rollback can undo data loss.
3. Run a fresh one-shot migrator from the new release using docker compose run --rm migrations. Check exit code. A previous service_completed_successfully container is not evidence that this release ran migrations.
4. On success activate the API image, then gate on /health plus an authenticated representative API read/write through HTTPS. A connectivity probe alone cannot prove schema/API compatibility.
5. On failure leave the new API inactive. If the old API keeps serving while migrations run, all changes must be backward-compatible. Otherwise deliberately stop writes under maintenance. Retain the previous image/config and document compatible rollback or restore.

---page---
# Slice 18 - Deployment verification

## Gate

- Fresh install and seeded previous-release upgrade both follow the same EF migration path.
- A new unapplied EF migration runs exactly once; a failing migration blocks activation and does not pretend earlier committed migrations rolled back.
- Concurrent deploy attempts serialize. Verify interrupted deployment can be safely inspected and resumed.
- Upgrade preserves ownership, placements, snapshots, brands, preferences, exact decimals and original units.
- Rehearse compatible image rollback and incompatible-change restore. Never rewrite a journaled migration.

## Acceptance

18.1 Fresh deployment. 18.2 New release runs its own migrator. 18.3 Failed migration blocks activation. 18.4 Data is preserved. 18.5 Deployment is serialized. 18.6 Recovery respects schema compatibility.

Legacy UUID/brand/pounds-only upgrade cases are conditional work only if actual retained legacy data exists; they are not part of the fresh-install critical path.

@progress 18

---page---
# Slice 19 - Backup and restore

## Already exists

Protected host configuration, controlled Postgres major, deployment runbook, and production data. Add a systemd timer/service or the host's existing scheduler; do not build an application backup worker.

## Add and implement

1. Daily pg_dump custom-format archive with the matching supported tool version. Write to a temporary filename; check command success and pg_restore --list before marking the archive complete.
2. Record failures with a nonzero exit. Do not treat an empty/partial archive as valid. Retain seven days in an explicitly designated backup directory; restrict deletion to that directory.
3. Copy completed archives off-host to a retrievable protected destination. Protect deployment configuration/secrets separately; logical DB dumps do not replace host/role/config recovery.
4. Document restore into a separate empty database, including roles/grants, migration journal, application configuration, and the exact release image. Verify users/templates/snapshots, not just table counts.
5. Rehearse recovery without original containers and record measured duration. Identity sequence state is part of restore; an ordinary new insert must not collide.

## Gate

- Backup a seeded DB; inspect archive and retrieve the off-host copy.
- Break DB authentication/destination; failure is visible and no incomplete file is retained as a successful backup.
- Test seven-day boundary retention inside a disposable directory.
- Restore separately and verify accounts, schedule, source IDs, snapshots, zero/null reps/weights, 135.5 lb, 60 kg, original units, null/named brands and preferences.
- Create a new row after restore to check sequence state. Recover protected config without relying on original containers; record recovery time.

## Acceptance

19.1 Usable daily archives. 19.2 Visible failure. 19.3 Retention and off-host retrieval. 19.4 Data fidelity. 19.5 Independent recovery. 19.6 Config/roles recoverable. 19.7 Brands preserved. 19.8 Identities/units preserved.

@progress 19

---page---
# Slice 20 - Recovery and diagnostics

## Already exists

Working release/deploy/backup/restore procedures. Add bounded logging configuration and a short operator runbook. Reuse ILogger and native Docker log rotation first.

## Add and implement

1. Enable Docker at host startup and restart policies for long-running db/api/proxy. Migrator remains one-shot. Reboot/crash checks use existing data volumes.
2. Bound connection, command, shutdown and outbound metadata operations. Verify readiness moves 503->200 after DB recovery; do not restart every API process for one failed connection.
3. Log request trace ID, method/route, status, duration, and safe error classification. Never log JWTs, authorization headers, connection strings, or full bodies/SQL parameters. Use structured ILogger fields, not string-concatenated secret data.
4. Keep unexpected faults as sanitized 500. Translate positively identified dependency failures to 503; expected uniqueness/state conflicts stay 409. If IExceptionHandler is introduced, handle .NET 10's diagnostic suppression deliberately, with safe logging exactly once [13].
5. Set Docker log max-size/max-file limits. Document health, logs, restart, deploy, backup, restore, and last successful recovery evidence in one runbook.

---page---
# Slice 20 - Recovery verification

## Gate

- Reboot the host and crash API/proxy; services recover, volumes remain intact, migrations do not loop.
- Restart Postgres; health becomes 503 and recovers. Interrupt start/result/timer transactions and verify full commit or rollback; safe retry preserves idempotency and units.
- Repeat SIGTERM during real writes and confirm graceful shutdown or rollback.
- Trigger rotation; disk use stays bounded. Inspect logs for useful trace/status/duration and absence of test secrets.
- Follow the runbook from a clean operator session; restore the off-host backup independently.

## Acceptance

20.1 Reboot recovery. 20.2 Crash restart. 20.3 DB reconnection. 20.4 Atomic interrupted writes. 20.5 Useful safe logs. 20.6 Bounded disk usage. 20.7 Routine operations preserve data.

@progress 20

---page---
# Final release gate

Run against the Linux host through HTTPS. Retain evidence before marking the app release-ready.

- Sign in with two ordinary accounts and an admin; verify global visibility, admin-only global writes, private isolation even from admins, native 401/403, and foreign-private 404.
- Create all five routines and weekday assignments with descriptions, optional brands, repeated movements, warmup/working sets, rest overrides, and mixed lb/kg targets.
- Start with a retry key, retry concurrently, log zero and nonzero reps at 135.5 lb/60 kg, correct a result, add/remove an extra set, reconstruct the timer, and finish partially.
- Edit template targets and library brand; old active/completed snapshots remain identical and new snapshots use current metadata. Previous performance matches source placements.
- Switch imperial/metric preferences; display converts from original stored weights. Verify explicit units during preference changes and preserve null versus zero.
- Recreate containers/reboot; validate persistent state, readiness recovery, trusted HTTPS, private internal ports, bounded safe logs and interrupted-write rollback.
- Restore an off-host backup separately, then insert a new row. Verify identities, source relationships, units, preferences, brands, timers, grants and migration journal.
- Rehearse failed new migration, serialized deployments, compatible rollback and incompatible-change recovery. Record the tested release digests.

## Current implementation and evidence

The repository implements slices 01-03 with EF persistence and a separate MigrateAsync executable. The transition verification record and slice-02/03 records contain the actual build, real-Postgres, image, and Compose checks. PDF regeneration does not prove a feature gate passed.

Slice 04 implements Clerk identity, local profiles and preferences; its original gate passed with 35 tests (docs/slice-04-verification.md). Slice 05 adds global/private exercises and native admin policies. Its gate passed with 72 tests, seeded EF upgrade/replay and refreshed image/Compose checks (docs/slice-05-verification.md). Slices 06-20 remain planned. SQL contracts describe future mappings, not authorization to generate all tables now.

Request validation now uses automatically registered FluentValidation validators and standardized 422 errors. The follow-up verification passed 89 tests with no skips; see docs/request-validation-verification.md. Earlier slice records retain their original evidence and validation policy.

## Continue with 06 when requested

Retain the scoped context, native migration history, slim startup, exercise validation, global/private boundaries and shared KiloApiFactory. Routine templates remain private; placement/snapshot access keys arrive only in 07/10 and archive writes in 15. Configure the real Clerk role/session claim as documented. No future feature scaffolding is needed.

---page---
# API reference - route catalog

All business paths below are prefixed /api/v1 and protected after 04. IDs are positive ints. The path variable names distinguish library exercise, routine placement, and session exercise. Request validators return 422 for invalid bound input; malformed JSON/type binding returns 400. Both use ValidationProblemDetails.

| Slice | Method / path | Request -> result |
| --- | --- | --- |
| 01 | GET /health (unprefixed) | Public readiness 200/503 text |
| 04 | GET /me | UserDto |
| 04 | PUT /me/preferences | PreferencesRequest -> UserDto |
| 05 | GET /exercises | Visible global + own custom ExerciseDto[] |
| 05 | POST /exercises | Create own custom -> 201 ExerciseDto |
| 05 | GET /exercises/{id} | ExerciseDto |
| 05 | PUT /exercises/{id} | Replace own custom metadata -> ExerciseDto |
| 15 | DELETE /exercises/{id} | Own custom archive; global 403; usage 409 |
| 06 | GET /routines | includeArchived=false -> RoutineSummaryDto[] |
| 06 | POST /routines | NameDescriptionRequest -> 201 RoutineDto |
| 06 | GET /routines/{id} | Ordered RoutineDto |
| 06 | PUT /routines/{id} | NameDescriptionRequest -> RoutineDto |
| 15 | DELETE /routines/{id} | Archive/clear schedule -> 204 |
| 07 | POST /routines/{id}/exercises | PlacementCreateRequest -> 201 placement |
| 07 | PUT /routines/{id}/exercises/{placementId} | PlacementUpdateRequest -> placement |
| 15 | DELETE /routines/{id}/exercises/{placementId} | Archive -> 204 |
| 15 | PUT /routines/{id}/exercises/order | ReorderRequest -> RoutineDto |

Use versioned CreatedAtAction links for creates. PUT replaces the editable fields represented by its request. Route mismatch/foreign resource is 404; owned archived state is 409 for writes. Repeated owned archives return 204.

---page---
# API reference - Global catalog administration

All routes use the /api/v1 prefix. AdminExercisesController requires the native Admin policy. Ordinary authenticated callers receive 403; missing/invalid bearer receives 401. Existing GET /exercises and GET /exercises/{id} supply catalog reads for everyone.

| Slice | Method / route | Contract |
| --- | --- | --- |
| 05 | POST /admin/exercises | ExerciseWriteRequest -> 201 global ExerciseDto |
| 05 | PUT /admin/exercises/{id} | Replace global metadata -> ExerciseDto |
| 15 | DELETE /admin/exercises/{id} | Global archive -> 204; active usage 409 |

Admin writes match global rows only; a private/missing ID returns 404. Archived metadata update returns 409. Creation Location points to the shared versioned detail route. Archive is blocked by active usage in any account, with a generic response that reveals no private routine/user details.

Owner, role and scope are never writable request fields. Personal creation always sets the verified caller's owner, while global creation sets a null owner. The DTO's isGlobal is derived from persistence. Neither role nor global ownership gives access to other users' custom exercises.

---page---
# API reference - sets, schedule, sessions

| Slice | Method / path (prefix /api/v1) | Request -> result |
| --- | --- | --- |
| 08 | POST /routines/{id}/exercises/{placementId}/sets | RoutineSetRequest -> 201 set |
| 08 | PUT /routines/{id}/exercises/{placementId}/sets/{setId} | RoutineSetRequest -> set |
| 15 | DELETE /routines/{id}/exercises/{placementId}/sets/{setId} | Archive -> 204 |
| 15 | PUT /routines/{id}/exercises/{placementId}/sets/order | ReorderRequest -> placement |
| 09 | GET /schedule | ScheduleEntryDto[] |
| 09 | PUT /schedule/{weekday} | ScheduleRequest -> ScheduleEntryDto |
| 09 | DELETE /schedule/{weekday} | Idempotent clear -> 204 |
| 10 | POST /routines/{id}/sessions | StartSessionRequest + key -> 201/200 SessionDto |
| 10 | GET /sessions/{id} | SessionDto |
| 12 | GET /sessions | limit/cursor/status -> PageResponse |
| 11 | PUT /sessions/{sessionId}/sets/{setId}/result | SetResultRequest -> SessionSetDto |
| 11 | POST /sessions/{sessionId}/exercises/{sessionExerciseId}/sets | ExtraSetRequest -> 201 set |
| 11 | DELETE /sessions/{sessionId}/sets/{setId} | Unperformed only -> 204 |
| 12 | POST /sessions/{id}/complete | SessionDto |
| 12 | POST /sessions/{id}/abandon | SessionDto |
| 13 | GET /routines/{id}/previous-session | PreviousSessionResponse |
| 14 | PUT /sessions/{id}/rest-timer | RestTimerRequest -> SessionDto |
| 14 | DELETE /sessions/{id}/rest-timer | Clear -> 204 |

35 product routes total: 34 business routes and one public readiness route. Development OpenAPI is tooling, not an additional product operation. Unsupported URL versions do not execute v1 actions.

Timestamps are UTC ISO 8601, scheduledDate is YYYY-MM-DD. Warmup/working types, in_progress/completed/abandoned states, imperial/metric preferences, and lb/kg units are explicit lowercase strings. Null actualReps is untouched; zero is performed. Null weight is unrecorded; zero weight is recorded zero.

---page---
# Contract reference - requests

These are wire field definitions, not duplicate types to paste into every feature. Create a FluentValidation validator beside each actual request type, registered automatically in DI. Required numeric inputs must detect omission (nullable + NotNull, then range validation); do not accidentally turn missing reps/value into valid zero. Optional fields have explicit null semantics.

```text
PreferencesRequest
  timeZone: required IANA string (UTC allowed)
  measurementSystem: required imperial|metric
NameDescriptionRequest
  name: required nonblank string
  description: optional string -> ""
ExerciseWriteRequest
  name, description as above
  brandName: optional string -> trimmed null or <=100 characters
PlacementCreateRequest
  exerciseId: required positive int (library ID)
  position: required positive int
  description: optional string -> ""
  defaultRestSeconds: required int >=0
PlacementUpdateRequest
  position, description, defaultRestSeconds (exerciseId is immutable)
WeightInput
  value: required decimal >=0; unit: required lb|kg
RoutineSetRequest
  position: required int >0; setType: required warmup|working
  targetRepsMin: required int >0; targetRepsMax: required int >=min
  targetWeight: optional complete WeightInput; restSeconds: optional int >=0
ReorderRequest
  orderedIds: required int[]; exact active children once each
ScheduleRequest
  routineId: required positive int
StartSessionRequest
  scheduledDate: optional date
  header Idempotency-Key: required 1-128 visible ASCII characters
SetResultRequest
  actualReps: required int >=0; setType: required warmup|working
  actualWeight: optional complete WeightInput
ExtraSetRequest
  setType: required warmup|working; restSeconds: optional int >=0
RestTimerRequest
  endsAt: required DateTimeOffset compatible with UTC storage
```

Omitted/null actualWeight clears its recorded pair on replacement. An absent targetWeight means no planned weight. A valid zero needs value=0 and a unit. Normalize optional description/brand once before writing; reuse those values for validation and entity changes.

---page---
# Contract reference - responses

Nested collections are ordered and nonnull. UTC instant fields map from internal UTC DateTime (or zero-offset DateTimeOffset at the boundary). Database rows stay separate from these nested objects.

```text
UserDto: id, clerkUserId, timeZone, measurementSystem
WeightDto: value(decimal), unit(lb|kg)
ExerciseDto: id, name, description, brandName?, isGlobal, archivedAt?
RoutineSummaryDto: id, name, description, archivedAt?
RoutineSetDto:
  id, position, setType, targetRepsMin, targetRepsMax,
  targetWeight?, restSeconds?
RoutineExerciseDto:
  id(placement), exerciseId(library), exerciseName, brandName?,
  position, description, defaultRestSeconds, sets[]
RoutineDto:
  id, name, description, archivedAt?, exercises[]
ScheduleEntryDto: weekday, routineId, routineName
SessionSetDto:
  id, position, setType, targetRepsMin?, targetRepsMax?, targetWeight?,
  actualReps?, actualWeight?, restSeconds, completedAt?
SessionExerciseDto:
  id(session child), exerciseId(library), sourceRoutineExerciseId,
  exerciseName, brandName?, position, description,
  defaultRestSeconds, sets[]
SessionSummaryDto:
  id, routineId, routineName, status, scheduledDate?,
  startedAt, finishedAt?, performedSetCount
RestTimerDto: endsAt, afterSetId?
SessionDto:
  id, routineId, routineName, routineDescription, status,
  scheduledDate?, startedAt, finishedAt?, serverNow, restTimer?, exercises[]
PreviousSessionResponse: previousSession?
PageResponse<SessionSummaryDto>: items[], nextCursor?
```

Rest fields/serverNow are added to session responses in 14. If API consumers already exist by then, adding these optional fields is additive; do not rename or reinterpret existing fields. Maintain OpenAPI response metadata for actual action outcomes as the feature is implemented.

RoutineExerciseDto brand/name reflect the current library. SessionExerciseDto fields reflect only the copied snapshot. Performed count includes zero reps. History uses snapshot routineName, not a join that silently changes old workouts.

---page---
# Primary references

Documentation checked while rebuilding this guide. Links support the framework mechanisms; the workout rules and slice order are this project's design decisions.

1. Microsoft: controller API behavior, model validation and ProblemDetails. https://learn.microsoft.com/en-us/aspnet/core/web-api/?view=aspnetcore-10.0
2. .NET Foundation: current ASP.NET API Versioning project and package lineage. https://github.com/dotnet/aspnet-api-versioning
3. API Versioning: URL segment route matching. https://dotnet.github.io/aspnet-api-versioning/aspnet-core/how-to/version-by-url.html
4. Npgsql: EF Core PostgreSQL provider. https://www.npgsql.org/efcore/
5. Microsoft: efficient EF querying and projections. https://learn.microsoft.com/en-us/ef/core/performance/efficient-querying
6. Microsoft: SDK selection with global.json. https://learn.microsoft.com/en-us/dotnet/core/tools/global-json
7. Microsoft: native ASP.NET Core health checks. https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/health-checks?view=aspnetcore-10.0
8. Microsoft: managing and applying EF migrations. https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/managing and https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/applying
9. Docker: official Postgres image configuration and version 18 volume layout. https://hub.docker.com/_/postgres
10. Clerk: session-token verification and authorized parties. https://clerk.com/docs/guides/sessions/manual-jwt-verification
11. PostgreSQL: transaction isolation. https://www.postgresql.org/docs/current/transaction-iso.html
12. Microsoft: trusted proxies and forwarded headers. https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0
13. Microsoft: .NET 10 handled-exception diagnostic suppression. https://learn.microsoft.com/en-us/aspnet/core/breaking-changes/10/exception-handler-diagnostics-suppressed?view=aspnetcore-10.0
14. NuGet: checked foundation versioning and OpenAPI packages. https://www.nuget.org/packages/Asp.Versioning.Mvc/10.2.1 and https://www.nuget.org/packages/Microsoft.AspNetCore.OpenApi/10.0.12

15. Microsoft: EF transactions and savepoints. https://learn.microsoft.com/en-us/ef/core/saving/transactions
16. Microsoft: scoped DbContext lifetime and configuration. https://learn.microsoft.com/en-us/ef/core/dbcontext-configuration/

The following SQL is a schema contract for reviewed EF mappings and generated PostgreSQL output. It is not an executable script set. Configure explicit table/column names, identity ALWAYS, checks, defaults, filtered indexes, composite keys/FKs, and delete behavior in Fluent mappings. Use DeleteBehavior.Restrict for historical source references; do not inherit cascading deletion accidentally. Generate only the migration required by the current slice.


---page---
# Primary references - Authorization and shared exercises

17. Microsoft: native role policies and RequireRole. https://learn.microsoft.com/en-us/aspnet/core/security/authorization/roles?view=aspnetcore-10.0
18. Microsoft: NameClaimType and RoleClaimType for verified claims. https://learn.microsoft.com/en-us/aspnet/core/security/authentication/claims?view=aspnetcore-10.0
19. Clerk: metadata-based admin roles, adapted to this API's native policy. https://clerk.com/docs/guides/secure/basic-rbac
20. Clerk: individual metadata fields as compact session claims and refresh limitations. https://clerk.com/docs/guides/users/extending and https://clerk.com/docs/guides/sessions/customize-session-tokens
21. PostgreSQL 18: stored generated columns and composite foreign-key/null behavior. https://www.postgresql.org/docs/18/ddl-generated-columns.html and https://www.postgresql.org/docs/18/ddl-constraints.html
22. EF Core: key properties cannot use generation on update. https://github.com/dotnet/efcore/blob/v10.0.0/src/EFCore/Infrastructure/ModelValidator.cs
23. FluentValidation: async controller validation and assembly-based DI registration. https://docs.fluentvalidation.net/en/latest/aspnet.html and https://docs.fluentvalidation.net/en/latest/di.html
24. NuGet: pinned FluentValidation.DependencyInjectionExtensions 12.1.1. https://www.nuget.org/packages/FluentValidation.DependencyInjectionExtensions/12.1.1

The global/private product model, fixed-scope endpoints and slice timing are project decisions. These sources support the framework mechanisms; they do not authorize extra role management screens, custom handlers, seed catalogs, or future code.

---page---
# Schema contract - CreateUsers / introduced in 01

```sql
CREATE TABLE users (
    id              integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    clerk_user_id   text NOT NULL UNIQUE,
    time_zone       text NOT NULL DEFAULT 'UTC',
    measurement_system text NOT NULL DEFAULT 'imperial'
                       CHECK (measurement_system IN ('imperial', 'metric')),
    created_at      timestamptz NOT NULL DEFAULT now(),
    CHECK (btrim(clerk_user_id) <> '')
);
```


---page---
# Schema contract - AddExercises / introduced in 05

```sql
CREATE TABLE exercises (
    id              integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    user_id         integer REFERENCES users(id),
    name            text NOT NULL,
    description     text NOT NULL DEFAULT '',
    brand_name      text CHECK (brand_name IS NULL OR
                        (btrim(brand_name) <> '' AND length(brand_name) <= 100)),
    archived_at     timestamptz,
    created_at      timestamptz NOT NULL DEFAULT now(),
    CHECK (user_id IS NULL OR user_id > 0),
    CHECK (btrim(name) <> '')
);
CREATE INDEX exercises_active_list
    ON exercises(user_id, name)
    WHERE archived_at IS NULL;
```

Null user_id is global; a positive user_id references its custom owner. Use restrictive deletion. AddExercises does not add placement/snapshot tables or their derived access key. Only authorized admin routes create globals; the normal route sets its owner from CurrentUser. No owner or scope transfer operation is exposed.

---page---
# Schema contract - AddRoutines / introduced in 06

```sql
CREATE TABLE routines (
    id              integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    user_id         integer NOT NULL REFERENCES users(id),
    name            text NOT NULL,
    description     text NOT NULL DEFAULT '',
    archived_at     timestamptz,
    created_at      timestamptz NOT NULL DEFAULT now(),
    UNIQUE (id, user_id),
    CHECK (btrim(name) <> '')
);
CREATE INDEX routines_active_list
    ON routines(user_id, name)
    WHERE archived_at IS NULL;
```


---page---
# Schema contract - Library access key / introduced in 07

AddRoutineExercises extends the existing exercise table before creating placements. A nullable (exercise_id,user_id) foreign key would skip checking global NULL values. Use a nonnullable derived key with a native composite FK/check instead [21].

```sql
ALTER TABLE exercises
    ADD COLUMN access_scope_id integer
    GENERATED ALWAYS AS (coalesce(user_id, 0)) STORED;
ALTER TABLE exercises
    ADD CONSTRAINT exercises_id_access_scope_key
    UNIQUE (id, access_scope_id);
```

Zero denotes global scope internally; a positive value is the real custom owner's ID. Zero is not an entity ID or user row. Requests and DTOs never contain this key. A placement/session child stores exercise_scope_id, with CHECK (exercise_scope_id = 0 OR exercise_scope_id = user_id), and references the composite library key.

In EF, explicitly configure the shadow key and its immutable generation behavior. HasComputedColumnSql alone defaults to generation on update, which EF rejects for key properties [22]. This partial mapping uses Microsoft.EntityFrameworkCore.Metadata.PropertySaveBehavior:

```csharp
var scope = exercise.Property<int>("AccessScopeId")
    .HasColumnName("access_scope_id")
    .HasComputedColumnSql("coalesce(user_id, 0)", stored: true)
    .ValueGeneratedOnAdd();
scope.Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
exercise.HasAlternateKey("Id", "AccessScopeId");
```

Owner/scope never change after creation, so EF reads the generated key on insert and keeps it fixed. Map the child's scope as a nonnullable int and its composite relationship with DeleteBehavior.Restrict. Native EF migration metadata tracks these mappings.

Assign child scope from the selected visible library row (UserId ?? 0). A forged private exercise under global scope 0 fails the FK; another user's private scope fails the owner check. Existing parent/child tenant FKs stay in place. Owner/scope remain immutable through API requests.

Do this in 07, when the first child reference exists; copy the validated key into SessionExercise in 10. Keep historical references and restrictive deletion. No trigger, generic permissions table, or custom database access layer is required.

---page---
# Schema contract - AddRoutineExercises / introduced in 07

```sql
CREATE TABLE routine_exercises (
    id                      integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    user_id                 integer NOT NULL REFERENCES users(id),
    routine_id              integer NOT NULL,
    exercise_id             integer NOT NULL,
    exercise_scope_id       integer NOT NULL,
    position                integer NOT NULL CHECK (position > 0),
    description             text NOT NULL DEFAULT '',
    default_rest_seconds    integer NOT NULL DEFAULT 120
                            CHECK (default_rest_seconds >= 0),
    archived_at             timestamptz,
    UNIQUE (id, user_id),
    FOREIGN KEY (routine_id, user_id)
        REFERENCES routines(id, user_id),
    CHECK (exercise_scope_id = 0 OR exercise_scope_id = user_id),
    FOREIGN KEY (exercise_id, exercise_scope_id)
        REFERENCES exercises(id, access_scope_id)
);
CREATE UNIQUE INDEX routine_exercises_active_position
    ON routine_exercises(routine_id, position)
    WHERE archived_at IS NULL;
```


---page---
# Schema contract - AddRoutineSets / introduced in 08

```sql
CREATE TABLE routine_sets (
    id                      integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    user_id                 integer NOT NULL REFERENCES users(id),
    routine_exercise_id     integer NOT NULL,
    position                integer NOT NULL CHECK (position > 0),
    set_type                text NOT NULL
                            CHECK (set_type IN ('warmup', 'working')),
    target_reps_min         integer NOT NULL CHECK (target_reps_min > 0),
    target_reps_max         integer NOT NULL,
    target_weight           numeric CHECK (target_weight >= 0
                            AND target_weight < 'Infinity'::numeric),
    target_weight_unit      text CHECK (target_weight_unit IN ('lb', 'kg')),
    CHECK ((target_weight IS NULL) = (target_weight_unit IS NULL)),
    rest_seconds            integer CHECK (rest_seconds >= 0),
    archived_at             timestamptz,
    CHECK (target_reps_max >= target_reps_min),
    FOREIGN KEY (routine_exercise_id, user_id)
        REFERENCES routine_exercises(id, user_id)
);
CREATE UNIQUE INDEX routine_sets_active_position
    ON routine_sets(routine_exercise_id, position)
    WHERE archived_at IS NULL;
```


---page---
# Schema contract - AddRoutineSchedule / introduced in 09

```sql
CREATE TABLE routine_schedule (
    user_id         integer NOT NULL REFERENCES users(id),
    weekday         smallint NOT NULL CHECK (weekday BETWEEN 1 AND 7),
    routine_id      integer NOT NULL,
    PRIMARY KEY (user_id, weekday),
    FOREIGN KEY (routine_id, user_id)
        REFERENCES routines(id, user_id)
);
CREATE INDEX routine_schedule_routine
    ON routine_schedule(routine_id);
```


---page---
# Schema contract - AddSessions / part 1 / introduced in 10

Map all three session entities and constraints, then generate one AddSessions EF migration. These four pages describe one schema contract, not separate files to execute.

```sql
CREATE TABLE workout_sessions (
    id                      integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    user_id                 integer NOT NULL REFERENCES users(id),
    routine_id              integer NOT NULL,
    -- Copied template metadata.
    routine_name            text NOT NULL,
    routine_description     text NOT NULL DEFAULT '',
    scheduled_date          date,
    status                  text NOT NULL DEFAULT 'in_progress'
                            CHECK (
                                status IN (
                                    'in_progress',
                                    'completed',
                                    'abandoned'
                                )
                            ),
    started_at              timestamptz NOT NULL DEFAULT now(),
    finished_at             timestamptz,
    start_request_key       text NOT NULL
                            CHECK (length(start_request_key) BETWEEN 1 AND 128),
    UNIQUE (id, user_id),
    UNIQUE (user_id, start_request_key),
    FOREIGN KEY (routine_id, user_id)
        REFERENCES routines(id, user_id),
    CHECK (
        (status = 'in_progress' AND finished_at IS NULL)
        OR
        (status IN ('completed', 'abandoned')
            AND finished_at IS NOT NULL)
    ),
    CHECK (
        finished_at IS NULL OR finished_at >= started_at
    )
);
```


---page---
# Schema contract - AddSessions / part 2 / same EF migration

Map all three session entities and constraints, then generate one AddSessions EF migration. These four pages describe one schema contract, not separate files to execute.

```sql
CREATE TABLE session_exercises (
    id                          integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    user_id                     integer NOT NULL REFERENCES users(id),
    session_id                  integer NOT NULL,
    exercise_id                 integer NOT NULL,
    exercise_scope_id           integer NOT NULL,
    source_routine_exercise_id  integer NOT NULL,
    -- Copied exercise metadata.
    exercise_name               text NOT NULL,
    brand_name                  text CHECK (brand_name IS NULL OR
                                    (btrim(brand_name) <> ''
                                     AND length(brand_name) <= 100)),
    description                 text NOT NULL DEFAULT '',
    position                    integer NOT NULL CHECK (position > 0),
    default_rest_seconds        integer NOT NULL
                                CHECK (default_rest_seconds >= 0),
    UNIQUE (session_id, position),
    UNIQUE (id, session_id, user_id),
    FOREIGN KEY (session_id, user_id)
        REFERENCES workout_sessions(id, user_id),
    CHECK (exercise_scope_id = 0 OR exercise_scope_id = user_id),
    FOREIGN KEY (exercise_id, exercise_scope_id)
        REFERENCES exercises(id, access_scope_id),
    FOREIGN KEY (source_routine_exercise_id, user_id)
        REFERENCES routine_exercises(id, user_id)
);
```


---page---
# Schema contract - AddSessions / part 3 / same EF migration

Map all three session entities and constraints, then generate one AddSessions EF migration. These four pages describe one schema contract, not separate files to execute.

```sql
CREATE TABLE session_sets (
    id                      integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    user_id                 integer NOT NULL REFERENCES users(id),
    session_id              integer NOT NULL,
    session_exercise_id     integer NOT NULL,
    position                integer NOT NULL CHECK (position > 0),
    set_type                text NOT NULL
                            CHECK (set_type IN ('warmup', 'working')),
    -- Copied targets; extra sets may have no rep target.
    target_reps_min         integer,
    target_reps_max         integer,
    target_weight           numeric CHECK (target_weight >= 0
                            AND target_weight < 'Infinity'::numeric),
    target_weight_unit      text CHECK (target_weight_unit IN ('lb', 'kg')),
    CHECK ((target_weight IS NULL) = (target_weight_unit IS NULL)),
    -- Actual performance.
    actual_reps             integer CHECK (actual_reps >= 0),
    actual_weight           numeric CHECK (actual_weight >= 0
                            AND actual_weight < 'Infinity'::numeric),
    actual_weight_unit      text CHECK (actual_weight_unit IN ('lb', 'kg')),
    CHECK ((actual_weight IS NULL) = (actual_weight_unit IS NULL)),
    completed_at            timestamptz,
    -- Resolved when the set is created.
    rest_seconds            integer NOT NULL CHECK (rest_seconds >= 0),
    UNIQUE (session_exercise_id, position),
    UNIQUE (id, session_id, user_id),
    FOREIGN KEY (session_exercise_id, session_id, user_id)
        REFERENCES session_exercises(id, session_id, user_id),
    CHECK (
        (target_reps_min IS NULL AND target_reps_max IS NULL)
        OR
        (
            target_reps_min IS NOT NULL
            AND target_reps_max IS NOT NULL
            AND target_reps_min > 0
            AND target_reps_max >= target_reps_min
        )
    ),
    CHECK (
        (actual_reps IS NULL AND completed_at IS NULL)
        OR
        (actual_reps IS NOT NULL AND completed_at IS NOT NULL)
    )
);
```


---page---
# Schema contract - AddSessions / part 4 / same EF migration

Map all three session entities and constraints, then generate one AddSessions EF migration. These four pages describe one schema contract, not separate files to execute.

```sql
ALTER TABLE session_sets ADD CONSTRAINT session_sets_weight_requires_result
    CHECK (actual_reps IS NOT NULL OR actual_weight IS NULL);
CREATE INDEX session_sets_session
    ON session_sets(session_id);
```

Session targets/results and lifecycle exist in 10. Their endpoints arrive in 11-12. The extra check prevents a stored weight on an untouched set. No timer columns exist until AddRestTimer.


---page---
# Schema contract - AddHistoryIndexes / introduced in 12

```sql
CREATE INDEX workout_sessions_history
    ON workout_sessions(user_id, started_at DESC, id DESC);
CREATE INDEX workout_sessions_previous_completed
    ON workout_sessions(
        user_id,
        routine_id,
        finished_at DESC,
        id DESC
    )
    WHERE status = 'completed';
```


---page---
# Schema contract - AddRestTimer / introduced in 14

```sql
ALTER TABLE workout_sessions
    ADD COLUMN rest_ends_at timestamptz,
    ADD COLUMN rest_after_set_id integer,
    ADD CONSTRAINT workout_sessions_timer_pair CHECK (
        rest_ends_at IS NOT NULL OR rest_after_set_id IS NULL),
    ADD CONSTRAINT workout_sessions_final_timer CHECK (
        status = 'in_progress'
        OR (rest_ends_at IS NULL AND rest_after_set_id IS NULL)),
    ADD CONSTRAINT workout_sessions_rest_set_fk
        FOREIGN KEY (rest_after_set_id, id, user_id)
        REFERENCES session_sets(id, session_id, user_id);
```

Configure these changes in the EF model, then generate AddRestTimer and review its operations. Timer attribution must reference a set from the exact session and owner. Finalization must clear both fields.
