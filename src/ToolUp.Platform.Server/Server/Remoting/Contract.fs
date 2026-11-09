namespace ToolUp.Remoting.Server

open System
open System.Collections.Generic
open System.Reflection
open Microsoft.FSharp.Reflection

// =============================================================================
// ApiContract — the one description of an API record
// =============================================================================
//
// An API record is read ONCE, by `Contract.read`, into a value: one
// `MethodContract` per method, the synthesised companions of its long-running
// methods, its wire-schema table, and every refusal the record carries
// whatever is composed around it. Each seam reads its slice of a method by
// name; the dispatcher reads the contract, never the record. The generator
// emits the same value as data beside a generated invocation table, so a
// registered table means no reflective walk of the record at boot.
//
// `Contract.bind` then applies a composition's flags: it binds each route
// through the composed `RouteBuilder` and adds the refusals that depend on
// what was composed (an unclassified method under a resolver, a route that
// does not round-trip, a role-gated method left unaudited). Both are pure.

/// How a method answers.
[<RequireQualifiedAccess>]
type MethodShape =
    /// One value: `Async<'r>` (or any non-stream return).
    | Unary
    /// `'arg -> IAsyncEnumerable<'element>`, served as SSE frames.
    | Streaming of element: Type
    /// A function finally returning `Async<JobHandle<'T>>`, served with
    /// three companion routes.
    | LongRunning of companions: JobCompanionCalls

/// An audited method's emission: its kind and the input record its payload
/// redacts (`None` when the first argument is not a record).
[<RequireQualifiedAccess>]
type AuditContract = { Kind: AuditKind; Input: Type option }

/// Everything the dispatcher knows about one method of an API record.
[<RequireQualifiedAccess>]
type MethodContract = {
    /// The record field's name: the route's method segment.
    Name: string
    /// The route the composed `RouteBuilder` builds for the method.
    Route: string
    /// The field's type flattened through its curried chain, without the
    /// final range (`TypeInfo.flattenFuncTypes`).
    Arguments: Type list
    /// The final range of the flattened chain, e.g. `Async<'r>`.
    Returns: Type
    Shape: MethodShape
    /// The declared authorisation; `None` when the field carries no marker.
    Classification: Classified option
    /// `[<RateLimit>]` budgets, AND-composed.
    Budgets: RateBudget list
    Audit: AuditContract option
    Idempotent: bool
    /// The first argument's record type when it carries validation attributes.
    Validated: Type option
}

/// A companion route synthesised for a long-running method: `<m>/status` and
/// `<m>/cancel` answer once, `<m>/progress` streams.
[<RequireQualifiedAccess>]
type CompanionContract = {
    /// `<method>/<companion>`.
    Name: string
    Route: string
    /// The long-running method the companion serves.
    Of: string
    Kind: JobCompanion
    Shape: MethodShape
}

/// A compose-time refusal: the dispatcher refuses to start rather than serve
/// a record whose declared guards it cannot honour as written.
[<RequireQualifiedAccess>]
type ContractRefusal =
    /// A seam that reads record fields was composed over a non-record type.
    | NotARecord of api: string
    /// Authorisation markers whose simple name collides with a sanctioned one:
    /// `(field, renderedAttributeType)`.
    | ForeignMarker of api: string * collisions: (string * string) list
    /// Methods carrying no authorisation marker while a resolver is composed.
    | UnclassifiedMethods of api: string * methods: string list
    /// Methods whose route's last segment is not their name: `(field, key)`.
    | RouteDoesNotRoundTrip of api: string * divergences: (string * string) list
    /// Role-gated methods without `[<Audit>]` while the gate is composed.
    | RoleGatedUnaudited of api: string * methods: string list
    /// Pre-flight markers (audit, PII-safe, rate limit, idempotency) whose
    /// simple name collides with a sanctioned one: `(surface, subject, type)`.
    | MarkerCollision of api: string * collisions: (string * string * string) list
    /// Declared `[<Custom>]` validators the engine cannot build.
    | UnbuildableValidators of api: string * refusals: string list
    /// A wire-schema annotation that cannot be served as written.
    | InvalidSchemaVersioning of api: string * refusal: SchemaVersionRefusal
    /// Streaming methods carrying `[<Idempotent>]`: a stream has no response
    /// to memoise, so a retry with the same key could never be replayed.
    | IdempotentStreaming of api: string * methods: string list

