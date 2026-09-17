# 19 — Production Scorecard

Scoring rule: 9–10 excellent · 7–8 ready with improvements · 5–6 beta · &lt;5 not production ready.

| Category | Score | Justification |
|----------|-------|---------------|
| Architecture | **6** | Clean DAG; Persistence↔Protocol coupling; Commands megastore; unused Networking DI; replication stubs |
| Performance | **3** | Global gate + blocking snapshot/rewrite + O(N) SCAN dominate; cannot claim high QPS |
| Memory | **4** | Unbounded parse; full AOF/snapshot buffers; UTF-8 duplication; no pooling |
| Networking | **5** | Pipelines/NoDelay/max clients real; pipe deadlock; no KeepAlive; IPv6 broken; reject blocks accept |
| Storage | **5** | Timing wheel + shards + generations good ideas; gate + binary-unsafe sets/hashes undermine them |
| Persistence | **6** | Fsync policies, CRC gens, handler replay, BGREWRITEAOF; exclusive stalls + full replay buffer |
| Transactions | **6** | MULTI/EXEC/WATCH with generations; no UNWATCH; exclusive+fsync; string watch keys |
| PubSub | **5** | Non-blocking overflow good; soft close; no patterns; string channels |
| Security | **3** | Timing-safe compare + lockout exist; trivial DoS via RESP; reconnect AUTH bypass; cleartext secrets |
| Observability | **5** | Prometheus metrics present; health wrong; no tracing; missing AOF lag gauges |
| Testing | **5** | 93 focused unit/integration tests; no fuzz/crash/scale/hostile payload |
| Maintainability | **6** | Clear projects/docs; god engine; static mutation sets; handler file clumps |

## Overall: **4.9 / 10** → **Not Production Ready**

Rounded category mean ≈ 4.9. Per rules: **below 5 = Not Production Ready**.

This is appropriate for an advanced prototype / internal Redis-*like* subset, **not** a Redis replacement for real production workloads under hostile or large-keyspace conditions.

## What would move overall to ≥7

Must land P0s: RESP caps + pipe fix, remove/narrow `_dbGate`, non-blocking snapshot/rewrite, binary-safe collections — then re-score Performance/Memory/Security/Storage.
