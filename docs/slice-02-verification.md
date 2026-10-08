# Slice 02 verification

Status: **Done** for the current Linux AMD64 Docker host. Reverified October 7, 2026 (Phoenix) after the EF-only cleanup and entity organization.

## Build and runtime gate

```powershell
pwsh -NoProfile -File scripts/verify-images.ps1 -Release ef-cleanup-entities-20261007 -Platform linux/amd64
```

The command completed successfully. It built both Release images with `--pull --no-cache`, locked NuGet restores, and a clean source snapshot. Neither build required a database or supplied database credentials. The runtime probe is a dependency-free helper outside the application solution and is mounted only into temporary verification containers; it is not included in either release image.

| Acceptance | Evidence |
| --- | --- |
| 02.1 Clean image builds | Both Docker builds succeeded from source without local bin/obj. The migrator image discovers the compiled EF migrations, `CreateUsers` and `OrganizeUserEntity`, in shared `Kilo.Persistence.dll`. Its EF runtime dependencies load successfully. |
| 02.2 Target architecture | Both image metadata and running .NET processes reported Linux AMD64/X64. |
| 02.3 Non-root runtime | Both configured and actual runtime UIDs were 1654. |
| 02.4 HTTPS/globalization | Both final images completed a trusted HTTPS request to Microsoft Learn, loaded French numeric culture, and resolved America/New_York winter/summer offsets. |
| 02.5 Shutdown | The production API accepted SIGTERM, logged host shutdown, and exited 0 without a forced kill or OOM. |

The context audit excluded harmless `.env`, key, build-output, Git, editor, temporary, and output sentinels, including ones under the application directory. Every saved image layer was inspected for excluded secret/editor paths; image environment settings contained no connection strings or credentials. The API bound to port 8080 with environment-only configuration, returned native 503 readiness with an unavailable database, and returned 404 for production OpenAPI. The migration executable returned nonzero with the expected sanitized diagnostic when its connection string was absent.

The separate [slice 03 gate](slice-03-verification.md) reverified fresh EF migration success, readiness, persistence, and failure/repair against disposable Compose resources. Existing development containers and data were preserved.

## Image identifiers

Both images carry `org.opencontainers.image.version=ef-cleanup-entities-20261007`.

| Local image tag | Verified local image ID |
| --- | --- |
| `kilo:ef-cleanup-entities-20261007` | `sha256:3775b86b07b33e4e72ed87c2660cfbd3bb4bb9298d5176b8d4001acc5679587b` |
| `kilo-migrations:ef-cleanup-entities-20261007` | `sha256:560bd97f6457887e6884758a30259a1c10a432c987e0b819b84b242d61efc197` |

These are local build identifiers, not published registry references. A rebuild can produce different image IDs; record the outputs for each release and use a new release tag.

## Pinned official base manifests

Resolved from Microsoft Container Registry using `docker buildx imagetools inspect` and verified by the builds. The manifests support AMD64 and ARM variants; this gate verified AMD64 only.

| Base | Multi-platform digest |
| --- | --- |
| `mcr.microsoft.com/dotnet/sdk:10.0` | `sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317` |
| `mcr.microsoft.com/dotnet/aspnet:10.0` | `sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4` |
| `mcr.microsoft.com/dotnet/runtime:10.0` | `sha256:b89586dc17781f25531909993658aa8161205ae38b8cec8847df4a8221a403d5` |

Review base-image security updates explicitly and rerun the gate after changing a digest. See [Docker's pinning guidance](https://docs.docker.com/build/building/best-practices/) and [Microsoft's globalization guidance](https://learn.microsoft.com/en-us/dotnet/core/docker/container-images).

## Carry forward

Transactional-write shutdown remains deferred until feature writes exist, as required by the workbook, and is rechecked in slice 20. This record does not mark slice 03's persistence/failure gates or later identity/features complete. The plan and canonical PDF now describe cumulative EF implementation. See the [transition record](ef-core-transition.md) for model/tooling checks and cleanup.
