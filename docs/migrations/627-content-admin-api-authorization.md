# Phase 627 — Authenticating `IContentAdminApi` and arming its classifier (consumer migration)

**What changes.** Two things, and the second is the one that actually mattered.

1. Every method on `ToolUp.ContentAuthoring.IContentAdminApi` moved from `[<AllowAnonymous>]` to `[<RequiresRole "PlatformAdmin">]`.
2. `ContentAdminCompose.withContentAdmin` now mounts through `Api.make` instead of raw `Remoting.buildHttpHandler`, which **arms the Phase 69d classifier for this record for the first time**.

Before this phase, the six `[<AllowAnonymous>]` attributes were **inert metadata**. The bare mount composed no auth-context resolver, and the classifier only runs when one is present — so the dispatcher never read the attributes at all. Changing them without changing the mount would have been decoration; changing the mount is what makes any classification on this contract mean anything.

**BREAKING, deliberately — and more broadly than it looks.** A deployment reaching `/api/content-admin/*` without a platform-admin caller now receives `ErrorCategory.Auth` denials. In practice that is *every* deployment, because nothing was enforcing anything before: whatever gate you believed was in front of this surface, it was not this one.

**Why this was urgent.** Three facts compounded:

- All six methods were blanket-anonymous, and the record's own comment conceded the classification had outlived itself.
- The classifier was never armed, so the attributes were not enforced.
- `ContentAdminApiImpl.create` binds a **fixed** `PublicPageEntity.PublicScope` (`"_public"`), so the `StorageScope` isolation that defends every other blanket-anonymous record in the tree had nothing to isolate.

Composed: an unauthenticated, cross-scope write to the publicly-served page overlay — including `SetStatus`, which carries `[<Audit "PolicyChanged">]`. And because the bare mount composed no `IAuditEmitter` either, that annotation emitted nothing: an audited policy change anyone could invoke, whose audit never fired. `Api.make` composes the classifier **and** the audit emitter, so both halves close in one move.

