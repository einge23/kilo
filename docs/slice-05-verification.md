# Slice 05 verification

Validation policy was subsequently revised to FluentValidation and 422 request-rule errors; see [the follow-up verification](request-validation-verification.md). The results below describe the original slice verification.

Verified October 8, 2026 on Windows with .NET SDK 10.0.100 and Docker Desktop's Linux engine. Scope: global exercise reads/admin writes and private custom CRUD. Archive endpoints and future feature tables remain outside this slice.

## Implementation

- Native `Admin` policy requires authenticated `admin` role; JwtBearer maps `RoleClaimType = "role"` from verified Clerk claims. Existing signature/issuer/audience/lifetime/authorized-party checks remain.
- `ExercisesController` reads globals plus the caller's custom entries; personal writes derive ownership from `CurrentUser`. `AdminExercisesController` writes global rows only. Neither path can transfer scope or expose another account's custom entry.
- Shared `ExerciseWriteRequest` and `ExerciseDto` support trimmed names/brands, optional descriptions, 100-character brand bounds, `isGlobal`, native validation errors and versioned detail Location links. URL generation supplies `version = "1"` as a string, required by the versioning route constraint.
- `20261008230628_AddExercises` adds only the exercise table, restrictive optional owner FK, metadata checks and active-list index. Generated migration, designer and snapshot are included. Applied migrations are unchanged; no startup migration or future access-key columns were added.
- `KiloApiFactory` replaces `MeApiFactory` and serves both features. Test-only subject/role headers never authorize production requests. Native JWT tests retain the existing Clerk fixture. No new dependency was added.

## Checks

```powershell
dotnet restore Kilo.slnx --locked-mode
dotnet build Kilo.slnx --no-restore
dotnet test Kilo.slnx --no-restore --no-build --logger 'trx;LogFileName=slice05.trx'
dotnet ef migrations has-pending-model-changes --project Kilo.Persistence --startup-project Kilo.Persistence --no-build
pwsh -NoProfile -File scripts/verify-images.ps1 -Release slice05 -Platform linux/amd64
pwsh -NoProfile -File scripts/verify-compose.ps1 -Platform linux/amd64
git diff --check
```

Locked restore/build passed. An existing IDE0005 analyzer configuration warning (`EnableGenerateDocumentationFile`) appears on a fresh API compilation; no build errors occurred. EF reports no pending model changes. The final test suite passed **72 tests, 0 failures, 0 skips**, including:

- Global/custom create, reads and replacement; Unicode/apostrophes/multiline descriptions; positive IDs/camelCase; shared detail Location round trips for both controllers.
- Empty/ordered lists, duplicate-name tie ordering, archive filtering/detail and 409 updates; trimmed brand null/blank/padded/100/101 boundaries and omitted-brand clearing.
- Private isolation including admins, admin personal creation remaining private, global personal edit 403, admin edit of private/missing IDs 404, and ignored owner/scope injection with unchanged rows after rejection.
- Native signed `admin` success, missing/user/wrong-case role 403, invalid/forged/missing-token 401 at Me and exercise endpoints, and no role elevation through bodies or headers.
- Native 400 validation, routing constraints, database checks/FKs, fixture migrations into fresh databases, and a seeded upgrade from `OrganizeUserEntity` preserving preferences followed by repeat migrator execution.
- Existing identity/concurrency, key rotation, metadata outage, CORS, readiness, persistence cancellation/rollback and model-snapshot regressions.

## Images and isolated Compose

Actual `linux/amd64` images passed clean context/layer exclusion, locked release build, non-root UID 1654, release labels, platform/HTTPS/ICU/IANA checks, compiled EF migration count 3, production API startup and clean process shutdown.

| Tag | Tested local image ID |
| --- | --- |
| `kilo:slice05` | `sha256:09382d5715644a3c2a9fe0cc51ed3ce4f3ef548b018a9161624a7607779e2bb7` |
| `kilo-migrations:slice05` | `sha256:e073fab7af5eba320e691da9e8070f3b7a3d4df53a738e5cea37acee215a4771` |

Compose gate completed at `2026-10-08T23:13:37Z`: fresh startup, migration-before-API dependency order, 200 Healthy, host networking, loopback development ports, no base ports, required password, retained users/journal after recreation, failing migration rollback/blocking activation, repair and successful repeat. Only disposable verification containers/networks/volumes were removed; retained development data was preserved.

Local evidence is ignored: `Kilo.Tests/TestResults/slice05.trx`, `.artifacts/image-check-eb669622c3c24d3ab5bf8d8b5cbbdd5c/images.json`, `.artifacts/kilo-compose-check-31c21d6d8dd04c81a5d6b560bb489b0e/checks.json`, and `.artifacts/slice05-*.log`.

## Workbook and operational limits

Source/PDF regeneration, rendered layout, 69 matching bookmarks and all 100 interactive progress fields were checked. The canonical PDF remains tracked; renders/QA are ignored. Nothing was committed or pushed.

Clerk role metadata and the top-level session claim must be configured in the actual Clerk instance as documented in README/workbook; this verification used fixture-signed tokens, not a real Clerk account. Role changes affect refreshed tokens rather than instantly invalidating issued tokens. Transactional workflow shutdown is checked when those workflows arrive; this slice uses single-save CRUD. Slice 06 (private routine templates) is next.
