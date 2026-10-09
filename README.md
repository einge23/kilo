# Kilo

The current implementation covers slices 01-06 of the [workout tracker plan](docs/workout-tracker-plan.md): controller host, URL API versioning, scoped EF Core/Postgres persistence, readiness, separate EF migrator, release images, local Compose, Clerk identity/preferences, the global/private exercise library, and private routine templates. Verification is recorded in [slice 04](docs/slice-04-verification.md), [slice 05](docs/slice-05-verification.md), and [slice 06](docs/slice-06-verification.md). The [EF transition record](docs/ef-core-transition.md) summarizes the completed transition and cleanup checks.

The [PDF workbook](output/pdf/workout-tracker-dotnet10-revised-plan.pdf) contains the cumulative implementation guide. Agents must follow [AGENTS.md](AGENTS.md). Edit the plan's Markdown source and regenerate the PDF with `python docs/build_workout_plan.py` (requires ReportLab and pypdf).

## Exercise library (slice 05)

GET `/api/v1/exercises` returns active global entries plus the caller's custom entries, ordered by name then ID; `?includeArchived=true` includes visible archives. GET `/api/v1/exercises/{id}` reads visible detail. POST/PUT on personal routes creates/replaces only the caller's custom metadata. Global POST/PUT uses `/api/v1/admin/exercises` and the native ASP.NET Core `Admin` policy (`RequireAuthenticatedUser()` and `RequireRole("admin")`); JWT roles map to the signed Clerk `role` claim. Admins cannot access another account's private entries. Both routes share name/description/brand validation and return an `isGlobal` DTO without owner IDs.

Run the separate migrator to apply additive `20261008230628_AddExercises`; the API never migrates on startup. Archive endpoints remain slice 15, and placements/snapshots arrive with their own slices. The [slice-05 record](docs/slice-05-verification.md) covers 72 passing tests, migration upgrade/replay, and refreshed image/Compose gates.

## Private routines (slice 06)

GET `/api/v1/routines` returns the caller's active templates ordered by name then ID; `?includeArchived=true` includes owned archives. POST creates a private routine and returns its versioned detail Location. GET/PUT `/api/v1/routines/{id}` reads/replaces owned metadata. Names are trimmed and required; omitted/null descriptions become empty strings. Detail/create/update return `exercises: []` until placements arrive in slice 07. Archived detail remains readable; archived updates return 409. Foreign/missing IDs return 404, including for admins. Requests cannot set owner or archive state.

Apply additive `20261008234727_AddRoutines` through the separate migrator. [Slice-06 verification](docs/slice-06-verification.md) records **114 passing tests**, migration upgrade/replay, release-image and isolated Compose gates.

The reusable [ownership query](Kilo.Persistence/Queries/OwnershipQueryExtensions.cs) composes into EF SQL:

```csharp
var userId = await currentUser.GetIdAsync(cancellationToken);
var routine = await db.Routines.OwnedBy(userId)
    .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
```

Import `Kilo.Persistence.Queries`. `OwnedBy` requires an EF-mapped integer/nullable-integer `UserId` and a verified positive local user ID. It excludes null/global owners; apply it before materializing private queries, adding resource/nesting/archive predicates as needed. It does not resolve identity, set an owner on creation, or grant admin/global visibility. Shared exercise reads and native admin writes retain their explicit scope rules.

## Release images (slice 02)

Both Dockerfiles restore locked packages and publish Release with the same pinned .NET 10 SDK. The final API and migrator images use separate pinned ASP.NET and runtime bases, run as `APP_UID`, and share the `org.opencontainers.image.version` label. Supply one release identifier to both builds:

```powershell
docker build --pull --platform linux/amd64 --build-arg RELEASE_VERSION=slice02 -f Kilo/Dockerfile -t kilo:slice02 .
docker build --pull --platform linux/amd64 --build-arg RELEASE_VERSION=slice02 -f Kilo.Migrations/Dockerfile -t kilo-migrations:slice02 .
```

Use a new immutable tag for each release; the default `development` label is for local Compose builds. The API listens on 8080. EF migrations are compiled into shared `Kilo.Persistence.dll` and published with the migrator, and neither build requires a database or credentials. The build-context allowlist excludes the PDF, docs, tests, scripts, local secrets, editor state, and build artifacts.

