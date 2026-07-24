# Virtual-ticket payout

Virtual-ticket payout is available through:

- `POST /api/tickets/payout/lookup`
- `POST /api/tickets/payout`
- `POST /api/tickets/cancel`

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

## Cancellation

Cancellation uses the same authenticated terminal and configured server-side
user as payout. A ticket can be cancelled only while its receipt is pending,
every selection remains unsettled, and the first event has not started.

Successful cancellation changes `ReceiptStatus` to `Cancelled (-1)`, sets
`IsCanceled`, and writes a `VirtualTicketCancellations` audit record. The audit
table uniquely constrains both `ReceiptId` and `CancelReference`, so concurrent
requests cannot cancel the same receipt twice. Cancellation does not change the
branch balance because virtual-ticket placement in this API does not debit that
balance.
