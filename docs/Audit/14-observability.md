# 14 — Observability Audit

## Present

| Signal | Implementation |
|--------|----------------|
| Metrics | `NovaDbMetrics` — Meter `"NovaDB"`: command latency, hit/miss, clients, memory, eviction, expiry |
| Export | OpenTelemetry → Prometheus at `/metrics` (`Program.cs`) |
| Health | `NovaDbHealthCheck` on `/health` |
| Logging | `ILogger` throughout TCP/AOF/snapshot |

## Missing / broken

| Gap | Severity | Detail |
|-----|----------|--------|
| Recovery readiness | **P1** | Health ignores `DatabaseRecoveryGate` — HTTP Healthy while TCP still waiting |
| Liveness vs readiness | **P1** | Single `/health`; should split |
| Health does ScanAsync | **P2** | Contends on `_dbGate` |
| Distributed tracing | **P2** | No ActivitySource / spans around command or AOF |
| Correlation IDs | **P2** | ConnectionId in logs inconsistently; no traceparent |
| Latency histograms | Partial | Recorded via OTel instruments — verify bucket config in exporter setup |
| GC / LOH metrics | **P3** | Not exported explicitly |
| AOF lag / queue depth metric | **P1** | No gauge for channel count / rewrite in progress |
| Slowlog | **Missing** | No Redis SLOWLOG equivalent |

## Plan to complete OpenTelemetry

1. Inject `DatabaseRecoveryGate` into ready check; map `/health/ready` and `/health/live`.
2. `ActivitySource("NovaDB")` around `CommandDispatcher.ExecuteHandlerAsync` and AOF batch write.
3. Gauges: `aof_queue_length`, `aof_rewrite_in_progress`, `db_gate_waiters` (if measurable), `pipeline_input_bytes`.
4. Exemplars linking high latency to ConnectionId.
5. Remove full scan from health — use `KeyCount` + gate flag only.
