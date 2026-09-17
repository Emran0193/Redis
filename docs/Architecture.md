# NovaDB Architecture

NovaDB is a Redis-compatible in-memory key-value database implemented in C# on **.NET 10** (host SDK; plan targeted .NET 9, which is unavailable on this machine).

## Module boundaries

| Project | Responsibility |
| --- | --- |
| `NovaDB.Server` | Generic Host, HTTP health/metrics, DI composition |
| `NovaDB.Core` | Domain models, exceptions, `ClientSession` |
| `NovaDB.Protocol` | Zero-copy-friendly RESP2 parser/writer over `ReadOnlySequence<byte>` |
| `NovaDB.Networking` | Async TCP accept loop, Pipelines duplex, idle timeout, backpressure |
| `NovaDB.Storage` | `IStorageEngine`, sharded `MemoryStorageEngine`, timing wheel, eviction, value types |
| `NovaDB.Commands` | Command registry/dispatcher, handlers, auth gate, MULTI queue |
| `NovaDB.Transactions` | Optimistic `WATCH`/`MULTI`/`EXEC`/`DISCARD` |
| `NovaDB.PubSub` | Channel hub with async fan-out |
| `NovaDB.Persistence` | RDB-like snapshots, AOF, recovery gate |
| `NovaDB.Configuration` | `NovaDbOptions` bound from `appsettings.json` |
| `NovaDB.Monitoring` | OpenTelemetry metrics + health check |

## Request path

```text
TCP Client
  -> Socket + System.IO.Pipelines
  -> RespParser.TryParse
  -> CommandProcessor / CommandDispatcher
  -> Handlers -> IStorageEngine / IPubSubHub / ITransactionManager
  -> RespWriter -> PipeWriter
```

## Storage design

- Keys are sharded by Redis-compatible CRC16 hash slot mapped onto `ShardCount` striped dictionaries.
- Expiration uses a hierarchical timing wheel (no full-dictionary scan each tick) plus lazy expire-on-read.
- Sorted sets use an in-process skip list.
- Eviction policies: `noeviction`, `lru`, `lfu`, `ttl`.

## Persistence

- Snapshot: binary format with magic, version, optional compression, CRC32, atomic replace.
- AOF: RESP command frames, buffered channel writer, flush policies `always` / `everysec` / `no`.
- Recovery gate blocks TCP accept until snapshot+AOF replay completes.

## Topology

NovaDB is **single-node ephemeral cache** by product mode. Replication/HA is not implemented (see `docs/Replication.md`).
Treat the process as rebuildable unless AOF/snapshots are enabled for a demo durable path.

## Eviction

Policies: `noeviction`, `lru` / `allkeys-lru`, `lfu` / `allkeys-lfu`, `ttl`, and `volatile-*` variants via `ConfigurableEvictionPolicy` (live `CONFIG SET`).
