# Integrating NovaDB into other software

NovaDB is a **Redis-compatible RESP2 cache**. Other apps talk to it with any Redis client — there is no NovaDB-specific SDK.

> **Product mode:** single-node ephemeral / rebuildable cache. Not Cluster or Sentinel. Prefer cache-aside against your source of truth.

## Quick start (server only)

```bash
dotnet run --project src/NovaDB.Server
```

| Endpoint | Default | Purpose |
| --- | --- | --- |
| RESP (Redis protocol) | `127.0.0.1:6379` | Client data plane |
| HTTP health | `http://127.0.0.1:7380/health` | Readiness / liveness |
| Prometheus metrics | `http://127.0.0.1:7380/metrics` | Scrapable OTEL meters |
| gRPC Admin API | `http://127.0.0.1:7381` | Ops UI / internal tools (HTTP/2 cleartext) |
| Root | `http://127.0.0.1:7380/` | Service card |

Verify:

```bash
redis-cli -h 127.0.0.1 -p 6379 PING
curl http://127.0.0.1:7380/health
curl http://127.0.0.1:7380/metrics
```

## One-command local stack (Aspire + Admin)

```bash
dotnet run --project aspire/NovaDB.AppHost
```

This starts:

1. **Aspire dashboard** — resources, console logs, OTLP traces/metrics
2. **NovaDB.Server** — RESP `:6379`, HTTP `:7380`, gRPC `:7381`
3. **NovaDB.Admin** — Blazor ops UI (RedisInsight-style), talks to Server **only via gRPC**

Admin never opens a RESP connection. See [Admin/architecture.md](Admin/architecture.md).

Default Admin login (override via env / user-secrets):

| User | Password env | Role |
| --- | --- | --- |
| `admin` | `NOVADB_ADMIN_PASSWORD` (default `changeme`) | Admin |
| `operator` | `NOVADB_OPERATOR_PASSWORD` (default `changeme`) | Operator |
| `readonly` | `NOVADB_READONLY_PASSWORD` (default `changeme`) | ReadOnly |

## Architecture pattern (cache-aside)

```text
Your API / worker
   │
   ├─ Redis client ──► NovaDB (hot cache, TTL + maxmemory)
   │
   └─ DB / HTTP ─────► source of truth (on miss or eviction)

Operators
   └─ NovaDB.Admin ──gRPC──► NovaDB.Server (same process host as metrics/health)
```

1. `GET` / `HGET` from NovaDB.
2. On miss, load from primary store.
3. `SET` / `HSET` with `EX`/`PX` (or `SETEX`/`PSETEX`).
4. Optionally `DEL`/`UNLINK` on write-through invalidation.

## .NET (StackExchange.Redis)

```bash
dotnet add package StackExchange.Redis
```

```csharp
using StackExchange.Redis;

builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(new ConfigurationOptions
    {
        EndPoints = { "127.0.0.1:6379" },
        AbortOnConnectFail = false,
        ConnectRetry = 3
    }));

var db = mux.GetDatabase();
await db.StringSetAsync("user:42", json, TimeSpan.FromMinutes(5));
var value = await db.StringGetAsync("user:42");
```

With password: `"127.0.0.1:6379,password=YOUR_SECRET"`.

## Other languages

| Stack | Package | Example |
| --- | --- | --- |
| Node.js | `ioredis` / `redis` | `new Redis({ host: "127.0.0.1", port: 6379 })` |
| Python | `redis` | `Redis(host="127.0.0.1", port=6379)` |
| Go | `go-redis` | `redis.NewClient(&redis.Options{Addr: "127.0.0.1:6379"})` |
| Java | Lettuce / Jedis | `RedisURI.create("redis://127.0.0.1:6379")` |

## Supported command surface (integration allowlist)

**Strings / keys:** `SET` (`NX`/`XX`/`GET`/`EX`/`PX`…), `GET`, `SETEX`, `PSETEX`, `SETNX`, `GETEX`, `GETDEL`, `MGET`, `MSET`, `MSETNX`, `INCR`/`DECR`/`INCRBY`/`DECRBY`/`INCRBYFLOAT`, `APPEND`, `STRLEN`, `DEL`, `UNLINK`, `EXISTS`, `TOUCH`, `TTL`/`PTTL`, `EXPIRE`/`PEXPIREAT`, `PERSIST`

**Hashes:** `HSET`, `HGET`, `HMGET`, `HDEL`, `HGETALL`, `HINCRBY`, `HEXISTS`, `HLEN`, `HKEYS`, `HVALS`

**Also present:** lists/sets/zsets (basic), `MULTI`/`EXEC`/`WATCH`, pub/sub, `INFO`/`MEMORY`/`CLIENT`/`CONFIG`, minimal `ACL`, `AUTH`, `PING`

**Not a drop-in for:** Cluster, Sentinel, Lua `EVAL`, full ACL `SETUSER`.

## Configuration (`NovaDB` section)

| Key | Meaning |
| --- | --- |
| `Port` | RESP listen port (default `6379`) |
| `HttpPort` | Health / metrics HTTP/1.1 (default `7380`) |
| `GrpcPort` | Admin gRPC HTTP/2 cleartext (default `7381`) |
| `BindAddress` | Default `127.0.0.1` |
| `Password` | Empty = no AUTH |
| `MemoryLimitBytes` | `0` = unlimited |
| `EvictionPolicy` | e.g. `allkeys-lru` |
| `AofEnabled` / `AofFlushPolicy` | Durability for demos |
| `DataDirectory` | Snapshot + AOF |
| `ShardCount` | Storage striping |

Live ops: `CONFIG SET maxmemory` / `maxmemory-policy`, or Admin Settings page (audited).

## Observability

| Surface | URL / tool |
| --- | --- |
| Health JSON | `/health` |
| Prometheus | `/metrics` |
| Aspire dashboard | AppHost launch |
| Admin UI | Blazor app from AppHost |
| RESP `INFO` | redis-cli / clients |

## Production checklist

1. Set `NovaDB__Password` before non-loopback bind.
2. Set `MemoryLimitBytes` + eviction policy for cache demos.
3. Override Admin passwords via env / user-secrets.
4. Confirm `/health` is Healthy before traffic.
5. Scrape `/metrics` or use Aspire OTLP.
6. Treat data as ephemeral unless AOF/snapshots are intentional.
7. Keep application clients on the command allowlist.

## Testing

```bash
dotnet test NovaDB.slnx
dotnet test tests/IntegrationTests --filter FullyQualifiedName~StackExchange
dotnet test tests/AdminTests
```

## Further reading

- [Admin architecture](Admin/architecture.md)
- [Admin gRPC](Admin/grpc.md)
- [Admin security](Admin/security.md)
- [Admin deployment](Admin/deployment.md)
