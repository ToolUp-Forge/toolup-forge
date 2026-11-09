namespace ToolUp.Remoting.Server

open System
open Microsoft.FSharp.Reflection

// =============================================================================
// First-class authorisation metadata
// =============================================================================
//
// API record fields are attributed with one of the marker classes below; the
// dispatcher walks them at startup (refusing to start if any method lacks a
// classification) and evaluates them per-request against an `IAuthContext`
// resolved per the consumer's resolver.
//
// Wire-compatible: server-side enforcement only; the deny response uses the
// categorised envelope (`ErrorCategory.Auth`) so clients reading the `error`
// body still parse.

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
/// against tenant-bound subjects (`Subject.Tenant`).
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

/// The recognition mechanism of the auth markers, shared so the other
/// attribute families riding the same dispatch path use ONE implementation
/// instead of five copies that drift. Two matchers disagreeing about what
/// counts as a marker is precisely the drift identity matching ends, and
/// five hand-rolled
/// copies of "is this one of ours" would have re-created it a family at
/// a time.
///
/// A family is an allow-list of CLR TYPES (`typeof<>`, so both the
/// namespace and the declaring ASSEMBLY are pinned — a consumer type
/// declared straight into `namespace ToolUp.Remoting.Server` from its own
/// assembly is foreign for the same reason a `Contoso.*` one is), plus
/// the SIMPLE names derived from that set so a *collision* — an attribute
/// carrying a marker's name that is not one of the sanctioned types — can
/// be reported rather than silently ignored.
///
/// Every family in this dispatch path spans two tiers by design: the
/// server-tier attributes declared in this assembly, and the tier-shared
/// `ToolUp.Platform.*` mirrors in `ToolUp.Platform.Core` that Fable-
/// compiled API records carry (they cannot reference a server-tier
/// assembly). `Platform.Server` references `Platform.Core`, so both are
/// compile-time referenceable here and the set is built from `typeof<>`
/// rather than from strings.
type internal MarkerFamily(sanctioned: Type seq) =
    let types = Collections.Generic.HashSet<Type>(sanctioned)
    let names = Collections.Generic.HashSet<string>(types |> Seq.map _.Name)

    /// True when the attribute's CLR type is one of the sanctioned ones.
    member _.IsSanctioned(t: Type) = types.Contains t

    /// True when the attribute's SIMPLE name collides with a sanctioned
    /// marker but its CLR type is not sanctioned — the forgeable shape.
    member _.IsForeign(t: Type) =
        names.Contains t.Name && not (types.Contains t)

    /// Render a type for a diagnostic: assembly-qualified where possible,
    /// so the message names both the namespace and the assembly it came
    /// from (which is what tells a consumer *which* of their packages
    /// declared it).
    member _.Render(t: Type) : string =
        if isNull t.AssemblyQualifiedName then
            t.FullName
        else
            t.AssemblyQualifiedName

    /// Every attribute on one field whose simple name collides with a
    /// sanctioned marker without being one, rendered.
    member this.CollisionsOf(field: System.Reflection.PropertyInfo) : string list =
        field.GetCustomAttributes(true)
        |> Array.choose (fun a ->
            let t = a.GetType()
            if this.IsForeign t then Some(this.Render t) else None)
        |> Array.toList

    /// Walk a record type's fields and return `(fieldName, renderedType)`
    /// for every attribute whose simple name collides with a sanctioned
    /// marker without being one. Empty for a non-record type.
    member this.Collisions(recordType: Type) : (string * string) list =
        ApiRecord.fields recordType
        |> Array.toList
        |> List.collect (fun pi -> this.CollisionsOf pi |> List.map (fun rendered -> pi.Name, rendered))

/// Result of evaluating a method's classification against an `IAuthContext`.
[<RequireQualifiedAccess>]
type AuthDecision =
    /// Method is allowed for this caller. Dispatch proceeds.
    | Allow
    /// Method is denied; the dispatcher emits a categorised
    /// `ErrorCategory.Auth` envelope. `Reason` is server-side only — not
    /// surfaced in the wire body to avoid leaking authorisation rules.
    | Deny of reason: string

