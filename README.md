# Kilo

The current implementation covers the foundation (slice 01) and verified release images (slice 02) of the [workout tracker plan](docs/workout-tracker-plan.md): controller host, URL API versioning, pooled Postgres, readiness, and a separate SQL migrator. Local Compose setup is also present; its remaining slice 03 gates come next. Clerk and business endpoints arrive in later slices.

The [PDF workbook](output/pdf/workout-tracker-dotnet10-revised-plan.pdf) contains the cumulative implementation guide. Agents must follow [AGENTS.md](AGENTS.md). Edit the plan's Markdown source and regenerate the PDF with `python docs/build_workout_plan.py` (requires ReportLab and pypdf).

## Release images (slice 02)

Both Dockerfiles restore locked packages and publish Release with the same pinned .NET 10 SDK. The final API and migrator images use separate pinned ASP.NET and runtime bases, run as `APP_UID`, and share the `org.opencontainers.image.version` label. Supply one release identifier to both builds:

```powershell
docker build --pull --platform linux/amd64 --build-arg RELEASE_VERSION=slice02 -f Kilo/Dockerfile -t kilo:slice02 .
docker build --pull --platform linux/amd64 --build-arg RELEASE_VERSION=slice02 -f Kilo.Migrations/Dockerfile -t kilo-migrations:slice02 .
```

Use a new immutable tag for each release; the default `development` label is for local Compose builds. The API listens on 8080. SQL is embedded in the migrator, and neither build requires a database or credentials. The build-context allowlist excludes the PDF, docs, tests, scripts, local secrets, editor state, and build artifacts.

Run the actual-image gate with Docker Desktop in Linux mode, PowerShell 7, and Git on PATH:

```powershell
pwsh -NoProfile -File scripts/verify-images.ps1 -Release slice02 -Platform linux/amd64
```

The gate builds from a clean source snapshot without the Docker build cache, exercises context exclusions with harmless sentinels, scans image layers, and verifies non-root execution, shared release labels, environment-only startup, production OpenAPI exclusion, embedded SQL, trusted HTTPS, ICU/IANA timezone data, and clean SIGTERM shutdown. It leaves the tagged images and records their local image IDs in ignored `.artifacts/image-check-*/images.json`; temporary containers are removed. Transactional shutdown is checked again once feature writes exist. Use `linux/arm64` only when that is the intended target; an emulated run does not prove performance on an ARM host.

The [slice 02 verification record](docs/slice-02-verification.md) records the completed gate, base digests, and tested image IDs.

Base digests pin official multi-platform manifests. Review updates using `docker buildx imagetools inspect mcr.microsoft.com/dotnet/<sdk|aspnet|runtime>:10.0`, update the corresponding `FROM` lines, and rerun the gate. Pinning prevents silent base changes but requires deliberate security updates ([Docker guidance](https://docs.docker.com/build/building/best-practices/)). Keep the full runtime's CA certificates and globalization dependencies ([Microsoft image guidance](https://learn.microsoft.com/en-us/dotnet/core/docker/container-images)).

## Local Docker stack

Copy `.env.example` to `.env`, choose a local password, then run:

```powershell
docker compose up --build -d
Invoke-WebRequest http://127.0.0.1:8080/health
```

Compose waits for Postgres, runs the migrator once, then starts the API. The development override exposes API 8080 and Postgres 5432 on loopback. The base `compose.yaml` publishes no ports; production roles, TLS, and deployment are later slices. `/openapi/v1.json` is available in Development. No business controller is deployed yet.

An existing Postgres volume keeps its existing database names/passwords. Match its credentials rather than deleting the volume. New SQL scripts are additive; never edit an already-applied migration. For a new release, run its migrator freshly rather than relying on a previous container's success.

## Native development

Both executables use `ConnectionStrings:Postgres` but have separate user-secret stores. Replace the placeholder with a connection to an available local database:

```powershell
$env:DOTNET_ENVIRONMENT = "Development"
dotnet user-secrets set "ConnectionStrings:Postgres" "<connection>" --project Kilo
dotnet user-secrets set "ConnectionStrings:Postgres" "<connection>" --project Kilo.Migrations
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

Configuration, native HTTP/versioning/validation, pool lifetime, and unavailable-readiness checks run without a database. The Postgres check reports **Skipped** until `KILO_TEST_POSTGRES` supplies an admin connection with CREATE DATABASE permission. It creates and drops its own uniquely named test database; it never migrates the supplied admin database. With that variable set, it verifies migrations twice, identity/defaults, transactional rollback, cancellation, and healthy readiness.

The API never migrates on startup. The migrator returns nonzero with a sanitized diagnostic on failure. Health uses the native `Healthy`/`Unhealthy` response with 200/503; HTTP errors use ProblemDetails and standard MVC validation returns 400.
