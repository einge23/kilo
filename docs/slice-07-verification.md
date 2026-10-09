# Slice 07 verification

Verified October 8, 2026 (America/Phoenix) with .NET SDK 10.0.100 and Docker Desktop's Linux engine. Slice 07 is complete: ordered exercise placements, current library metadata, private ownership/access constraints, and transactional writes. Planned sets remain slice 08; archive/bulk reorder routes remain slice 15.

## Implementation

- `RoutinePlacementsController` adds authenticated versioned POST/PUT under `/routines/{routineId}/exercises`. POST returns a distinct placement and 201 Location to the existing routine detail GET. PUT replaces position/instructions/rest without changing the library ID or owner/scope/archive state. Required numeric inputs are nullable and validated asynchronously before provisioning/writes using automatically discovered FluentValidation validators; omitted/null description becomes empty.
- Scoped `RoutinePlacementService` owns one ReadCommitted transaction. `WriteAsync` takes the verified user ID, route routine ID, an immutable `PlacementWrite` input and cancellation token. Controllers build its named fields from validated, normalized requests; required position/description/rest fields stay separate from verified identity. It locks the owned routine first and the visible global/owned exercise when attaching, checking archive state after lock acquisition. Updates compose `OwnedBy(userId)` with both placement and route parent IDs. Missing/foreign/wrong-parent resources return 404, archived write targets return 409, and only PostgreSQL unique violation `routine_exercises_active_position` becomes a position 409. Disposal rolls rejected/cancelled transactions back; other exceptions reach native sanitized error handling.
- Generated `20261009002516_AddRoutineExercises`, designer and snapshot add the stored computed shadow library key `coalesce(user_id,0)` and alternate key, plus the placements table with identity/ownership keys, restrictive composite FKs, scope/position/rest checks, active-position uniqueness and native FK indexes. The computed key is generated on insert, ignored before save, and immutable to EF. Placement scope is selected from the verified visible row, never supplied by the request. Earlier applied migrations are unchanged.
- `DefaultRestSeconds` retains a database default of 120 but uses `ValueGeneratedNever()` so EF always sends the validated value, including zero. The entity defaults to 120; API omission is still invalid. No pounds/kilograms or planned set schema is introduced early.
- `RoutineDto.Project` supplies typed ordered active placements for routine GET and metadata PUT. Names/brands join the current library; instructions belong to each placement, and `sets` remains a nonnull empty array. Routine summaries stay unchanged. Existing equality checks now compare DTO contents recursively, including collections.
- Reused native EF, `CurrentUser`, `OwnedBy`, validator registration, ProblemDetails, `KiloApiFactory`, PostgreSQL Testcontainers and Bogus. Added one concrete workflow with feature-specific outcomes, two request fakers and placement integration tests. No new package, repository, interface, context wrapper, auth pipeline or architecture project.

## Checks

```powershell
dotnet restore Kilo.slnx --locked-mode
dotnet build Kilo.slnx --no-restore
dotnet test Kilo.slnx --no-build --no-restore
dotnet ef migrations has-pending-model-changes --project Kilo.Persistence --startup-project Kilo.Persistence --no-build
dotnet ef migrations script --idempotent --project Kilo.Persistence --startup-project Kilo.Persistence --no-build
pwsh -NoProfile -File scripts/verify-images.ps1 -Release slice07 -Platform linux/amd64
pwsh -NoProfile -File scripts/verify-compose.ps1 -Platform linux/amd64
git diff --check
```

