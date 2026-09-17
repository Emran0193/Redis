# 08 — Security

## Data plane

| Control | Implementation |
| --- | --- |
| Connection rate limit | `ConnectionRateLimiter` in TCP accept loop (`ConnectionRateLimitPerSecond`) |
| Max payload / RESP limits | `MaxPayloadBytes`, `MaxBulkBytes`, `MaxArrayLength`, `MaxLineLength` |
| AUTH | Redis-style; timing-safe compare via `AuthPasswordVerifier` |
| AUTH lockout | Per-connection + remote IP exponential backoff |
| Optional TLS | PFX via `TlsEnabled` |
| Public bind guard | Non-loopback requires Password and/or TLS |
| Read-only replica | Mutating commands rejected |
| RequireAuthForWrites | Opt-in write gate when configured |
| Roles | `ClientRole` Admin / Operator / ReadOnly (session mapping after AUTH) |
| Audit | `IAuditTrail` / `FileAuditTrail` for FLUSH* and CONFIG SET |

## Admin plane

| Control | Implementation |
| --- | --- |
| Cookie auth | Roles Admin / Operator / ReadOnly |
| Password compare | `FixedTimeEquals` in `AdminCredentialStore` |
| Policies | ReadOnlyAccess / OperatorAccess / AdminAccess |
| Chaos API | Development-only |

Empty `NovaDB:Password` remains open access for demos unless `RequireAuthForWrites` is enabled.
