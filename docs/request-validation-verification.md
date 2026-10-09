# Request validation verification

Verified October 8, 2026 on Windows with Docker Desktop's Linux engine. This revision replaces the original slice-04/05 request-rule 400 policy with FluentValidation and HTTP 422. The earlier records retain their historical results.

## Implementation

- Pin `FluentValidation.DependencyInjectionExtensions` 12.1.1 and its FluentValidation dependency in the API/test lock files. `AddKiloApi` discovers public validators in the API assembly with the default scoped lifetime; `Program.cs` remains unchanged.
- `PreferencesRequestValidator` validates required text, resolvable IANA timezones including UTC, and exact imperial/metric choices. `ExerciseWriteRequestValidator` is shared by personal/admin POST and PUT: nonblank name, optional description, and at most 100 characters after brand trimming. Existing normalization and replacement semantics remain intact.
- Controllers inject `IValidator<TRequest>` and await `ValidateAsync` with cancellation before provisioning or writes. Native authentication/admin policies still execute first. Ownership, archive checks and database constraints remain independent.
- The shared `RequestValidationProblem` helper adds failures to native ModelState and returns 422 `ValidationProblemDetails` with camelCase error keys, validation title, the native RFC 4918 type, request-path instance and traceId. Malformed JSON, absent/null bodies and incompatible types remain native 400. OpenAPI write actions advertise 422.
- Request DataAnnotations/IValidatableObject and implicit MVC required annotations are replaced by FluentValidation. Entity mapping annotations remain unchanged. No model change or migration was required.
- AGENTS.md, README and the cumulative workbook document this pattern for future requests. Future feature validators are added only with those features.

## Checks

```powershell
dotnet restore Kilo.slnx --locked-mode
dotnet build Kilo.slnx --no-restore
dotnet test Kilo.slnx --no-restore
pwsh -NoProfile -File scripts/verify-images.ps1 -Release slice05-validation -Platform linux/amd64
git diff --check
```

- Locked restore and build passed. A clean API compile retains the existing IDE0005/GenerateDocumentationFile configuration warning; no new analyzer warning or build error remains.
- Full suite: **89 passed, 0 failed, 0 skipped** using disposable PostgreSQL Testcontainers. Checks include valid requests on all five write actions, null/empty/whitespace/unsupported preferences, missing/null/blank exercise names, trimmed brand boundaries, aggregated camelCase errors, exact native problem shape, malformed/type-binding/empty/null-body 400, rejection before first-account provisioning, unchanged rows, and authorization before validation. Existing signed-JWT, ownership, migration upgrade/replay and model-snapshot checks also passed.
- Finalized Linux image gate passed: clean locked builds, non-root UID 1654, Production startup from environment configuration, trusted HTTPS, ICU/IANA timezone support, three compiled EF migrations and clean SIGTERM shutdown. Local image IDs:
  - `kilo:slice05-validation`: `sha256:4b1ab1ed408d396a177391d4aa79277d77ca3b32952dfd21400efac058b6d533`
  - `kilo-migrations:slice05-validation`: `sha256:51c8d69944fea577353f7e4c661d68c0c5134d524dd76eb48563a6a869e0aba1`
- Workbook regenerated and rendered: **70 pages, 70 matching bookmarks, 100 unique interactive fields** with appearances, matching canonical/widget values and in-bounds rectangles. Complete contact sheets and the new validation page were visually inspected.
- Compose configuration was unchanged by this revision; its previous slice-05 isolation/startup/replay evidence remains in [slice-05 verification](slice-05-verification.md). The Compose gate was not rerun for this request-layer change. No deployment, real Clerk account check or future slice completion is claimed.
- Temporary logs/renders are ignored under `.artifacts`. Existing staged/unrelated work is preserved. Nothing was committed or pushed.

## Primary references

- [FluentValidation: explicit async controller validation](https://docs.fluentvalidation.net/en/latest/aspnet.html)
- [FluentValidation: assembly-scanning DI registration](https://docs.fluentvalidation.net/en/latest/di.html)
- [ASP.NET Core ControllerBase native ValidationProblem status override](https://github.com/dotnet/aspnetcore/blob/v10.0.0/src/Mvc/Mvc.Core/src/ControllerBase.cs)
