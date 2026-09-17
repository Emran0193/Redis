# Aspire AppHost

Local orchestration for NovaDB Server + Admin + Aspire dashboard.

```bash
dotnet run --project aspire/NovaDB.AppHost
```

| Resource | What you get |
| --- | --- |
| Aspire dashboard | Logs, traces, OTLP metrics |
| `novadb` | RESP `127.0.0.1:6379`, HTTP `7380`, gRPC `7381` |
| `admin` | Blazor Admin UI (login admin/changeme) |

See [docs/Integration.md](../docs/Integration.md) and [docs/Admin/](../docs/Admin/).
