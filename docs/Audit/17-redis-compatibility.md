# 17 — Redis Compatibility Matrix

NovaDB speaks **RESP2 only** (`HELLO 3` → NOPROTO). Baseline: Redis 7 standalone subset.

## Protocol

| Feature | NovaDB | Notes |
|---------|--------|-------|
| RESP2 | Yes | |
| RESP3 | No | Explicit reject |
| Pipelining | Yes | Parse loop in `ClientConnection` |
| Inline commands | No | Array form only |
| Pub/sub push | Yes | |
| Max bulk | Unbounded | Redis has `proto-max-bulk-len` |

## Command coverage (high level)

| Family | Status |
|--------|--------|
| Strings (subset) | Partial — SET/GET/MGET/MSET/DEL/EXISTS/INCR/DECR/APPEND |
| Hashes | Partial — HSET/HGET/HDEL/HGETALL |
| Lists | Partial — LPUSH/RPUSH/LPOP/RPOP/LRANGE |
| Sets | Partial — SADD/SREM/SMEMBERS — **not binary-safe** |
| Sorted sets | Partial — ZADD/ZRANGE/ZSCORE |
| Keys | DBSIZE/FLUSHDB/FLUSHALL/KEYS/SCAN/TTL/EXPIRE/PERSIST |
| Tx | MULTI/EXEC/DISCARD/WATCH — **no UNWATCH** |
| Pub/Sub | SUBSCRIBE/UNSUBSCRIBE/PUBLISH — **no PSUBSCRIBE** |
| Connection | PING/ECHO/QUIT/AUTH/HELLO/SELECT/CLIENT/COMMAND/INFO/CONFIG |
| Persistence cmds | BGREWRITEAOF — no BGSAVE/SAVE/LASTSAVE |
| Scripts/Streams/Geo/Bitmap/HyperLogLog/ACL/Cluster/Modules | **Absent** |

## Behavior differences

| Topic | Redis | NovaDB |
|-------|-------|--------|
| Multi-DB | 16 DBs | SELECT ≠0 errors; single engine |
| HGETALL order | Unspecified | Sorted by field string (`OrderBy`) — extra cost, still not a compat guarantee clients need |
| SCAN | Cursor over dict | Full snapshot then index — wrong complexity |
| Set/Hash binary | SDS bytes | UTF-8 strings |
| AUTH errors | WRONGPASS / etc. | `NOAUTH` prefix via `AuthenticationException` even for bad password |
| AOF+RDB | Hybrid possible | AOF XOR snapshot |
| Blocking cmds | BLPOP etc. | Absent |
| Replication | Full | Stubs only |

## TTL semantics

Relative → absolute on AOF (`AofCommandRewriter`) — correct durability intent. Lazy + timing wheel — OK if wheel started post-recovery.

## Error format

`PREFIX message` via `NovaDbException.ToRespError()`. Prefixes: ERR, WRONGTYPE, NOAUTH, OOM, etc.

## Compatibility verdict

**Not a drop-in Redis replacement.** Suitable only for clients using the narrow implemented subset with UTF-8 textual payloads and RESP2.
