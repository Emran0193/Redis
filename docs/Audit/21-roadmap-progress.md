# Roadmap progress (live)

Updated: 2026-09-17

## Showcase / cache-maturity track

### Done
- Product mode: single-node **ephemeral cache** (honest HA docs)
- SET `NX`/`XX`/`GET` + `SETEX`/`PSETEX`/`SETNX` + `GETEX`/`GETDEL` + `PTTL`
- `MSETNX`, `INCRBY`/`DECRBY`/`INCRBYFLOAT`, `STRLEN`, `UNLINK`, `TOUCH`
- Hash cache: `HMGET`, `HINCRBY`, `HEXISTS`, `HLEN`, `HKEYS`, `HVALS`
- Redis eviction policy names + volatile + live `CONFIG SET`
- Eviction pressure p99-bounded test
- `INFO` / `MEMORY` / `CLIENT` / minimal `ACL`
- StackExchange.Redis smoke + TCP cache soak + **pipeline soak**
- Removed replication stubs; README repositioned as portfolio showcase

### Prior (S1–S4 remediation)
Complete — see earlier entries.

## Tests
`dotnet test NovaDB.slnx` — **121** passed (Protocol 27 + Unit 82 + Integration 9 + Load 3).

## Explicitly out of scope (for now)
- Replica / Sentinel / Cluster
- Full ACL `SETUSER`
- Lua `EVAL`