/// A method's authorisation requirements, normalised from the attribute
/// instances when the record is read. Per-request evaluation runs over this
/// data shape, never over raw attribute instances — which is what makes the
/// classifier attribute-family-agnostic (see the note on `AuthClassifier`).
type AuthRequirement =
    | RoleRequired of role: string
    | ClaimRequired of claim: string * value: string option
    | TenantRequired

/// A method's declared authorisation, as an API record's contract carries it.
/// There is no unclassified case: a method carrying no marker has no
/// `Classified` at all, and the contract refuses it wherever a resolver is
/// composed.
[<RequireQualifiedAccess>]
type Classified =
    /// `[<PublicEndpoint>]`: the resolver is not consulted.
    | PublicEndpoint
    /// `[<AllowAnonymous>]`: any caller, authenticated or not.
    | AllowAnonymous
    /// The caller must meet every requirement.
    | Requires of requirements: AuthRequirement list

/// The evaluator's view of a classified method. A field with no marker has
/// no classification at all (it is absent from `AuthClassifier.classify`),
/// so the evaluator never meets a method it would have to refuse by default.
/// Internal visibility: the adapter assemblies consume it via
/// InternalsVisibleTo.
[<RequireQualifiedAccess>]
type internal MethodClassification =
    | Public
    | Anonymous
    | RequiresAuth of requirements: AuthRequirement list

