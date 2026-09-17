# NovaDB

A Redis-compatible **single-node in-memory cache** in C# (.NET 10) — built as an engineering showcase: RESP2, sharded storage, AOF/snapshots, transactions, eviction, and production hardening.

> **Product mode:** ephemeral / rebuildable cache (single process). Not Redis Cluster/Sentinel. Prefer treating the data plane as disposable unless AOF/snapshots are explicitly enabled for demos.

## Highlights (portfolio-ready)

- **RESP2** over TCP with `System.IO.Pipelines`, size caps, and large-bulk safe pause thresholds
- **String cache:** `SET` (`NX`/`XX`/`GET`/`EX`/`PX`…), `SETEX`/`PSETEX`, `SETNX`, `GETEX`/`GETDEL`, `MGET`/`MSET`/`MSETNX`, `INCR`/`INCRBY`/`INCRBYFLOAT`, `STRLEN`, `UNLINK`, `TOUCH`
- **Hash cache:** `HSET`/`HGET`/`HMGET`/`HDEL`/`HGETALL`/`HINCRBY`/`HEXISTS`/`HLEN`/`HKEYS`/`HVALS`
- **maxmemory** eviction with Redis policy names; live `CONFIG SET maxmemory` / `maxmemory-policy`
- Persistence: AOF (incl. Always group-commit for `EXEC`) + CRC snapshots with short-freeze serialize
- `MULTI`/`EXEC`/`WATCH`, pub/sub, AUTH + IP lockout, TLS option, Prometheus metrics, readiness-aware `/health`
- Ops surface: `INFO`, `MEMORY`, `CLIENT`, minimal `ACL` probe commands
- Client trust: StackExchange.Redis smoke + multi-client **pipeline soak**

## Run

```bash
dotnet run --project src/NovaDB.Server
```

Or full local stack (Aspire dashboard + Server + Admin UI):

```bash
dotnet run --project aspire/NovaDB.AppHost
```

Default RESP port: **6379**. HTTP health/metrics: **7380**. Admin gRPC (HTTP/2): **7381**. Admin UI from AppHost (login `admin` / `changeme`).

See [docs/Integration.md](docs/Integration.md) and [docs/Admin/](docs/Admin/).

## Test

```bash
dotnet test NovaDB.slnx
```

## Security (showcase defaults)

- Loopback bind (`127.0.0.1`) with empty password is fine for demos.
- Non-loopback bind **requires** `Password` and/or `TlsEnabled` (unless `AllowUnauthenticatedPublicBind`).
- `AUTH` + IP lockout are implemented; `ACL` exposes WHOAMI/LIST/USERS for client probes — **not** full Redis ACL (single default user).
- Prefer: set `NovaDB:Password` in `appsettings.json` before any shared deployment.

## Benchmark

```bash
dotnet run -c Release --project benchmarks/NovaDB.Benchmarks
```
