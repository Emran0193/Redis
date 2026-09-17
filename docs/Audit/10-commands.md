# 10 — Command System Audit

**Dispatcher:** `CommandDispatcher`  
**Registration:** `ServiceCollectionExtensions.AddNovaDbCommands` → `IEnumerable<ICommandHandler>` → `ToDictionary` by `Name`

---

## Open/Closed

- **Open for extension:** add handler class + `RegisterHandler<T>` — no switch on command name in dispatcher.
- **Closed violations:** static `MutatingCommands`, auth allowlist, pub/sub allowlist, transaction allowlist HashSets in `CommandDispatcher` — must edit dispatcher when adding mutating/auth-exempt commands (silent AOF data loss if forgotten).

## Reflection

None at runtime for dispatch (good). DI type registration only.

## Allocation-free dispatch

- Dictionary lookup O(1) — fine at 52 commands; perfect hash unnecessary until 1000s of commands.
- Per-command cost dominated by `RespValue` / UTF-8 / storage gate, not dispatch.

## Mutation sink coupling

`MutatingCommands` must stay in sync with handlers — **P1 process risk**. Prefer interface marker on handler.

## Missing commands (compat)

See `17-redis-compatibility.md`. Notably **no UNWATCH** handler registered.

## Auth gate

Allowlist: AUTH, HELLO, COMMAND, PING, QUIT. COMMAND leaks catalog without auth (**P3**).

## Replay path

`ExecuteDirectAsync` used for AOF replay / EXEC — bypasses MULTI queueing correctly; still records mutations when `recordMutation: true` (EXEC durability interaction with gate — see concurrency).
