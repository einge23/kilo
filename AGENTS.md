# Agent instructions for Kilo

These instructions apply to the entire repository.

## Read the plan before changing code

- Use [the revised PDF workbook](output/pdf/workout-tracker-dotnet10-revised-plan.pdf) as the implementation guide. Read its shared standards, migration policy, the requested slice, its prerequisites, and its verification gate before starting. Extract the PDF text when necessary; inspect rendered pages for layout work.
- [docs/workout-tracker-plan.md](docs/workout-tracker-plan.md) is the editable source of that PDF and provides searchable code recipes. Keep the source and PDF consistent when revising the plan. Do not use an older Downloads copy or the superseded N-number notes.
- The user's current request defines the work. Document examples and the final API/schema catalog do not authorize implementing future slices. Build cumulatively: preserve earlier work and add only what the requested slice needs.
- The repository implements slices 01-06: foundation, local Docker/Compose, Clerk/profiles, global/private exercises with native admin policies, and private routine templates. Use docs/slice-04-verification.md and docs/slice-05-verification.md for original gate evidence, docs/request-validation-verification.md for the FluentValidation/422 revision, and docs/slice-06-verification.md for routines and ownership queries. Inspect the code and verification evidence to establish progress; PDF checkboxes alone do not prove completion.
- If the plan and code disagree, resolve the difference within the requested scope and report material deviations. Never silently change a product requirement.

## .NET and architecture standards

- Target stable .NET 10 with nullable reference types and implicit usings enabled. Keep SDK selection in `global.json` and commit NuGet lock files; restore with `--locked-mode`.
- Keep `Program.cs` focused on composing the host and middleware. Put cohesive registrations in `Hosting` extension methods such as `AddKiloApi`, `AddKiloPersistence`, and, when needed, `AddKiloClerk`.
- Use one controller API, one separate EF Core migrator, a shared Kilo.Persistence project consumed by both executables, and the existing test project. Keep persistence entities in `Kilo.Persistence/Entities`; generate additive migration metadata when entity namespaces or mappings change. Add feature folders only when implementing their feature. Controllers bind/validate requests and translate outcomes. Use scoped KiloDbContext directly for simple CRUD; concrete workflow services own real multi-step transactions.
- Always use the Ponytail skill when implementing new features or tests. Read its SKILL.md before starting and apply its simplicity rule throughout: reuse existing code, then standard-library/platform features, then installed dependencies. Use the available .NET backend skills when relevant. If Ponytail is unavailable, report that limitation and still follow its simplicity rules and these repository standards; never simplify away required behavior or meaningful verification.
- Avoid speculative interfaces, pass-through services, generic repositories, custom connection/transaction wrappers, MediatR, AutoMapper, and additional speculative architecture projects. Never simplify away validation, authorization, error handling, or data integrity.
- Use `Asp.Versioning.Mvc` with URL-segment versioning and explicit controller routes. Do not hand-roll API versioning or use the deprecated `Microsoft.AspNetCore.Mvc.Versioning` package.
- Use `[ApiController]`, camelCase JSON, and positive integer IDs. Every request type must have a FluentValidation validator beside it, automatically registered by API assembly scanning. Await `ValidateAsync` with cancellation before provisioning or writes; return standardized 422 `ValidationProblemDetails` with camelCase errors through the shared native response helper. Keep malformed JSON/type-binding failures as native 400. Disable implicit required MVC annotations; do not mix request DataAnnotations/IValidatableObject or use the unsupported FluentValidation.AspNetCore auto-validation pipeline. Use `ProblemDetails` for HTTP errors and the workbook's status-code conventions. Keep `/health` anonymous and unversioned with native 200/503 readiness responses; expose OpenAPI only in Development.

## Persistence, identity, and product rules