**Scope.** Server-side enforcement only. No wire change — denials use the existing categorised `ErrorCategory.Auth` envelope, and the mounted routes are byte-identical (`/api/content-admin/<Method>`, from the contract's own `ContentAdminApi.routeBuilder`). No data migration. `ToolUp.PublicRendering`'s anonymous **published-page** read path is untouched.

**Version.** Minor bump under the SemVer-on-`0.x` policy (breaking changes are permitted in a minor).

## The classification, method by method

Read-vs-write matters here, but every method landed on the same gate — the reasoning differs, the answer does not.

| Method | Before | After | Why |
|---|---|---|---|
| `ListPages` | `[<AllowAnonymous>]` | `[<RequiresRole "PlatformAdmin">]` | A read of the whole authoring surface, including `draft` and `scheduled` pages the public renderer deliberately does not serve. |
| `GetPage` | `[<AllowAnonymous>]` | `[<RequiresRole "PlatformAdmin">]` | As `ListPages`, and this one returns the body rather than the summary row. |
| `SavePage` | `[<AllowAnonymous>]` | `[<RequiresRole "PlatformAdmin">]` | An unauthenticated write to the publicly-served overlay — content injection into the deployment's own public site. The headline defect. |
| `SetStatus` | `[<AllowAnonymous>]` | `[<RequiresRole "PlatformAdmin">]` | The publish/unpublish/schedule lever, and the audited one. |
| `ListRevisions` | `[<AllowAnonymous>]` | `[<RequiresRole "PlatformAdmin">]` | History carries every prior body, including drafts never published. |
| `RestoreRevision` | `[<AllowAnonymous>]` | `[<RequiresRole "PlatformAdmin">]` | A write, and one that can silently republish withdrawn content. |

**Why `"PlatformAdmin"` here, when [Phase 619] rejected every role gate for `IReportApi`.** 619's rejection was about *scope ownership* — a report template is scope-owned, so gating on the platform role would break per-team management. This surface is the opposite: it writes the deployment-wide `_public` overlay, which is platform-owned by construction. Platform-owned surface, platform-level gate.

**And it is a live gate, not a Phase 132 dead one.** `"PlatformAdmin"` is the *only* role string the default `ForgeAuthContext` resolver can emit — it bridges to the server-resolved `ToolUp.PlatformRole` (from `IPlatformAdminStore`), not to the `AuthenticatedUser.Roles` list the first-party providers leave empty. `[<RequiresRole "Owner">]` / `"Admin"` would have denied every caller including real admins. 619 warned this phase about exactly that trap; the discriminator is that the role this surface needs happens to be the one that is emittable.

**Why not `[<RequiresClaim "scope">]`** (619's answer). Against the default resolver that resolves to exactly `not isAnonymous` — any authenticated subject, *including a share-token `ClaimBearer`*. For a scope-owned surface that suffices, because `StorageScope` then isolates the caller to their own scope (GP 4). Here the binding is fixed, so there is no per-caller scope to isolate to and the attribute gate carries the whole weight. "Any signed-in caller may rewrite the public site" is not the policy.

## The fixed `PublicScope` binding — decided, and kept

Phase 627 filed this as an open question because it is what removes the isolation defence. The decision is to **keep it**, because a per-caller scope would be *incorrect*, not merely stricter: the public renderer, sitemap, narrative feed, and scheduled-publish sweep all read `PublicPageEntity.PublicScope` unconditionally. An admin whose writes landed in `team-a` would be editing an overlay nothing serves — the page would simply never appear, with no error to explain it.

So the isolation GP 4 provides elsewhere had to be *replaced*, not restored, and the platform-level attribute gate is that replacement. A deployment wanting per-team content authoring wants a different surface — team-scoped entities projected into the public overlay at publish time — not a rescoped `IContentAdminApi`.

## What a deployment must do

**1. Make sure a platform admin exists and is resolvable.** `HasRole "PlatformAdmin"` reads `HttpContext.Items["ToolUp.PlatformRole"]`, populated by the middleware from `IPlatformAdminStore`. If your deployment never registered a platform-admin store, **no caller clears this gate** and the content-admin UI stops working entirely. That is a fail-closed outcome, not a silent one — but confirm it before upgrading.

**2. Expect a startup refusal if you extended the contract.** Now that the classifier is armed, any method on this record without one of `[<RequiresRole>]` / `[<RequiresClaim>]` / `[<TenantScoped>]` / `[<AllowAnonymous>]` / `[<PublicEndpoint>]` **refuses startup**, naming the record and field. If you forked or extended `IContentAdminApi`, annotate every method.

**3. Point the authoring UI at an authenticated surface.** If the "Content" client module was reachable from an anonymous-admitting surface, tighten the admit set to match what the API now requires:

```fsharp
DefaultSurfaceRequirement = SurfaceRequirement.userOrTeam   // or stricter
```

**4. If you pass a custom `?authContext`,** it must emit `"PlatformAdmin"` truthfully, or every content-admin call denies. The default resolver's bridge is the reference:

```fsharp
member _.HasRole role =
    if role = "PlatformAdmin" then
        // your deployment's platform-admin source of truth
        isPlatformAdmin ()
    else
        user.Roles |> List.contains role
```

**5. Nothing to do for the public site.** Anonymous visitors read published pages through the `ToolUp.PublicRendering` overlay renderer, which serves only `published` pages and is unchanged. This phase closed the *authoring* door, not the reading one.

## Also in this phase — `anonymousReachable` now means what its name says

`AuthorizationSurface` gained a fourth `AccessClassification` case, `GatedInHandler`, plus `AuthorizationSurface.resolveWithInHandlerGates` and the queries `gatedInHandler` / `anonymousAtAttributeLayer`.

The artefact a security review opens with was **overstating** exposure. Records that are blanket-anonymous at the attribute layer but carry a real in-handler gate — `IFormApi` (16 methods), `JobApi` (8), `ModelExecutionApi` (7), `IModuleQueryBusApi` (1) — all landed in `anonymousReachable`, so a genuine open door did not stand out from thirty-odd entries that were fine. That is plausibly *why* this phase's defect hid for as long as it did.

A component can now declare that a named attribute-anonymous endpoint is gated inside its handler:

```fsharp
let declarations = [
    { GatedComponent = ComponentId.create "toolup.forms"
      GatedEndpoint  = "IFormApi.SubmitPublic"
      GatedRationale = "handler validates the share token before dispatch" }
]

surface |> AuthorizationSurface.resolveWithInHandlerGates declarations
```

Four properties keep the mechanism honest, and each is pinned by a test:

- **Only `AnonymousReachable` entries are refined** — the `resolveWithPolicy` rule. A declaration can never downgrade a real attribute gate.
- **A blank rationale is ignored.** A gate nobody can name is indistinguishable from a gate nobody wrote.
- **`GatedInHandler` is weaker than `InheritedDefaultDeny`**, because the dispatcher genuinely lets the caller through and nothing in the manifest can verify handler code. So losing a declared gate diffs as a **critical weakening**, and the case cannot be used to launder an open door into a quiet one.
- **A stale declaration is inert, not an error** — declarations live beside handlers, the surface is derived from records, and a rename should not fail a composition.

**Consumer impact:** additive, except for an exhaustive `match` over `AccessClassification`, which gains a case. `AccessClassification.ofLabel` still reads an unknown label as `InheritedDefaultDeny`, so a persisted baseline written before this phase round-trips unchanged.

## Verification

`src/ToolUp.Platform.Tests/InProcess/ContentAdminAuthorizationTests.fs` — 31 cases. The two that matter most are falsifiers rather than assertions:

- **The arming is proved against its own control.** `Api.make` over a deliberately unclassified contract refuses; the *pre-627 bare `buildHttpHandler` shape over the very same contract* does not. Without the second half, "it refuses" could be a property of the fixture rather than of the mount.
- **The deny is proved against the real resolver**, driven through `ApiSeams.defaultForgeAuthContextResolver` against a bare `HttpContext` — not a hand-rolled double — and falsified by a live fixture still annotated the pre-627 way, which must still *allow*.

Both directions of the gate are pinned: a genuine `PlatformAdmin` passes (so it is not a dead gate), and an ordinary authenticated caller is denied (this phase's deliberate break, pinned so it cannot be softened by accident).

## See also

- [Phase 619 migration](619-report-api-authorization.md) — the precedent, and where 627's evidence came from.
- [Phase 69d migration](69d-authorization-metadata.md) — the classifier and the attribute family.
- `docs/platform/cms-authoring.md` — the composition guide for this companion.
