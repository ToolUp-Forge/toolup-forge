module ToolUp.Platform.Tests.Contracts.FailClosedContract

open Expecto
open ToolUp.Remoting.Server

// ─── Phase 196 — adversarial fail-closed contract for the auth
// classifier ───────────────────────────────────────────────────────────
//
// The Phase 69d happy-path pack (AuthorizationTests) proves a CORRECTLY
// annotated method ALLOWS the right caller. This contract proves the
// inverse half — the property that actually makes the substrate safe: an
// un-annotated / mis-annotated / under-credentialled call FAILS CLOSED.
//
// Every assertion drives `AuthClassifier.evaluate` (the exact function the
// dispatcher calls per request on every endpoint lookup) and demands a
// `Deny`. The contract is parameterised over a classification map so it
// runs identically against the server-tier attribute family and the
// tier-shared `ToolUp.Platform.*` mirror family (Fable-compiled records
// carry the mirror) — fail-closed must not depend on which family did the
// annotating.
//
// The two ready-made classification maps below are exported so the
// registered suite (InProcess/AdversarialFailClosedTests.fs) instantiates
// the contract for both families. Kept here, next to the contract, so the
// fixture shape and the contract's method-key assumptions can never
// silently drift apart.

// ── god-mode + adversarial auth-context doubles ─────────────────────────

/// A "god-mode" caller: claims every role, every claim, a resolved
/// tenant, and non-anonymous. The whole point of the adversarial pack is
/// that even THIS caller is denied an `Unclassified` / map-missed method —
/// the classification, not the credential, is what fails closed.
let godMode: IAuthContext =
    { new IAuthContext with
        member _.HasRole _ = true
        member _.HasClaim(_, _) = true
        member _.HasTenant() = true
        member _.IsAnonymous() = false
        member _.SubjectId = "god-mode"
    }

let private ctx
    (roles: string list)
    (claims: (string * string option) list)
    (hasTenant: bool)
    (anonymous: bool)
    : IAuthContext =
    { new IAuthContext with
        member _.HasRole role = List.contains role roles
        member _.HasClaim(claim, value) = List.contains (claim, value) claims
        member _.HasTenant() = hasTenant
        member _.IsAnonymous() = anonymous
        member _.SubjectId = "adversary"
    }

let private mustDeny (label: string) (decision: AuthDecision) =
    match decision with
    | Deny _ -> ()
    | Allow -> failtestf "%s: FAIL-OPEN — expected Deny, got Allow" label

// ── the contract ────────────────────────────────────────────────────────