- Use the Npgsql EF Core provider and scoped `KiloDbContext` through `AddKiloDatabase`. Await context operations sequentially, use `AsNoTracking`/DTO projections for reads and tracked mutations with `SaveChangesAsync`. Pass cancellation through async I/O and use bounded timeouts. Use explicit EF transactions for locks or multiple saves; no singleton contexts, generic repositories, or custom unit-of-work wrappers.
- Generate additive C# migrations in `Kilo.Persistence/Migrations` with the pinned local `dotnet-ef` tool. Commit migration, designer, and model snapshot together. The separate runner calls `MigrateAsync` and uses native `__EFMigrationsHistory`; never use `EnsureCreated` or edit an applied migration. Check pending model changes. The SQL appendix is a schema contract, not a script set. Only native EF history is supported; retained non-EF databases require a separately designed conversion. Never invent history or restore the removed legacy compatibility path. The API must never migrate on startup; run one migrator per deployment and require success before activating the release.
- Once the identity slice is implemented, use native JwtBearer validation for Clerk and a fallback authorization policy. Derive the local user ID from verified identity; scope private queries to it and enforce ownership in database relationships. Exercise reads may additionally include global rows; global writes require the native `Admin` policy with `RequireRole("admin")` and JWT `RoleClaimType = "role"`, sourced from signed Clerk server-controlled metadata. Admins cannot access other accounts' private exercises. Never trust a client-supplied owner ID.
- Reuse `Kilo.Persistence.Queries.OwnershipQueryExtensions.OwnedBy(userId)` for private EF queries on mapped integer/nullable-integer `UserId` entities. Pass only the verified positive ID from CurrentUser; compose resource IDs, nesting, and archive predicates before materializing. The helper excludes null/global owners and does not authorize global reads/admin writes, assign owners on creation, or enforce all child relationships. Keep those checks explicit and backed by database constraints.
- Exercises have a fixed scope: null owner for the shared global catalog, verified local owner for private custom entries. Requests cannot set owner, scope, or roles. Carry the global-or-own access constraints into placements and snapshots when their slices arrive.
- Preserve the workbook's product model: integer identity keys, text Clerk subjects and retry keys, imperial defaults, explicit `lb`/`kg` on every present weight, and workout snapshots independent of editable routine templates. Preference changes must not rewrite stored weights.
- Use UTC `timestamptz` instants, native EF `DateOnly` mapping to PostgreSQL `date`, and `TimeProvider` for application-generated time. Implement locking, idempotency, lifecycle rules, and ordering constraints as their slices require.
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

- Run the requested slice's meaningful verification gate. Use isolated PostgreSQL Testcontainers for real database checks when persistence changes; Docker is required for the full suite and database checks must not silently skip. Add focused regression checks for changed behavior, not tests that merely mirror the implementation.
- For Docker or Compose changes, build the affected images and verify startup order, migration success, and readiness in an isolated stack. Clean up only resources created for that verification.
- Plan edits belong in `docs/workout-tracker-plan.md`. Follow the efficient workbook workflow below to regenerate and verify the canonical PDF. When committing is requested, include the source, generator changes if any, and regenerated PDF together. Keep renders and temporary QA files out of Git.
- Review the staged diff before committing. Include source, migrations, tests, lock files, configuration templates, documentation, and the canonical PDF; exclude build output, caches, editor state, secrets, and temporary artifacts.
- Do not commit or push unless the user explicitly asks. This is the user's standing delivery preference. Preserve existing staged and unrelated work. When a commit is requested, stage explicit paths and review the diff; never force-push or rewrite shared history.
- Add targeted `.gitignore` rules for local/generated files that do not belong in the repository. Keep reusable source, verification scripts, shared templates, and the canonical PDF tracked; never ignore real project files just to hide an unrelated change. Reference `.env.example` in shared solution files; keep actual `.env` files local.
- State what changed, what passed, and any remaining limitation. Mark a slice complete only after its verification gate passes; do not claim later slices are complete because scaffolding exists.

## Efficient workbook updates

- Edit the Markdown source, not PDF pages directly. Finish the relevant implementation decisions first, then batch all related wording, recipes, references, and evidence changes before generating. Reuse `docs/build_workout_plan.py` and the installed ReportLab/pypdf tooling; avoid recreating the document or adding a new document pipeline.
- Regenerate the full canonical PDF once after the source edits are ready. Regenerate again only to fix an actual content or layout issue. An AGENTS.md-only or other documentation edit that does not change the workbook source/generator does not require PDF regeneration.
- Run lightweight structural checks on every regenerated PDF: source sections versus page count and bookmark destinations, expected fillable fields, canonical field/widget values, appearance streams, and widget bounds. Identify affected pages from section titles/bookmarks rather than hard-coded page numbers.
- For localized content edits, render and visually inspect only changed sections and relevant neighboring/boundary pages with `pdftoppm -f <first> -l <last>`. After inserting/reordering sections, also verify navigation and affected page boundaries. Expand the render range if content reflows; render the whole document when shared styles, fonts, margins, the generator/template, or broad layout changes could affect every page.
- Reuse verified unchanged renders during iteration; inspect fresh renders of every changed page before delivery. Keep QA files in an ignored `.artifacts` subdirectory. Do not rerun application builds, tests, or container gates solely because workbook wording changed; run those checks when code/configuration changes warrant them.
