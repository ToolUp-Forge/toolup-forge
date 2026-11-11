module ToolUp.AI.PlatformAITools

open System
open System.Text
open System.Text.Json
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.Infra.IEntityStore
open ToolUp.Platform.EntityTypes
open ToolUp.Platform.EntityQueryTypes
open ToolUp.AI
open ToolUp.AI.AIToolRegistry
open ToolUp.AI.ToolHelpers
open DataManagementTypes
open ToolUp.Platform.Infra
open ToolUp.Platform.Jobs

/// Built-in cross-module AI tool family (`_platform.ai.*`): six SDK tools,
/// registered by `composeAI`, that wrap existing platform substrate so a model
/// can discover modules, enumerate data types, query module handlers and
/// entity stores, and read result blobs — each subject to the RBAC the
/// substrate already enforces.
///
/// Every tool carries `SourceModule = "_platform.ai"`, an SDK-reserved source
/// the per-source filter admits. The four reach tools then run the one gate
/// sequence (`Gates.admit`) over the TARGET module they read: the caller's
/// Read and live grant (rendered alike as `PermissionDenied`, so the model is
/// never told which stopped it), the module's AI-queryability opt-in
/// (`UnqueryableModule`, reported after them because it is not fixable by any
/// grant), and the user's consent last (`UserDenied`), so an outer refusal
/// never shows a dialog. The opt-in is off unless the module declares it
/// (`ServerModule.withAIExposure`); see
/// `docs/migrations/per-module-ai-queryability.md`.
///
/// Each tool is a typed executor over a declared argument schema, decoded by
/// `ToolHelpers.decodeArg`: a malformed argument is a fault naming its path.

let private permToken =
    function
    | ModulePermission.Read -> "Read"
    | ModulePermission.Write -> "Write"
    | ModulePermission.Admin -> "Admin"
    | ModulePermission.SchemaOnly -> "SchemaOnly"

/// A malformed argument, as the fault the model is shown: the path, what the
/// schema declared there, and what arrived.
let private invalid (toolName: string) (refusal: ToolArgumentRefusal) : ToolOutcome =
    ToolOutcome.Faulted(ToolFault.InvalidArguments(toolName, ToolArgumentRefusal.toMessage refusal))

/// The `UnqueryableModule` refusal, in one place so all four reach tools
/// render it identically and a consumer can match on one shape.
let private unqueryableModule (moduleName: string) (detail: string) : string =
    fableSerialize {|
        error = "UnqueryableModule"
        targetModule = moduleName
        message = detail
    |}

/// A reach refusal as the body the model reads. The caller-side pair renders
/// as one `PermissionDenied`; the opt-in as `UnqueryableModule`, a remedy no
/// grant reaches; the user's refusal as `UserDenied`, a fourth remedy.
let private rendered (refusal: ToolRefusal) : string =
    match refusal with
    | ToolRefusal.Unreadable(_, moduleName)
    | ToolRefusal.GrantNotLive(_, moduleName) ->
        fableSerialize {|
            error = "PermissionDenied"
            targetModule = moduleName
        |}
    | ToolRefusal.Unqueryable(_, moduleName) -> unqueryableModule moduleName (ToolRefusal.notOptedInMessage moduleName)
    | ToolRefusal.ConsentRefused(_, moduleName, reason) ->
        fableSerialize {|
            error = "UserDenied"
            targetModule = moduleName
            message = reason
        |}
    | refusal ->
        fableSerialize {|
            error = "Denied"
            message = ToolRefusal.reason refusal
        |}

/// The calls a reach of `targets` makes, one per target module.
let private callsTo (context: ToolContext) (def: AIToolDefinition) (args: ToolArgs) (targets: string list) =
    targets
    |> List.map (fun target -> {
        Request = context.Request
        Access = context.Access
        Tool = def
        Arguments = args.Json
        Module = target
    })

/// The reach door: the gate sequence over the TARGET modules. The tool gate
/// asks for the caller's Read and live grant on each target (the ceiling was
/// the loop door's, for this same invocation, so it is not asked again, and
/// nor are the allowlist and approval); then the opt-in; then consent.
let private reachGates (context: ToolContext) (queryKey: string) : GateSet = {
    ToolGate = Gates.toolGate (moduleGrantGate context.Request) ToolPolicy.unrestricted
    Allowlist = Gates.pass
    Approval = Gates.pass
    Queryability = Gates.queryability
    Consent = AIConsentDispatch.gate context.Channel queryKey
}

/// Admit a reach of `target`, or the body its refusal renders.
let private reach
    (context: ToolContext)
    (def: AIToolDefinition)
    (args: ToolArgs)
    (target: string)
    (queryKey: string)
    : Async<Result<unit, string>> =
    async {
        match! Gates.admitAll (reachGates context queryKey) (callsTo context def args [ target ]) with
        | Ok _ -> return Ok()
        | Error refusal -> return Error(rendered refusal)
    }