- Locked restore/build/diff checks passed; no dependency or lock-file change. Fresh API compilation retains the existing IDE0005/GenerateDocumentationFile configuration warning; new formatting warnings were fixed with the native formatter.
- Full suite: **149 passed, 0 failed, 0 skipped**, including a full rerun after grouping the placement write parameters into `PlacementWrite`. Repeated global/custom exercises have distinct IDs/instructions; positions 1-3 read in ascending order; zero rest persists; omitted descriptions clear. PUT preserves identities, owner/scope and existing routine children. Library name/brand updates appear in every repeated placement.
- Authentication/ownership checks cover ordinary and signed admin isolation, test-header/client-owner spoofing, wrong parent chains, foreign library entries, global attachment from another account, native 401 before validation, invalid routes, and all existing invalid/forged/missing signed-token variants at placement endpoints. Request-rule failures use standardized camelCase 422; malformed/fractional/overflow/absent bodies stay native 400, without first-user provisioning or existing-row mutation.
- PostgreSQL checks reject invalid position/rest, wrong routine owner, foreign exercise scope and forged global scope. Duplicate inserts/updates return 409 and retain prior values; archived positions can be reused while retained rows remain intact. Concurrent same-position HTTP inserts produce exactly one 201, one 409 and one committed row. An unrelated named test CHECK failure stays sanitized 500 and leaves no placement.
- Deterministic concurrency checks observe real `pg_stat_activity` lock waits rather than timing a sleep: attachment waits for the routine/exercise transaction, then sees committed archive state and rejects it. Cancelling while waiting for the exercise releases the acquired routine lock; another attachment succeeds afterward. All test databases are uniquely named disposable containers; no retained database/volume was changed.
- EF reports no pending model changes. Reviewed C# and idempotent SQL contain only the intended computed key/placements changes. Migrator subprocess tests seed explicit pre-exercise, slice-05 and slice-06 baselines, verify generated-key backfill and preservation of users/preferences/exercises/routines, insert a placement with zero rest and replay without changing rows/history. Old-schema exercises are inserted through parameterized SQL; current EF metadata cannot seed a column before its migration. Timestamp comparisons use stored PostgreSQL microsecond precision.

## Release gates

The slice-07 `linux/amd64` images passed clean locked builds, context/layer exclusions, non-root UID 1654, Production configuration, native readiness, trusted HTTPS, ICU/IANA support, five compiled migrations and idle SIGTERM shutdown. These image/Compose checks preceded the parameter-only `PlacementWrite` refactor; the locked restore/build and all 149 tests were rerun afterward:

| Tag | Tested local image ID |
| --- | --- |
| `kilo:slice07` | `sha256:521a569f0495558fc0610fbee1a4108e81983c5e534650153b52559afe3e5c50` |
| `kilo-migrations:slice07` | `sha256:2e5801276f472426652f4b7ae68c84a43217cff357134bd8775f0ecb9060b855` |

The isolated Compose gate completed at `2026-10-09T00:34:22Z`: fresh startup, migration-before-API activation, healthy readiness, host networking, loopback development ports, data/history survival across recreation, failed migration rollback and blocked activation, repair and fresh replay. Cleanup removed only its own containers/network/volume and temporary image tags.

Ignored evidence: `.artifacts/image-check-472fd49875344b538fc49716f669bc38/images.json`, `.artifacts/kilo-compose-check-6b3ff763b876493297aa76b1e81dde21/checks.json`, `.artifacts/slice07-images.log`, `.artifacts/slice07-compose.log`, `.artifacts/slice07-migrations.sql`, and `.artifacts/slice07-pdf`.

## Documentation and limits

README, AGENTS.md and the canonical workbook describe the implemented placements, validators, locks, access-key mapping and next slice. The regenerated PDF has 71 pages, 71 matching bookmarks and 100 unique fillable fields. All-page structural checks passed, including bookmark destinations, field/widget values, appearance streams and bounds. Eleven changed/neighboring/boundary pages were rendered and visually inspected; no clipping or overlap remains. Two independent read-only Ponytail/.NET subagent reviews found no actionable simplicity or correctness issues. The user subsequently requested committing and pushing this slice.

Live Clerk account configuration is not claimed verified; tests use native JwtBearer with fixture-signed tokens. Real PostgreSQL cancellation/rollback and idle image shutdown passed; terminating a container during the narrow SaveChanges/commit window is not covered by these checks. Future snapshot/archive workflows retain their own verification gates. Slice 08 is next.
