# Persistence

## Snapshot (RDB-like)

- Location: `{DataDirectory}/dump.ndb` (implementation file names may vary; see `FileSnapshotStore`).
- Header: magic + version + flags (compression bit).
- Body: key entries with type, expire, version, typed payload.
- Trailer: CRC32 over payload.
- Write path: temp file → CRC → atomic replace so readers never observe partial files.
- Optional GZip when `SnapshotCompression` is enabled.
- Background service runs on `SnapshotInterval`.

## Append Only File (AOF)

- Enabled via `AofEnabled`.
- Mutating commands are serialized as RESP arrays and queued to a bounded channel
  (`AofWriteQueueCapacity`); producers wait once the queue is full rather than growing it without
  bound.
- Background worker appends to `appendonly.aof` in batches.
- Relative expirations never reach the log. `EXPIRE key 60` is stored as `PEXPIREAT key <absolute>`
  and `SET key v EX 60` as `SET key v PXAT <absolute>`, so a replay hours later cannot hand a key a
  fresh TTL or resurrect one that already expired.
- Flush policy (`AofFlushPolicy`):
  - `always` — one fsync per drained batch, and the command is not acknowledged to the client until
    that fsync has returned, so a successful reply means the write is durable
  - `everysec` — periodic fsync; up to one second of acknowledged writes can be lost on power failure
  - `no` — OS buffering only
- If the writer fails (for example a full disk) further writes are refused with an error instead of
  being silently dropped.

## Recovery

Exactly one source is applied, because replaying the log on top of a snapshot would apply every
append-style command (`LPUSH`, `SADD`, `HSET`, `ZADD`) a second time:

1. If AOF is enabled and `appendonly.aof` is non-empty, the log is the source of truth and the
   snapshot is ignored. A warning is logged when both are present.
2. Otherwise the latest snapshot is loaded.
3. `DatabaseRecoveryGate.MarkReady()` unlocks the TCP accept loop.

Replay goes through the real command handlers (`DispatchingAofCommandReplayer`) rather than a
parallel implementation, so it cannot silently skip a command the handlers support. It is
fail-closed: a corrupt record, an unknown command, or a rejected command aborts startup with
`AofReplayException`. The one tolerated case is a truncated final record, which is the expected
result of a crash midway through an append.

Reads are never blocked by snapshot/AOF flush workers; writers use background channels and striped
storage locks only.

None for the basic rewrite path: after each successful periodic snapshot the AOF is rewritten from
the live dataset (`IAofLog.RewriteFromStorageAsync`), so the log stays proportional to the keyspace
rather than to the write history. Clients can also trigger the same rewrite with `BGREWRITEAOF`
(returns an error if a rewrite is already in progress).

Snapshot writes keep up to `SnapshotGenerationCount` prior files (`snapshot.novdb`,
`snapshot.novdb.1`, …). On load, a CRC or format failure on a newer generation falls back to the
next older generation before giving up.

Failed `AUTH` / `HELLO … AUTH` attempts apply per-connection exponential backoff
(`AuthLockoutBase` … `AuthLockoutMax`).
