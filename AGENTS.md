# Agent instructions for Kilo

These instructions apply to the entire repository.

## Read the plan before changing code

- Use [the revised PDF workbook](output/pdf/workout-tracker-dotnet10-revised-plan.pdf) as the implementation guide. Read its shared standards, migration policy, the requested slice, its prerequisites, and its verification gate before starting. Extract the PDF text when necessary; inspect rendered pages for layout work.
- [docs/workout-tracker-plan.md](docs/workout-tracker-plan.md) is the editable source of that PDF and provides searchable code recipes. Keep the source and PDF consistent when revising the plan. Do not use an older Downloads copy or the superseded N-number notes.
- The user's current request defines the work. Document examples and the final API/schema catalog do not authorize implementing future slices. Build cumulatively: preserve earlier work and add only what the requested slice needs.
- The repository currently has the foundation and local Docker/Compose setup. Inspect the code and verification evidence to establish progress; PDF checkboxes alone do not prove completion.
- If the plan and code disagree, resolve the difference within the requested scope and report material deviations. Never silently change a product requirement.

## .NET and architecture standards

- Target stable .NET 10 with nullable reference types and implicit usings enabled. Keep SDK selection in `global.json` and commit NuGet lock files; restore with `--locked-mode`.
- Keep `Program.cs` focused on composing the host and middleware. Put cohesive registrations in `Hosting` extension methods such as `AddKiloApi`, `AddKiloPersistence`, and, when needed, `AddKiloClerk`.
- Use one controller API, one separate DbUp migrator, and the existing test project. Add feature folders only when implementing their feature. Controllers bind/validate requests and translate outcomes; concrete repositories own SQL; workflow services own real multi-step operations.
- Apply Ponytail's simplicity rule: reuse existing code, then standard-library/platform features, then installed dependencies. Use the available .NET backend skills and Ponytail skill when relevant; missing skills do not remove these repository standards.
- Avoid speculative interfaces, pass-through services, generic repositories, custom connection/transaction wrappers, MediatR, AutoMapper, and extra architecture projects. Never simplify away validation, authorization, error handling, or data integrity.
- Use `Asp.Versioning.Mvc` with URL-segment versioning and explicit controller routes. Do not hand-roll API versioning or use the deprecated `Microsoft.AspNetCore.Mvc.Versioning` package.
- Use `[ApiController]`, DataAnnotations, camelCase JSON, positive integer IDs, and native 400 `ValidationProblemDetails`. Use `ProblemDetails` for HTTP errors and the workbook's status-code conventions. Keep `/health` anonymous and unversioned with native 200/503 readiness responses; expose OpenAPI only in Development.

## Persistence, identity, and product rules

- Register one `NpgsqlDataSource` pool. Open and dispose native connections per operation. Pass cancellation tokens through async I/O and use bounded command timeouts. Dapper commands must explicitly carry their transaction when one exists.
- Embed ordered, additive SQL migrations in `Kilo.Migrations`; DbUp journals them and runs a transaction per script. Never edit or rename an applied migration in a retained database. The API must never migrate on startup; run one migrator per deployment and require success before activating the release.
- Once the identity slice is implemented, use native JwtBearer validation for Clerk and a fallback authorization policy. Derive the local user ID from verified identity; scope every tenant query to it and enforce ownership in database relationships. Never trust a client-supplied owner ID.
- Preserve the workbook's product model: integer identity keys, text Clerk subjects and retry keys, imperial defaults, explicit `lb`/`kg` on every present weight, and workout snapshots independent of editable routine templates. Preference changes must not rewrite stored weights.
- Use UTC `timestamptz` instants, explicit `DateOnly` mapping for dates, and `TimeProvider` for application-generated time. Implement locking, idempotency, lifecycle rules, and ordering constraints as their slices require.
- Keep credentials in user secrets or environment configuration. Never commit `.env`, real passwords, tokens, production connection strings, or logs containing them. Return sanitized errors without exposing configuration values or database internals.
- Preserve existing databases and volumes. Use uniquely named disposable databases for integration checks; never migrate a supplied admin database or delete a user's volume to make a test pass.

## Verification and delivery

Use [README.md](README.md) for development setup and test configuration:

```powershell
dotnet restore Kilo.slnx --locked-mode
dotnet build Kilo.slnx --no-restore
dotnet test Kilo.slnx --no-restore
git diff --check
```

- Run the requested slice's meaningful verification gate. Supply `KILO_TEST_POSTGRES` for real Postgres checks when persistence changes; report skipped checks accurately. Add focused regression checks for changed behavior, not tests that merely mirror the implementation.
- For Docker or Compose changes, build the affected images and verify startup order, migration success, and readiness in an isolated stack. Clean up only resources created for that verification.
- Plan edits belong in `docs/workout-tracker-plan.md`. Regenerate with `python docs/build_workout_plan.py` using ReportLab and pypdf, then check rendered layout, bookmarks, and fillable fields. Commit the source, generator changes if any, and regenerated PDF together. Keep renders and temporary QA files out of Git.
- Review the staged diff before committing. Include source, migrations, tests, lock files, configuration templates, documentation, and the canonical PDF; exclude build output, caches, editor state, secrets, and temporary artifacts.
- For future completed tasks, commit the necessary repository files and push the current branch to its configured remote unless the user asks otherwise. This is the user's standing delivery preference; do not ask for permission again for routine commits and pushes. Stage explicit paths, preserve unrelated work, and never force-push or rewrite shared history.
- Add targeted `.gitignore` rules for local/generated files that do not belong in the repository. Keep reusable source, verification scripts, shared templates, and the canonical PDF tracked; never ignore real project files just to hide an unrelated change. Reference `.env.example` in shared solution files; keep actual `.env` files local.
- State what changed, what passed, and any remaining limitation. Mark a slice complete only after its verification gate passes; do not claim later slices are complete because scaffolding exists.
