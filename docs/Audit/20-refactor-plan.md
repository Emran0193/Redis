# 20 — Refactor Roadmap

## Sprint 1 — Critical fixes (availability & DoS)

| Task | Files | Reason | Risk | Hours | Perf gain |
|------|-------|--------|------|-------|-----------|
| Add MaxBulk/MaxArray/MaxLine; reject+disconnect | `RespParser.cs`, `NovaDbOptions.cs`, `ClientConnection.cs` | Stop OOM DoS | Low | 8–12 | Survives hostile traffic |
| Align `pauseWriterThreshold` ≥ max bulk; early reject by length | `SocketPipeline.cs`, `RespParser.cs` | Stop 64KB livelock | Low | 4–8 | Large SET works |
| Health ready = `DatabaseRecoveryGate` | `NovaDbHealthCheck.cs`, `Program.cs` | Correct orchestration | Low | 2–4 | Ops correctness |
| IP-based AUTH throttle + bind/auth policy | `AuthRateLimiter.cs`, `Program.cs`/`NovaDbOptions` | Brute-force / open bind | Med | 8–12 | Security |
| Force-cancel connection on pub/sub overflow | `ConnectionPubSubSubscriber.cs`, `ClientConnection.cs` | Leak fix | Low | 4–6 | Stability |
| Fix IPv6 listener family | `TcpServerHostedService.cs` | Bind bug | Low | 2–3 | Correctness |
| RESP fuzz + &gt;64KB integration tests | `ProtocolTests`, `IntegrationTests` | Lock fixes | Low | 8–12 | Confidence |

## Sprint 2 — Performance

| Task | Files | Reason | Risk | Hours | Perf gain |
|------|-------|--------|------|-------|-----------|
| Remove `_dbGate` from Get/Mutate; exclusive only for EXEC version check + apply | `MemoryStorageEngine.cs`, tests | Unlock parallelism | **High** | 24–40 | Orders of magnitude under concurrency (gate removal) |
| Async gate wait (no sync `Wait`) if exclusive retained | same | Thread-pool health | Med | 4–8 | Tail latency |
| Real shard SCAN cursor API | `IStorageEngine`, `KeyspaceCommands.cs` | O(batch) SCAN | Med | 16–24 | Large-N admin viable |
| Remove HGETALL OrderBy; cut LINQ ToArray on hot paths | `HashCommands.cs`, others | Alloc | Low | 4–8 | Lower GC |
| ArrayPool for AOF serialize path | `RespWriter.cs`, `AofWriter.cs` | Alloc | Med | 12–16 | Lower GC on writes |

## Sprint 3 — Reliability

| Task | Files | Reason | Risk | Hours | Perf gain |
|------|-------|--------|------|-------|-----------|
| Snapshot: freeze metadata under short lock; serialize unlocked / stream to disk | `SnapshotWriter.cs` | Stop multi-second stalls | **High** | 24–40 | Availability during backup |
| AOF rewrite from frozen snapshot file, not live exclusive scan | `AofWriter.cs`, `SnapshotBackgroundService.cs` | Same | High | 16–24 | Availability |
| Stream AOF replay | `AofReplayEngine.cs` | Startup RAM | Med | 12–16 | Large AOF boot |
| Defer AOF for EXEC batch (group fsync) | `CommandDispatcher.cs`, `AofWriter.cs`, `ExecCommandHandler` | Gate hold | High | 16–24 | EXEC latency |
| Start timing wheel after recovery | `TimingWheelExpirationScheduler`, DI | Replay races | Med | 4–8 | Correctness |
| Binary-safe Set/Hash + watch keys | `SetValue.cs`, `HashValue.cs`, `TransactionManager.cs`, codecs | Redis correctness | Med | 16–24 | Compat + less UTF8 churn |
| Crash-injection tests | new test harness | Durability proof | Med | 16–24 | Confidence |

## Sprint 4 — Scalability

| Task | Files | Reason | Risk | Hours | Perf gain |
|------|-------|--------|------|-------|-----------|
| Multi-acceptor / SO_REUSEPORT or partition listeners | `TcpServerHostedService.cs` | Accept scaling | High | 24+ | Conn setup rate |
| Connection / session memory slim | `ClientConnection`, sessions | 100k conns | Med | 16–24 | Density |
| AOF queue depth + rewrite metrics; tracing | `Monitoring/*`, AOF | Operate at scale | Low | 8–12 | Operability |
| Command ACL / admin isolation | Dispatcher + options | Protect data plane | Med | 12–16 | Safety |
| Replication MVP or delete stubs | `Core/Replication`, docs | Honest product | High | 40+ | HA (if implemented) |
| redis-benchmark / soak CI | tests/LoadTests | Evidence-based claims | Med | 16–24 | Truth in marketing |

## Sequencing note

Do **not** invest Sprint 4 connection scale before Sprint 2 gate removal — otherwise 100k connections share one mutex and amplify failure.
