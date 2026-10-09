# Slice 06 verification

Verified October 8, 2026 on Windows with .NET SDK 10.0.100 and Docker Desktop's Linux engine. Slice 06 is complete: private routine metadata CRUD and a reusable ownership query. Placement/set tables, archive endpoints, sessions and future workflows remain outside this slice.

## Implementation

- `RoutinesController` provides versioned create/list/detail/metadata replacement. Lists return summaries ordered by name/id, hide archives by default and support `includeArchived`. Detail/create/update return `RoutineDto` with a nonnull empty `exercises` array. A Ponytail comment marks the temporary empty-array contract for typed placements in slice 07.
- `RoutineWriteRequestValidator` is automatically discovered by existing API assembly scanning. Async validation precedes provisioning/writes: trimmed nonblank name, optional description preserving Unicode/multiline content, omitted/null description clearing to empty. Invalid rules return standardized 422; malformed JSON/type binding/absent bodies remain native 400.
- Owner IDs come only from `CurrentUser`. Requests cannot assign owner, scope, creation/archive timestamps or exercises. Foreign/missing detail/update returns 404, including for admins; owned archived updates return 409. Creation returns 201 with a working detail Location using string `version = "1"`.
- `Kilo.Persistence/Queries/OwnershipQueryExtensions.cs` adds `OwnedBy(userId)`: a composable `IQueryable` filter using native `EF.Property<int?>` on mapped integer/nullable-integer `UserId`. It rejects nonpositive caller IDs and excludes null/global owners. Routines and existing personal exercise updates use it. Identity resolution, creation ownership, global visibility, admin policies, archive state and nested relationships remain explicit responsibilities.
- Generated `20261008234727_AddRoutines`, designer and snapshot add only the routines table: identity key, restrictive required user FK, name check, `(id,user_id)` alternate key, filtered active-list index and UTC timestamp/default mappings. Applied migrations, API startup, package versions and lock files are unchanged.
- Reused scoped EF CRUD, `KiloApiFactory`, PostgreSQL Testcontainers and Bogus; added `RoutineWriteRequestFaker` and `RoutinesControllerTests`. No new dependency, generic repository, workflow service, identity-aware DbContext or interface was added.

## Checks

```powershell
dotnet restore Kilo.slnx --locked-mode
dotnet build Kilo.slnx --no-restore
dotnet test Kilo.slnx --no-restore
dotnet ef migrations has-pending-model-changes --project Kilo.Persistence --startup-project Kilo.Persistence --no-build
dotnet ef migrations script --idempotent --project Kilo.Persistence --startup-project Kilo.Persistence --no-build
pwsh -NoProfile -File scripts/verify-images.ps1 -Release slice06 -Platform linux/amd64
pwsh -NoProfile -File scripts/verify-compose.ps1 -Platform linux/amd64
git diff --check
```

- Locked restore, solution build and diff checks passed. Fresh API compilation retains the existing IDE0005/GenerateDocumentationFile configuration warning; no new analyzer warnings or build errors remain.
- Full suite: **114 passed, 0 failed, 0 skipped**. This includes the five named templates, empty collections in the actual JSON, description clearing, versioned Location round trips, deterministic ordering, private isolation including signed admins, owner/scope/archive injection, filtering/archived 409, standardized 422 and native 400, rejection before provisioning/mutation, anonymous 401 and invalid routes.
- Ownership queries executed against PostgreSQL for required routine owners and nullable exercise owners: caller-only rows, global/foreign exclusion, composed projections, tracked writes and nonpositive-ID rejection. Existing exercise/global/admin isolation regressions passed.
- Native JwtBearer tests verify valid signed routine ownership and denial to another signed admin, plus all existing invalid/forged/missing-token variants at routine reads/writes. Test-only subject headers cannot change production identity.
- EF reports no pending model changes. Reviewed generated C# and idempotent SQL: only routines are added, with intended keys/FK/check/index/defaults. Fresh Testcontainer databases migrate successfully. Migrator subprocess checks upgrade explicit pre-exercise and slice-05 baselines, preserving users/preferences and both global/custom exercises, then replay without duplicating history or changing stored routines. Fixtures capture PostgreSQL's stored microsecond precision before comparing timestamps.

## Linux images and isolated Compose

Final `linux/amd64` images passed clean locked builds, context/layer exclusions, non-root UID 1654, Production environment configuration, native readiness, trusted HTTPS, ICU/IANA timezone support, four compiled EF migrations and clean SIGTERM shutdown.

| Tag | Tested local image ID |
| --- | --- |
| `kilo:slice06` | `sha256:85730599a65976e9f76368e6cb3c15f7beabc2eabda422ba7a04f2648d1ab05c` |
| `kilo-migrations:slice06` | `sha256:5901a28efea13389441c35c17741b196df5a1ab67706fb9cc771ab4b447c0e21` |

The isolated Compose gate completed at `2026-10-08T23:54:13Z`: fresh startup, migration-before-API activation, healthy readiness, host networking, loopback development ports, retained data/history across recreation, failed migration rollback and blocked activation, repair and repeat success. Only verification-owned containers, network, volume and temporary image tags were removed.

Ignored local evidence: `.artifacts/image-check-b55bb7a7c2a04e00b23c898ebfb802e3/images.json`, `.artifacts/kilo-compose-check-153453d5a6fa4aae91f26a43caa7cd7a/checks.json`, `.artifacts/slice06-images.log`, `.artifacts/slice06-compose.log`, review-only migration SQL, and targeted PDF QA under `.artifacts/slice06-pdf`.

## Documentation and limits

README, AGENTS.md and the canonical workbook document routines, verified progress and ownership-query usage. The source/PDF were regenerated together: 71 pages and matching bookmarks, 100 unique interactive fields with appearances and in-bounds widgets. All-page structure checks passed, and 14 changed pages/boundaries were rendered and visually checked. Nothing was committed or pushed; existing staged/unrelated work was preserved.

Live Clerk account configuration is not claimed verified; native authentication tests use fixture-signed tokens. Concurrent archive/edit lifecycle locking arrives with slice 15. `OwnedBy` is an explicit query filter for mapped `UserId` entities, not an automatic authorization system or a substitute for child relationship constraints. Slice 07 (ordered exercise placements) is next.

Native query basis: [EF.Property](https://learn.microsoft.com/en-us/dotnet/api/microsoft.entityframeworkcore.ef.property?view=efcore-10.0). The PostgreSQL integration tests verify its use with both owner column types in this repository.
