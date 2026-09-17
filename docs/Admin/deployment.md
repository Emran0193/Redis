# NovaDB Admin — Deployment

## Local (Aspire)

```bash
dotnet run --project aspire/NovaDB.AppHost
```

Set secrets:

```bash
setx NOVADB_ADMIN_PASSWORD "..."
setx NOVADB_OPERATOR_PASSWORD "..."
setx NOVADB_READONLY_PASSWORD "..."
```

Or user-secrets on Admin project.

## Production checklist

1. TLS terminate Admin (and ideally Server HTTP/gRPC).
2. Strong unique passwords for all three roles.
3. Bind Server RESP carefully; require `NovaDB__Password` off loopback.
4. Scraping `/metrics` only from Prometheus network.
5. Persist Admin audit file to durable volume if required.
6. Run `dotnet test NovaDB.slnx` in CI before release.

## Ports

| Port | Service |
| --- | --- |
| 6379 | RESP |
| 7380 | Health, Prometheus (HTTP/1.1) |
| 7381 | Admin gRPC (HTTP/2 cleartext) |
| (dynamic) | Admin Blazor (Aspire assigns) |
| (Aspire) | Aspire dashboard |
