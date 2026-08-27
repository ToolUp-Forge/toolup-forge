# Migration — Phase 198: draft preview-link surface

**Status:** additive. One new opt-in surface on `ToolUp.PublicRendering`, plus one bug fix on the existing `/preview` route. No consumer action is required to upgrade; a deployment that does not register an `IShareTokenStore` is unaffected.

## Why

Phase 89 shipped the *validating* half of draft preview: an `/preview?token=…` route that accepts an `IShareTokenStore`-signed, scope-bound, TTL'd token (`ResourceKind = "PublicPage"`) and renders the referenced page past the publish-visibility filter, so an editor can see a `Draft`.

What it did not ship was an authorised way to **create** such a link. The only minting path was `ContentPreview.issuePreviewToken`, an unguarded primitive that takes a scope id and an issuer id as plain strings — so an admin surface wanting a "Copy preview link" button had to bring its own role check, derive its own scope, and construct its own URL. That is exactly the plumbing an SDK exists to remove, and every consumer that re-derived it was a chance to get the scope wrong.

Phase 198 adds the minting half: a role-gated affordance returning typed values, over the same token substrate and the same validation path.

## What is new

### 1. Typed request / response records (`ToolUp.PublicRendering`, `Shared/PublicContentTypes.fs`)

```fsharp skip=fragment
type MintPreviewLinkRequest = {
    Slug: string
    Ttl: System.TimeSpan
    AttributedHandle: string option
}

type PreviewLink = {
    Url: string          // absolute — {baseUrl}/preview?token=…
    Path: string         // site-relative — /preview?token=…
    Token: string
    TokenId: string      // for IShareTokenStore.Revoke
    Slug: string
    IssuedBy: string
    ExpiresAt: System.DateTimeOffset
}

[<RequireQualifiedAccess>]
type PreviewLinkDecline =
    | Unauthorised
    | PreviewsNotEnabled
    | InvalidRequest of reason: string
    | MintFailed of error: ToolUp.Platform.ShareTokenError
```

The request carries **no scope id, no issuer, and no base URL**. All three are derived server-side — the scope and the issuer from the caller's resolved `AccessContext`, the base URL from the deployment — so a caller cannot widen scope or retarget the link. Helpers: `MintPreviewLinkRequest.forSlug`, `withTtl`, `withAttribution`, and the bounds `DefaultTtl` (24h) / `MaxTtl` (30d).

Declines are a typed `Result` error, not an exception and not a raw 500: "previews are not enabled here" and "you may not mint" are ordinary answers on this surface.

### 2. `ContentPreview.mintPreviewLink` (`Server/ContentPreview.fs`)

```fsharp skip=fragment
open ToolUp.PublicRendering

// In an admin handler, with the caller's resolved AccessContext:
let mint (ctx: Microsoft.AspNetCore.Http.HttpContext) access slug = async {
    let request = MintPreviewLinkRequest.forSlug slug
    match! ContentPreview.mintPreviewLinkForRequest ctx access request with
    | Ok link -> return Ok link.Url
    | Error decline -> return Error decline
}
```

Three entry points, narrowest first:

| Function | Use |
|---|---|
| `mintPreviewLinkForRequest ctx access request` | resolves the store from request-scoped DI and the base URL from the request's own scheme / host / path base |
| `mintPreviewLink store baseUrl access request` | both deployment inputs supplied explicitly |
| `mintPreviewLinkWithGuard guardName store baseUrl access request` | as above, at a non-default guard name |

`canMintPreviewLink` / `canMintPreviewLinkWithGuard` expose the same gate as a pure predicate, for rendering the button at all.

### 3. The gate

Minting is gated on `ContentPreview.mintGuard`, whose value is `"content:can-approve"` — deliberately the **same** guard the editorial lifecycle registers its approval predicate under. Handing out a link that bypasses the publish-visibility filter is the same editorial authority as approving the publish, so it is one gate rather than two, and a deployment that has already configured approval needs no new configuration. The test pack pins the two spellings together.

Default-deny in the two directions that matter:

- an **anonymous** caller never mints;
- a **share-token bearer** never mints, even though it is a resolved, non-anonymous subject — otherwise a leaked preview link could mint further links off its own authority and re-attribute them.

Beyond that it is the SDK's ordinary module-RBAC axis: a platform admin passes, and a deployment that has configured no permissions at all is unrestricted for authenticated callers exactly as everywhere else, so adopting this surface does not lock an existing deployment out of its own authoring tools.

### 4. Validation parity

There is one preview-token format. `issuePreviewToken` and the new mint surface both route through a single private `IShareTokenStore.Issue` call, and the URL shape is spelled once in `ContentPreview.previewPath`. A minted token is validated by the *existing* Phase 89 `/preview` route, unmodified — an expired or wrong-scope token is refused there exactly as any other is.

## Bug fix — `/preview` with no `IShareTokenStore` registered

`previewHandler` resolved the store with `GetService(typeof<IShareTokenStore>) :?> IShareTokenStore`. F# interface types are non-nullable, so casting the **absent** service raised a `NullReferenceException` before the `isNull` guard on the next line could see it: the route's documented "no store registered → decline → 404" path in fact threw, after the handler had been entered. The lookup now pattern-matches the service (the idiom the Forms compose root already uses), so the route declines as documented.

**Behaviour change**, and the only one in this phase: a deployment with no `IShareTokenStore` that received a request for `/preview` used to get a 500; it now gets the intended 404. Every deployment that *does* register a store is byte-for-byte unchanged.

## Consumer action

None required. To adopt:

1. Register an `IShareTokenStore` (Phase 21b) if you have not already — without one, minting declines with `PreviewsNotEnabled` and nothing else changes.
2. Register the approval guard predicate you already use for editorial approval; the mint reads the same RBAC axis.
3. Call `ContentPreview.mintPreviewLinkForRequest` from your admin handler and surface `link.Url` as a copy-able value. Render the control conditionally on `ContentPreview.canMintPreviewLink access`.
4. Offer revocation by passing `link.TokenId` to `IShareTokenStore.Revoke`.

## Verification

- `dotnet build ToolUp.Forge.sln` clean.
- `dotnet run --project Build.fsproj -- VerifyAll` — 0 failures.
- New coverage in `src/ToolUp.Platform.Tests/InProcess/ContentAuthoringTests.fs`: guard-name parity; mint → validate → the shipped `/preview` route renders the Draft; an expired link and a wrong-resource-kind token do not; no store registered → `PreviewsNotEnabled` and the route unchanged; non-approver / anonymous / claim-bearer denied with nothing issued; the TTL and slug bounds as typed `InvalidRequest`s; attribution and `TokenId` round-tripping onto the claim.

## Rollback

Every addition is new surface. Remove the `MintPreviewLinkRequest` / `PreviewLink` / `PreviewLinkDecline` types and the `mint*` / `canMint*` / `previewPath` functions; existing call sites are unaffected. To revert the `/preview` fix, restore the `:?>` cast — which restores the 500.
