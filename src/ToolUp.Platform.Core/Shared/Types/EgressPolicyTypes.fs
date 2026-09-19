// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Phase 772 — server-side egress policy: the pure half ────────────
//
// The decision over an outbound HTTP call — WHICH component is calling
// WHICH origin through WHICH surface — is a pure function of three
// values, and this file holds exactly that: the value types, the seam
// (`IEgressPolicy`), the two shipped policies and the per-component
// grant they are resolved from. Nothing here opens a socket, reads a
// clock or touches a container. The `DelegatingHandler` that consults
// the seam before every request, the audit emission on a deny and the
// platform client factory all live in the Server tier
// (`EgressPolicyHandler.fs`), because `System.Net.Http` is not a Fable
// surface and this file compiles under Fable with the rest of `Shared/`.
//
// Two decisions are fixed here and everything downstream inherits them:
//
//   • A destination is an ORIGIN (`scheme://host[:port]`) and never the
//     full URL. A denial names what it refused, and a refusal that
//     carried the path or the query string would put exactly the bytes
//     a policy exists to keep in-process onto the audit trail — the
//     lesson the renderer's sanitize-seam egress policy learned first.
//     `EgressDestination.tryParse` is the ONE construction path and it
//     discards everything after the authority by construction.
//   • Absence is the additive floor (GP 11 / GP 13). The permit-all
//     policy is the default; a component absent from a grant signature
//     resolves to whatever `undeclared` posture the profile chose, so a
//     composition that declares nothing under `Standard` is byte-for-byte
//     unchanged, and one under `Verified` denies by shape.

/// An outbound destination, reduced to its origin: lowercase
/// `scheme://host`, plus `:port` only when the port is not the scheme's
/// default. Constructed through `EgressDestination.tryParse`, which is
/// what guarantees a value never carries a path, a query or a fragment.
type EgressDestination =
    | EgressDestination of string

    /// The canonical origin string this destination names.
    member this.Origin =
        let (EgressDestination o) = this
        o

    override this.ToString() = this.Origin

[<RequireQualifiedAccess>]
module EgressDestination =

    let private defaultPort (scheme: string) : int option =
        match scheme with
        | "http" -> Some 80
        | "https" -> Some 443
        | _ -> None

    let private isSchemeChar (c: char) : bool =
        (c >= 'a' && c <= 'z')
        || (c >= '0' && c <= '9')
        || c = '+'
        || c = '-'
        || c = '.'

    /// Build a destination from its parts. `scheme` and `host` are
    /// lowercased; `port` is dropped when it is the scheme's default so
    /// `https://api.example.com` and `https://api.example.com:443` are one
    /// origin. `None` for an empty scheme or host, or a non-positive port.
    let ofParts (scheme: string) (host: string) (port: int option) : EgressDestination option =
        let scheme =
            if isNull scheme then
                ""
            else
                scheme.Trim().ToLowerInvariant()

        let host = if isNull host then "" else host.Trim().ToLowerInvariant()

        if scheme = "" || host = "" || not (Seq.forall isSchemeChar scheme) then
            None
        else
            match port with
            | Some p when p <= 0 -> None
            | Some p when defaultPort scheme = Some p -> Some(EgressDestination(scheme + "://" + host))
            | Some p -> Some(EgressDestination(scheme + "://" + host + ":" + string p))
            | None -> Some(EgressDestination(scheme + "://" + host))

    /// Parse an absolute URL down to its origin, discarding userinfo, the
    /// path, the query and the fragment. `None` when there is no scheme,
    /// no host, or a port that is not a number. Plain string work rather
    /// than `System.Uri`, which this tier treats as a Fable hazard.
    let tryParse (url: string) : EgressDestination option =
        if isNull url then
            None
        else
            let url = url.Trim()
            let sep = url.IndexOf "://"

            if sep <= 0 then
                None
            else
                let scheme = url.Substring(0, sep)
                let rest = url.Substring(sep + 3)

                let authorityEnd =
                    [ rest.IndexOf '/'; rest.IndexOf '?'; rest.IndexOf '#' ]
                    |> List.filter (fun i -> i >= 0)
                    |> function
                        | [] -> rest.Length
                        | ends -> List.min ends

                let authority = rest.Substring(0, authorityEnd)

                // Userinfo never reaches the origin — it is a credential.
                let hostPort =
                    match authority.LastIndexOf '@' with
                    | -1 -> authority
                    | at -> authority.Substring(at + 1)

                // `[::1]:8080` — a bracketed IPv6 literal keeps its colons.
                let host, portText =
                    if hostPort.StartsWith "[" then
                        match hostPort.IndexOf ']' with
                        | -1 -> "", None
                        | close ->
                            let after = hostPort.Substring(close + 1)

                            if after = "" then
                                hostPort.Substring(0, close + 1), None
                            elif after.StartsWith ":" then
                                hostPort.Substring(0, close + 1), Some(after.Substring 1)
                            else
                                "", None
                    else
                        match hostPort.LastIndexOf ':' with
                        | -1 -> hostPort, None
                        | colon -> hostPort.Substring(0, colon), Some(hostPort.Substring(colon + 1))

                match portText with
                | None -> ofParts scheme host None
                | Some text ->
                    match System.Int32.TryParse text with
                    | true, p -> ofParts scheme host (Some p)
                    | _ -> None

    /// `tryParse`, raising `ArgumentException` on an unparseable URL — the
    /// grant-authoring shape, where a destination that cannot be named
    /// must not silently become an unmatchable set member.
    let parse (url: string) : EgressDestination =
        match tryParse url with
        | Some d -> d
        | None -> invalidArg "url" (sprintf "'%s' is not an absolute URL with a scheme and host." url)

    /// The canonical origin string.
    let origin (EgressDestination o) : string = o

