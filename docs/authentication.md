# Authentication

## Current Phase

`VirtualTickets.Api` supports display-terminal JWT authentication backed by
`dbo.Terminals`. During migration, sensitive ticket endpoints also accept the
legacy shared `X-Virtual-Tickets-Key` header.

JWT configuration can be supplied through appsettings or environment variables:

- `VIRTUAL_TICKETS_CONNECTION_STRING`
- `VIRTUAL_TICKETS_JWT_ISSUER`
- `VIRTUAL_TICKETS_JWT_AUDIENCE`
- `VIRTUAL_TICKETS_JWT_SIGNING_KEY`
- `VIRTUAL_TICKETS_JWT_MINUTES`

`VIRTUAL_TICKETS_CONNECTION_STRING` is the canonical SQL connection string
setting for all database access in this API.

Protected endpoints:

- `GET /api/virtual-tickets`
- `GET /api/virtual-tickets/{receiptId}`
- `POST /api/tickets/validate`
- `POST /api/tickets/place`

## Target Terminal Identity Model

Future display authentication should reuse the existing `dbo.Terminals` table
instead of creating a separate display registry.

Existing columns:

- `TerminalId`
- `TerminalName`
- `BranchId`
- `IpAddress`
- `DateCreated`
- `IsActive`

Additional columns are captured in
`Database/Scripts/001_extend_terminals_for_authenticated_displays.sql`.

Terminal types:

- `1` = Virtual Display
- `2` = Cashier Display
- `3` = Wallboard
- `4` = Kiosk

## JWT Flow

`POST /api/auth/display` should accept:

```json
{
  "terminalCode": "SHOP01-DISPLAY01",
  "secret": "...",
  "version": "1.0.0"
}
```

The API looks up `dbo.Terminals` by `TerminalCode`, requires `IsActive = 1`,
requires `TerminalType = 1`, verifies `SecretHash` with the ASP.NET Identity
password hasher, updates `LastSeenAt`, `LastVersion`, and optionally
`IpAddress`, then issues a short-lived JWT.

JWT claims should include:

- `terminal_id`
- `terminal_code`
- `branch_id`
- `terminal_type`

Future internal APIs should validate the shared JWT instead of each API owning a
separate terminal authentication mechanism.

## Development CORS

Development CORS allows only the local display origins on ports `3000`, `3001`,
and `3002` for both `localhost` and `127.0.0.1`. JWTs are sent through the
`Authorization: Bearer` header, so browser credential mode is not required for
display API calls.
