# EF Core transition verification

Verified October 7, 2026. This converts the implemented foundation and Docker/Compose slices 01-03; slices 04-20 remain planned work.

## Code and migration pipeline

The API and separate migrator reference `Kilo.Persistence`: a scoped `KiloDbContext`, User mapping, native EF migrations, shared registration, and a design-time factory. The former data-access packages and embedded SQL migration are removed. EF Relational/Design and the local CLI are pinned to 10.0.12; the Npgsql EF provider is 10.0.3. Design dependencies are private tooling references.

`20261007165648_CreateUsers` preserves the original users schema: integer identity ALWAYS, unique nonblank Clerk subject, UTC/imperial defaults, measurement check, and UTC creation instant. The native journal is `public."__EFMigrationsHistory"`. The API never migrates; the console runner awaits `MigrateAsync`, handles host cancellation, returns nonzero on failure, and emits sanitized diagnostics.

## Initial transition checks (before cleanup)

- Locked package restores and full solution build succeeded with zero warnings and errors.
- All **8 tests passed, 0 skipped**, with `KILO_TEST_POSTGRES` pointing to a uniquely named disposable PostgreSQL 18 container. Test databases were created/dropped independently of the supplied admin database.
- Tests verified scoped context lifetime, snapshot consistency, real EF inserts and generated IDs/defaults, migration replay, constraint enforcement, failure/cancellation rollback, readiness 200/503, native HTTP/versioning/validation, and safe configuration diagnostics.
- The initial compatibility bridge passed its preservation/drift checks. That bridge and its fixtures were subsequently removed at the user's request; those checks describe the earlier transition, not current runtime support.
- `dotnet ef migrations has-pending-model-changes --project Kilo.Persistence --no-build` reported no model changes. Generated idempotent PostgreSQL output was reviewed for identity, defaults, names/checks, and native history insertion.
- Actual Linux AMD64 image and Compose gates passed; their commands and evidence are recorded in the adjacent slice verification documents.
- PDF verification checked all 64 pages/bookmarks, 100 canonical form fields and matching widgets/appearances, text bounds, save/reopen values, and rendered layout. The progress fields remain editable.

## Requested cleanup

The obsolete compatibility bridge, argument handling, old SQL fixtures, and adoption instructions are removed. Kilo.Migrations remains the separate native EF runner. User moved to `Kilo.Persistence/Entities/User.cs` and the Entities namespace. The approved Clerk subject and measurement-system length annotations remain; the three-character time zone limit was removed. Generated `20261008032930_OrganizeUserEntity` records metadata only, with empty Up/Down and no column changes. The original applied migration is unchanged. Explicit text columns remain unrestricted at database level; validation annotations are not database length constraints. Old project bin/obj outputs were cleared before rebuilding to remove stale assemblies.

The existing local database volume is preserved. The native runner supports empty databases or databases already managed by this application's EF history. Any retained non-EF database needs a backup, inventory, and separately designed conversion; source cleanup does not reset its data. README documents this boundary.

Future schema changes start with entity/Fluent mapping edits and generated migrations, designers, and snapshots. SQL in the workbook appendix remains a schema contract for review; it is not an executable migration set.

## Cleanup verification

- Locked restore and solution build succeeded. The concurrent local analyzer configuration produced two style/documentation warnings; that configuration remains outside this cleanup commit.
- All **7 tests passed, 0 skipped**, against disposable PostgreSQL 18. Migration replay retains the full `America/Phoenix` time zone; entity validation accepts it. The removed legacy-adoption test no longer counts toward the gate.
- Native pending-model checking passed. The reviewed SQL for OrganizeUserEntity contains only the native EF history insertion in a transaction; it performs no DDL. The original CreateUsers migration and designer are unchanged.
- Linux AMD64 image and Compose gates passed again with the final entity metadata. Both compiled migrations were discovered; fresh startup, readiness, persistence, failure rollback/startup blocking, repair, and repeat runs passed. Updated tags, image IDs, and UTC verification time are in the slice 02/03 records.
- The regenerated PDF passed 64-page/bookmark, 100-field/widget/appearance, text-boundary, save/reopen, and rendered-layout checks.
- Only the disposable Postgres fixture was removed. Existing application containers and volumes were preserved. Build caches and PDF QA output remain ignored by the existing rules.