module internal AuthClassifier =

    // Two attribute families exist by design: the
    // `ToolUp.Remoting.Server.*` set in this assembly (server-tier
    // consumers) and the `ToolUp.Platform.*` mirrors in the tier-shared
    // `ToolUp.Platform.Core` (carried by API records the Fable client
    // also compiles — those records cannot reference this server-tier
    // assembly). The dispatcher honours both: reflective property reads
    // cost microseconds once per API record at startup, and per-request
    // evaluation runs over the normalised `AuthRequirement` data.
    //
    // Recognition is by CLR TYPE IDENTITY over an allow-list
    // of exactly those two families, NOT by bare simple name. Simple-name
    // matching would make the classification input
    // forgeable: any attribute applicable to a record field whose type
    // name happened to be `PublicEndpointAttribute` or
    // `AllowAnonymousAttribute` — very common names a consumer or a
    // third-party package may well define — would silently classify the
    // method `Public` / `Anonymous`, so `evaluate` would return `Allow` for
    // every caller with NO startup signal (the field *is* classified, so
    // the default-deny startup gate is satisfied). Identity matching is
    // stronger than a namespace-qualified name: it also pins the
    // declaring ASSEMBLY, so a consumer type declared into
    // `namespace ToolUp.Remoting.Server` from its own assembly is foreign
    // too. Both families are compile-time referenceable from here
    // (Platform.Server already references Platform.Core), so the set is
    // built from `typeof<>` rather than from strings.

    // The set and the simple-name collision detection ride the shared
    // `MarkerFamily` (above), the same code the audit / rate-limit /
    // idempotency families use, so the recognisers cannot drift apart.
    let private markers =
        MarkerFamily [
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

    let private isSanctioned (t: Type) = markers.IsSanctioned t

    /// The one flag set every reader of an API record uses (`ApiRecord`).
    let reflectionFlags = ApiRecord.reflectionFlags

    let private stringProperty (name: string) (a: Attribute) : string option =
        match a.GetType().GetProperty name with
        | null -> None
        | p ->
            match p.GetValue a with
            | :? string as s when not (isNull s) -> Some s
            | _ -> None

    /// Decode one sanctioned requirement marker. The name dispatch runs
    /// inside the identity gate, so it decodes a type already established as
    /// one of ours; the mirror's properties are read here, once per field,
    /// when the record is read (never per request).
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

    let private isMarker (name: string) (a: Attribute) =
        let t = a.GetType()
        isSanctioned t && t.Name = name

    /// One field's declared classification; `None` when it carries no
    /// sanctioned authorisation marker.
    let classifiedOf (field: System.Reflection.PropertyInfo) : Classified option =
        let attrs = field.GetCustomAttributes(true) |> Array.map (fun a -> a :?> Attribute)

        let requirements = attrs |> Array.choose tryRequirement |> Array.toList

        if attrs |> Array.exists (isMarker "PublicEndpointAttribute") then
            Some Classified.PublicEndpoint
        elif attrs |> Array.exists (isMarker "AllowAnonymousAttribute") then
            Some Classified.AllowAnonymous
        elif not (List.isEmpty requirements) then
            Some(Classified.Requires requirements)
        else
            None

    /// The evaluator's view of a contract classification.
    let ofClassified (classified: Classified) : MethodClassification =
        match classified with
        | Classified.PublicEndpoint -> MethodClassification.Public
        | Classified.AllowAnonymous -> MethodClassification.Anonymous
        | Classified.Requires requirements -> MethodClassification.RequiresAuth requirements

    /// The authorisation-marker collisions on one field, rendered.
    let foreignMarkersOf (field: System.Reflection.PropertyInfo) : string list = markers.CollisionsOf field

    /// Walk an API record type's fields and return the classification of
    /// every field that carries a marker. A field with none is absent.
    let classify (apiType: Type) : Map<string, MethodClassification> =
        ApiRecord.fields apiType
        |> Array.choose (fun pi ->
            classifiedOf pi
            |> Option.map (fun classified -> pi.Name, ofClassified classified))
        |> Map.ofArray

    /// The fields of an API record that carry no authorisation marker, in
    /// declaration order.
    let unclassified (apiType: Type) : string list =
        ApiRecord.fields apiType
        |> Array.filter (classifiedOf >> Option.isNone)
        |> Array.map _.Name
        |> List.ofArray

    /// Walk an API record's fields and return every attribute
    /// whose SIMPLE NAME collides with one of the sanctioned markers but
    /// whose CLR type is not sanctioned, as `(fieldName, attributeType)`
    /// pairs (the type rendered assembly-qualified, so the diagnostic
    /// names both the namespace and the assembly it came from).
    ///
    /// A collision is refused at startup rather than merely ignored. Two
    /// reasons, and the second is why "ignore it" is not enough: a field
    /// carrying ONLY the foreign marker has no classification, so the
    /// unclassified-method refusal would already fire — but naming it "unclassified"
    /// misdescribes the cause and sends the consumer to annotate a field
    /// they believe they already annotated; and a field carrying a
    /// foreign `PublicEndpointAttribute` ALONGSIDE a genuine
    /// `[<RequiresRole>]` classifies `RequiresAuth` and would start
    /// silently, with the consumer believing the method open. A name
    /// collision must never silently decide a method's classification in
    /// either direction.
    let foreignMarkers (apiType: Type) : (string * string) list = markers.Collisions apiType


    /// Verify every classified field name round-trips through
    /// the active `RouteBuilder`: applying the builder to the field name
    /// then extracting the trailing path segment (the runtime auth key)
    /// must yield back the field name. The classification map is keyed by
    /// field name; the dispatcher looks classifications up by runtime key.
    /// A custom `RouteBuilder` / casing / alias that breaks the round-trip
    /// makes the per-request lookup miss, so the method's guard would not
    /// be found. Returns the `(fieldName, divergentRuntimeKey)`
    /// pairs that do not round-trip; empty means every key aligns.
    let nonRoundTripping
        (routeBuilder: string -> string -> string)
        (apiTypeName: string)
        (classifications: Map<string, MethodClassification>)
        : (string * string) list =
        classifications
        |> Map.toList
        |> List.choose (fun (fieldName, _) ->
            let runtimeKey = Route.methodSegment (routeBuilder apiTypeName fieldName)

            if runtimeKey = fieldName then
                None
            else
                Some(fieldName, runtimeKey))

    /// Given a predicate describing which role strings the
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
            | MethodClassification.RequiresAuth reqs ->
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
        | MethodClassification.Public -> AuthDecision.Allow
        | MethodClassification.Anonymous ->
            // Anonymous methods accept anyone — the auth context, if
            // present, isn't enforced (used for telemetry only).
            AuthDecision.Allow
        | MethodClassification.RequiresAuth requirements ->
            match context with
            | None ->
                // No auth-context resolver registered, but the method
                // requires one. Fail-closed.
                AuthDecision.Deny "no-auth-context-resolver"
            | Some ctx ->
                if ctx.IsAnonymous() then
                    AuthDecision.Deny "anonymous-not-permitted"
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
                        AuthDecision.Allow
                    else
                        AuthDecision.Deny(String.concat "; " denials)