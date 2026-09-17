# 13 — Security Audit

## AUTH / secrets

| Item | Finding | File |
|------|---------|------|
| Password storage | Cleartext in `NovaDbOptions.Password` / config | `NovaDbOptions.cs` |
| Compare | Padded `FixedTimeEquals` | `AuthPasswordVerifier.cs` |
| Lockout | Per `connectionId`, exponential backoff | `AuthRateLimiter.cs` |
| Reconnect bypass | `Forget` on disconnect clears lockout | `CommandProcessor.OnDisconnectedAsync` |
| TLS PFX password | Cleartext option | `NovaDbOptions.TlsCertificatePassword` |
| Default auth | Off when password empty | Defaults |

**Mitigation:** Hash password (Redis ACL style); rate-limit by remote IP + global budget; refuse non-loopback bind without auth+TLS.

## DoS vectors

| Vector | Severity | Evidence |
|--------|----------|----------|
| Huge RESP bulk/array length | **P0** | `RespParser` allocates before cap |
| Pipe pause livelock | **P0** | 64KB pause + incomplete parse |
| KEYS / fake SCAN | **P1** | Full keyspace materialization |
| Unbounded memory (maxmemory 0) | **P1** | Default unlimited |
| AOF queue Wait pile-up | **P1** | Disk stall blocks writers |
| Snapshot exclusive stall | **P0** | Availability DoS via BGREWRITE/snapshot |
| COMMAND without auth | **P3** | Info disclosure |
| Max connections | Mitigated | Reject path (slow) |

## ACL surface (showcase)

| Item | Status |
|------|--------|
| `ACL WHOAMI` / `USERS` / `LIST` / `GETUSER` / `CAT` / `HELP` | Implemented (single `default` user) |
| `ACL SETUSER` / multi-user | **Not implemented** — use `AUTH` password |
| Pre-auth allowlist | Includes `ACL` so clients can probe before `AUTH` |

## Input validation

- Arity checks via `CommandArgumentReader` — present on handlers.
- Malformed RESP → `ProtocolException` — connection warned; no global quarantine.
- No per-connection command rate limit / request size budget beyond missing RESP caps.

## TLS

- Optional; revocation check disabled (`X509RevocationMode.NoCheck`) — operational risk on public PKI (less relevant for private PFX).

## Suggested mitigations (priority)

1. MaxBulkBytes / MaxArrayLen / MaxLineLen + disconnect.
2. Align pipe thresholds; reject oversize from header.
3. IP auth throttle; bind+auth policy.
4. Admin command ACL (KEYS/FLUSH/CONFIG).
5. Separate liveness from data-plane under load (timeouts on exclusive ops).