/// The kind of platform component making the outbound call — the third
/// coordinate of the decision, so a policy can distinguish an AI
/// provider reaching its API from a webhook reaching a customer's URL.
[<RequireQualifiedAccess>]
type EgressSurface =
    /// A module's own handler code, calling out through the platform's
    /// named `IHttpClientFactory` client.
    | ModuleHandler
    /// An `IAIProvider` / embedding / transcription companion.
    | AIProvider
    /// An `IAuthProvider` or its config validator (JWKS, discovery,
    /// membership lookups).
    | AuthProvider
    /// An `INotificationChannel` sink (email / SMS gateways).
    | Notification
    /// An outbound webhook delivery.
    | Webhook
    /// Anything else — peer transport, container schedulers, bulk import.
    | Other

[<RequireQualifiedAccess>]
module EgressSurface =

    /// Stable lowercase label for audit payloads and report lines.
    let label (surface: EgressSurface) : string =
        match surface with
        | EgressSurface.ModuleHandler -> "module-handler"
        | EgressSurface.AIProvider -> "ai-provider"
        | EgressSurface.AuthProvider -> "auth-provider"
        | EgressSurface.Notification -> "notification"
        | EgressSurface.Webhook -> "webhook"
        | EgressSurface.Other -> "other"

/// What the policy said about one outbound call.
[<RequireQualifiedAccess>]
type EgressVerdict =
    /// The call may proceed.
    | Permit
    /// The call is refused; `reason` is the sentence the typed exception
    /// and the audit row carry. It names the origin, never the URL.
    | Deny of reason: string

/// The seam. A pure decision over (calling component, destination
/// origin, surface). Implementations hold no per-call state and open no
/// connection; the Server-tier handler asks this before every request
/// and either forwards it or raises.
type IEgressPolicy =
    abstract Decide: component: ComponentId * destination: EgressDestination * surface: EgressSurface -> EgressVerdict

/// One component's declared outbound authority, mirroring `SeamGrant`'s
/// two-case shape for the same reason it has two cases: `UnrestrictedEgress`
/// is "nothing declared" (every origin permitted) and
/// `DeclaredDestinations Set.empty` is a real declaration meaning "this
/// component makes no outbound call at all". The two are opposite ends of
/// the order and are never normalised into each other.
type EgressGrant =
    /// No declaration — every origin is permitted (GP 11: absence is the
    /// no-op).
    | UnrestrictedEgress
    /// Exactly these origins are permitted; every other origin is denied.
    | DeclaredDestinations of Set<EgressDestination>

/// Declared egress authority per composed unit, keyed by the same stable
/// `ComponentId` the `SeamGrantSignature` and the composition manifest
/// use. What an absent id resolves to is the PROFILE's decision, not the
/// signature's — see `EgressGrant.resolve`.
type EgressGrantSignature = Map<ComponentId, EgressGrant>

