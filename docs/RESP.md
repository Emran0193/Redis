# RESP Protocol

NovaDB implements **RESP2** manually in `NovaDB.Protocol` (no external Redis client/server libraries).

## Types

| Prefix | Type | Example |
| --- | --- | --- |
| `+` | Simple String | `+OK\r\n` |
| `-` | Error | `-ERR unknown command\r\n` |
| `:` | Integer | `:1000\r\n` |
| `$` | Bulk String | `$5\r\nhello\r\n` / `$-1\r\n` null |
| `*` | Array | `*2\r\n$3\r\nGET\r\n$3\r\nkey\r\n` |

## Parser

`RespParser.TryParse(ref ReadOnlySequence<byte> buffer, out RespValue? value, out SequencePosition consumed)`:

- Returns `false` when the buffer does not yet contain a full value (incremental Pipelines reads).
- Copies bulk payloads into owned `byte[]` so pipeline advances remain safe.
- Throws `ProtocolException` on malformed frames.

## Writer

`RespWriter.Write(IBufferWriter<byte>|PipeWriter, RespValue)` emits CRLF-terminated RESP with minimal intermediate strings (integer formatting via stackalloc spans).

## Commands

Clients send command arrays of bulk strings. NovaDB replies with the appropriate RESP type; application exceptions map to `-PREFIX message\r\n` without stack traces.
