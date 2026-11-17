# 1306 — a public form link is used as many times as it says

## What changes

- **`IPublicFormApi.SubmitWithToken` reserves the use before it stores the submission.** It used to store, then
  count, so N concurrent submissions on a single-use link were all stored. It now validates the values, calls
  `IShareTokenStore.MarkUsed`, and stores only on `Ok`: fifty concurrent submissions on a `UseLimit = 1` link store
  exactly one, and the others answer `NotFound("token", "use-limit-exceeded")`.
- **A submission that fails to store returns the use.** The handler calls the new
  `IShareTokenStore.ReleaseUse`, so the respondent can submit again on the same link. If the release itself fails,
  the use stays spent and the link fails closed; the handler logs the failure at Error.
- **Issued links carry a default rate limit: 30 uses per rolling minute.** `IssueTokens` and
  `DispatchInvitationsByEmail` used to issue with no per-window limit. The default is
  `FormsOptions.defaultPublicLinkRateLimit`, held on the new `FormsOptions.PublicLinkRateLimit` field; a deployment
  replaces it with `FormsOptions.withPublicLinkRateLimit` (below). Links issued before the upgrade keep the rate they
  were issued with.
- Team invite links are unchanged: an invite consumes its use before it adds the member, and a use spent on a
  failed add stays spent.

## Breaking: `IShareTokenStore` gains `ReleaseUse`

```fsharp skip=fragment
abstract ReleaseUse: scopeId: string * tokenId: string -> Async<Result<unit, ShareTokenError>>
```

Callers are unaffected. A type that **implements** `IShareTokenStore` (a custom store, a decorator, a test fake)
must add the member:

```diff
 interface IShareTokenStore with
     member _.MarkUsed(scopeId, tokenId) = inner.MarkUsed(scopeId, tokenId)
+    member _.ReleaseUse(scopeId, tokenId) = inner.ReleaseUse(scopeId, tokenId)
```

A store that holds the count itself decrements `UsedCount`, never below zero, as a **conditional** write (conditional
on the claim not having changed since it was read, as `MarkUsed`'s increment is), and returns `Error` when it cannot
make the write conditional, never a check-then-write. `NotFound` for an unknown token; revocation and expiry are left
as they are. The `IShareTokenStoreContract` pack holds a store to this.

Which shipped stores implement it conditionally:

| Store | `ReleaseUse` |
|---|---|
| `BlobShareTokenStore` | conditional on the claim's ETag on every `IConditionalBlobStorage` backend (every shipped backend); serialised in-process on a backend without conditional writes, which is then single-replica, as for `MarkUsed` |
| `StoreIdSanitising` share-token decorator | forwards after validating the ids |
| `RevokeOnIssuerRemoved` decorator | forwards |

## Breaking: `FormsOptions` gains `PublicLinkRateLimit`, and `formApi` takes it

A deployment that builds `FormsOptions` with `FormsOptions.create ()` and the `with*` builders is unaffected. A full
record literal of `FormsOptions` adds the field (`PublicLinkRateLimit = Some FormsOptions.defaultPublicLinkRateLimit`
keeps the default). A direct caller of `FormApiHandler.formApi` passes the rate limit after the analyser list.

## Changing the default rate limit

```fsharp skip=fragment
app
|> FormsCompose.withForms (
    FormsOptions.create ()
    |> FormsOptions.withPublicLinkRateLimit (Some { MaxUses = 5; Window = TimeSpan.FromMinutes 1.0 })
)
```

`FormsOptions.withPublicLinkRateLimit None` issues links with no per-window limit. The limit is enforced by the
composed `IShareTokenRateLimiter` (`FormsOptions.withShareTokenRateLimiter`); the in-memory default is single-replica.

## Verification

- `dotnet run --project src/ToolUp.Forms.Tests/ToolUp.Forms.Tests.fsproj -- --filter "ToolUp.Forms.Tests.Public form links — the use is reserved before the submission is stored"`
  runs 7 cases: the fifty-concurrent case is red on the previous handler.
- The share-token contract pack (`IShareTokenStoreContract`) carries the `ReleaseUse` cases.

## Rollback

Revert the handler and the interface member together; a custom store that added `ReleaseUse` keeps compiling only
once the member is removed from it too.