Run the actual-image gate with Docker Desktop in Linux mode, PowerShell 7, and Git on PATH:

```powershell
pwsh -NoProfile -File scripts/verify-images.ps1 -Release slice02 -Platform linux/amd64
```

The gate builds from a clean source snapshot without the Docker build cache, exercises context exclusions with harmless sentinels, scans image layers, and verifies non-root execution, shared release labels, environment-only startup, production OpenAPI exclusion, compiled EF migrations, trusted HTTPS, ICU/IANA timezone data, and clean SIGTERM shutdown. It leaves the tagged images and records their local image IDs in ignored `.artifacts/image-check-*/images.json`; temporary containers are removed. Transactional shutdown is checked again once feature writes exist. Use `linux/arm64` only when that is the intended target; an emulated run does not prove performance on an ARM host.

The [slice 02 verification record](docs/slice-02-verification.md) records the completed gate, base digests, and tested image IDs.

Base digests pin official multi-platform manifests. Review updates using `docker buildx imagetools inspect mcr.microsoft.com/dotnet/<sdk|aspnet|runtime>:10.0`, update the corresponding `FROM` lines, and rerun the gate. Pinning prevents silent base changes but requires deliberate security updates ([Docker guidance](https://docs.docker.com/build/building/best-practices/)). Keep the full runtime's CA certificates and globalization dependencies ([Microsoft image guidance](https://learn.microsoft.com/en-us/dotnet/core/docker/container-images)).

## Local Docker stack

Copy `.env.example` to `.env`, choose a local password, and set `CLERK_ISSUER` and `CLERK_AUTHORIZED_PARTY` for your Clerk instance and frontend origin, then run:

```powershell
docker compose up --build -d
Invoke-WebRequest http://127.0.0.1:8080/health
```

Compose waits for Postgres, runs the migrator once, then starts the API. The development override exposes API 8080 and Postgres 5432 on loopback. The base `compose.yaml` publishes no ports; production roles, TLS, and deployment are later slices. `/openapi/v1.json` is available in Development. `GET /api/v1/me` and `PUT /api/v1/me/preferences` require a valid Clerk bearer token.

An existing Postgres volume keeps its existing database names/passwords. Match its credentials rather than deleting the volume. New EF migrations are additive; never edit an already-applied migration. For a new release, run its migrator freshly rather than relying on a previous container's success.

For a local code/schema update, stop the API and remove the completed migration container before starting the rebuilt stack:

```powershell
docker compose stop api
docker compose rm --stop --force migrations
docker compose up --build -d
```

This preserves the database volume and makes migration success gate API startup. Because dependency conditions gate startup, stop the API first when updating the schema ([Compose startup behavior](https://docs.docker.com/compose/how-tos/startup-order/)).

Run the repeatable slice 03 gate with PowerShell 7, Git, Docker in Linux mode, and Compose 2.24.4 or newer:

```powershell
pwsh -NoProfile -File scripts/verify-compose.ps1 -Platform linux/amd64
```

It builds uniquely tagged images from a source snapshot, uses a unique Compose project and database volume, generates a temporary password, and replaces the development ports with random loopback ports. The check verifies startup order, data survival after container recreation, failure/rollback of a new migration, blocked API startup, repair and repeatability, and base/development configuration. The bad migration exists only in the temporary copy. Cleanup removes that project's containers, network, volume, image tags, and temporary sources; only sanitized evidence remains under ignored `.artifacts`. Existing `.env` values and development resources are preserved. The temporary port replacement uses Compose's native [`!override` tag](https://docs.docker.com/reference/compose-file/merge/).

All slice 03 gates passed; see the [verification record](docs/slice-03-verification.md).

## Native development

Both executables use `ConnectionStrings:Postgres` but have separate user-secret stores. Replace the placeholder with a connection to an available local database:

```powershell
$env:DOTNET_ENVIRONMENT = "Development"
dotnet user-secrets set "ConnectionStrings:Postgres" "<connection>" --project Kilo
dotnet user-secrets set "ConnectionStrings:Postgres" "<connection>" --project Kilo.Migrations
# Public identity configuration belongs to the API only:
dotnet user-secrets set "Clerk:Issuer" "https://<instance>.clerk.accounts.dev" --project Kilo
dotnet user-secrets set "Clerk:AuthorizedParties:0" "http://localhost:3000" --project Kilo
dotnet restore Kilo.slnx --locked-mode
dotnet build Kilo.slnx --no-restore
dotnet run --project Kilo.Migrations --no-build
dotnet run --project Kilo --no-build
```

Use `Host=localhost` for native processes and `Host=db` inside Compose. Set `Timeout=3;Command Timeout=15` for bounded database operations. Credentials stay in development user secrets or environment configuration, never committed appsettings.

Compose uses password authentication with `GSS Encryption Mode=Disable` to avoid optional Kerberos library warnings in the slim runtime images, as described in the [Npgsql 10 release notes](https://www.npgsql.org/doc/release-notes/10.0.html). Production TLS configuration arrives in the deployment slice.

## Checks

```powershell
dotnet test Kilo.slnx --no-restore
```

`Kilo.Tests/IntegrationTests/MeControllerTests.cs` runs happy-path HTTP checks against a disposable PostgreSQL 18.6 Testcontainer. Start Docker Desktop's Linux engine before running it; no connection string or Clerk credentials are required. The class fixture applies the real EF migrations, uses random container ports and credentials, and removes its container afterward. Each test uses a unique Clerk subject. `PreferencesRequestFaker` uses Bogus with per-instance seeds and valid IANA time zones.

```powershell
dotnet test Kilo.Tests/Kilo.Tests.csproj --no-restore --filter FullyQualifiedName~MeControllerTests
```

These tests verify first-request provisioning, existing-profile retrieval, both measurement systems, and preference persistence through subsequent GET requests and direct database checks. Their test-only authentication handler supplies the principal. They also check invalid preferences and isolation between two subjects.

`ClerkAuthenticationTests` uses the real JwtBearer handler with RSA-signed fixture tokens and an in-process HTTP transport serving OIDC discovery and public JWKS documents. It covers concurrent provisioning, rejected signatures/claims/algorithms, 403 policy denials, key rotation, cached-key operation during outages, cold-cache 503 responses, CORS, public readiness, and authenticated production OpenAPI exclusion. It does not contact a real Clerk account; the image gate separately checks outbound trusted HTTPS.

`KiloApiFactory` shares disposable Postgres setup between Me, exercise and routine feature tests; its subject/role headers are test-only and have no production authentication meaning. `ExercisesControllerTests` uses `ExerciseWriteRequestFaker` for global/custom CRUD, versioned Location links, fixed scope, isolation including admins, brand normalization/bounds, native validation, archived filtering and database constraints. Native Clerk tests also verify signed admin roles, rejected/missing roles, and invalid/forged tokens at exercise endpoints. `RoutinesControllerTests` and `RoutineWriteRequestFaker` cover routine CRUD, archives, validation and ownership query translation; signed-token tests verify routine ownership and private isolation from admins.

`IntegrationTests/FoundationTests.cs` checks configuration, native HTTP/versioning/validation, scoped contexts, model-snapshot consistency, and bounded unavailable readiness. Its Postgres check starts its own Testcontainer and verifies migration replay, identity/defaults, rollback, cancellation, healthy readiness, and database constraints. Docker is required for the full suite; there is no external admin connection or silently skipped database check. The migrator subprocess has a 45-second deadline and is terminated if it exceeds that deadline.

## Request validation

Each API request type has a FluentValidation validator in its feature's `Requests` folder. `AddKiloApi` automatically registers validators from the API assembly with `AddValidatorsFromAssemblyContaining<Program>()`. Controllers inject `IValidator<TRequest>` and await `ValidateAsync` with cancellation before user provisioning or writes; this also supports async rules. Request DataAnnotations and MVC implicit required validation are replaced by these rules.

Rule failures return HTTP 422 `application/problem+json` with `type`, `title`, `status`, `instance`, camelCase `errors`, and `traceId` through the shared native `RequestValidationProblem` extension. Invalid JSON, absent bodies and incompatible bound types remain native HTTP 400; authorization stays 401/403 and route mismatches stay 404. Validators do not replace ownership checks or database constraints. See [validation verification](docs/request-validation-verification.md).

## Clerk configuration and failure behavior

The API requires an HTTPS `Clerk:Issuer` and at least one `Clerk:AuthorizedParties` origin. Compose maps the corresponding `.env` variables into the API; the migrator needs neither. Outside Compose, use `Clerk__Issuer` and indexed `Clerk__AuthorizedParties__0` environment keys. Set `Clerk__Audience` only when your token setup defines an audience; omission leaves audience validation disabled. Add additional origins with further indexed keys. No Clerk secret API key is required for signature verification.

`AddKiloClerk` uses native discovery, signing-key caching and rotation, RS256, expiration, issuer, optional audience, nonblank verified `sub`, and allowed `azp`. CORS separately grants the configured origins. A fallback policy protects routes, while health and Development OpenAPI explicitly allow anonymous access.

Before validating a bearer token, the auth hook asks the native configuration manager for trusted metadata. A positively identified transport failure with no cached configuration becomes a sanitized 503. Cached configuration still permits native token validation: invalid signatures and unknown keys remain 401. No custom JWT parser, signing-key cache, or general exception-to-503 mapper is used. Cancellation is propagated; metadata HTTP calls have a 10-second timeout.

An anonymous request to the absent Production OpenAPI route returns 401 because of the fallback policy. The image smoke check expects that response; the authenticated JWT integration check proves the route returns 404. Both checks are needed to verify the policy and route exclusion.

## Clerk admin role setup

For global administration, set the selected Clerk user's **public metadata** to `{"role":"admin"}` through the Dashboard or a trusted backend. Under **Sessions / Customize session token**, merge this top-level claim:

```json
{ "role": "{{user.public_metadata.role}}" }
```

Keep ordinary users without the admin role. Public metadata is server-controlled; client-writable unsafe metadata, request bodies, and headers do not grant roles. Refresh the session token after a role change; already issued tokens keep their claims until expiry. No local role table or per-request Clerk API lookup is used.

## EF migration development

`Kilo.Persistence/Entities` holds persistence entities; `Kilo.Persistence` owns Fluent schema mapping, generated migrations, and model snapshot. Both executables reference it. Use native EF LINQ/DTO projections for reads and tracked changes with `SaveChangesAsync` for writes; no repository or connection wrapper is needed.

The private Design reference and local tool manifest pin EF tooling to 10.0.12; the Npgsql EF provider is 10.0.3. The design-time factory requires environment `ConnectionStrings__Postgres` and does not inherit executable user secrets. Supply that setting securely for an available local database, then:

```powershell
dotnet tool restore
# After changing the model for a requested slice:
dotnet ef migrations add <DescriptiveName> --project Kilo.Persistence
dotnet ef migrations has-pending-model-changes --project Kilo.Persistence
dotnet ef migrations script --idempotent --project Kilo.Persistence
dotnet run --project Kilo.Migrations
```

Review generated operations and SQL, then commit the migration, designer, and snapshot together. CreateUsers is already present; do not regenerate it or generate future slice entities early. Never use EnsureCreated, automatically migrate the API, or alter an applied migration. A failed migration does not undo earlier committed migrations ([EF migration guidance](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/applying)).

## EF-only database setup

Use an empty database or one already managed by this application's native EF migration history. The old SQL migrator and compatibility bridge have been removed. The existing local volume is preserved; removing source code does not upgrade or reset a running database.

If a retained database was created by the earlier SQL runner, ordinary EF migrations will fail rather than silently adopt it. Back it up and inventory it before designing a separate conversion, or select a new empty database for development. Never fabricate EF history or delete retained volumes to make startup pass.

`User` is now in `Kilo.Persistence/Entities/User.cs`. Its namespace is `Kilo.Persistence.Entities`. `OrganizeUserEntity` records the namespace and approved length annotations in generated metadata without changing database columns; the applied `CreateUsers` migration remains intact. Time zones retain their unrestricted text mapping, supporting IANA names such as `America/Phoenix`.

The API never migrates on startup. The migrator returns nonzero with a sanitized diagnostic on failure. Health uses the native `Healthy`/`Unhealthy` response with 200/503; HTTP errors use ProblemDetails. FluentValidation request-rule failures return 422 ValidationProblemDetails; malformed JSON/type binding returns native 400.
