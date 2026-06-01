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
    member val Value : string = null with get, set

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

/// Method's classification, cached at startup. `Unclassified` is allowed
/// to exist as a transient internal value but the dispatcher refuses to
/// start when any method ends up classified this way. Internal visibility:
/// the type is consumed by the adapter assemblies via InternalsVisibleTo,
/// not part of the public surface.
type internal MethodClassification =
    | Public
    | Anonymous
    | RequiresAuth of attrs: Attribute list
    | Unclassified

module internal AuthClassifier =

    /// Walk an API record type's fields, return classification per field.
    let classify (apiType: Type) : Map<string, MethodClassification> =
        if not (FSharpType.IsRecord apiType) then
            // The Empty implementation path on RemotingOptions never has a
            // real API record to inspect. Empty classification map.
            Map.empty
        else
            let fields = FSharpType.GetRecordFields apiType
            fields
            |> Array.map (fun pi ->
                let attrs = pi.GetCustomAttributes(true) |> Array.choose (fun a -> a :?> Attribute |> Some)
                let hasPublic = attrs |> Array.exists (fun a -> a :? PublicEndpointAttribute)
                let hasAnon = attrs |> Array.exists (fun a -> a :? AllowAnonymousAttribute)
                let authAttrs =
                    attrs
                    |> Array.filter (fun a ->
                        a :? RequiresRoleAttribute
                        || a :? RequiresClaimAttribute
                        || a :? TenantScopedAttribute)
                    |> Array.toList
                let cls =
                    if hasPublic then Public
                    elif hasAnon then Anonymous
                    elif not (List.isEmpty authAttrs) then RequiresAuth authAttrs
                    else Unclassified
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

    /// Build a startup-time exception for unclassified methods.
    let unclassifiedException (apiTypeName: string) (methods: string list) : exn =
        let msg =
            sprintf
                "ToolUp.Remoting refused to start: API record '%s' has %d unclassified method(s): [%s]. Phase 69d requires every API record field to carry one of [<RequiresRole>], [<RequiresClaim>], [<TenantScoped>], [<AllowAnonymous>], or [<PublicEndpoint>]. See ToolUp.Remoting README §69d."
                apiTypeName
                methods.Length
                (String.concat "; " methods)
        invalidOp msg

    /// Evaluate a method's classification against an auth context. The
    /// dispatcher calls this on every successful endpoint lookup.
    let evaluate
        (classification: MethodClassification)
        (context: IAuthContext option)
        : AuthDecision =
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
        | RequiresAuth attrs ->
            match context with
            | None ->
                // No auth-context resolver registered, but the method
                // requires one. Fail-closed.
                Deny "no-auth-context-resolver"
            | Some ctx ->
                if ctx.IsAnonymous () then
                    Deny "anonymous-not-permitted"
                else
                    let denials =
                        attrs
                        |> List.choose (fun a ->
                            match a with
                            | :? RequiresRoleAttribute as r ->
                                if ctx.HasRole r.Role then None
                                else Some (sprintf "missing-role: %s" r.Role)
                            | :? RequiresClaimAttribute as c ->
                                let v = if isNull c.Value then None else Some c.Value
                                if ctx.HasClaim (c.Claim, v) then None
                                else Some (sprintf "missing-claim: %s" c.Claim)
                            | :? TenantScopedAttribute ->
                                if ctx.HasTenant () then None
                                else Some "missing-tenant"
                            | _ -> None)
                    if List.isEmpty denials then Allow
                    else Deny (String.concat "; " denials)
