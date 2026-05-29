# ToolUp.ShareTokenStoreDecorators.RevokeOnIssuerRemoved

An `IShareTokenStore` decorator that revokes a departed team member's
outstanding share-tokens. When a member is removed from a team, any
share-link they minted while a member stops granting access.

## How it works

The decorator wraps the resolved `IShareTokenStore` and subscribes to
`MembershipChanged` notifications on the reserved `_platform` topic. On a
`MembershipChanged.Removed` event it:

1. Enumerates the leaver's outstanding claims via
   `IShareTokenStore.ListByIssuer(teamId, affectedUserId)`.
2. Revokes each still-active claim with
   `actor = "system:RevokeOnIssuerRemoved"`.

All other `IShareTokenStore` methods delegate to the inner store
unchanged.

## Wiring

```fsharp
open ToolUp.ShareTokenStoreDecorators

app
|> ServerApp.withShareTokenStoreDecorator
    (RevokeOnIssuerRemoved.decorator notifications (Some logger))
```

The deployment must wire an `IShareTokenStore` (a `ClaimBearer` surface
auto-promotes the default `BlobShareTokenStore`) and a `Team` surface;
`SurfaceCoherenceValidator` warns at startup if the decorator is wired
without either.

## Idempotency

Revocation is idempotent. `IShareTokenStore.Revoke` is itself idempotent
(re-revoking returns `Ok`), and the handler skips already-revoked claims,
so a duplicate or out-of-order `Removed` delivery cannot over-revoke or
resurrect a token — matching the at-least-once, no-cross-publisher-
ordering `INotificationChannel` contract.

## Audit

The decorator carries no `IAuditLog` dependency. The `actor` string it
passes to `Revoke` flows into the `ShareTokenRevoked` audit event emitted
by the underlying store (the default `BlobShareTokenStore`), so the
revocation trail attributes to `system:RevokeOnIssuerRemoved`.