/// The one description of an API record.
[<RequireQualifiedAccess>]
type ApiContract = {
    /// The record type's name.
    Name: string
    /// The record type's full name, `+` read as `.`.
    Key: string
    /// False for a type that is not an F# record (which has no methods).
    Record: bool
    /// One per record field, in declaration order.
    Methods: MethodContract list
    /// The companions of every long-running method, in method order.
    Companions: CompanionContract list
    /// The wire-schema table, keyed by logical method name.
    Schemas: Map<string, MethodSchema>
    /// The refusals the record carries whatever is composed around it, plus
    /// its foreign authorisation markers (refused only under a resolver).
    Findings: ContractRefusal list
}

/// What a composition contributes to reading a contract.
[<RequireQualifiedAccess>]
type ContractFlags = {
    /// An `AuthContextResolver` is composed: every method must be classified.
    AuthResolverComposed: bool
    /// A seam that reads record fields (auth, rate limit, audit, idempotency)
    /// is composed: the API type must be a record.
    RecordSeamsComposed: bool
    /// Every role-gated method must carry `[<Audit>]`.
    RequireAuditOnRoleGated: bool
    /// Builds a method's route from the record's name and the method's.
    RouteBuilder: string -> string -> string
}

[<RequireQualifiedAccess>]
module ContractFlags =

    /// The default route (`/<Record>/<method>`) and nothing composed.
    let defaults: ContractFlags = {
        ContractFlags.AuthResolverComposed = false
        ContractFlags.RecordSeamsComposed = false
        ContractFlags.RequireAuditOnRoleGated = false
        ContractFlags.RouteBuilder = sprintf "/%s/%s"
    }

    /// The flags a set of remoting options composes.
    let ofOptions (options: RemotingOptions<'ctx, 'impl>) : ContractFlags = {
        ContractFlags.AuthResolverComposed = options.AuthContextResolver.IsSome
        ContractFlags.RecordSeamsComposed =
            options.AuthContextResolver.IsSome
            || options.RateLimitStore.IsSome
            || options.AuditEmitter.IsSome
            || options.IdempotencyStore.IsSome
        ContractFlags.RequireAuditOnRoleGated = options.RequireAuditOnRoleGated
        ContractFlags.RouteBuilder = options.RouteBuilder
    }