/// A required text argument.
let private requiredText (args: ToolArgs) (name: string) : Result<string, ToolArgumentRefusal> =
    ToolHelpers.decodeArg<string> args.Root name ToolSchema.text

/// An optional text argument; blank reads as absent.
let private optionalText (args: ToolArgs) (name: string) : Result<string option, ToolArgumentRefusal> =
    ToolHelpers.decodeOptionalArg<string> args.Root name ToolSchema.text
    |> Result.map (Option.filter (String.IsNullOrWhiteSpace >> not))

/// An argument the schema admits as either a JSON string or an inline JSON
/// value (`payloadJson`, `predicateJson`): its text, or `None` when absent.
let private jsonText (args: ToolArgs) (name: string) : string option =
    match args.Root.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
    | true, v when v.ValueKind = JsonValueKind.Null -> None
    | true, v -> Some(v.GetRawText())
    | _ -> None

let private dataObjectErrorText (err: DataObjectError) : string =
    match err with
    | DataObjectError.NotFound -> "NotFound"
    | DataObjectError.VersionNotFound v -> sprintf "VersionNotFound %d" v
    | DataObjectError.DeleteForbidden -> "DeleteForbidden"
    | DataObjectError.PolicyMismatch(expected, supplied) ->
        sprintf "PolicyMismatch (expected %A, supplied %A)" expected supplied
    | DataObjectError.StorageFailure msg -> sprintf "StorageFailure: %s" msg

/// An ISO-8601 instant, read as UTC; `None` for text that is not one.
let private instant (text: string) : DateTime option =
    match
        DateTime.TryParse(
            text,
            Globalization.CultureInfo.InvariantCulture,
            Globalization.DateTimeStyles.AdjustToUniversal
            ||| Globalization.DateTimeStyles.AssumeUniversal
        )
    with
    | true, dt -> Some dt
    | _ -> None

// ─── _platform.ai.list_accessible_modules ────────────────────────

let private listAccessibleModulesDef: AIToolDefinition = {
    Name = "_platform.ai.list_accessible_modules"
    Description =
        "List the modules in this deployment the current user may read, each with the permission grants they hold. Use this first to discover what cross-module data is reachable before calling list_data_types / query_module / list_results. `rbacConfigured` is false when the deployment hasn't configured per-module permissions — in that case every module is accessible and each `permissions` list reads `[\"unrestricted\"]`. Module discovery is sourced from the data catalogue (data-producing modules) plus any module named in the user's permission map. Scoped to the current user/team — modules the user can't read are omitted. `queryable` is false for a module that has not opted into the cross-module AI surface: it exists and the user may read it in its own UI, but list_data_types omits its data and query_module / query_entity / list_results / get_latest_result return `{\"error\":\"UnqueryableModule\"}` against it. Query only modules reporting `queryable: true`."
    Parameters = []
    SourceModule = "_platform.ai"
    EmitsActions = None
    Location = ServerResident
    Surface = Both
    IsLiveInterface = false
    ResultBudget = DefaultResultBudget
    Effects = ToolEffectDeclaration.readContent
}

let private executeListModules (context: ToolContext) (_: ToolArgs) : Async<ToolOutcome> = async {
    let ctx = context.Request
    let access = context.Access

    let! catalogModules = async {
        match ctx.RequestServices.GetService(typeof<IDataCatalog>) with
        | :? IDataCatalog as catalog ->
            let! types = catalog.ListTypes()

            let! producerLists = types |> List.map (fun t -> catalog.GetProducers t.Id) |> Async.Parallel

            return producerLists |> Array.collect List.toArray |> Array.toList |> List.distinct
        | _ -> return []
    }

    let allModules =
        (catalogModules
         @ (match access.ModulePermissions with
            | ModulePermissions.Restricted map -> map |> Map.toList |> List.map fst
            | ModulePermissions.Unrestricted -> []))
        |> List.distinct
        |> List.sort

    let isGrantLive = moduleGrantGate ctx
    let isQueryable = moduleAIQueryGate ctx

    let entries =
        allModules
        // A listing filters silently (nothing was attempted), exactly as the
        // tool-registry list filter does.
        |> List.filter (fun m -> AccessContext.canAccessModule m access && isGrantLive m)
        |> List.map (fun m ->
            let perms =
                match access.ModulePermissions with
                | ModulePermissions.Restricted map ->
                    match map.TryFind m with
                    | Some ps -> ps |> List.map permToken
                    | None -> []
                | ModulePermissions.Unrestricted -> [ "unrestricted" ]

            {|
                moduleName = m
                permissions = perms
                // Annotated rather than filtered: a module the caller may
                // read but which has not opted in still exists, and naming
                // it lets the model route the user to its own UI instead of
                // planning around data it will never get.
                queryable = isQueryable m
            |})

    return
        ToolOutcome.Produced(
            fableSerialize {|
                rbacConfigured =
                    match access.ModulePermissions with
                    | ModulePermissions.Restricted _ -> true
                    | ModulePermissions.Unrestricted -> false
                modules = entries
            |}
        )
}

