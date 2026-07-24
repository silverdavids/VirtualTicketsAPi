# Virtual-ticket payout

Virtual-ticket payout is available through:

- `POST /api/tickets/payout/lookup`
- `POST /api/tickets/payout`

Both endpoints require an authenticated, active virtual-display terminal. The
terminal branch must match the receipt branch.

## Payout actor

This installation does not use terminal shifts. Payout authorization never
queries or depends on `dbo.Shifts`.

The server resolves the payout actor from configuration:

Payout requires a configured, active payout user resolved by the server.

```json
"VirtualTicketPayout": {
  "Currency": "UGX",
  "PayoutUserId": ""
}
```

`PayoutUserId` must be a GUID belonging to an activated `AspNetUsers` row with
an `Accounts` row for the terminal branch. Missing, malformed, nonexistent,
inactive, or wrong-branch users produce `PayoutUserNotConfigured`.

The configured, active payout user is recorded on the receipt and in
`VirtualTicketPayouts.PaidByUserId`, together with the authenticated terminal,
branch, amount, timestamp, payout reference, and optional confirmation
reference.

## Duplicate protection

Payout uses a serializable SQL transaction and locks the receipt with
`UPDLOCK, HOLDLOCK`. A unique index on `VirtualTicketPayouts.ReceiptId` prevents
more than one payout ledger row for a receipt, while a second unique index
protects payout references. Branch balance changes, ledger insertion, and the
receipt update are rolled back together if any operation fails.
