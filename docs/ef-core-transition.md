# EF Core transition verification

Verified October 7, 2026. This converts the implemented foundation and Docker/Compose slices 01-03; slices 04-20 remain planned work.

## Code and migration pipeline

The API and separate migrator reference `Kilo.Persistence`: a scoped `KiloDbContext`, User mapping, native EF migrations, shared registration, and a design-time factory. The former data-access packages and embedded SQL migration are removed. EF Relational/Design and the local CLI are pinned to 10.0.12; the Npgsql EF provider is 10.0.3. Design dependencies are private tooling references.

`20261007165648_CreateUsers` preserves the original users schema: integer identity ALWAYS, unique nonblank Clerk subject, UTC/imperial defaults, measurement check, and UTC creation instant. The native journal is `public."__EFMigrationsHistory"`. The API never migrates; the console runner awaits `MigrateAsync`, handles host cancellation, returns nonzero on failure, and emits sanitized diagnostics.

## Executed checks

- Locked package restores and full solution build succeeded with zero warnings and errors.
- All **8 tests passed, 0 skipped**, with `KILO_TEST_POSTGRES` pointing to a uniquely named disposable PostgreSQL 18 container. Test databases were created/dropped independently of the supplied admin database.
- Tests verified scoped context lifetime, snapshot consistency, real EF inserts and generated IDs/defaults, migration replay, constraint enforcement, failure/cancellation rollback, readiness 200/503, native HTTP/versioning/validation, and safe configuration diagnostics.
- Explicit adoption preserved a seeded legacy user's ID and data; normal migration replay then succeeded. The ordinary runner rejected the unadopted schema. Adoption rejected altered defaults without adding EF history or changing rows.
- `dotnet ef migrations has-pending-model-changes --project Kilo.Persistence --no-build` reported no model changes. Generated idempotent PostgreSQL output was reviewed for identity, defaults, names/checks, and native history insertion.
- Actual Linux AMD64 image and Compose gates passed; their commands and evidence are recorded in the adjacent slice verification documents.
- PDF verification checked all 64 pages/bookmarks, 100 canonical form fields and matching widgets/appearances, text bounds, save/reopen values, and rendered layout. The progress fields remain editable.

## Retained local database

The existing local stack and volume were preserved. Read-only inventory found the known users baseline and no users rows; the running development stack was not upgraded. The guarded `--adopt-legacy-baseline` flag is a one-time bridge for that exact baseline, not a generic importer. It audits under a users lock, records CreateUsers using native EF history APIs, and preserves the legacy journal as metadata. Unknown schema/history or already-applied EF migrations makes adoption fail. Follow the backup, inventory, stop, and adoption commands in [README](../README.md) before rebuilding against a retained pre-EF volume.

Future schema changes start with entity/Fluent mapping edits and generated migrations, designers, and snapshots. SQL in the workbook appendix remains a schema contract for review; it is not an executable migration set.
