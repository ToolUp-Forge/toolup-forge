namespace ToolUp.Remoting.Server

open System
open Microsoft.FSharp.Reflection

// =============================================================================
// Phase 69d — first-class authorisation metadata
// =============================================================================
//
// API record fields are attributed with one of the marker classes below; the
// dispatcher walks them at startup (refusing to start if any method lacks a
// classification) and evaluates them per-request against an `IAuthContext`
// resolved per the consumer's resolver.
//
// Wire-compatible: server-side enforcement only; the deny response uses
// 69b.E's categorised envelope (`ErrorCategory.Auth`) so clients reading the
// `error` body still parse.

/// Caller must hold the named role for the method to dispatch.
[<AttributeUsage(AttributeTargets.Property ||| AttributeTargets.Field, AllowMultiple = true)>]
type RequiresRoleAttribute(role: string) =
    inherit Attribute()
    member _.Role = role

/// Caller must hold the named claim. When `Value` is set, the claim must
/// match it exactly; otherwise mere presence of the claim is sufficient.
[<AttributeUsage(AttributeTargets.Property ||| AttributeTargets.Field, AllowMultiple = true)>]
type RequiresClaimAttribute(claim: string) =
    inherit Attribute()
    member _.Claim = claim
    member val Value: string = null with get, set

/// Caller must have a tenant context resolved. Use this to gate a method
/// against tenant-bound subjects (Phase 66's `Subject.Tenant`).
[<AttributeUsage(AttributeTargets.Property ||| AttributeTargets.Field)>]
type TenantScopedAttribute() =
    inherit Attribute()

/// Method explicitly accepts anonymous (no-auth-context) callers as well
/// as authenticated ones. Distinct from `PublicEndpoint` because the auth
/// context, if present, is still respected for telemetry / audit.
[<AttributeUsage(AttributeTargets.Property ||| AttributeTargets.Field)>]
type AllowAnonymousAttribute() =
    inherit Attribute()

/// Method is a public endpoint — the auth-context resolver isn't consulted
/// at all and the method dispatches regardless of caller identity. Use for
/// share-token-gated public surfaces (forge's `IPublicFormApi` shape).
[<AttributeUsage(AttributeTargets.Property ||| AttributeTargets.Field)>]
type PublicEndpointAttribute() =
    inherit Attribute()

// -----------------------------------------------------------------------------

/// Result of evaluating a method's classification against an `IAuthContext`.
type AuthDecision =
    /// Method is allowed for this caller. Dispatch proceeds.
    | Allow
    /// Method is denied; the dispatcher emits a categorised
    /// `ErrorCategory.Auth` envelope. `Reason` is server-side only — not
    /// surfaced in the wire body to avoid leaking authorisation rules.
    | Deny of reason: string

/// Phase 69d.tail — a method's authorisation requirements, normalised
/// from the attribute instances at classify time. Per-request evaluation
/// runs over this data shape, never over raw attribute instances — which
/// is what makes the classifier attribute-family-agnostic (see the note
/// on `AuthClassifier` below).
type internal AuthRequirement =
    | RoleRequired of role: string
    | ClaimRequired of claim: string * value: string option
    | TenantRequired

/// Method's classification, cached at startup. `Unclassified` is allowed
/// to exist as a transient internal value but the dispatcher refuses to
/// start when any method ends up classified this way. Internal visibility:
/// the type is consumed by the adapter assemblies via InternalsVisibleTo,
/// not part of the public surface.
type internal MethodClassification =
    | Public
    | Anonymous
    | RequiresAuth of requirements: AuthRequirement list
    | Unclassified