// ─── _platform.ai.list_data_types ────────────────────────────────

let private listDataTypesDef: AIToolDefinition = {
    Name = "_platform.ai.list_data_types"
    Description =
        "Enumerate the data types stored in this deployment that the current user can read, with their producing module(s) and (when published) schema. Each entry pairs a data-type id with the accessible modules that produce it — use the id with query_entity (for entity-store types) or the module name with query_module / list_results. Only types with at least one producer module that is BOTH accessible to the user and opted into the cross-module AI surface (`queryable: true` in list_accessible_modules) are returned; a type whose every producer is out of that surface is omitted entirely. `hasSchema` plus `schemaDescription` flag whether a column schema is available; fetch finer column detail through the producing module when needed."
    Parameters = []
    SourceModule = "_platform.ai"
    EmitsActions = None
    Location = ServerResident
    Surface = Both
    IsLiveInterface = false
    ResultBudget = DefaultResultBudget
    Effects = ToolEffectDeclaration.readContent
}

let private executeListDataTypes (context: ToolContext) (_: ToolArgs) : Async<ToolOutcome> = async {
    let ctx = context.Request
    let access = context.Access
    let isGrantLive = moduleGrantGate ctx
    // The opt-in FILTERS here: a type is listed only when one of its
    // producers is both reachable by this caller and on the AI surface.
    let isQueryable = moduleAIQueryGate ctx

    match ctx.RequestServices.GetService(typeof<IDataCatalog>) with
    | :? IDataCatalog as catalog ->
        let! types = catalog.ListTypes()

        let! entries =
            types
            |> List.map (fun t -> async {
                let! producers = catalog.GetProducers t.Id

                let accessible =
                    producers
                    |> List.filter (fun p -> AccessContext.canAccessModule p access && isGrantLive p && isQueryable p)

                return t, accessible
            })
            |> Async.Parallel

        let result =
            entries
            |> Array.filter (fun (_, accessible) -> not (List.isEmpty accessible))
            |> Array.map (fun (t, accessible) -> {|
                id = t.Id
                displayName = t.DisplayName
                producers = accessible
                hasSchema = t.Schema.IsSome
                schemaDescription = t.Schema |> Option.map _.Description
            |})
            |> Array.toList

        return ToolOutcome.Produced(fableSerialize {| dataTypes = result |})
    | _ ->
        return
            ToolOutcome.Produced(
                fableSerialize {|
                    dataTypes = ([]: obj list)
                    note = "no data catalog registered in this deployment"
                |}
            )
}

// ─── _platform.ai.query_module ───────────────────────────────────

let private queryModuleDef: AIToolDefinition = {
    Name = "_platform.ai.query_module"
    Description =
        "Call a registered module-to-module query handler by name and key, passing a JSON payload. Thin shell over the platform query bus, which enforces the caller's per-module Read permission: targeting a module the user can't read returns `{\"error\":\"PermissionDenied\"}`. A module that has not opted into the cross-module AI surface returns `{\"error\":\"UnqueryableModule\"}` — a deployment-level declaration no permission grant can change, so do not retry it. A module not present in this deployment returns `{\"error\":\"ModuleNotFound\"}`; an unknown queryKey returns `{\"error\":\"NoHandler\"}`. On success returns `{ targetModule, queryKey, result }` where `result` is the handler's own JSON response. Discover module names + their query keys via list_accessible_modules and the module's documentation."
    Parameters = [
        ToolSchema.parameter
            "moduleName"
            "The target module's name (as listed by list_accessible_modules)."
            true
            ToolSchema.text
        ToolSchema.parameter
            "queryKey"
            "The module-declared query discriminator, e.g. \"latest-analysis\"."
            true
            ToolSchema.text
        {
            Name = "payloadJson"
            Type = "string"
            Description =
                "JSON-encoded request payload the handler expects. Pass an empty string for handlers that take no input. May also be supplied as an inline JSON object."
            Required = false
            Default = Some "\"\""
        }
    ]
    SourceModule = "_platform.ai"
    EmitsActions = None
    Location = ServerResident
    Surface = Both
    IsLiveInterface = false
    ResultBudget = DefaultResultBudget
    Effects = ToolEffectDeclaration.readFacts
}

