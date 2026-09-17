# NovaDB Admin — Security

## Roles

| Role | Capabilities |
| --- | --- |
| `ReadOnly` | View dashboard, keys, clients, metrics, health, logs |
| `Operator` | ReadOnly + kill clients, publish test pub/sub, trigger snapshot/rewrite |
| `Admin` | Operator + change config (maxmemory, eviction, etc.) |

## Authentication

- ASP.NET Core cookie authentication on Admin.
- Passwords from environment / user-secrets — **not** committed in `appsettings.json`.
- Blazor antiforgery applies to interactive server forms.

## Audit

Destructive and config-changing actions write to `IAdminAuditLog` (in-memory ring + optional file under Admin data directory):

- `SetConfig`
- `KillClient`
- `DeleteKey` / `RenameKey` / `SetString`
- `TriggerSnapshot` / `TriggerAofRewrite`

## Threat model (Phase 1)

- Admin and Server expected on loopback or private network.
- Do not expose Admin or Server HTTP to the public internet without TLS + hardened auth.
- RESP password (`NovaDB__Password`) is independent of Admin UI passwords.
