# Slice 03 verification

Status: **Done** for the current Linux AMD64 Docker host. Verified October 6, 2026, at 21:41 UTC with Docker Compose 2.31.0.

```powershell
pwsh -NoProfile -File scripts/verify-compose.ps1 -Platform linux/amd64
```

The reusable check completed successfully against the actual Compose configuration and freshly built API/migrator images. It used a uniquely named disposable project, an empty database volume, generated environment credentials, and random loopback ports. It neither loaded the repository's local `.env` nor changed the existing development stack.

| Acceptance | Observed result |
| --- | --- |
| 03.1 Fresh startup | Postgres became healthy; migrations exited 0; the API started after migration completion and returned 200 Healthy. |
| 03.2 Container networking | Both executables connected using Host=db and Database=kilo on the isolated Compose network. |
| 03.3 Persistent data | A users fixture retained its identity after compose down/up recreated all containers. The new DB container mounted the same named volume, and the migration journal count remained unchanged. |
| 03.4 Failed migration | A new embedded test script exited nonzero; Compose up failed; the API stayed in Created state. Repairing only that unapplied script, rebuilding, and running a fresh migrator restored 200 Healthy. |
| 03.5 Local access | Original development configuration binds API 8080 and DB 5432 to 127.0.0.1. Actual test container bindings used random 127.0.0.1 ports. Base configuration publishes no service ports. |

The base configuration also rejected a missing password. Its DB password and both connection strings matched the generated environment value, confirming there was no hardcoded development credential.

## Migration failure and repair

The check added `999999_compose_gate_<random-id>.sql` only to a temporary source snapshot. It created a test table, inserted a row, then executed division by zero. The migrator emitted the sanitized failure diagnostic, the table did not exist afterward, the failed script had no journal entry, and the existing users fixture survived.

The repair removed the failing statement from that same unapplied test script. A new migration container applied it successfully, leaving the repaired marker and exactly one new journal entry. Another fresh migration run left the journal count unchanged. No applied repository migration was edited; `Kilo.Migrations/Migrations` still contains only the users baseline.

The running API was removed before the failure phase because Compose dependency conditions apply to startup. The README provides the corresponding local update commands to stop the API and replace the completed migrator before rebuilding the stack. See [Compose startup ordering](https://docs.docker.com/compose/how-tos/startup-order/).

## Cleanup and carry forward

The check removed its containers, network, named volume, image tags, and temporary source files. Cleanup was confirmed by Docker resource listings; only the original `kilo-api-1` and `kilo-db-1` containers remained running. Sanitized machine-readable results stay under ignored `.artifacts/kilo-compose-check-*/checks.json`.

The production Compose and development override needed no changes. Temporary random-port replacement uses native [`!override`](https://docs.docker.com/reference/compose-file/merge/) and requires Compose 2.24.4 or newer. No Compose unit-test framework was added.

Slice 04 can now extend registration with Clerk identity and authorization. The cumulative plan and canonical PDF are unchanged.
