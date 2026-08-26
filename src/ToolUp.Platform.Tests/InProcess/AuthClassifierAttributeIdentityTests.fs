namespace Contoso.Consumer.Auth

open System

// ─── Phase 335 fixtures — a THIRD-PARTY attribute family ─────────────
//
// Deliberately named identically to the sanctioned markers and applied
// to record fields, which is exactly what the pre-335 simple-name
// recognition honoured. Nothing here is a ToolUp type: a different
// namespace, and (being defined in the test assembly) a different
// assembly too. Both halves of that are load-bearing — identity
// matching pins the assembly as well as the namespace, so a consumer
// type declared straight into `namespace ToolUp.Remoting.Server` from
// its own assembly is foreign for the same reason these are.

/// Collides with the most dangerous marker: pre-335 this opened a
/// method to every caller with no startup signal.
[<AttributeUsage(AttributeTargets.Property ||| AttributeTargets.Field)>]
type PublicEndpointAttribute() =
    inherit Attribute()

/// The other "open" marker — `AllowAnonymous` is a very common name.
[<AttributeUsage(AttributeTargets.Property ||| AttributeTargets.Field)>]
type AllowAnonymousAttribute() =
    inherit Attribute()

/// A colliding CLOSED marker. Honouring this one would not open a
/// method, but it would let a foreign type decide a gate — and a
/// consumer's `Role` property need not even mean what the classifier
/// reads it as.
[<AttributeUsage(AttributeTargets.Property ||| AttributeTargets.Field, AllowMultiple = true)>]
type RequiresRoleAttribute(role: string) =
    inherit Attribute()
    member _.Role = role

namespace ToolUp.Platform.Tests.InProcess

open System
open Expecto
open Microsoft.AspNetCore.Http
open ToolUp.Remoting.Server