module internal AuthClassifier =

    // Phase 69d.tail — two attribute families exist by design: the
    // `ToolUp.Remoting.Server.*` set in this assembly (server-tier
    // consumers) and the `ToolUp.Platform.*` mirrors in the tier-shared
    // `ToolUp.Platform.Core` (carried by API records the Fable client
    // also compiles — those records cannot reference this server-tier
    // assembly). The dispatcher honours both: reflective property reads
    // cost microseconds once per API record at startup, and per-request
    // evaluation runs over the normalised `AuthRequirement` data.
    //
    // Phase 335 — recognition is by CLR TYPE IDENTITY over an allow-list
    // of exactly those two families, NOT by bare simple name as 69d.tail
    // shipped it. Simple-name matching made the classification input
    // forgeable: any attribute applicable to a record field whose type
    // name happened to be `PublicEndpointAttribute` or
    // `AllowAnonymousAttribute` — very common names a consumer or a
    // third-party package may well define — silently classified the
    // method `Public` / `Anonymous`, so `evaluate` returned `Allow` for
    // every caller with NO startup signal (the field *was* classified, so
    // the default-deny startup gate was satisfied). Identity matching is
    // stronger than the namespace-qualified name the phase asked for: it
    // also pins the declaring ASSEMBLY, so a consumer type declared into
    // `namespace ToolUp.Remoting.Server` from its own assembly is foreign
    // too. Both families are compile-time referenceable from here
    // (Platform.Server already references Platform.Core), so the set is
    // built from `typeof<>` rather than from strings.

    let private sanctionedMarkers: Collections.Generic.HashSet<Type> =
        Collections.Generic.HashSet<Type>(
            [
                // Server-tier family (this assembly).
                typeof<RequiresRoleAttribute>
                typeof<RequiresClaimAttribute>
                typeof<TenantScopedAttribute>
                typeof<AllowAnonymousAttribute>
                typeof<PublicEndpointAttribute>
                // Tier-shared mirror family (`ToolUp.Platform.Core`).
                typeof<ToolUp.Platform.RequiresRoleAttribute>
                typeof<ToolUp.Platform.RequiresClaimAttribute>
                typeof<ToolUp.Platform.TenantScopedAttribute>
                typeof<ToolUp.Platform.AllowAnonymousAttribute>
                typeof<ToolUp.Platform.PublicEndpointAttribute>
            ]
        )

    /// Phase 335 — the marker SIMPLE names, derived from the sanctioned
    /// set so the two can never drift. Used only to detect a *collision*:
    /// an attribute carrying one of these names that is not one of the
    /// sanctioned types is reported at startup rather than ignored, so a
    /// consumer whose own `PublicEndpointAttribute` silently stopped
    /// opening a method learns why instead of discovering it as a 403.
    let private markerSimpleNames: Collections.Generic.HashSet<string> =
        Collections.Generic.HashSet<string>(sanctionedMarkers |> Seq.map _.Name)

    let private isSanctioned (t: Type) = sanctionedMarkers.Contains t

    let private stringProperty (name: string) (a: Attribute) : string option =
        match a.GetType().GetProperty name with
        | null -> None
        | p ->
            match p.GetValue a with
            | :? string as s when not (isNull s) -> Some s
            | _ -> None

    let private tryRequirement (a: Attribute) : AuthRequirement option =
        let t = a.GetType()

        if not (isSanctioned t) then
            None
        else
            match t.Name with
            | "RequiresRoleAttribute" -> stringProperty "Role" a |> Option.map RoleRequired
            | "RequiresClaimAttribute" ->
                stringProperty "Claim" a
                |> Option.map (fun claim -> ClaimRequired(claim, stringProperty "Value" a))
            | "TenantScopedAttribute" -> Some TenantRequired
            | _ -> None

    let private isPublicEndpoint (a: Attribute) =
        let t = a.GetType()
        isSanctioned t && t.Name = "PublicEndpointAttribute"

    let private isAllowAnonymous (a: Attribute) =
        let t = a.GetType()
        isSanctioned t && t.Name = "AllowAnonymousAttribute"

    // Reflection must see non-public record types too: consumer API
    // records can be `internal`/`private` (module-internal contracts,
    // test fixtures). Without `BindingFlags.NonPublic`, `IsRecord`
    // reports `false` for them and classification silently returns the
    // empty map — which would skip the startup classifier entirely for
    // exactly those records (fail-OPEN). Public records are unaffected.
    // Internal (not private): the same `Public ||| NonPublic` flag set is
    // needed by the seam-composition guard (`Api.fs`) and the dispatcher
    // record check (`GiraffeAdapter.fs`); they reference this single
    // binding rather than re-inlining the expression.
    let reflectionFlags =
        System.Reflection.BindingFlags.Public
        ||| System.Reflection.BindingFlags.NonPublic

    /// Walk an API record type's fields, return classification per field.
    let classify (apiType: Type) : Map<string, MethodClassification> =
        if not (FSharpType.IsRecord(apiType, reflectionFlags)) then
            // The Empty implementation path on RemotingOptions never has a
            // real API record to inspect. Empty classification map.
            Map.empty
        else
            let fields = FSharpType.GetRecordFields(apiType, reflectionFlags)

            fields
            |> Array.map (fun pi ->
                let attrs =
                    pi.GetCustomAttributes(true) |> Array.choose (fun a -> a :?> Attribute |> Some)

                let hasPublic = attrs |> Array.exists isPublicEndpoint
                let hasAnon = attrs |> Array.exists isAllowAnonymous
                let requirements = attrs |> Array.choose tryRequirement |> Array.toList

                let cls =
                    if hasPublic then
                        Public
                    elif hasAnon then
                        Anonymous
                    elif not (List.isEmpty requirements) then
                        RequiresAuth requirements
                    else
                        Unclassified

                pi.Name, cls)
            |> Map.ofArray

    /// Return the list of unclassified method names for a classification map.
    let unclassified (classifications: Map<string, MethodClassification>) : string list =
        classifications
        |> Map.toList
        |> List.choose (fun (name, cls) ->
            match cls with
            | Unclassified -> Some name
            | _ -> None)

    /// Phase 335 — walk an API record's fields and return every attribute
    /// whose SIMPLE NAME collides with one of the sanctioned markers but
    /// whose CLR type is not sanctioned, as `(fieldName, attributeType)`
    /// pairs (the type rendered assembly-qualified, so the diagnostic
    /// names both the namespace and the assembly it came from).
    ///
    /// A collision is refused at startup rather than merely ignored. Two
    /// reasons, and the second is why "ignore it" is not enough: a field
    /// carrying ONLY the foreign marker now classifies `Unclassified`, so
    /// the 69d gate would already refuse — but naming it "unclassified"
    /// misdescribes the cause and sends the consumer to annotate a field
    /// they believe they already annotated; and a field carrying a
    /// foreign `PublicEndpointAttribute` ALONGSIDE a genuine
    /// `[<RequiresRole>]` classifies `RequiresAuth` and would start
    /// silently, with the consumer believing the method open. A name
    /// collision must never silently decide a method's classification in
    /// either direction.
    let foreignMarkers (apiType: Type) : (string * string) list =
        if not (FSharpType.IsRecord(apiType, reflectionFlags)) then
            []
        else
            FSharpType.GetRecordFields(apiType, reflectionFlags)
            |> Array.collect (fun pi ->
                pi.GetCustomAttributes(true)
                |> Array.choose (fun a ->
                    let t = a.GetType()

                    if markerSimpleNames.Contains t.Name && not (isSanctioned t) then
                        let rendered =
                            if isNull t.AssemblyQualifiedName then
                                t.FullName
                            else
                                t.AssemblyQualifiedName

                        Some(pi.Name, rendered)
                    else
                        None))
            |> Array.toList

    /// Build a startup-time exception for attributes whose simple name
    /// collides with a sanctioned marker (Phase 335).
    let foreignMarkerException (apiTypeName: string) (collisions: (string * string) list) : exn =
        let detail =
            collisions
            |> List.map (fun (field, attrType) -> sprintf "%s carries '%s'" field attrType)
            |> String.concat "; "

        invalidOp (
            sprintf
                "ToolUp.Remoting refused to start: API record '%s' has %d field(s) carrying an attribute whose name matches an authorisation marker but which is NOT one of the two sanctioned families: [%s]. Only ToolUp.Remoting.Server.* (server-tier) and ToolUp.Platform.* (tier-shared mirror) markers classify a method; an attribute of the same name from any other namespace or assembly is refused rather than honoured, because a name collision must never decide a method's authorisation. Replace it with the sanctioned attribute of the same name, or rename your own attribute. See docs/migrations/335-qualified-auth-attribute-matching.md."
                apiTypeName
                collisions.Length
                detail
        )

    /// Build a startup-time exception for unclassified methods.
    let unclassifiedException (apiTypeName: string) (methods: string list) : exn =
        let msg =
            sprintf
                "ToolUp.Remoting refused to start: API record '%s' has %d unclassified method(s): [%s]. Phase 69d requires every API record field to carry one of [<RequiresRole>], [<RequiresClaim>], [<TenantScoped>], [<AllowAnonymous>], or [<PublicEndpoint>]. See ToolUp.Remoting README §69d."
                apiTypeName
                methods.Length
                (String.concat "; " methods)

        invalidOp msg

    // Phase 132 — extract the runtime authorisation key from a built
    // route, the SAME way the dispatcher does per request: the trailing
    // path segment after the last '/'. Kept here next to the round-trip
    // check so the two derivations can never silently diverge (the whole
    // point of the assertion is that they agree).
    let private runtimeKeyOf (route: string) : string =
        let lastSlash = route.LastIndexOf '/'

        if lastSlash >= 0 then
            route.Substring(lastSlash + 1)
        else
            route

    /// Phase 132 — verify every classified field name round-trips through
    /// the active `RouteBuilder`: applying the builder to the field name
    /// then extracting the trailing path segment (the runtime auth key)
    /// must yield back the field name. The classification map is keyed by
    /// field name; the dispatcher looks classifications up by runtime key.
    /// A custom `RouteBuilder` / casing / alias that breaks the round-trip
    /// makes the per-request lookup miss — and (Phase 132 deny-on-miss)
    /// deny every call. Returns the `(fieldName, divergentRuntimeKey)`
    /// pairs that do not round-trip; empty means every key aligns.
    let nonRoundTripping
        (routeBuilder: string -> string -> string)
        (apiTypeName: string)
        (classifications: Map<string, MethodClassification>)
        : (string * string) list =
        classifications
        |> Map.toList
        |> List.choose (fun (fieldName, _) ->
            let runtimeKey = runtimeKeyOf (routeBuilder apiTypeName fieldName)

            if runtimeKey = fieldName then
                None
            else
                Some(fieldName, runtimeKey))

    /// Build a startup-time exception for classified field names whose
    /// route does not round-trip to the field name (Phase 132).
    let roundTripException (apiTypeName: string) (divergences: (string * string) list) : exn =
        let detail =
            divergences
            |> List.map (fun (field, runtimeKey) -> sprintf "%s → runtime-key '%s'" field runtimeKey)
            |> String.concat "; "

        invalidOp (
            sprintf
                "ToolUp.Remoting refused to start: API record '%s' has %d classified method(s) whose field name does not round-trip through the active RouteBuilder: [%s]. The dispatcher keys per-request authorisation by the trailing path segment of the built route; when that diverges from the field name the classification lookup misses and (Phase 132 deny-on-miss) denies every call. Use a RouteBuilder whose trailing segment equals the method name."
                apiTypeName
                divergences.Length
                detail
        )

    /// Phase 132 — given a predicate describing which role strings the
    /// armed resolver can ever emit, return the distinct role names that
    /// appear in `[<RequiresRole>]` requirements but can never be emitted.
    /// Such a role gate denies *every* caller (the dead-gate trap): the
    /// SDK's first-party providers leave `IAuthContext.HasRole` resolving
    /// against an always-empty `user.Roles` for every role except the
    /// server-resolved `"PlatformAdmin"`, so any other required role is a
    /// silent always-deny. Empty when every required role is emittable.
    let unemittableRoles (canEmit: string -> bool) (classifications: Map<string, MethodClassification>) : string list =
        classifications
        |> Map.toList
        |> List.collect (fun (_, cls) ->
            match cls with
            | RequiresAuth reqs ->
                reqs
                |> List.choose (function
                    | RoleRequired r -> Some r
                    | _ -> None)
            | _ -> [])
        |> List.filter (canEmit >> not)
        |> List.distinct

    /// Evaluate a method's classification against an auth context. The
    /// dispatcher calls this on every successful endpoint lookup.
    let evaluate (classification: MethodClassification) (context: IAuthContext option) : AuthDecision =
        match classification with
        | Public -> Allow
        | Anonymous ->
            // Anonymous methods accept anyone — the auth context, if
            // present, isn't enforced (used for telemetry only).
            Allow
        | Unclassified ->
            // Should never reach here; startup-classifier refuses to start.
            // Belt + braces — fail-closed if reached at runtime.
            Deny "unclassified-method"
        | RequiresAuth requirements ->
            match context with
            | None ->
                // No auth-context resolver registered, but the method
                // requires one. Fail-closed.
                Deny "no-auth-context-resolver"
            | Some ctx ->
                if ctx.IsAnonymous() then
                    Deny "anonymous-not-permitted"
                else
                    let denials =
                        requirements
                        |> List.choose (fun req ->
                            match req with
                            | RoleRequired role ->
                                if ctx.HasRole role then
                                    None
                                else
                                    Some(sprintf "missing-role: %s" role)
                            | ClaimRequired(claim, value) ->
                                if ctx.HasClaim(claim, value) then
                                    None
                                else
                                    Some(sprintf "missing-claim: %s" claim)
                            | TenantRequired -> if ctx.HasTenant() then None else Some "missing-tenant")

                    if List.isEmpty denials then
                        Allow
                    else
                        Deny(String.concat "; " denials)