[<RequireQualifiedAccess>]
module EgressGrant =

    /// The undeclared posture — every origin permitted.
    let unrestricted: EgressGrant = UnrestrictedEgress

    /// Declare exactly these destinations. An EMPTY sequence produces
    /// `DeclaredDestinations Set.empty` — "calls nothing" — and is
    /// deliberately NOT folded to `UnrestrictedEgress`.
    let ofDestinations (destinations: EgressDestination seq) : EgressGrant =
        DeclaredDestinations(Set.ofSeq destinations)

    /// Declare exactly these origins by URL — the shape a composition root
    /// writes by hand. Each URL is reduced to its origin; an unparseable
    /// one raises rather than silently narrowing the grant.
    let ofOrigins (urls: string seq) : EgressGrant =
        DeclaredDestinations(urls |> Seq.map EgressDestination.parse |> Set.ofSeq)

    /// Whether the component declared a destination set at all.
    let isDeclared (grant: EgressGrant) : bool =
        match grant with
        | UnrestrictedEgress -> false
        | DeclaredDestinations _ -> true

    /// The declared set — empty for `UnrestrictedEgress`, which declares
    /// no set. Read with `isDeclared`.
    let destinations (grant: EgressGrant) : Set<EgressDestination> =
        match grant with
        | UnrestrictedEgress -> Set.empty
        | DeclaredDestinations d -> d

    /// Whether `destination` may be reached under this grant.
    let permits (grant: EgressGrant) (destination: EgressDestination) : bool =
        match grant with
        | UnrestrictedEgress -> true
        | DeclaredDestinations d -> Set.contains destination d

    /// Look a component up in a signature, resolving an absent id to
    /// `undeclared` — the profile's posture for a component that declared
    /// nothing: `UnrestrictedEgress` under `Standard` (GP 11),
    /// `DeclaredDestinations Set.empty` under `Verified` (default-deny by
    /// shape).
    let resolve (signature: EgressGrantSignature) (undeclared: EgressGrant) (componentId: ComponentId) : EgressGrant =
        match Map.tryFind componentId signature with
        | Some grant -> grant
        | None -> undeclared

    /// A readable one-line rendering: `unrestricted`, `declared{}`, or
    /// `declared{https://a.example,https://b.example}` — origins sorted
    /// ordinally so the line is deterministic.
    let render (grant: EgressGrant) : string =
        match grant with
        | UnrestrictedEgress -> "unrestricted"
        | DeclaredDestinations d ->
            let origins =
                d
                |> Set.toList
                |> List.map EgressDestination.origin
                |> List.sortWith (fun a b -> System.String.CompareOrdinal(a, b))

            "declared{" + String.concat "," origins + "}"

/// The posture a composition's egress policy takes — what the boot log
/// says and what the deployment verification report renders. Derived
/// once by `EgressPolicy.bind` beside the policy it describes, so the
/// two cannot disagree.
[<RequireQualifiedAccess>]
type EgressPosture =
    /// The permit-all default is composed: every outbound call from every
    /// component is permitted and nothing is bounded.
    | Unenforced
    /// Declaration is mandatory and nothing was declared: every outbound
    /// call from every component is refused before the socket opens.
    | DenyAll
    /// Declared destinations bind `components` component(s); an undeclared
    /// component is refused when `undeclaredDenied`, permitted otherwise.
    | Declared of components: int * undeclaredDenied: bool

[<RequireQualifiedAccess>]
module EgressPosture =

    /// Stable lowercase label for logs and report lines.
    let label (posture: EgressPosture) : string =
        match posture with
        | EgressPosture.Unenforced -> "unenforced"
        | EgressPosture.DenyAll -> "deny-all"
        | EgressPosture.Declared _ -> "declared"

    /// The one-sentence account a composition root logs at boot.
    let describe (posture: EgressPosture) : string =
        match posture with
        | EgressPosture.Unenforced ->
            "egress policy: unenforced — the permit-all default is composed; every outbound call from every component is permitted"
        | EgressPosture.DenyAll ->
            "egress policy: deny-all — declaration is mandatory and no component declares a destination, so every outbound call is refused before the socket opens"
        | EgressPosture.Declared(components, true) ->
            sprintf
                "egress policy: declared destinations bind %d component(s); an undeclared component is refused"
                components
        | EgressPosture.Declared(components, false) ->
            sprintf
                "egress policy: declared destinations bind %d component(s); an undeclared component remains unrestricted"
                components