[<RequireQualifiedAccess>]
module ContractRefusal =

    let private one (refusal: ContractRefusal) : string =
        match refusal with
        | ContractRefusal.NotARecord api ->
            sprintf
                "ToolUp.Remoting refused to start: API type '%s' is not an F# record, but at least one attribute-driven seam (authorisation / rate-limit / audit / idempotency / validation) was composed. These seams require record-field reflection. Convert '%s' to an F# record, or remove the seam composition."
                api
                api
        | ContractRefusal.ForeignMarker(api, collisions) ->
            sprintf
                "ToolUp.Remoting refused to start: API record '%s' has %d field(s) carrying an attribute whose name matches an authorisation marker but which is NOT one of the two sanctioned families: [%s]. Only ToolUp.Remoting.Server.* (server-tier) and ToolUp.Platform.* (tier-shared mirror) markers classify a method; an attribute of the same name from any other namespace or assembly is refused rather than honoured, because a name collision must never decide a method's authorisation. Replace it with the sanctioned attribute of the same name, or rename your own attribute. See docs/migrations/335-qualified-auth-attribute-matching.md."
                api
                collisions.Length
                (collisions
                 |> List.map (fun (field, attrType) -> sprintf "%s carries '%s'" field attrType)
                 |> String.concat "; ")
        | ContractRefusal.UnclassifiedMethods(api, methods) ->
            sprintf
                "ToolUp.Remoting refused to start: API record '%s' has %d unclassified method(s): [%s]. Phase 69d requires every API record field to carry one of [<RequiresRole>], [<RequiresClaim>], [<TenantScoped>], [<AllowAnonymous>], or [<PublicEndpoint>]. See ToolUp.Remoting README §69d."
                api
                methods.Length
                (String.concat "; " methods)
        | ContractRefusal.RouteDoesNotRoundTrip(api, divergences) ->
            sprintf
                "ToolUp.Remoting refused to start: API record '%s' has %d classified method(s) whose field name does not round-trip through the active RouteBuilder: [%s]. The dispatcher keys per-request authorisation by the trailing path segment of the built route; when that diverges from the field name the classification lookup misses and (Phase 132 deny-on-miss) denies every call. Use a RouteBuilder whose trailing segment equals the method name."
                api
                divergences.Length
                (divergences
                 |> List.map (fun (field, key) -> sprintf "%s → runtime-key '%s'" field key)
                 |> String.concat "; ")
        | ContractRefusal.RoleGatedUnaudited(api, methods) ->
            sprintf
                "ToolUp.Remoting refused to start: audit-required-on-role-gated is composed (TOOLUP_AUDIT_ADMIN_REQUIRED), but API record '%s' has %d role-gated method(s) without an [<Audit>] annotation: [%s]. Annotate each with [<Audit \"<kind>\">] (see ToolUp.Remoting README §69h), or unset the gate."
                api
                methods.Length
                (String.concat "; " methods)
        | ContractRefusal.MarkerCollision(api, collisions) ->
            sprintf
                "ToolUp.Remoting refused to start: API record '%s' has %d attribute(s) whose name matches a dispatch pre-flight marker but which are NOT one of the two sanctioned families: [%s]. Only ToolUp.Remoting.Server.* (server-tier) and ToolUp.Platform.* (tier-shared mirror) attributes arm rate limiting, idempotency, audit emission or PII-safe payload inclusion; an attribute of the same name from any other namespace or assembly is refused rather than honoured, because a name collision must never silently decide whether a guard is armed — in either direction. Replace it with the sanctioned attribute of the same name, or rename your own attribute. See docs/migrations/727-attribute-recognition-sweep.md."
                api
                collisions.Length
                (collisions
                 |> List.map (fun (surface, subject, attrType) ->
                     sprintf "%s [%s] carries '%s'" subject surface attrType)
                 |> String.concat "; ")
        | ContractRefusal.UnbuildableValidators(api, refusals) -> Validation.unbuildableRefusal api refusals
        | ContractRefusal.InvalidSchemaVersioning(api, refusal) -> SchemaVersion.refusalSentence api refusal
        | ContractRefusal.IdempotentStreaming(api, methods) ->
            sprintf
                "ToolUp.Remoting refused to start: API record '%s' has streaming method(s) carrying [<Idempotent>]: [%s]. A stream has no single response to memoise, so a retry with the same X-Idempotency-Key would run the producer again rather than replay it. Fix: drop [<Idempotent>], or move the method off the IAsyncEnumerable<'T> return shape."
                api
                (String.concat "; " methods)

    /// The compose-time message: one sentence per refusal, a line each.
    let render (refusals: ContractRefusal list) : string =
        refusals |> List.map one |> String.concat Environment.NewLine