/// The reusable fail-closed contract. `cls` is a classification map that
/// MUST carry exactly these method keys (the standard adversarial fixture
/// shape): `AdminOnly` (RequiresRole "Admin"), `NeedsScope` (RequiresClaim
/// "scope"), `TenantOnly` (TenantScoped), `AdminAndTenant` (role AND
/// tenant). Returns one Expecto `test` per fail-closed invariant.
let internal classifierFailClosedContract (family: string) (cls: Map<string, MethodClassification>) : Test list =
    let clsOf m =
        cls
        |> Map.tryFind m
        |> Option.defaultWith (fun () -> failtestf "%s: adversarial fixture is missing method %s" family m)

    let requiresAuthMethods = [ "AdminOnly"; "NeedsScope"; "TenantOnly"; "AdminAndTenant" ]

    [
        // 1 — Unclassified denies even god-mode. The startup classifier
        //     should have refused, but if a miss ever reaches runtime it
        //     must fail closed (belt + braces), never fall open to Public.
        test (sprintf "%s: Unclassified denies even a god-mode caller (belt+braces fail-closed)" family) {
            mustDeny "Unclassified/god-mode" (AuthClassifier.evaluate Unclassified (Some godMode))
            mustDeny "Unclassified/no-context" (AuthClassifier.evaluate Unclassified None)
        }

        // 2 — A classification-map MISS defaults to Unclassified → denies
        //     (Phase 132 deny-on-miss). A typo'd / aliased runtime key must
        //     not fall open to Public for any caller.
        test (sprintf "%s: a classification-map miss denies (deny-on-miss, not fall-open-to-Public)" family) {
            let missed =
                cls |> Map.tryFind "MethodThatDoesNotExist" |> Option.defaultValue Unclassified

            Expect.equal missed Unclassified "a miss must default to Unclassified, not Public"
            mustDeny "miss/god-mode" (AuthClassifier.evaluate missed (Some godMode))
        }

        // 3 — RequiresRole: wrong role, absent role, and no resolved context
        //     all deny.
        test (sprintf "%s: RequiresRole denies wrong role / absent role / no context" family) {
            let c = clsOf "AdminOnly"
            mustDeny "wrong-role" (AuthClassifier.evaluate c (Some(ctx [ "Member" ] [] false false)))
            mustDeny "absent-role" (AuthClassifier.evaluate c (Some(ctx [] [] false false)))
            mustDeny "no-context" (AuthClassifier.evaluate c None)
        }

        // 4 — RequiresClaim: claim absent denies.
        test (sprintf "%s: RequiresClaim denies when the claim is absent" family) {
            let c = clsOf "NeedsScope"
            mustDeny "claim-absent" (AuthClassifier.evaluate c (Some(ctx [] [] false false)))
            mustDeny "no-context" (AuthClassifier.evaluate c None)
        }

        // 5 — TenantScoped invoked with no resolved tenant denies (the
        //     mis-annotation case: a tenant-bound method reached without a
        //     tenant context).
        test (sprintf "%s: TenantScoped denies when no tenant is resolved" family) {
            let c = clsOf "TenantOnly"
            mustDeny "no-tenant" (AuthClassifier.evaluate c (Some(ctx [] [] false false)))
            mustDeny "no-context" (AuthClassifier.evaluate c None)
        }

        // 6 — Anonymous caller denied on ANY RequiresAuth method before the
        //     per-requirement predicates run — even when the double lies and
        //     claims the role / claim / tenant.
        test (sprintf "%s: anonymous caller is denied on every RequiresAuth method before predicates run" family) {
            let lyingAnon = ctx [ "Admin" ] [ "scope", None ] true true

            for m in requiresAuthMethods do
                mustDeny (sprintf "anon/%s" m) (AuthClassifier.evaluate (clsOf m) (Some lyingAnon))
        }

        // 7 — AND semantics: a method requiring role AND tenant denies on
        //     partial satisfaction (role-only, tenant-only).
        test (sprintf "%s: multi-requirement methods deny on partial satisfaction (AND, not OR)" family) {
            let c = clsOf "AdminAndTenant"
            mustDeny "role-only" (AuthClassifier.evaluate c (Some(ctx [ "Admin" ] [] false false)))
            mustDeny "tenant-only" (AuthClassifier.evaluate c (Some(ctx [] [] true false)))
        }

        // 8 — A classified RequiresAuth method with NO resolver at all fails
        //     closed (no silent allow when the auth substrate is half-wired).
        test (sprintf "%s: RequiresAuth with no resolved context denies (no silent allow)" family) {
            for m in requiresAuthMethods do
                mustDeny (sprintf "no-resolver/%s" m) (AuthClassifier.evaluate (clsOf m) None)
        }
    ]

// ── shared adversarial fixtures + their classification maps ─────────────

/// Server-tier attribute family (this assembly's own attribute set).
type private ServerAdversarialApi = {
    [<RequiresRole "Admin">]
    AdminOnly: unit -> Async<int>
    [<RequiresClaim "scope">]
    NeedsScope: unit -> Async<int>
    [<TenantScoped>]
    TenantOnly: unit -> Async<int>
    [<RequiresRole "Admin">]
    [<TenantScoped>]
    AdminAndTenant: unit -> Async<int>
}

/// Tier-shared `ToolUp.Platform.*` mirror family — the attributes a
/// Fable-compiled API record carries (it cannot reference the server-tier
/// assembly). Fail-closed behaviour must be identical to the server family.
type private MirrorAdversarialApi = {
    [<ToolUp.Platform.RequiresRole "Admin">]
    AdminOnly: unit -> Async<int>
    [<ToolUp.Platform.RequiresClaim "scope">]
    NeedsScope: unit -> Async<int>
    [<ToolUp.Platform.TenantScoped>]
    TenantOnly: unit -> Async<int>
    [<ToolUp.Platform.RequiresRole "Admin">]
    [<ToolUp.Platform.TenantScoped>]
    AdminAndTenant: unit -> Async<int>
}

/// Classification map for the server-tier adversarial fixture.
let internal serverFamilyClassification: Map<string, MethodClassification> =
    AuthClassifier.classify typeof<ServerAdversarialApi>

/// Classification map for the mirror-family adversarial fixture.
let internal mirrorFamilyClassification: Map<string, MethodClassification> =
    AuthClassifier.classify typeof<MirrorAdversarialApi>