/// ─── Phase 335 — namespace-qualified (CLR-identity) auth-attribute
/// matching in the dispatch classifier ───────────────────────────────
///
/// Phase 69d.tail recognised the five auth markers by bare simple name,
/// to bridge the two sanctioned families (`ToolUp.Remoting.Server.*` and
/// the tier-shared `ToolUp.Platform.*` mirror). That made the
/// classification input FORGEABLE: any field-applicable attribute whose
/// type name collided with a marker was honoured as a security
/// classification. The dangerous direction is the "open" markers — a
/// consumer's own `PublicEndpointAttribute` classified the method
/// `Public`, `evaluate` returned `Allow` for everyone, and the
/// default-deny startup gate was SATISFIED (the field *was* classified),
/// so nothing anywhere said so.
///
/// What this pack pins:
///   * a foreign attribute of a colliding name does not classify a
///     method — in either direction, open or closed;
///   * the collision is REFUSED at startup, naming the field and the
///     offending attribute, rather than silently ignored;
///   * the case the 69d gate structurally cannot see — a foreign open
///     marker beside a genuine requirement, where the record would start
///     fine and the consumer believes the method open;
///   * GP 11: the two sanctioned families classify exactly as before,
///     and an unannotated method still refuses startup.
module AuthClassifierAttributeIdentityTests =

    // ── Fixtures ────────────────────────────────────────────────────

    /// The pre-335 hole, in its purest form: a foreign attribute named
    /// `PublicEndpointAttribute` on an otherwise unannotated method.
    type private ForeignPublicApi = {
        [<Contoso.Consumer.Auth.PublicEndpoint>]
        LooksOpen: unit -> Async<int>
    }

    /// Same shape via the other open marker.
    type private ForeignAnonymousApi = {
        [<Contoso.Consumer.Auth.AllowAnonymous>]
        LooksAnonymous: unit -> Async<int>
    }

    /// A colliding closed marker — must not produce an `AuthRequirement`
    /// either. Recognition is an allow-list, not a "reject the open ones"
    /// special case.
    type private ForeignRoleApi = {
        [<Contoso.Consumer.Auth.RequiresRole "Admin">]
        LooksGated: unit -> Async<int>
    }

    /// The case the unclassified gate cannot see: a foreign open marker
    /// sitting BESIDE a genuine requirement. Pre-335 `hasPublic` won and
    /// the method dispatched for everyone; post-335 the field classifies
    /// `RequiresAuth`, so the record would start clean and silently
    /// stricter than the consumer believes — hence the explicit refusal.
    type private ForeignBesideGenuineApi = {
        [<Contoso.Consumer.Auth.PublicEndpoint>]
        [<RequiresRole "PlatformAdmin">]
        MixedMarkers: unit -> Async<int>
    }

    /// GP 11 control — the server-tier sanctioned family.
    type private SanctionedServerApi = {
        [<RequiresRole "PlatformAdmin">]
        AdminOnly: unit -> Async<int>
        [<RequiresClaim "scope">]
        NeedsScope: unit -> Async<int>
        [<TenantScoped>]
        TenantOnly: unit -> Async<int>
        [<AllowAnonymous>]
        OpenToAll: unit -> Async<int>
        [<PublicEndpoint>]
        PublicSurface: unit -> Async<int>
    }

    /// GP 11 control — the tier-shared mirror family.
    type private SanctionedMirrorApi = {
        [<ToolUp.Platform.RequiresRole "PlatformAdmin">]
        AdminOnly: unit -> Async<int>
        [<ToolUp.Platform.RequiresClaim "scope">]
        NeedsScope: unit -> Async<int>
        [<ToolUp.Platform.TenantScoped>]
        TenantOnly: unit -> Async<int>
        [<ToolUp.Platform.AllowAnonymous>]
        OpenToAll: unit -> Async<int>
        [<ToolUp.Platform.PublicEndpoint>]
        PublicSurface: unit -> Async<int>
    }

    /// 69d control — still unclassified, still refuses.
    type private UnannotatedApi = { Naked: unit -> Async<int> }

    // ── Helpers ─────────────────────────────────────────────────────

    /// Capture the composition-time refusal so the diagnostic text can be
    /// asserted (Expecto's `Expect.throwsT` returns unit).
    let private refusalOf (compose: unit -> unit) (label: string) : InvalidOperationException =
        try
            compose ()
            failtestf "%s: expected the classifier to refuse composition" label
        with :? InvalidOperationException as ex ->
            ex

    let private anonymousContext: IAuthContext =
        { new IAuthContext with
            member _.HasRole _ = false
            member _.HasClaim(_, _) = false
            member _.HasTenant() = false
            member _.IsAnonymous() = true
            member _.SubjectId = "anonymous"
        }

    [<Tests>]
    let tests =
        testList "Phase 335 — auth-attribute matching is by CLR identity" [

            // ── The foreign family classifies nothing ──
            test "a foreign PublicEndpointAttribute does not classify a method Public" {
                let cls = AuthClassifier.classify typeof<ForeignPublicApi>

                Expect.equal
                    cls["LooksOpen"]
                    Unclassified
                    "a consumer attribute named PublicEndpointAttribute must not open a method — \
                     pre-335 this classified Public and evaluate returned Allow for every caller"
            }

            test "the un-honoured foreign open marker cannot Allow an anonymous caller" {
                // The end-to-end consequence, driven through the same
                // `evaluate` the dispatcher calls per request: whatever the
                // foreign marker was meant to say, an anonymous caller is
                // denied. This is the assertion the defect was about.
                let cls = AuthClassifier.classify typeof<ForeignPublicApi>

                match AuthClassifier.evaluate cls["LooksOpen"] (Some anonymousContext) with
                | Deny _ -> ()
                | Allow -> failtest "a foreign 'PublicEndpoint' must not admit an anonymous caller"
            }

            test "a foreign AllowAnonymousAttribute does not classify a method Anonymous" {
                let cls = AuthClassifier.classify typeof<ForeignAnonymousApi>
                Expect.equal cls["LooksAnonymous"] Unclassified "a foreign AllowAnonymous must not open a method"
            }

            test "a foreign RequiresRoleAttribute produces no requirement" {
                // The allow-list is symmetric: a foreign CLOSED marker is
                // not honoured either, so the method reads as unclassified
                // rather than as a gate a foreign type defined.
                let cls = AuthClassifier.classify typeof<ForeignRoleApi>
                Expect.equal cls["LooksGated"] Unclassified "a foreign RequiresRole must not be honoured as a gate"
            }

            // ── The collision is reported, not merely ignored ──
            test "foreignMarkers names the field and the offending attribute" {
                let collisions = AuthClassifier.foreignMarkers typeof<ForeignPublicApi>

                Expect.hasLength collisions 1 "exactly one colliding attribute on the fixture"
                let field, attrType = collisions[0]
                Expect.equal field "LooksOpen" "the finding names the record field"

                Expect.stringContains
                    attrType
                    "Contoso.Consumer.Auth.PublicEndpointAttribute"
                    "the finding names the offending attribute's namespace-qualified type"
            }

            test "foreignMarkers is empty for both sanctioned families" {
                Expect.isEmpty
                    (AuthClassifier.foreignMarkers typeof<SanctionedServerApi>)
                    "the server-tier family is sanctioned — no collision"

                Expect.isEmpty
                    (AuthClassifier.foreignMarkers typeof<SanctionedMirrorApi>)
                    "the tier-shared mirror family is sanctioned — no collision"
            }

            test "foreignMarkers is empty across shipped forge API records" {
                // A false positive here would redden every Api.make in the
                // SDK; pinning a few real records keeps that failure mode
                // attributable to this check rather than to the caller.
                for name, apiType in
                    [
                        "PlatformAdminApi", typeof<ToolUp.Platform.PlatformAdminApi>
                        "TeamApi", typeof<ToolUp.Platform.TeamApi>
                        "IConfigApi", typeof<ToolUp.Platform.IConfigApi>
                    ] do
                    Expect.isEmpty
                        (AuthClassifier.foreignMarkers apiType)
                        (sprintf "%s carries only sanctioned markers" name)
            }

            // ── Startup refusal through the real composition path ──
            test "Api.make refuses startup on a record carrying a foreign marker" {
                let builder (_: HttpContext) : ForeignPublicApi = {
                    LooksOpen = fun () -> async { return 1 }
                }

                let ex =
                    refusalOf (fun () -> ToolUp.Platform.Api.make builder |> ignore) "foreign open marker"

                Expect.stringContains ex.Message "ForeignPublicApi" "the diagnostic names the record"
                Expect.stringContains ex.Message "LooksOpen" "the diagnostic names the field"

                Expect.stringContains
                    ex.Message
                    "Contoso.Consumer.Auth.PublicEndpointAttribute"
                    "the diagnostic names the offending attribute, not merely 'unclassified'"
            }

            test "the refusal fires even when the field also carries a genuine requirement" {
                // Without the explicit collision check this record starts
                // clean: the field classifies `RequiresAuth`, the 69d gate
                // is satisfied, and the consumer's belief that the method
                // is public is never contradicted.
                let builder (_: HttpContext) : ForeignBesideGenuineApi = {
                    MixedMarkers = fun () -> async { return 1 }
                }

                let ex =
                    refusalOf
                        (fun () -> ToolUp.Platform.Api.make builder |> ignore)
                        "foreign marker beside a genuine one"

                Expect.stringContains ex.Message "MixedMarkers" "the diagnostic names the field"

                Expect.stringContains
                    ex.Message
                    "Contoso.Consumer.Auth.PublicEndpointAttribute"
                    "the diagnostic names the colliding attribute"
            }

            // ── GP 11 — sanctioned behaviour is unchanged ──
            test "the sanctioned families classify exactly as before (GP 11)" {
                let server = AuthClassifier.classify typeof<SanctionedServerApi>
                let mirror = AuthClassifier.classify typeof<SanctionedMirrorApi>

                Expect.equal mirror server "both sanctioned families still classify identically"

                match server["AdminOnly"] with
                | RequiresAuth [ RoleRequired "PlatformAdmin" ] -> ()
                | other -> failtestf "AdminOnly: unexpected classification %A" other

                match server["NeedsScope"] with
                | RequiresAuth [ ClaimRequired("scope", None) ] -> ()
                | other -> failtestf "NeedsScope: unexpected classification %A" other

                match server["TenantOnly"] with
                | RequiresAuth [ TenantRequired ] -> ()
                | other -> failtestf "TenantOnly: unexpected classification %A" other

                Expect.equal server["OpenToAll"] Anonymous "AllowAnonymous still classifies Anonymous"
                Expect.equal server["PublicSurface"] Public "PublicEndpoint still classifies Public"
            }

            test "both sanctioned families still compose cleanly through Api.make (GP 11)" {
                let serverBuilder (_: HttpContext) : SanctionedServerApi = {
                    AdminOnly = fun () -> async { return 1 }
                    NeedsScope = fun () -> async { return 2 }
                    TenantOnly = fun () -> async { return 3 }
                    OpenToAll = fun () -> async { return 4 }
                    PublicSurface = fun () -> async { return 5 }
                }

                let mirrorBuilder (_: HttpContext) : SanctionedMirrorApi = {
                    AdminOnly = fun () -> async { return 1 }
                    NeedsScope = fun () -> async { return 2 }
                    TenantOnly = fun () -> async { return 3 }
                    OpenToAll = fun () -> async { return 4 }
                    PublicSurface = fun () -> async { return 5 }
                }

                ToolUp.Platform.Api.make serverBuilder |> ignore
                ToolUp.Platform.Api.make mirrorBuilder |> ignore
            }

            test "an unannotated method still refuses startup (69d preserved)" {
                let builder (_: HttpContext) : UnannotatedApi = { Naked = fun () -> async { return 1 } }

                let ex =
                    refusalOf (fun () -> ToolUp.Platform.Api.make builder |> ignore) "unannotated record"

                Expect.stringContains
                    ex.Message
                    "unclassified"
                    "the 69d diagnostic is unchanged for a genuinely bare field"

                Expect.stringContains ex.Message "Naked" "the 69d diagnostic still names the method"
            }
        ]