[<RequireQualifiedAccess>]
module Contract =

    let private isAsync (t: Type) =
        t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<Async<_>>

    let private companionsFor (resultType: Type) : JobCompanionCalls =
        typeof<JobCompanionCalls>.GetMethod("For").MakeGenericMethod([| resultType |]).Invoke(null, [||])
        :?> JobCompanionCalls

    let private shapeOf (field: PropertyInfo) : MethodShape =
        match Streaming.shapeOf field, LongRunning.resultOf field with
        | Some(_, element), _ -> MethodShape.Streaming element
        | None, Some resultType -> MethodShape.LongRunning(companionsFor resultType)
        | None, None -> MethodShape.Unary

    let private methodOf (apiName: string) (field: PropertyInfo) : MethodContract =
        let flattened = TypeInfo.flattenFuncTypes field.PropertyType |> List.ofArray

        {
            MethodContract.Name = field.Name
            MethodContract.Route = ContractFlags.defaults.RouteBuilder apiName field.Name
            MethodContract.Arguments = List.take (flattened.Length - 1) flattened
            MethodContract.Returns = List.last flattened
            MethodContract.Shape = shapeOf field
            MethodContract.Classification = AuthClassifier.classifiedOf field
            MethodContract.Budgets = RateLimit.budgetsOf field
            Audit =
                Audit.kindOf field
                |> Option.map (fun kind -> {
                    AuditContract.Kind = kind
                    AuditContract.Input = Audit.inputOf field
                })
            MethodContract.Idempotent = Idempotency.isIdempotent field
            MethodContract.Validated = Validation.validatedInputOf field
        }

    let private companionsOf (m: MethodContract) : CompanionContract list =
        match m.Shape with
        | MethodShape.LongRunning calls -> [
            {
                CompanionContract.Name = m.Name + "/status"
                CompanionContract.Route = m.Route + "/status"
                CompanionContract.Of = m.Name
                CompanionContract.Kind = JobCompanion.Status
                CompanionContract.Shape = MethodShape.Unary
            }
            {
                CompanionContract.Name = m.Name + "/progress"
                CompanionContract.Route = m.Route + "/progress"
                CompanionContract.Of = m.Name
                CompanionContract.Kind = JobCompanion.Progress
                CompanionContract.Shape = MethodShape.Streaming calls.StatusType
            }
            {
                CompanionContract.Name = m.Name + "/cancel"
                CompanionContract.Route = m.Route + "/cancel"
                CompanionContract.Of = m.Name
                CompanionContract.Kind = JobCompanion.Cancel
                CompanionContract.Shape = MethodShape.Unary
            }
          ]
        | _ -> []

    let private nonEmpty (make: 'a list -> ContractRefusal) (items: 'a list) : ContractRefusal list =
        if List.isEmpty items then [] else [ make items ]

    /// Read an API record into its contract: the ONE reflective reader of an
    /// API record on the server. Routes are the default `/<Record>/<method>`;
    /// `bind` rebinds them to a composition's `RouteBuilder`.
    let read (apiType: Type) : ApiContract =
        let name = apiType.Name
        let fields = ApiRecord.fields apiType
        let methods = fields |> Array.map (methodOf name) |> List.ofArray
        let schemas, schemaRefusals = SchemaVersion.read fields

        let findings =
            List.concat [
                fields
                |> Array.toList
                |> List.collect (fun f -> AuthClassifier.foreignMarkersOf f |> List.map (fun r -> f.Name, r))
                |> nonEmpty (fun c -> ContractRefusal.ForeignMarker(name, c))
                fields
                |> Array.toList
                |> List.collect (fun f ->
                    Audit.foreignMarkersOf f
                    @ RateLimit.foreignMarkersOf f
                    @ Idempotency.foreignMarkersOf f)
                |> List.sortBy (fun (surface, _, _) ->
                    match surface with
                    | "audit emission" -> 0
                    | "PII-safe audit payload" -> 1
                    | "rate limiting" -> 2
                    | _ -> 3)
                |> nonEmpty (fun c -> ContractRefusal.MarkerCollision(name, c))
                methods
                |> List.collect (fun m ->
                    match m.Validated with
                    | Some input -> Validation.unbuildableOf m.Name input
                    | None -> [])
                |> nonEmpty (fun r -> ContractRefusal.UnbuildableValidators(name, r))
                schemaRefusals
                |> List.map (fun r -> ContractRefusal.InvalidSchemaVersioning(name, r))
                methods
                |> List.filter (fun m ->
                    m.Idempotent
                    && (match m.Shape with
                        | MethodShape.Streaming _ -> true
                        | _ -> false))
                |> List.map _.Name
                |> nonEmpty (fun ms -> ContractRefusal.IdempotentStreaming(name, ms))
            ]

        {
            ApiContract.Name = name
            ApiContract.Key = apiType.FullName.Replace('+', '.')
            ApiContract.Record = ApiRecord.isRecord apiType
            ApiContract.Methods = methods
            ApiContract.Companions = methods |> List.collect companionsOf
            ApiContract.Schemas = schemas
            ApiContract.Findings = findings
        }

    /// Apply a composition's flags to a contract: rebind every route through
    /// its `RouteBuilder`, and return every refusal the composition makes of
    /// the record, in the order the dispatcher has always reported them.
    let bind (flags: ContractFlags) (contract: ApiContract) : Result<ApiContract, ContractRefusal list> =
        if flags.RecordSeamsComposed && not contract.Record then
            Error [ ContractRefusal.NotARecord contract.Name ]
        else
            let routeOf methodName =
                flags.RouteBuilder contract.Name methodName

            let methods =
                contract.Methods |> List.map (fun m -> { m with Route = routeOf m.Name })

            let bound = {
                contract with
                    Methods = methods
                    Companions = methods |> List.collect companionsOf
            }

            let foreignAuth, alwaysRefused =
                contract.Findings
                |> List.partition (function
                    | ContractRefusal.ForeignMarker _ -> true
                    | _ -> false)

            let authRefusals =
                if not flags.AuthResolverComposed then
                    []
                elif not (List.isEmpty foreignAuth) then
                    // The specific diagnostic wins: a field whose marker is
                    // foreign is reported as such, not as unclassified.
                    foreignAuth
                else
                    List.concat [
                        methods
                        |> List.filter (fun m -> m.Classification.IsNone)
                        |> List.map _.Name
                        |> nonEmpty (fun ms -> ContractRefusal.UnclassifiedMethods(contract.Name, ms))
                        methods
                        |> List.choose (fun m ->
                            let key = Route.methodSegment m.Route
                            if key = m.Name then None else Some(m.Name, key))
                        |> nonEmpty (fun d -> ContractRefusal.RouteDoesNotRoundTrip(contract.Name, d))
                    ]

            let auditRefusals =
                if not flags.RequireAuditOnRoleGated then
                    []
                else
                    methods
                    |> List.filter (fun m ->
                        m.Audit.IsNone
                        && (match m.Classification with
                            | Some(Classified.Requires requirements) ->
                                requirements
                                |> List.exists (function
                                    | RoleRequired _ -> true
                                    | _ -> false)
                            | _ -> false))
                    |> List.map _.Name
                    |> nonEmpty (fun ms -> ContractRefusal.RoleGatedUnaudited(contract.Name, ms))

            match authRefusals @ auditRefusals @ alwaysRefused with
            | [] -> Ok bound
            | refusals -> Error refusals

    /// Read and bind in one step: `read`, then `bind flags`.
    let ofType (flags: ContractFlags) (apiType: Type) : Result<ApiContract, ContractRefusal list> =
        read apiType |> bind flags

    /// The routes a batch envelope may never name: every streaming and
    /// long-running method's, and every companion's.
    let refusedBatchRoutes (contract: ApiContract) : string list = [
        for m in contract.Methods do
            match m.Shape with
            | MethodShape.Streaming _
            | MethodShape.LongRunning _ -> m.Route
            | MethodShape.Unary -> ()
        for c in contract.Companions do
            c.Route
    ]

    /// Every name a caller can address on the record: each field under its
    /// own name and each versioned method under its logical name.
    let addressableMethods (contract: ApiContract) : Set<string> =
        SchemaVersion.addressable (contract.Methods |> List.map _.Name) contract.Schemas