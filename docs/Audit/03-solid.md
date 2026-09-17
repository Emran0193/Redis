# 03 — SOLID & Design Patterns

Inspection of public/significant types. Severity: P0 critical · P1 high · P2 medium · P3 low.

| Class | Issue | Severity | Recommended refactor | File |
|-------|-------|----------|----------------------|------|
| `MemoryStorageEngine` | God class: sharding, eviction, TTL notify, generations, global gate, scan | P0 | Split `ShardStore`, `EvictionCoordinator`, `GenerationTracker`, `ExclusiveGate` | `Storage/MemoryStorageEngine.cs` |
| `MemoryStorageEngine` | SRP + scalability: `_dbGate` serializes all ops (remarks admit Redis single-thread emulation) | P0 | Remove global gate from reads; exclusive only for EXEC snapshot of versions | same |
| `AofWriter` | SRP overload: channel, fsync policies, rewrite, hosted lifecycle, mutation sink | P1 | Split `AofAppender`, `AofRewriter`, `AofHostedService` | `Persistence/Aof/AofWriter.cs` |
| `ClientConnection` | Large class: TLS, idle, pipelines, write lock, processor loop | P2 | Extract `IdleTracker`, `TransportFactory` | `Networking/ClientConnection.cs` |
| `CommandDispatcher` | Static `HashSet` for mutating/auth/pubsub — OCP friction | P2 | Handler metadata attributes (`IMutatingCommand`, `AuthExempt`) | `Commands/CommandDispatcher.cs` |
| `RespParser` / `RespValue` | Primitive obsession + allocation: every bulk = owned `byte[]` | P1 | Sequence-backed values with explicit consume lifetime | `Protocol/RespParser.cs` |
| `SetValue` | Wrong abstraction: `HashSet<string>` for Redis set | P0 | `HashSet<byte[]>` / `MemberKey` like sorted sets | `Storage/Values/SetValue.cs` |
| `HashValue` | Field names as `string` | P0 | Binary field keys | `Storage/Values/HashValue.cs` |
| `TransactionManager` | Feature envy / primitive obsession: watches via `keys[i].ToString()` | P1 | `Dictionary<RedisKey,long>` with byte comparer | `Transactions/TransactionManager.cs` |
| `PubSubHub` | Channels keyed by `string`; nested ConcurrentDictionaries | P2 | Binary channel names; single index by connection | `PubSub/PubSubHub.cs` |
| `ScanCommandHandler` | Duplicate full-scan pattern vs KEYS; fake cursor | P1 | Shard-native cursor iterator on `IStorageEngine` | `Commands/Handlers/KeyspaceCommands.cs` |
| `*CommandHandler` groups | Many handlers per file (String/Hash/List…) — maintainability | P3 | One type per file or source generators | `Commands/Handlers/*` |
| `AuthPasswordVerifier` + `AuthRateLimiter` | Good SRP split | — | Keep; extend limiter to IP | `Commands/Auth*.cs` |
| `BgRewriteAofCommandHandler` | Service locator `GetService<IAofLog>()` | P3 | Optional `IAofLog?` via factory registration | `Handlers/PersistenceCommands.cs` |
| `NovaDbHealthCheck` | Wrong responsibility: “healthy” ≠ recovered | P1 | Inject `DatabaseRecoveryGate`; split ready/live | `Monitoring/NovaDbHealthCheck.cs` |
| `TcpServerHostedService` | Accept + reject + drain in one type | P2 | Extract `ConnectionAcceptor` | `Networking/TcpServerHostedService.cs` |
| `SnapshotWriter` | Holds exclusive gate for serialize (comment acknowledges) | P0 | Capture immutable view quickly; serialize unlocked | `Persistence/Snapshot/SnapshotWriter.cs` |
| `DispatchingAofCommandReplayer` | Adapter is correct DIP | — | Keep | `Commands/Persistence/DispatchingAofCommandReplayer.cs` |
| `ICommandMutationSink` dual paths | Adapter + Persistence sink — interface duplication | P3 | Single sink in Persistence; Commands depends on abstraction only | `Persistence` + `Commands` |
| `CommandContext.Services` | Ambient service locator for handlers | P2 | Explicit ctor DI only | `Commands/CommandContext.cs` |
| `Replica*` / `IReplication*` | Interface pollution / dead API surface | P2 | Remove until implemented, or quarantine | `Core/Replication/*` |
| `InfoCommandHandler` | Incomplete INFO vs Redis — stub risk | P2 | Document as non-compatible or expand | `Handlers/ConnectionCommands.cs` |
| `ConfigCommandHandler` | Partial CONFIG — clients may assume Redis | P2 | Explicit unsupported errors | `Handlers/HandshakeCommands.cs` |

## Pattern usage (observed)

| Pattern | Where | Assessment |
|---------|-------|------------|
| Strategy | `IEvictionPolicy` | Appropriate |
| Adapter | `PersistenceMutationSinkAdapter`, `StorageMetricsAdapter` | Appropriate; order-sensitive DI |
| Hosted service | AOF, snapshot, TCP, timing wheel | Appropriate |
| Optimistic concurrency | WATCH generations | Partially correct (see transactions audit) |
| Channel producer/consumer | AOF, pub/sub outbox | Appropriate with caveats |
| Singleton handlers | All `ICommandHandler` | Fine; must stay thread-safe (session is per-connection) |

## Duplicate logic

- Full `ScanAsync` materialization in `KEYS`, `SCAN`, `FLUSHDB` (`KeyspaceCommands.cs`).
- AUTH path duplicated conceptually between `AuthCommandHandler` and `HelloCommandHandler` — mitigated by shared `AuthPasswordVerifier` (good).
- Exclusive scan+encode in both `SnapshotWriter` and `AofWriter.RewriteFromStorageCoreAsync`.
