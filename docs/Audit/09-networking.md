# 09 — Networking Audit

**Files:** `TcpServerHostedService.cs`, `ClientConnection.cs`, `SocketPipeline.cs`, `ConnectionManager.cs`, `TlsCertificateProvider.cs`

---

## Checklist

| Item | Status | Evidence |
|------|--------|----------|
| Socket NoDelay | Yes | Listener + accepted socket |
| ReuseAddress | Yes | Listener |
| KeepAlive | **Missing** | No `SocketOptionName.KeepAlive` in `src` |
| Pipelines | Yes | Dual pipes + pumps |
| Backpressure | Partial | Pipe pause 64KB; AOF channel Wait; pub/sub drop |
| Cancellation | Yes | Linked CTS, shutdown registration |
| Graceful shutdown | Partial | Close listener, WaitAll connection tasks, drain timeout 30s |
| Connection cleanup | Yes | `OnDisconnectedAsync` session/hub/auth |
| Max clients | Yes | `ConnectionManager.TryAcquire` / reject |
| Idle timeout | Partial | On read path; refreshed by writes/pubsub |
| TLS 1.2/1.3 | Yes | `SslStream` server auth; revocation NoCheck |
| Nagle disabled | Yes | NoDelay=true |

---

## Slow client isolation

**Happy path:** Each connection runs `HandleConnectionAsync` independently — one slow client does **not** block Accept for other accepts (except reject path).

**Shared fate:** All clients share `MemoryStorageEngine._dbGate`. A slow client running `KEYS *` or blocked behind Always-AOF still holds/competes for the gate → **logical** head-of-line blocking across connections.

**Pipe stall:** One client sending large incomplete bulk can stall **that** connection (P0), not the accept loop.

---

## Findings

1. **P0** `pauseWriterThreshold: 65536` vs unbounded bulk (`SocketPipeline` + `RespParser`).
2. **P1** Idle timeout refreshed on `TouchActivity` during writes — silent pub/sub subscribers immortal.
3. **P2** `RejectConnectionAsync` awaited on accept thread.
4. **P2** No TCP keepalive — half-open connections until idle timeout (default 5m).
5. **P2** `AddressFamily.InterNetwork` only — IPv6 bind path resolves `IPAddress.IPv6Any` but listener is IPv4 socket → IPv6 bind broken.
6. **P3** Information log per connect/disconnect — noisy at 100k churn.

---

## IPv6 bug detail

`CreateListener` always `AddressFamily.InterNetwork`. `ResolveBindAddress("::")` returns `IPv6Any`, then `Bind` will throw. File: `TcpServerHostedService.CreateListener` / `ResolveBindAddress`.