let private executeQueryModule (context: ToolContext) (args: ToolArgs) : Async<ToolOutcome> = async {
    match requiredText args "moduleName", requiredText args "queryKey" with
    | Error refusal, _
    | _, Error refusal -> return invalid queryModuleDef.Name refusal
    | Ok moduleName, Ok queryKey ->
        let payload = jsonText args "payloadJson" |> Option.defaultValue ""

        match! reach context queryModuleDef args moduleName queryKey with
        | Error body -> return ToolOutcome.Produced body
        | Ok() ->
            match context.Request.RequestServices.GetService(typeof<IModuleQueryBus>) with
            | :? IModuleQueryBus as bus ->
                let! outcome =
                    bus.Ask(
                        context.Access,
                        {
                            TargetModule = moduleName
                            QueryKey = queryKey
                            Payload = payload
                        }
                    )

                match outcome with
                | None ->
                    return
                        ToolOutcome.Produced(
                            fableSerialize {|
                                error = "ModuleNotFound"
                                targetModule = moduleName
                                message = sprintf "Module '%s' is not registered in this deployment." moduleName
                            |}
                        )
                | Some(Error(PermissionDenied m)) ->
                    return
                        ToolOutcome.Produced(
                            fableSerialize {|
                                error = "PermissionDenied"
                                targetModule = m
                            |}
                        )
                | Some(Error(NoHandler(m, q))) ->
                    return
                        ToolOutcome.Produced(
                            fableSerialize {|
                                error = "NoHandler"
                                targetModule = m
                                queryKey = q
                            |}
                        )
                | Some(Error(HandlerFailed msg)) ->
                    return
                        ToolOutcome.Produced(
                            fableSerialize {|
                                error = "HandlerFailed"
                                message = msg
                            |}
                        )
                | Some(Ok resp) ->
                    // The handler's payload is JSON already; read it back as a
                    // value so the wrapper serialises it structured.
                    let resultElement =
                        fableDeserialize<JsonElement> (
                            if String.IsNullOrWhiteSpace resp.Payload then
                                "null"
                            else
                                resp.Payload
                        )

                    let! gated =
                        gateModuleToolResult
                            context.Request
                            queryModuleDef.Name
                            moduleName
                            (fableSerialize {|
                                targetModule = moduleName
                                queryKey = queryKey
                                result = resultElement
                            |})

                    return ToolOutcome.Produced gated
            | _ ->
                return
                    ToolOutcome.Produced(
                        fableSerialize {|
                            error = "ModuleQueryBusUnavailable"
                        |}
                    )
}

// ─── _platform.ai.query_entity ───────────────────────────────────

/// The predicate grammar's operators, declared: a node's `op` is one of these.
let private predicateOps = [ "eq"; "ne"; "gt"; "gte"; "lt"; "lte"; "in"; "and"; "or"; "not" ]

/// One predicate node, as declared. Its children (`left`, `right`, `inner`)
/// are nodes of the same shape, decoded by recursion.
let private predicateNode =
    ToolSchema.objectOf [
        ToolSchema.field "op" "The operator." true (ToolSchema.enumOf predicateOps)
        ToolSchema.field "field" "The indexed field the comparison tests." false ToolSchema.text
        ToolSchema.field "values" "The values `in` tests against." false (ToolSchema.arrayOf ToolSchema.text)
    ]

/// A comparison operand as text: a string verbatim, any other JSON value as
/// written.
let private operandText (v: JsonElement) : string =
    if v.ValueKind = JsonValueKind.String then
        v.GetString()
    else
        v.GetRawText()

