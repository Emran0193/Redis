# NovaDB Admin — gRPC API

Base URL: cleartext HTTP/2 on Server `GrpcPort` (`http://127.0.0.1:7381` by default).
Health and Prometheus stay on `HttpPort` (`7380`) over HTTP/1.1 — Kestrel cannot multiplex HTTP/1.1 and HTTP/2 without TLS on one port.

Protos live in `src/NovaDB.Contracts/Protos/admin.proto`.

## Services (Phase 1)

| Service | RPCs |
| --- | --- |
| `MetricsService` | `GetSnapshot` |
| `KeyService` | `ScanKeys`, `GetKey`, `SetString`, `DeleteKey`, `RenameKey` |
| `ClientService` | `ListClients`, `KillClient` |
| `ConfigService` | `GetConfig`, `SetConfig` |
| `HealthService` | `GetHealth` |
| `PersistenceService` | `GetStatus`, `TriggerSnapshot`, `TriggerAofRewrite` |
| `PubSubService` | `GetStats`, `Publish` |

## Authn for gRPC

Phase 1: gRPC is bound to loopback via Server Kestrel. Network isolation is the primary control. Admin UI enforces cookie RBAC before calling mutating RPCs.

## Client usage (.NET)

```csharp
builder.Services.AddGrpcClient<MetricsService.MetricsServiceClient>(o =>
{
    o.Address = new Uri(builder.Configuration["NovaDB:AdminGrpc:Address"]!);
});
```
