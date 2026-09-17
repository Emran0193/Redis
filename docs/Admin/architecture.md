# NovaDB Admin — Architecture

Phase-1 ops platform: Blazor Server UI + gRPC control plane on NovaDB.Server + Aspire orchestration.

```mermaid
flowchart TB
  subgraph browser [Browser]
    UI[Blazor_Server_circuit]
  end
  subgraph admin [NovaDB_Admin]
    Pages[MudBlazor_pages]
    Hub[SignalR_LiveHub]
    Feeder[LiveTelemetryFeeder]
    GrpcClient[gRPC_clients]
  end
  subgraph server [NovaDB_Server]
    Resp[RESP_TCP_6379]
    Http[HTTP_7380]
    GrpcSvc[gRPC_Admin_services]
    Storage[MemoryStorageEngine]
    Metrics[INovaDbMetrics]
  end
  UI --> Pages
  Pages --> GrpcClient
  Pages --> Hub
  Feeder --> GrpcClient
  Feeder --> Hub
  GrpcClient -->|HTTP2| GrpcSvc
  GrpcSvc --> Storage
  GrpcSvc --> Metrics
  Clients[App_Redis_clients] --> Resp
```

## Principles

- **UI never speaks RESP.** All ops go through gRPC.
- **Live updates:** Admin hosts SignalR; a hosted feeder calls unary gRPC snapshots ~1s and pushes to browsers.
- **Auth:** Cookie auth with roles Admin / Operator / ReadOnly.
- **Telemetry:** ServiceDefaults export OTLP when Aspire injects `OTEL_EXPORTER_OTLP_ENDPOINT`.

## Projects

| Project | Role |
| --- | --- |
| `NovaDB.Contracts` | `.proto` + generated gRPC stubs |
| `NovaDB.ServiceDefaults` | OTEL + health helpers |
| `NovaDB.Server` | RESP + HTTP health/metrics + gRPC admin |
| `NovaDB.Admin` | Blazor Server + MudBlazor |
| `NovaDB.AppHost` | Aspire orchestration |