/// Decode one predicate node into the typed `Predicate` AST. The shape is
/// simpler than the `FableConverters` DU form so the model can author it:
///   {"op":"eq","field":"Mood","value":"happy"}
///   {"op":"in","field":"Mood","values":["happy","calm"]}
///   {"op":"and","left":{...},"right":{...}}
///   {"op":"not","inner":{...}}
let rec private decodePredicate (path: string) (el: JsonElement) : Result<Predicate, string> =
    let at (name: string) =
        if path = "" then name else path + "." + name

    match ToolHelpers.validateSchema predicateNode el with
    | Error refusal -> Error(ToolArgumentRefusal.toMessage { refusal with Path = at refusal.Path })
    | Ok() ->
        let op = el.GetProperty("op").GetString()

        let fieldKey, valueKey, valuesKey = "field", "value", "values"

        let child (name: string) =
            match el.TryGetProperty name with
            | true, node -> decodePredicate (at name) node
            | _ -> Error $"'{op}' requires a '{name}' predicate node (at {at name})"

        let field () =
            match el.TryGetProperty "field" with
            | true, v when v.ValueKind = JsonValueKind.String -> Ok(v.GetString())
            | _ -> Error $"'{op}' requires 'field' (at {at fieldKey})"

        match op with
        | "eq"
        | "ne"
        | "gt"
        | "gte"
        | "lt"
        | "lte" ->
            match field (), el.TryGetProperty "value" with
            | Ok f, (true, v) ->
                let v = operandText v

                Ok(
                    match op with
                    | "eq" -> Eq(f, v)
                    | "ne" -> Ne(f, v)
                    | "gt" -> Gt(f, v)
                    | "gte" -> Gte(f, v)
                    | "lt" -> Lt(f, v)
                    | _ -> Lte(f, v)
                )
            | Error e, _ -> Error e
            | _, _ -> Error $"'{op}' requires 'value' (at {at valueKey})"
        | "in" ->
            match field (), el.TryGetProperty "values" with
            | Ok f, (true, vs) -> Ok(In(f, [ for item in vs.EnumerateArray() -> operandText item ]))
            | Error e, _ -> Error e
            | _, _ -> Error $"'in' requires a 'values' array (at {at valuesKey})"
        | "and"
        | "or" ->
            match child "left", child "right" with
            | Ok l, Ok r -> Ok(if op = "and" then And(l, r) else Or(l, r))
            | Error e, _
            | _, Error e -> Error e
        | _ -> child "inner" |> Result.map Not

/// `predicateJson`, which the model may pass as a JSON string or inline.
let private decodePredicateArg (args: ToolArgs) : Result<Predicate option, string> =
    match jsonText args "predicateJson" with
    | None -> Ok None
    | Some text when String.IsNullOrWhiteSpace text -> Ok None
    | Some text ->
        try
            use doc = JsonDocument.Parse text
            decodePredicate "predicateJson" doc.RootElement |> Result.map Some
        with ex ->
            Error(sprintf "predicateJson is not valid JSON: %s" ex.Message)

let private queryEntityDef: AIToolDefinition = {
    Name = "_platform.ai.query_entity"
    Description =
        "Query the typed entity store for the current user's scope by entity type and an optional predicate over declared indexes. Predicates reference declared index fields only; a non-indexed field returns an InvalidIndex error. Predicate JSON shape: {\"op\":\"eq\",\"field\":\"Mood\",\"value\":\"happy\"}; ops are eq/ne/gt/gte/lt/lte (string-ordered), in ({\"op\":\"in\",\"field\":\"x\",\"values\":[...]}), and/or ({\"op\":\"and\",\"left\":{...},\"right\":{...}}), not ({\"op\":\"not\",\"inner\":{...}}). Returns up to `take` (max 100) entity records. Scope-isolated — only the caller's own entities are visible. An entity type produced only by modules outside the cross-module AI surface returns `{\"error\":\"UnqueryableModule\"}`; no grant fixes that, so do not retry it. Discover entity-type ids via list_data_types, which already lists only queryable producers."
    Parameters = [
        ToolSchema.parameter
            "entityType"
            "The entity type to query, e.g. \"Mood\" or \"TrainingDay\"."
            true
            ToolSchema.text
        {
            Name = "predicateJson"
            Type = "string"
            Description =
                "Optional predicate as a JSON string (see the tool description for the shape). Omit or pass an empty string to return all entities of the type (subject to `take`)."
            Required = false
            Default = None
        }
        ToolSchema.parameter
            "take"
            "Maximum number of records to return (default 100, hard-capped at 100)."
            false
            ToolSchema.integer
    ]
    SourceModule = "_platform.ai"
    EmitsActions = None
    Location = ServerResident
    Surface = Both
    IsLiveInterface = false
    ResultBudget = DefaultResultBudget
    Effects = ToolEffectDeclaration.readFacts
}

/// Every field a predicate tests.
let rec private predicateFields (predicate: Predicate) : string list =
    match predicate with
    | Eq(f, _)
    | Ne(f, _)
    | Gt(f, _)
    | Gte(f, _)
    | Lt(f, _)
    | Lte(f, _)
    | In(f, _)
    | FullText(f, _) -> [ f ]
    | And(l, r)
    | Or(l, r) -> predicateFields l @ predicateFields r
    | Not inner -> predicateFields inner

