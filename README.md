# VirtualTickets.Api

Standalone ASP.NET Core 8 Web API for validating virtual ticket requests before ticket creation is implemented.

This project is intentionally generic. SmartBet is only one possible client/database copy and is not used in the app name or API surface.

## Configuration

Set the database connection string with an environment variable. Do not put secrets in `appsettings.json`.
`VIRTUAL_TICKETS_CONNECTION_STRING` is the canonical SQL connection string setting used by all API database access.

PowerShell:

```powershell
$env:VIRTUAL_TICKETS_CONNECTION_STRING="Server=localhost;Database=virtualdb_data;User Id=...;Password=...;TrustServerCertificate=True"
```

Bash:

```bash
export VIRTUAL_TICKETS_CONNECTION_STRING="Server=localhost;Database=virtualdb_data;User Id=...;Password=...;TrustServerCertificate=True"
```

JWT terminal authentication can be configured with:

```powershell
$env:VIRTUAL_TICKETS_JWT_SIGNING_KEY="long-random-secret"
$env:VIRTUAL_TICKETS_JWT_ISSUER="VirtualTickets.Api"
$env:VIRTUAL_TICKETS_JWT_AUDIENCE="VirtualDisplay"
$env:VIRTUAL_TICKETS_JWT_MINUTES="120"
```

## Run Locally

```bash
dotnet restore
dotnet run
```

Swagger is available at `/swagger`.

Health check:

```bash
curl http://127.0.0.1:5088/api/health
```

## Validate Request Example

```bash
curl -X POST http://127.0.0.1:5088/api/tickets/validate \
  -H "Content-Type: application/json" \
  -d '{
    "source": "VirtualDisplay",
    "provider": "ExampleProvider",
    "providerEventId": "EVT-123",
    "externalTicketId": "EXT-456",
    "sourceDisplayId": "DISPLAY-1",
    "shopCode": "SHOP001",
    "username": "cashier1",
    "stake": 1000,
    "selections": [
      {
        "providerMatchId": "PM-1",
        "matchId": 12345,
        "matchOddId": 67890,
        "market": "Winner",
        "option": "Home",
        "line": null,
        "odd": 1.85,
        "shortCode": "H"
      }
    ]
  }'
```

## Linux and Docker Hosting Notes

- The API targets `net8.0` and has no WebUI, .NET Framework, or Windows-only project references.
- Provide `VIRTUAL_TICKETS_CONNECTION_STRING` through the host, container runtime, orchestrator secret, or deployment environment.
- The app performs read-only validation checks only. It does not run migrations and does not create or alter database tables.
- Expose the ASP.NET Core port with `ASPNETCORE_URLS`, for example `http://+:8080`.

Example container environment:

```bash
docker run -e ASPNETCORE_URLS=http://+:8080 \
  -e VIRTUAL_TICKETS_CONNECTION_STRING="Server=sql;Database=virtualdb_data;User Id=...;Password=...;TrustServerCertificate=True" \
  -p 8080:8080 virtualtickets-api
```

## Publish

```bash
dotnet publish -c Release -o ./publish
```

## Current Scope

Implemented endpoints:

- `GET /api/health`
- `POST /api/tickets/validate`

Not implemented yet:

- ticket creation
- `PaymentReference` idempotency
- receipts, bets, accounts, statements, or balance writes
- migrations or schema changes

The database probes use conservative read-only SQL against recognized table and column names. If a copied schema uses different names, validation returns a structured `*_schema_unknown` error instead of writing data or assuming a mapping.
# VirtualTicketsAPi