/// A policy together with the posture and the grants it was built from —
/// what `EgressPolicy.bind` returns and what the Server tier installs.
type EgressPolicyBinding = {
    /// The decision the handler consults.
    Policy: IEgressPolicy
    /// What that policy amounts to, for the boot log and the report.
    Posture: EgressPosture
    /// The grants it was resolved from — empty for the permit-all default.
    Grants: EgressGrantSignature
    /// Whether declaring destinations is mandatory under the composing
    /// profile (an undeclared component is then denied).
    DeclarationMandatory: bool
}

[<RequireQualifiedAccess>]
module EgressPolicy =

    /// The component an outbound call is attributed to when no module has
    /// claimed the async chain — the platform itself. The same derivation
    /// `EventTopology.platformComponent` uses, so the two ids agree.
    let platformComponent: ComponentId = ComponentId.ofModule "_platform"

    /// The default: every call from every component is permitted. What a
    /// composition that installs nothing runs under.
    let permitAll: IEgressPolicy =
        { new IEgressPolicy with
            member _.Decide(_, _, _) = EgressVerdict.Permit
        }

    /// A policy resolved from per-component grants. `undeclared` is what a
    /// component absent from `grants` resolves to — the profile's posture.
    /// A deny names the component, the ORIGIN and the surface, and renders
    /// the grant it was refused under, so the audit row is actionable
    /// without ever carrying a URL.
    let declaredDestinations (undeclared: EgressGrant) (grants: EgressGrantSignature) : IEgressPolicy =
        { new IEgressPolicy with
            member _.Decide(componentId, destination, surface) =
                let grant = EgressGrant.resolve grants undeclared componentId

                if EgressGrant.permits grant destination then
                    EgressVerdict.Permit
                else
                    EgressVerdict.Deny(
                        sprintf
                            "egress from component %s to origin %s (%s surface) is not declared; its grant is %s"
                            (ComponentId.value componentId)
                            (EgressDestination.origin destination)
                            (EgressSurface.label surface)
                            (EgressGrant.render grant)
                    )
        }

    /// Resolve the policy a composition runs under from its profile's
    /// stance and whatever it declared.
    ///
    ///   • not mandatory, nothing declared → `permitAll` (GP 11 — the
    ///     pre-772 world, byte-for-byte);
    ///   • not mandatory, grants → declared components are bound, the
    ///     rest stay unrestricted;
    ///   • mandatory, nothing declared → DENY ALL. A mandatory check with
    ///     nothing declared would permit everything while presenting as
    ///     enforcement, which is worse than no check because it is
    ///     believed — so the verified profile refuses by shape and says
    ///     so at boot (`EgressPosture.describe`);
    ///   • mandatory, grants → declared components are bound, an
    ///     undeclared component is refused.
    ///
    /// `declarationMandatory` is the profile's answer — `Verified` says
    /// yes, `Standard` says no — passed as a value because this tier
    /// cannot name the Server-tier `CompositionProfile`.
    let bind (declarationMandatory: bool) (grants: EgressGrantSignature option) : EgressPolicyBinding =
        match declarationMandatory, grants with
        | false, None -> {
            Policy = permitAll
            Posture = EgressPosture.Unenforced
            Grants = Map.empty
            DeclarationMandatory = false
          }
        | false, Some declared -> {
            Policy = declaredDestinations UnrestrictedEgress declared
            Posture = EgressPosture.Declared(Map.count declared, false)
            Grants = declared
            DeclarationMandatory = false
          }
        | true, None -> {
            Policy = declaredDestinations (DeclaredDestinations Set.empty) Map.empty
            Posture = EgressPosture.DenyAll
            Grants = Map.empty
            DeclarationMandatory = true
          }
        | true, Some declared -> {
            Policy = declaredDestinations (DeclaredDestinations Set.empty) declared
            Posture = EgressPosture.Declared(Map.count declared, true)
            Grants = declared
            DeclarationMandatory = true
          }

    /// The binding a composition runs under when nothing is installed.
    let permitAllBinding: EgressPolicyBinding = bind false None