/// A filter on a field the model may not read answers questions
/// about its value, row by row, so it is refused like the read: through the
/// gate, audited, and naming the field.
let private classifiedPredicateFields
    (ctx: HttpContext)
    (entityType: string)
    (predicate: Predicate option)
    : Async<string option> =
    async {
        let fields = predicate |> Option.map predicateFields |> Option.defaultValue []

        if List.isEmpty fields then
            return None
        else
            let probe = fields |> List.map (fun f -> f, "") |> dict |> JsonSerializer.Serialize
            let! gated = gateToolResult ctx queryEntityDef.Name [ entityType ] probe

            if gated = probe then
                return None
            else
                return
                    Some(
                        fableSerialize {|
                            error = "ClassifiedFieldInPredicate"
                            entityType = entityType
                            message =
                                "This predicate tests a classified field the deployment keeps from the model. Query without it; asking again returns the same."
                        |}
                    )
    }

/// The modules an entity type's data comes from: its catalogued producers.
/// An entity registration carries no module, so the catalogue is the only
/// attribution an entity type has; a type with none is reached ungated by
/// module — the documented attribution limit.
let private producersOf (ctx: HttpContext) (entityType: string) : Async<string list> = async {
    match ctx.RequestServices.GetService(typeof<IDataCatalog>) with
    | :? IDataCatalog as catalog ->
        let! producers = catalog.GetProducers entityType
        return producers |> List.distinct
    | _ -> return []
}

let private executeQueryEntity (context: ToolContext) (args: ToolArgs) : Async<ToolOutcome> = async {
    let ctx = context.Request

    match requiredText args "entityType", ToolHelpers.decodeOptionalArg<int> args.Root "take" ToolSchema.integer with
    | Error refusal, _
    | _, Error refusal -> return invalid queryEntityDef.Name refusal
    | Ok entityType, Ok take ->
        match decodePredicateArg args with
        | Error predErr ->
            return
                ToolOutcome.Produced(
                    fableSerialize {|
                        error = "InvalidPredicate"
                        message = predErr
                    |}
                )
        | Ok wherePred ->
            // The read returns data from every producer, so the gates ask
            // about every producer: the caller's Read and live grant on each,
            // then the opt-in (refused only when NO producer opted in — an
            // opted-in module's entities stay reachable), then consent from
            // the producers that are on the AI surface.
            let! producers = producersOf ctx entityType
            let isQueryable = moduleAIQueryGate ctx

            let gates = {
                reachGates context entityType with
                    Queryability =
                        fun call -> async {
                            if producers |> List.exists isQueryable then
                                return Ok()
                            else
                                return Error(ToolRefusal.Unqueryable(call.Tool.Name, String.Join(", ", producers)))
                        }
                    Consent =
                        fun call ->
                            if isQueryable call.Module then
                                AIConsentDispatch.gate context.Channel entityType call
                            else
                                Gates.pass call
            }

            match! Gates.admitAll gates (callsTo context queryEntityDef args producers) with
            | Error(ToolRefusal.Unqueryable(_, producerNames)) ->
                return
                    ToolOutcome.Produced(
                        unqueryableModule
                            producerNames
                            (sprintf
                                "Entity type '%s' is produced only by module(s) that have not opted into the cross-module AI surface (%s), so it cannot be read through the _platform.ai.* tools. This is a deployment-level declaration, not a permission the current user can be granted — do not retry. Use list_accessible_modules to see which modules report queryable: true."
                                entityType
                                producerNames)
                    )
            | Error refusal -> return ToolOutcome.Produced(rendered refusal)
            | Ok _ ->
                match ctx.RequestServices.GetService(typeof<IEntityStore>) with
                | :? IEntityStore as store ->
                    let query: EntityQuery<JsonElement> = {
                        EntityType = entityType
                        Where = wherePred
                        OrderBy = None
                        Skip = 0
                        Take = min (max (Option.defaultValue 100 take) 1) 100
                    }

                    let! refusedPredicate = classifiedPredicateFields ctx entityType wherePred

                    match refusedPredicate with
                    | Some body -> return ToolOutcome.Produced body
                    | None ->
                        match! store.Query<JsonElement>(context.Scope.ScopeId, query) with
                        | Ok entities ->
                            let! gated =
                                gateToolResult
                                    ctx
                                    queryEntityDef.Name
                                    [ entityType ]
                                    (fableSerialize {|
                                        entityType = entityType
                                        count = List.length entities
                                        entities = entities
                                    |})

                            return ToolOutcome.Produced gated
                        | Error err ->
                            return
                                ToolOutcome.Produced(
                                    fableSerialize {|
                                        error = "EntityQueryFailed"
                                        entityType = entityType
                                        message = EntityError.message err
                                    |}
                                )
                | _ ->
                    return
                        ToolOutcome.Produced(
                            fableSerialize {|
                                error = "EntityStoreUnavailable"
                                message = "No entity store is registered in this deployment."
                            |}
                        )
}

// ─── _platform.ai.list_results ───────────────────────────────────

