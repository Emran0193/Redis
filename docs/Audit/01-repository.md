# 01 — Repository Inventory

**Target:** `c:\Users\emran\Desktop\Redis`  
**TFM:** net10.0 (`Directory.Build.props`)  
**Solution:** `NovaDB.slnx`  
**Audit date:** 2026-09-16

---

## Tree (source of truth)

```
Redis/
├── NovaDB.slnx
├── Directory.Build.props
├── README.md
├── docs/
│   ├── Architecture.md, Benchmarks.md, Persistence.md, Replication.md, RESP.md
│   ├── progress/Phase1.md … Phase6.md
│   └── Audit/                    ← this review
├── src/
│   ├── NovaDB.Server/            ← entry point (Program.cs)
│   ├── NovaDB.Core/
│   ├── NovaDB.Configuration/
│   ├── NovaDB.Protocol/
│   ├── NovaDB.Storage/
│   ├── NovaDB.Persistence/
│   ├── NovaDB.Networking/
│   ├── NovaDB.Commands/
│   ├── NovaDB.Transactions/
│   ├── NovaDB.PubSub/
│   └── NovaDB.Monitoring/
├── tests/
│   ├── UnitTests/
│   ├── ProtocolTests/
│   ├── IntegrationTests/
│   └── LoadTests/
└── benchmarks/
    └── NovaDB.Benchmarks/
```

---

## Projects and responsibilities

| Project | Responsibility |
|---------|----------------|
| **NovaDB.Server** | ASP.NET Core host: DI composition, `/health`, Prometheus `/metrics`, registers TCP server after `DatabaseRecoveryGate` |
| **NovaDB.Core** | Domain primitives: `RedisKey`, `DatabaseEntry`, `ClientSession`, exception hierarchy, replication *stubs* only |
| **NovaDB.Configuration** | `NovaDbOptions` bind + `ValidateOnStart` |
| **NovaDB.Protocol** | RESP2 `RespParser` / `RespWriter` / `RespValue` |
| **NovaDB.Storage** | `MemoryStorageEngine`, sharding, eviction, timing-wheel expiration |
| **NovaDB.Persistence** | AOF writer/replay, snapshots, recovery gate/service |
| **NovaDB.Networking** | TCP accept, Pipelines, TLS, connection lifecycle |
| **NovaDB.Commands** | Command handlers, dispatcher, AUTH backoff, AOF command replay adapter |
| **NovaDB.Transactions** | MULTI/EXEC/DISCARD/WATCH |
| **NovaDB.PubSub** | Channel hub fan-out |
| **NovaDB.Monitoring** | OpenTelemetry meter, health check, storage metrics adapter |

---

## Entry points

| Entry | File |
|-------|------|
| Process main | `src/NovaDB.Server/Program.cs` |
| TCP accept loop | `src/NovaDB.Networking/TcpServerHostedService.cs` |
| Per-connection RESP loop | `src/NovaDB.Networking/ClientConnection.RunAsync` |
| Command pipeline | `CommandProcessor` → `CommandDispatcher` |
| Benchmarks | `benchmarks/NovaDB.Benchmarks/Program.cs` |

---

## DI registration surface (`AddNovaDb*`)

| Extension | File |
|-----------|------|
| `AddNovaDbConfiguration` | `Configuration/ServiceCollectionExtensions.cs` |
| `AddNovaDbProtocol` | `Protocol/ServiceCollectionExtensions.cs` (no-op) |
| `AddNovaDbStorage` | `Storage/ServiceCollectionExtensions.cs` |
| `AddNovaDbPersistence` | `Persistence/ServiceCollectionExtensions.cs` |
| `AddNovaDbNetworking` | `Networking/ServiceCollectionExtensions.cs` (**not called by Server**) |
| `AddNovaDbMonitoring` | `Monitoring/ServiceCollectionExtensions.cs` |
| `AddNovaDbCommands` | `Commands/ServiceCollectionExtensions.cs` |
| `AddNovaDbCommandReplay` | `Commands/Persistence/CommandReplayServiceCollectionExtensions.cs` |
| `AddNovaDbTransactions` | nested in `Transactions/TransactionManager.cs` |
| `AddNovaDbPubSub` | nested in `PubSub/PubSubHub.cs` |

Server composition order (`Program.cs`): Configuration → Protocol → Storage → Persistence → Transactions → PubSub → Monitoring → Commands → CommandReplay → manual `TcpServerHostedService` (gated).

---

## Hosted / background services

| Service | File | Role |
|---------|------|------|
| `TimingWheelExpirationScheduler` | `Storage/Expiration/TimingWheelExpirationScheduler.cs` | Key TTL firing |
| `AofWriter` | `Persistence/Aof/AofWriter.cs` | AOF drain + fsync + rewrite |
| `DatabaseRecoveryService` | `Persistence/Recovery/DatabaseRecoveryService.cs` | Startup load; opens gate |
| `SnapshotBackgroundService` | `Persistence/SnapshotBackgroundService.cs` | Periodic snapshot + AOF rewrite |
| `TcpServerHostedService` | `Networking/TcpServerHostedService.cs` | Accept + connection tasks |

---

## Configuration

- Options type: `NovaDB.Configuration.NovaDbOptions` (section `"NovaDB"`)
- Files: `src/NovaDB.Server/appsettings.json`, `appsettings.Development.json`
- Defaults of note: `BindAddress=127.0.0.1`, `Password=""`, `AofFlushPolicy=everysec`, `MaxConnections=10000`, `ShardCount=16`, `IdleTimeout=5m`, TLS off

---

## Important namespaces

| Assembly | Namespaces |
|----------|------------|
| Core | `NovaDB.Core.Models`, `.Sessions`, `.Exceptions`, `.Replication` |
| Storage | `NovaDB.Storage`, `.Values`, `.Eviction`, `.Expiration` |
| Persistence | `NovaDB.Persistence.Aof`, `.Snapshot`, `.Recovery`, `.Internal` |
| Commands | `NovaDB.Commands`, `.Handlers`, `.Internal`, `.Persistence`, `.PubSub` |
| Networking | `NovaDB.Networking` |

---

## Registered commands (52)

`PING ECHO QUIT AUTH HELLO SELECT CONFIG CLIENT COMMAND INFO SET GET MGET MSET DEL EXISTS INCR DECR APPEND EXPIRE PEXPIREAT TTL PERSIST HSET HGET HDEL HGETALL LPUSH RPUSH LPOP RPOP LRANGE SADD SREM SMEMBERS ZADD ZRANGE ZSCORE DBSIZE FLUSHDB FLUSHALL KEYS SCAN BGREWRITEAOF MULTI EXEC DISCARD WATCH SUBSCRIBE UNSUBSCRIBE PUBLISH`

**Absent vs Redis:** `UNWATCH`, pattern pub/sub (`PSUBSCRIBE`), most admin/cluster/stream/geo/scripting commands, multi-DB beyond `SELECT 0`.

---

## Test / benchmark assemblies

| Project | Focus |
|---------|-------|
| ProtocolTests | RESP parse/write |
| UnitTests | Commands, storage, AOF recovery, auth, pub/sub, transactions |
| IntegrationTests | TCP PING, reuse, TLS |
| LoadTests | 20 clients × 50 cmds (in-process) |
| NovaDB.Benchmarks | BenchmarkDotNet (Storage/Persistence) |
