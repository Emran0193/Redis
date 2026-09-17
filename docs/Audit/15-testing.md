# 15 — Test Quality Audit

## Inventory (approx.)

| Suite | Count (last full run) | Nature |
|-------|----------------------|--------|
| ProtocolTests | 24 | RESP unit |
| UnitTests | 63 | Commands, storage, AOF, auth, tx, pubsub |
| IntegrationTests | 5 | TCP/TLS |
| LoadTests | 1 | 20×50 in-process |
| **Total** | **93** | |

Quality ≠ coverage of production failure modes.

## Strengths (evidence)

- AOF recovery round-trip + corrupt fail-closed (`AofRecoveryTests`).
- Snapshot CRC fallback (`PersistenceRecoveryTests`).
- WATCH delete/recreate (`TransactionTests`).
- Auth backoff (`AuthRateLimitTests`).
- BGREWRITEAOF concurrent reject (`AofRecoveryTests`).
- Disconnect cleanup tests exist.

## Gaps (untested / weak)

| Area | Gap |
|------|-----|
| Security | No fuzz of RESP lengths; no >64KB bulk pipeline test |
| Binary safety | No `SADD`/`HSET` with non-UTF8 bytes |
| Crash | No process-kill mid-fsync / mid-EXEC |
| Scale | No 10k–100k connection soak; LoadTests not socket-bound |
| Networking | Idle timeout, KeepAlive, IPv6 bind, reject-path accept latency |
| Health | Gate vs `/health` coupling |
| Pub/Sub | Overflow → forced disconnect |
| Replication | Stubs only — no tests |
| Property/fuzz | None (no FsCheck/SharpFuzz) |
| Mutation testing | Not set up |
| Protocol compliance | No redis-test / redis-benchmark harness |

## Untested production code (examples)

- `SocketPipeline` pause thresholds under large messages
- `TcpServerHostedService` IPv6 path
- `NovaDbHealthCheck` + recovery interaction
- `TimingWheelExpirationScheduler` vs recovery race
- Full Always-mode EXEC gate hold duration
- TLS revocation disabled path (N/A)
- Many command edge arities beyond happy path

## Verdict

Solid **unit-level correctness** for implemented features; **inadequate** as production confidence for hostile traffic, large payloads, or multi-GB durability.
