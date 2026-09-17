# 18 — Technical Debt Backlog

| Priority | Issue | Impact | Effort | Risk | Owner |
|----------|-------|--------|--------|------|-------|
| P0 | Unbounded RESP lengths (`RespParser`) | OOM / DoS | S | Low | Protocol |
| P0 | Pipe 64KB pause vs large bulk (`SocketPipeline`) | Connection deadlock | S | Low | Networking |
| P0 | Global `_dbGate` on all ops (`MemoryStorageEngine`) | Throughput collapse | L | High | Storage |
| P0 | Snapshot/AOF rewrite exclusive serialize | Availability stalls | L | High | Persistence |
| P0 | Set/Hash not binary-safe | Data corruption vs Redis clients | M | Med | Storage |
| P1 | AUTH lockout per connection only | Brute-force | S | Low | Commands |
| P1 | Health ignores recovery gate | False healthy | S | Low | Monitoring |
| P1 | AOF replay loads full file | Startup OOM | M | Med | Persistence |
| P1 | SCAN/KEYS full materialization | CPU/RAM DoS | M | Med | Commands/Storage |
| P1 | No KeepAlive / IPv6 listener bug | Ops/network | S | Low | Networking |
| P1 | EXEC+Always AOF under exclusive | Latency amplification | M | High | Tx/Persistence |
| P1 | Missing fuzz/crash/large-payload tests | Unknown regressions | M | Low | QA |
| P2 | MutatingCommands static set drift | Silent AOF loss | S | Med | Commands |
| P2 | Expiration before recovery ready | Replay races | S | Med | Storage |
| P2 | Pub/sub overflow soft-close | Resource leak | S | Low | PubSub/Net |
| P2 | Cleartext password in config | Secret hygiene | S | Low | Config/Sec |
| P2 | AddNovaDbNetworking unused | Dual host footgun | S | Low | Server |
| P2 | No UNWATCH / PSUBSCRIBE | Compat | S–M | Low | Commands |
| P2 | Replication stubs | False completeness | M | — | Core |
| P3 | COMMAND without auth | Info leak | S | Low | Commands |
| P3 | Service locator in handlers | Testability | S | Low | Commands |
| P3 | No ArrayPool | Alloc pressure | M | Med | Protocol |
| P3 | Chatty connect logs | Log volume | S | Low | Networking |

Effort: S &lt; 1d · M 2–5d · L &gt; 1w (one senior engineer estimate).