let private listResultsDef: AIToolDefinition = {
    Name = "_platform.ai.list_results"
    Description =
        "List the persisted analytical results a module has produced in the current user's scope, newest first. Enforces the caller's Read permission on the named module — a module the user can't read returns `{\"error\":\"PermissionDenied\"}` — and the module's AI-queryability opt-in: a module outside the cross-module AI surface returns `{\"error\":\"UnqueryableModule\"}`, which no grant fixes. Each row carries objectId, resultType, version, createdAt, createdBy and dataType (metadata only — fetch a result's content with get_latest_result). Optional dateFrom/dateTo (ISO-8601) filter by creation time; supply both to bound a range. Returns `{\"error\":\"ResultStoreUnavailable\"}` when the deployment has no result store configured."
    Parameters = [
        ToolSchema.parameter
            "moduleName"
            "The module whose results to list (as listed by list_accessible_modules)."
            true
            ToolSchema.text
        ToolSchema.parameter
            "dateFrom"
            "Optional inclusive lower bound on creation time (ISO-8601). Supply together with dateTo."
            false
            ToolSchema.text
        ToolSchema.parameter
            "dateTo"
            "Optional inclusive upper bound on creation time (ISO-8601). Supply together with dateFrom."
            false
            ToolSchema.text
    ]
    SourceModule = "_platform.ai"
    EmitsActions = None
    Location = ServerResident
    Surface = Both
    IsLiveInterface = false
    ResultBudget = DefaultResultBudget
    Effects = ToolEffectDeclaration.readFacts
}

let private resultTypeOf (moduleName: string) (objectId: string) : string =
    let prefix = ResultObjectId.modulePrefix moduleName

    if objectId.StartsWith prefix then
        objectId.Substring prefix.Length
    else
        objectId

let private executeListResults (context: ToolContext) (args: ToolArgs) : Async<ToolOutcome> = async {
    match requiredText args "moduleName", optionalText args "dateFrom", optionalText args "dateTo" with
    | Error refusal, _, _
    | _, Error refusal, _
    | _, _, Error refusal -> return invalid listResultsDef.Name refusal
    | Ok moduleName, Ok dateFrom, Ok dateTo ->
        match! reach context listResultsDef args moduleName "" with
        | Error body -> return ToolOutcome.Produced body
        | Ok() ->
            match context.Request.RequestServices.GetService(typeof<IResultStore>) with
            | :? IResultStore as store ->
                let dateRange =
                    match dateFrom |> Option.bind instant, dateTo |> Option.bind instant with
                    | Some f, Some t -> Some(f, t)
                    | _ -> None

                let! results = store.ListResults(context.Scope.ScopeId, moduleName, dateRange)

                let rows =
                    results
                    |> List.map (fun o -> {|
                        objectId = o.ObjectId
                        resultType = resultTypeOf moduleName o.ObjectId
                        version = o.Version
                        createdAt = o.CreatedAt
                        createdBy = o.CreatedBy
                        dataType = o.DataType
                    |})

                return
                    ToolOutcome.Produced(
                        fableSerialize {|
                            targetModule = moduleName
                            results = rows
                        |}
                    )
            | _ ->
                return
                    ToolOutcome.Produced(
                        fableSerialize {|
                            error = "ResultStoreUnavailable"
                            message = "No result store is configured in this deployment."
                        |}
                    )
}

// ─── _platform.ai.get_latest_result ──────────────────────────────

[<Literal>]
let private ContentCharCap = 1_000_000

let private getLatestResultDef: AIToolDefinition = {
    Name = "_platform.ai.get_latest_result"
    Description =
        "Fetch the latest version of a single persisted result by module + resultType, returning its content (decoded as UTF-8 text — typically JSON) plus version metadata. Enforces the caller's Read permission on the module — a module the user can't read returns `{\"error\":\"PermissionDenied\"}` — and the module's AI-queryability opt-in: a module outside the cross-module AI surface returns `{\"error\":\"UnqueryableModule\"}`, which no grant fixes. Returns `{\"error\":\"NotFound\"}` when no result of that type has been saved in the scope. Content is capped at ~1MB (`truncated` flags when the body was cut). Discover available resultTypes for a module via list_results."
    Parameters = [
        ToolSchema.parameter "moduleName" "The module that produced the result." true ToolSchema.text
        ToolSchema.parameter
            "resultType"
            "The result-type discriminator (as listed by list_results, e.g. \"q1-sales\")."
            true
            ToolSchema.text
    ]
    SourceModule = "_platform.ai"
    EmitsActions = None
    Location = ServerResident
    Surface = Both
    IsLiveInterface = false
    ResultBudget = DefaultResultBudget
    Effects = ToolEffectDeclaration.readFacts
}

