# 07 — Storage Engine Review

**Primary type:** `NovaDB.Storage.MemoryStorageEngine`  
**Sharding:** CRC16 Redis Cluster slots → `shard = HashSlot.Compute(key) % ShardCount` (`HashSlot.cs`)

---

## Lookup complexity

| Op | Intended | Actual |
|----|----------|--------|
| GET/SET by key | O(1) dict in shard | O(1) after acquiring **process-wide** `_dbGate` |
| Cross-key | striped locks | Still serialized by gate |

Sharding reduces lock *granularity* only after the gate — under load it does not provide parallel throughput.

---

## Expiration

| Component | Assessment |
|-----------|------------|
| Lazy expire on read | Present in `GetAsync` / mutate paths via `IsExpired` |
| Active expire | `TimingWheelExpirationScheduler` — 4-level wheel, 256 slots, 50ms tick |
| Production-grade? | **Partially.** Timing wheel is the right algorithm family (better than full scan). Remaining risks: starts before recovery gate; holds `_wheelLock`; fires into storage that takes `_dbGate` |

**Suggestion:** Keep timing wheel; start only after `DatabaseRecoveryGate.MarkReady`; ensure expire callbacks don’t nest-deadlock with exclusive EXEC.

---

## Eviction

| Policy | File | Notes |
|--------|------|-------|
| `noeviction` | `NoEvictionPolicy` | Throws `OutOfMemoryNovaDbException` |
| LRU / LFU / TTL | respective classes | Sample size `EvictionSampleSize = 16` in engine |

Sampling under global gate: eviction walks shards sequentially outside shard locks (good), but still competes for gate.

`MemoryLimitBytes=0` means unlimited — production misconfig → host OOM killer, not Redis-style maxmemory.

---

## Snapshot consistency

`SnapshotWriter` takes `RunExclusiveAsync` for scan+serialize → **point-in-time yes**, **non-blocking no**. Redis RDB uses COW fork; NovaDB blocks writers/readers of the gate for the whole serialize.

Memory fragmentation: .NET GC; no slab allocator. Large value churn → LOH fragmentation (see memory audit).

---

## Value types

| Type | Binary-safe? | Notes |
|------|--------------|-------|
| String | Yes (`byte[]`) | |
| List | Yes | |
| Hash | Fields **No** (`string`) | Values Yes |
| Set | **No** (`HashSet<string>`) | Explicit in `SetValue` summary |
| Sorted set | Members closer to binary (`MemberKey`) | Prefer this pattern everywhere |

---

## Generations / WATCH support

`Shard.Generations` bumped on mutate/delete — supports WATCH without losing version on delete. Good design; undermined by string keys in `TransactionManager`.

---

## Verdict

Storage is a competent teaching/engine prototype with a timing wheel and striped shards, but the **global gate + UTF-8 set/hash + blocking snapshot** disqualify it as a Redis replacement for production multi-client workloads.
