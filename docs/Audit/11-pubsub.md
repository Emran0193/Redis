# 11 — Pub/Sub Audit

**Hub:** `PubSubHub`  
**Per-connection:** `ConnectionPubSubSubscriber`, `PubSubSubscriberCache`

---

| Concern | Status | Evidence |
|---------|--------|----------|
| Channel storage | `ConcurrentDictionary<string, ConcurrentDictionary<string, IPubSubSubscriber>>` | String channels only |
| Subscriber cleanup | `RemoveSubscriber` on disconnect | `CommandProcessor.OnDisconnectedAsync` |
| Slow consumer | Bounded outbox 1024, `TryWrite` | Overflow → `IsOverflowed` / `ShouldClose` |
| Backpressure on publish | Non-blocking drop | Does not stall publishers |
| Fan-out | Snapshot subscribers + `Task.WhenAll` | Allocates per publish |
| Ordering | Per-connection outbox FIFO | Cross-subscriber no global order (Redis same) |
| Pattern subscribe | **Missing** | No PSUBSCRIBE |
| Binary channels | **No** | `string` |
| Force close on overflow | **Incomplete** | Flag only; may wait for next command/idle |

## Memory leak risks

- Hub entry removed on disconnect — OK if all paths call `OnDisconnectedAsync` (TCP finally block does).
- Session `Subscriptions` may remain until disconnect even if hub dropped overflowed subscriber.

## Verdict

Adequate for light pub/sub; not production-safe under slow-consumer + long idle without forced teardown. No pattern pub/sub → not Redis-compatible for many apps.
