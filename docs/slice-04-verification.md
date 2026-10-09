# Slice 04 verification

Validation policy was subsequently revised to FluentValidation and 422 request-rule errors; see [the follow-up verification](request-validation-verification.md). The results below describe the original slice verification.

Reviewed October 8, 2026. Implementation includes Clerk identity and profile/preferences endpoints on the existing users schema. No new migration or future feature schema was added.

## Verification status

Slice 04 gate passed on October 8, 2026, on Windows with Docker Desktop's Linux engine (linux/amd64):

- Locked restore and solution build passed.
- Full suite: **35 passed, 0 failed, 0 skipped**. The fixture containers were removed afterward.
- Actual image gate passed: clean locked builds, context/layer exclusions, non-root UID 1654, environment-only Production startup, anonymous fallback authorization, trusted HTTPS, ICU/IANA timezones, two compiled EF migrations, and clean SIGTERM shutdown.
- Isolated Compose gate passed: environment-only Clerk settings, migration-before-API order, readiness, loopback ports, retained data across recreation, failed migration rollback and blocked API startup, repaired migration and repeatability. Only its own containers, network, volume and temporary image tags were removed.
- Workbook regeneration and rendered QA passed: 64 pages, 64 valid bookmarks, and 100 unique fillable fields/widgets with appearance streams. Progress fields remain blank workbook inputs rather than automated evidence.

Commands:

```powershell
dotnet restore Kilo.slnx --locked-mode
dotnet build Kilo.slnx --no-restore
dotnet test Kilo.slnx --no-restore
pwsh -NoProfile -File scripts/verify-images.ps1 -Release slice04-review -Platform linux/amd64
pwsh -NoProfile -File scripts/verify-compose.ps1 -Platform linux/amd64
git diff --check
```

The test logger recorded `.artifacts/TestResults/review-fixes/einge_EINGE-YOGA_2026-10-08_15_24_27.trx`. Sanitized image evidence is `.artifacts/image-check-b6dbbe2f847342e8b76817b75a9bce8b/images.json`; Compose evidence is `.artifacts/kilo-compose-check-6ca3bf8551844c9990e50b71b3969d9f/checks.json`. These local artifacts are intentionally ignored.

Release image IDs:

| Image | ID |
| --- | --- |
| kilo:slice04-review | sha256:21e67e8e3a28be61c7c314bf8e7792601dbb4cac961cc087af6970f91c27b4db |
| kilo-migrations:slice04-review | sha256:1b1e56b5b97b3767abb8392c0e1a65406259c590f74abc43f076ccaf0ea0e563 |

## Test scope

- MeControllerTests covers defaults, repeated reads, both measurement systems, persisted updates, invalid input with no mutation, and separate subjects.
- ClerkAuthenticationTests uses native JwtBearer with signed RSA tokens and fixture discovery/public JWKS HTTP responses. It covers claim restrictions, 401/403 behavior, concurrent first requests, key rotation, cached-key operation during outages, cold-cache sanitized 503, CORS, anonymous health and authenticated production OpenAPI exclusion.
- FoundationTests starts isolated Postgres through Testcontainers and verifies actual migrator replay, model consistency, defaults, rollback, cancellation, readiness and constraints. Its subprocess deadline is 45 seconds.
- Image and Compose scripts use dummy public Clerk settings, isolated resources and real builds. They preserve existing databases and volumes.

The JWKS transport is in process; it does not test a live Clerk account. Trusted outbound HTTPS and Linux IANA support are checked in the real images. Stored workout weight preservation is carried forward to the slices that introduce those weights; none exist in slice 04.