let private executeGetLatestResult (context: ToolContext) (args: ToolArgs) : Async<ToolOutcome> = async {
    match requiredText args "moduleName", requiredText args "resultType" with
    | Error refusal, _
    | _, Error refusal -> return invalid getLatestResultDef.Name refusal
    | Ok moduleName, Ok resultType ->
        match! reach context getLatestResultDef args moduleName resultType with
        | Error body -> return ToolOutcome.Produced body
        | Ok() ->
            match context.Request.RequestServices.GetService(typeof<IResultStore>) with
            | :? IResultStore as store ->
                match! store.GetLatest(context.Scope.ScopeId, moduleName, resultType) with
                | Ok(meta, bytes) ->
                    // Gated before the cap: a cut document no longer parses,
                    // and an unparsed result is not gated.
                    let! decoded =
                        gateModuleToolResult
                            context.Request
                            getLatestResultDef.Name
                            moduleName
                            (Encoding.UTF8.GetString bytes)

                    let truncated = decoded.Length > ContentCharCap

                    let content =
                        if truncated then
                            decoded.Substring(0, ContentCharCap)
                        else
                            decoded

                    return
                        ToolOutcome.Produced(
                            fableSerialize {|
                                targetModule = moduleName
                                resultType = resultType
                                version = meta.Version
                                createdAt = meta.CreatedAt
                                createdBy = meta.CreatedBy
                                dataType = meta.DataType
                                content = content
                                truncated = truncated
                            |}
                        )
                | Error DataObjectError.NotFound ->
                    return
                        ToolOutcome.Produced(
                            fableSerialize {|
                                error = "NotFound"
                                targetModule = moduleName
                                resultType = resultType
                            |}
                        )
                | Error err ->
                    return
                        ToolOutcome.Produced(
                            fableSerialize {|
                                error = "ResultReadFailed"
                                message = dataObjectErrorText err
                            |}
                        )
            | _ ->
                return
                    ToolOutcome.Produced(
                        fableSerialize {|
                            error = "ResultStoreUnavailable"
                            message = "No result store is configured in this deployment."
                        |}
                    )
}

// ─── What each tool is reaching for ──────────────────────────────
//
// One `describe` per tool, feeding the audit decorator, beside the executors
// because each reads the same declared argument the executor decodes. They
// run before the executor and must not throw.

/// One text argument, or `None` when it is absent or malformed: the executor
/// faults on exactly that case and the row lands with no target.
let private argText (args: ToolArgs) (name: string) : string option =
    match requiredText args name with
    | Ok value -> Some value
    | Error _ -> None

let private describeEnumeration (_: ToolContext) (_: ToolArgs) : Async<AICrossModuleAudit.ReadTarget> =
    async.Return AICrossModuleAudit.ReadTarget.enumeration

let private describeNamedModule
    (queryArg: string option)
    (_: ToolContext)
    (args: ToolArgs)
    : Async<AICrossModuleAudit.ReadTarget> =
    async.Return(
        AICrossModuleAudit.ReadTarget.ofModule (argText args "moduleName") (queryArg |> Option.bind (argText args))
    )

/// `query_entity` names a data SHAPE, so its targets are the producers the
/// gates ask about — UNFILTERED by the opt-in, so a read refused by it is
/// recorded as a read of the modules it would have reached.
let private describeEntityQuery (context: ToolContext) (args: ToolArgs) : Async<AICrossModuleAudit.ReadTarget> = async {
    let entityType = argText args "entityType"

    match entityType with
    | Some typeId ->
        let! producers = producersOf context.Request typeId

        return {
            Modules = List.sort producers
            QueryKey = entityType
        }
    | None -> return { Modules = []; QueryKey = None }
}

// ─── Registration ────────────────────────────────────────────────

/// The six built-in `_platform.ai.*` cross-module read tools, registered by
/// `composeAI` beside `NarrativeTools.builtInTools`, each through
/// `createAuditedTool`, so every invocation lands one `CrossModuleRead` row
/// whatever its outcome and a seventh cannot skip the trail.
let builtIn: RegisteredTool list = [
    AICrossModuleAudit.createAuditedTool listAccessibleModulesDef describeEnumeration executeListModules
    AICrossModuleAudit.createAuditedTool listDataTypesDef describeEnumeration executeListDataTypes
    AICrossModuleAudit.createAuditedTool queryModuleDef (describeNamedModule (Some "queryKey")) executeQueryModule
    AICrossModuleAudit.createAuditedTool queryEntityDef describeEntityQuery executeQueryEntity
    AICrossModuleAudit.createAuditedTool listResultsDef (describeNamedModule None) executeListResults
    AICrossModuleAudit.createAuditedTool
        getLatestResultDef
        (describeNamedModule (Some "resultType"))
        executeGetLatestResult
]