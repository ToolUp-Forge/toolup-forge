module ToolUp.AI.PlatformAITools

open System
open System.Text
open System.Text.Json
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.IEntityStore
open ToolUp.Platform.EntityTypes
open ToolUp.Platform.EntityQueryTypes
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.AI
open ToolUp.AI.AIToolRegistry
open DataManagementTypes

/// Phase 36.B — built-in cross-module AI tool family (`_platform.ai.*`).
///
/// Six SDK-built-in tools registered by `composeAI` that wrap existing
/// platform substrate so an LLM can roam across the user's module data
/// without each module re-exposing read primitives. They discover
/// modules, enumerate data types, query module handlers, query entity
/// stores, and read result blobs — each subject to the SAME RBAC the
/// substrate already enforces.
///
/// Every tool carries `SourceModule = "_platform.ai"` so the
/// per-module dispatch RBAC filter (Phase 36.A) does not hide them;
/// per-*target*-module RBAC is enforced internally by each tool
/// (either the bus's own `hasPermission` gate, or an explicit pre-check
/// before reaching the result store).
///
/// **Why these re-resolve the access context from `HttpContext.Items`
/// rather than DI.** Tool executors run inside the agent loop's
/// *background* `HttpContext` (`AIAssistantHandler.createBackgroundContext`),
/// which carries its own DI scope. The DI `AccessContext` factory reads
/// the live request via `IHttpContextAccessor`, which is null/unreliable
/// on the background async flow — resolving it there would silently
/// yield an *unrestricted* anonymous context and bypass RBAC. The
/// background context instead copies the pre-resolved
/// `ToolUp.StorageScope` / `ToolUp.UserId` / `ToolUp.ModulePermissions`
/// items forward, so reconstructing the context from those items is the
/// RBAC-correct path (GP 4). It also works unchanged on a real request
/// context (the same middleware populates the same items).
///
/// **Phase 36.C — the per-module opt-in.** The six tools additionally
/// honour each TARGET module's declared `ModuleAIExposure`
/// (`ServerModule.withAIExposure`), which is **off unless declared**.
/// `list_accessible_modules` annotates (`queryable: false`);
/// `list_data_types` filters; the four reach tools refuse with
/// `UnqueryableModule`. See `aiQueryable` below for why that refusal is
/// rendered differently from `PermissionDenied` and reported after it,
/// and `docs/migrations/per-module-ai-queryability.md` for what a
/// deployment that had adopted 36.B has to do.
///
/// Wired automatically by `composeAI`; apps do not register these.

// ─── JSON helpers ────────────────────────────────────────────────

let private fableJsonOptions = FableConverters.create ()

let private fableSerialize (value: obj) : string =
    JsonSerializer.Serialize(value, fableJsonOptions)

// ─── Access-context + scope reconstruction (from Items, see header) ──

let private permToken =
    function
    | ModulePermission.Read -> "Read"
    | ModulePermission.Write -> "Write"
    | ModulePermission.Admin -> "Admin"
    | ModulePermission.SchemaOnly -> "SchemaOnly"

/// Reconstruct the caller's `AccessContext` from the per-request items
/// copied into the agent loop's background context. `ModulePermissions`
/// is the load-bearing field — every RBAC gate below reads it. The
/// remaining fields are best-effort: a stashed `ToolUp.Subject` is
/// preferred when present, otherwise the Subject is derived from the
/// resolved scope / user the same way `ComposeScopeResolver` does.
let private reconstructAccess (ctx: HttpContext) : AccessContext =
    let userId =
        match ctx.Items.TryGetValue "ToolUp.UserId" with
        | true, (:? string as id) -> id
        | _ -> "anonymous"

    let scopeOpt =
        match ctx.Items.TryGetValue "ToolUp.StorageScope" with
        | true, (:? StorageScope as s) -> Some s
        | _ -> None

    let teamId =
        match scopeOpt with
        | Some s when s.Container.StartsWith "team-" -> Some s.ScopeId
        | _ -> None

    let modulePermissions =
        match ctx.Items.TryGetValue "ToolUp.ModulePermissions" with
        | true, (:? Map<string, ModulePermission list> as perms) -> perms
        | _ -> Map.empty

    let moduleExposure =
        match ctx.Items.TryGetValue "ToolUp.ModuleExposure" with
        | true, (:? Map<string, ModuleExposure> as exposure) -> exposure
        | _ -> Map.empty

    let platformRole =
        match ctx.Items.TryGetValue "ToolUp.PlatformRole" with
        | true, (:? PlatformRole as role) -> Some role
        | _ -> None

    let subject =
        match ctx.Items.TryGetValue "ToolUp.Subject" with
        | true, (:? Subject as s) -> s
        | _ ->
            match teamId with
            | Some tid -> TeamMember(userId, tid)
            | None when userId <> "anonymous" -> AuthenticatedUser userId
            | None -> AnonymousSession userId

    {
        UserId = userId
        TeamId = teamId
        Subject = subject
        ModulePermissions = modulePermissions
        ModuleExposure = moduleExposure
        PlatformRole = platformRole
    }

/// Phase 730 — the grant / consent gate for the `_platform.*` family.
///
/// These tools are exempt from the Phase 36.A per-SOURCE-module filter by
/// design: their `SourceModule` is an SDK-reserved namespace, and they
/// apply RBAC internally, per TARGET module, at the four sites below.
/// That exemption is exactly why the grant gate has to be repeated here.
/// Filtering `_platform.ai` out of the tool list would be wrong (it is not
/// a module and nobody is granted it), but the modules these tools REACH
/// are ordinary governed ones — so without this, `_platform.list_results`
/// would happily read a module whose grant is pending the subject's
/// acceptance, having been admitted by a permission entry that confers
/// nothing. The cross-module family would become the way around the very
/// control Phases 551 and 552 shipped.
///
/// Built per invocation rather than per turn: a tool executor has no turn
/// to hang it on, and the cost when nothing is declared is one failed
/// `GetService`.
let private grantLive (ctx: HttpContext) : string -> bool = moduleGrantGate ctx

/// Phase 36.C — the per-module AI-queryability gate: has the TARGET
/// module's author opted its data into this tool family at all.
///
/// **Third gate, third question, and the order they run in is the
/// contract.** Every reach site below asks, in this order: (1) may this
/// caller read the module (`hasPermission` / `canAccessModule`); (2) is
/// the authority behind that permission live (Phase 730's grant gate);
/// (3) is the module on the AI surface (this). One and two are about the
/// CALLER and render identically as `PermissionDenied` — the model is
/// never told which of them stopped it. Three is about the DEPLOYMENT
/// and renders as `UnqueryableModule`, because it is not a secret, it is
/// not fixable by any grant, and telling the model "denied" would send
/// it round a loop it cannot win. That is why the diagnosis is distinct;
/// it is also why it is reported LAST — a caller who may not read the
/// module learns nothing about which modules a deployment exposed.
///
/// **Default off.** No module opted in ⇒ no registry ⇒ constant `false`.
let private aiQueryable (ctx: HttpContext) : string -> bool = moduleAIQueryGate ctx

/// The `UnqueryableModule` refusal, in one place so all four reach tools
/// render it identically and a consumer can match on one shape.
let private unqueryableModule (moduleName: string) (detail: string) : string =
    fableSerialize {|
        error = "UnqueryableModule"
        targetModule = moduleName
        message = detail
    |}

/// Phase 36.D — the FOURTH gate, and the only one that asks the USER.
///
/// The three gates above are all answered by the deployment: may this
/// caller read the module, is that authority live, is the module on the AI
/// surface. This one asks the person in the conversation, once per target
/// module, whether the agent may go and read their data out of a module
/// they did not name. It is INNERMOST by the same argument that put 36.C
/// last: a caller who fails an outer gate is refused before any dialog is
/// shown, so the dialog can never tell them which modules exist or which
/// ones a deployment exposed.
///
/// The refusal is rendered as `UserDenied` — a fourth discriminator beside
/// `PermissionDenied` / `UnqueryableModule` / `NotFound`, for a fourth
/// remedy. `PermissionDenied` would be a lie (the caller holds the
/// permission), and `UnqueryableModule` would send the model to an
/// operator for something only the user can change.
let private userDenied (moduleName: string) (detail: string) : string =
    fableSerialize {|
        error = "UserDenied"
        targetModule = moduleName
        message = detail
    |}

/// The consent gate at one reach site, rendered. `None` means proceed.
let private consentRefusal
    (ctx: HttpContext)
    (toolName: string)
    (targetModule: string)
    (intendedQueryKey: string)
    (argsJson: string)
    : Async<string option> =
    async {
        let! outcome = AIConsentDispatch.requireConsent ctx toolName targetModule intendedQueryKey argsJson

        match outcome with
        | AIConsentDispatch.ConsentGranted -> return None
        | AIConsentDispatch.ConsentRefused reason -> return Some(userDenied targetModule reason)
    }

/// Consent over a SET of target modules — the shape `query_entity` needs,
/// where the data can come from several producers of one entity type.
///
/// The first refusal wins and the rest are not asked: the read cannot
/// happen, so further dialogs would ask the user to authorise something
/// that is not going to occur either way. An empty set is granted — there
/// is no module to name, which is the same documented limit 36.C's gate
/// reaches on an entity type the catalogue cannot attribute.
let rec private consentRefusalForAll
    (ctx: HttpContext)
    (toolName: string)
    (intendedQueryKey: string)
    (argsJson: string)
    (targets: string list)
    : Async<string option> =
    async {
        match targets with
        | [] -> return None
        | target :: rest ->
            let! refusal = consentRefusal ctx toolName target intendedQueryKey argsJson

            match refusal with
            | Some rendered -> return Some rendered
            | None -> return! consentRefusalForAll ctx toolName intendedQueryKey argsJson rest
    }

/// The standing explanation, written for the model that reads it: the
/// remedy is a deployment change, so re-planning as though this were a
/// permission problem is wasted turns.
let private notOptedInMessage (moduleName: string) =
    sprintf
        "Module '%s' has not opted into the cross-module AI surface, so its data cannot be read through the _platform.ai.* tools. This is a deployment-level declaration, not a permission the current user can be granted — do not retry, and do not treat it as PermissionDenied. Use list_accessible_modules to see which modules report queryable: true."
        moduleName

/// The caller's resolved storage-scope id — the structural tenant
/// isolation boundary for entity / result / catalog reads (GP 4). Falls
/// back to the user id (then `"anonymous"`) when no scope was resolved.
let private scopeIdOf (ctx: HttpContext) : string =
    match ctx.Items.TryGetValue "ToolUp.StorageScope" with
    | true, (:? StorageScope as s) -> s.ScopeId
    | _ ->
        match ctx.Items.TryGetValue "ToolUp.UserId" with
        | true, (:? string as id) -> id
        | _ -> "anonymous"

let private dataObjectErrorText (err: DataObjectError) : string =
    match err with
    | DataObjectError.NotFound -> "NotFound"
    | DataObjectError.VersionNotFound v -> sprintf "VersionNotFound %d" v
    | DataObjectError.DeleteForbidden -> "DeleteForbidden"
    | DataObjectError.PolicyMismatch(expected, supplied) ->
        sprintf "PolicyMismatch (expected %A, supplied %A)" expected supplied
    | DataObjectError.StorageFailure msg -> sprintf "StorageFailure: %s" msg

let private optString (root: JsonElement) (name: string) : string option =
    match root.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.String ->
        let s = v.GetString()
        if String.IsNullOrWhiteSpace s then None else Some s
    | _ -> None

let private optDateTime (root: JsonElement) (name: string) : DateTime option =
    match optString root name with
    | Some s ->
        match
            DateTime.TryParse(
                s,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal
                ||| System.Globalization.DateTimeStyles.AssumeUniversal
            )
        with
        | true, dt -> Some dt
        | _ -> None
    | None -> None

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
}

let private executeListModules (ctx: HttpContext) (_argsJson: string) : Async<string> = async {
    let access = reconstructAccess ctx
    let rbacConfigured = not access.ModulePermissions.IsEmpty

    let! catalogModules = async {
        match ctx.RequestServices.GetService(typeof<IDataCatalog>) with
        | :? IDataCatalog as catalog ->
            let! types = catalog.ListTypes()

            let! producerLists = types |> List.map (fun t -> catalog.GetProducers t.Id) |> Async.Parallel

            return producerLists |> Array.collect List.toArray |> Array.toList |> List.distinct
        | _ -> return []
    }

    let allModules =
        (catalogModules @ (access.ModulePermissions |> Map.toList |> List.map fst))
        |> List.distinct
        |> List.sort

    let isGrantLive = grantLive ctx
    let isQueryable = aiQueryable ctx

    let entries =
        allModules
        // Phase 730 — enumeration is a listing, so it filters silently
        // (nothing was attempted) exactly as the tool-registry list filter
        // does. A module whose grant is pending or consent-revoked is not
        // named here at all.
        |> List.filter (fun m -> AccessContext.canAccessModule m access && isGrantLive m)
        |> List.map (fun m ->
            let perms =
                if rbacConfigured then
                    match access.ModulePermissions.TryFind m with
                    | Some ps -> ps |> List.map permToken
                    | None -> []
                else
                    [ "unrestricted" ]

            {|
                moduleName = m
                permissions = perms
                // Phase 36.C — this tool ANNOTATES rather than filters,
                // and it is the one of the six that does. A module the
                // caller may read but which has not opted into the AI
                // surface still exists and is still worth naming: the
                // model can then route the user to the module's own UI,
                // or stop planning around data it will never get, rather
                // than inferring the module is absent and hallucinating
                // around the gap. Every OTHER tool filters or refuses —
                // the annotation is a discovery affordance, not a leak of
                // anything the caller could not already see by holding
                // the permission.
                queryable = isQueryable m
            |})

    return
        fableSerialize {|
            rbacConfigured = rbacConfigured
            modules = entries
        |}
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
}

let private executeListDataTypes (ctx: HttpContext) (_argsJson: string) : Async<string> = async {
    let access = reconstructAccess ctx
    // Phase 730 — same silent-listing treatment as `_platform.list_modules`.
    let isGrantLive = grantLive ctx
    // Phase 36.C — and the AI-queryability opt-in on top, which here
    // FILTERS rather than annotates: a data type is only listed when at
    // least one of its producers is both reachable by this caller and on
    // the AI surface. This is the intersection Phase 36.B's producer
    // filter was written against and could not yet compute — with no
    // opt-in declared it had nothing to narrow by, so every accessible
    // producer counted.
    let isQueryable = aiQueryable ctx

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

        return fableSerialize {| dataTypes = result |}
    | _ ->
        return
            fableSerialize {|
                dataTypes = ([]: obj list)
                note = "no data catalog registered in this deployment"
            |}
}

// ─── _platform.ai.query_module ───────────────────────────────────

let private queryModuleDef: AIToolDefinition = {
    Name = "_platform.ai.query_module"
    Description =
        "Call a registered module-to-module query handler by name and key, passing a JSON payload. Thin shell over the platform query bus, which enforces the caller's per-module Read permission: targeting a module the user can't read returns `{\"error\":\"PermissionDenied\"}`. A module that has not opted into the cross-module AI surface returns `{\"error\":\"UnqueryableModule\"}` — a deployment-level declaration no permission grant can change, so do not retry it. A module not present in this deployment returns `{\"error\":\"ModuleNotFound\"}`; an unknown queryKey returns `{\"error\":\"NoHandler\"}`. On success returns `{ targetModule, queryKey, result }` where `result` is the handler's own JSON response. Discover module names + their query keys via list_accessible_modules and the module's documentation."
    Parameters = [
        {
            Name = "moduleName"
            Type = "string"
            Description = "The target module's name (as listed by list_accessible_modules)."
            Required = true
            Default = None
        }
        {
            Name = "queryKey"
            Type = "string"
            Description = "The module-declared query discriminator, e.g. \"latest-analysis\"."
            Required = true
            Default = None
        }
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
}

let private executeQueryModule (ctx: HttpContext) (argsJson: string) : Async<string> = async {
    let access = reconstructAccess ctx

    let parsed =
        try
            use doc = JsonDocument.Parse argsJson
            let root = doc.RootElement

            let payload =
                match root.TryGetProperty "payloadJson" with
                | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
                | true, v when v.ValueKind = JsonValueKind.Object || v.ValueKind = JsonValueKind.Array -> v.GetRawText()
                | _ -> ""

            Some(optString root "moduleName", optString root "queryKey", payload)
        with _ ->
            None

    match parsed with
    | None
    | Some(None, _, _) ->
        return
            fableSerialize {|
                error = "InvalidArguments"
                message = "Required argument 'moduleName' is missing."
            |}
    | Some(_, None, _) ->
        return
            fableSerialize {|
                error = "InvalidArguments"
                message = "Required argument 'queryKey' is missing."
            |}
    // Phase 36.C — the opt-in gate, AFTER the caller-side question and
    // BEFORE the bus. After, because a caller who may not read the module
    // must learn nothing about the deployment's AI configuration — the
    // bus's own `PermissionDenied` still answers them. Before, because
    // the bus would otherwise EXECUTE the module's handler, and the whole
    // point of the opt-in is that a module which never opted in is not
    // reached at all.
    //
    // A consequence worth stating: a module name the deployment does not
    // register, on which the caller nominally holds Read, now reports
    // `UnqueryableModule` rather than `ModuleNotFound`. Both are true and
    // neither is reachable, and refusing before dispatch is the half that
    // has to hold.
    | Some(Some moduleName, Some _, _) when
        AccessContext.hasPermission moduleName ModulePermission.Read access
        && not (aiQueryable ctx moduleName)
        ->
        return unqueryableModule moduleName (notOptedInMessage moduleName)
    | Some(Some moduleName, Some queryKey, payload) ->
        // Phase 36.D — the user-consent gate, innermost.
        //
        // Gated on the caller's own `Read` permission first, deliberately.
        // Unlike the three other reach tools, this one does not pre-check
        // permission itself — the BUS answers `PermissionDenied`, and that
        // is the pre-36.C behaviour the ordering rationale above preserves.
        // Prompting before the bus has spoken would show a dialog naming a
        // module the caller may not read, which is exactly the leak the
        // report-last ordering exists to prevent.
        let! refusal =
            if AccessContext.hasPermission moduleName ModulePermission.Read access then
                consentRefusal ctx queryModuleDef.Name moduleName queryKey payload
            else
                async.Return None

        match refusal with
        | Some rendered -> return rendered
        | None ->
            match ctx.RequestServices.GetService(typeof<IModuleQueryBus>) with
            | :? IModuleQueryBus as bus ->
                let! outcome =
                    bus.Ask(
                        access,
                        {
                            TargetModule = moduleName
                            QueryKey = queryKey
                            Payload = payload
                        }
                    )

                match outcome with
                | None ->
                    return
                        fableSerialize {|
                            error = "ModuleNotFound"
                            targetModule = moduleName
                            message = sprintf "Module '%s' is not registered in this deployment." moduleName
                        |}
                | Some(Error(PermissionDenied m)) ->
                    return
                        fableSerialize {|
                            error = "PermissionDenied"
                            targetModule = m
                        |}
                | Some(Error(NoHandler(m, q))) ->
                    return
                        fableSerialize {|
                            error = "NoHandler"
                            targetModule = m
                            queryKey = q
                        |}
                | Some(Error(HandlerFailed msg)) ->
                    return
                        fableSerialize {|
                            error = "HandlerFailed"
                            message = msg
                        |}
                | Some(Ok resp) ->
                    // `resp.Payload` is JSON the handler already produced.
                    // Round-trip it to a self-contained `JsonElement` so the
                    // wrapper serialises as structured JSON, not a quoted string.
                    let resultElement =
                        let raw =
                            if String.IsNullOrWhiteSpace resp.Payload then
                                "null"
                            else
                                resp.Payload

                        JsonSerializer.Deserialize<JsonElement>(raw, fableJsonOptions)

                    return
                        fableSerialize {|
                            targetModule = moduleName
                            queryKey = queryKey
                            result = resultElement
                        |}
            | _ ->
                return
                    fableSerialize {|
                        error = "ModuleQueryBusUnavailable"
                    |}
}

// ─── _platform.ai.query_entity ───────────────────────────────────

/// Parse one predicate node from the LLM-friendly JSON shape into the
/// typed `Predicate` AST. The shape is deliberately simpler than the
/// `FableConverters` DU wire form so the model can author it directly:
///   {"op":"eq","field":"Mood","value":"happy"}
///   {"op":"in","field":"Mood","values":["happy","calm"]}
///   {"op":"and","left":{...},"right":{...}}
///   {"op":"not","inner":{...}}
let rec private parsePredicate (el: JsonElement) : Result<Predicate, string> =
    let field () =
        match el.TryGetProperty "field" with
        | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
        | _ -> None

    let value () =
        match el.TryGetProperty "value" with
        | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
        | true, v -> Some(v.GetRawText())
        | _ -> None

    match el.TryGetProperty "op" with
    | false, _ -> Error "predicate node requires an 'op' field"
    | true, opv when opv.ValueKind <> JsonValueKind.String -> Error "'op' must be a string"
    | true, opv ->
        match (opv.GetString()).ToLowerInvariant() with
        | ("eq" | "ne" | "gt" | "gte" | "lt" | "lte") as op ->
            match field (), value () with
            | Some f, Some v ->
                Ok(
                    match op with
                    | "eq" -> Eq(f, v)
                    | "ne" -> Ne(f, v)
                    | "gt" -> Gt(f, v)
                    | "gte" -> Gte(f, v)
                    | "lt" -> Lt(f, v)
                    | _ -> Lte(f, v)
                )
            | _ -> Error(sprintf "'%s' requires 'field' and 'value'" op)
        | "in" ->
            match field () with
            | None -> Error "'in' requires 'field'"
            | Some f ->
                match el.TryGetProperty "values" with
                | true, vs when vs.ValueKind = JsonValueKind.Array ->
                    let values = [
                        for item in vs.EnumerateArray() ->
                            if item.ValueKind = JsonValueKind.String then
                                item.GetString()
                            else
                                item.GetRawText()
                    ]

                    Ok(In(f, values))
                | _ -> Error "'in' requires a 'values' array"
        | ("and" | "or") as op ->
            match el.TryGetProperty "left", el.TryGetProperty "right" with
            | (true, l), (true, r) ->
                match parsePredicate l, parsePredicate r with
                | Ok lp, Ok rp -> Ok(if op = "and" then And(lp, rp) else Or(lp, rp))
                | Error e, _
                | _, Error e -> Error e
            | _ -> Error(sprintf "'%s' requires 'left' and 'right' predicate nodes" op)
        | "not" ->
            match el.TryGetProperty "inner" with
            | true, inner -> parsePredicate inner |> Result.map Not
            | _ -> Error "'not' requires an 'inner' predicate node"
        | other -> Error(sprintf "unknown predicate op '%s'" other)

let private parsePredicateJson (s: string) : Result<Predicate option, string> =
    if String.IsNullOrWhiteSpace s then
        Ok None
    else
        try
            use doc = JsonDocument.Parse s
            parsePredicate doc.RootElement |> Result.map Some
        with ex ->
            Error(sprintf "predicateJson is not valid JSON: %s" ex.Message)

let private queryEntityDef: AIToolDefinition = {
    Name = "_platform.ai.query_entity"
    Description =
        "Query the typed entity store for the current user's scope by entity type and an optional predicate over declared indexes. Predicates reference declared index fields only; a non-indexed field returns an InvalidIndex error. Predicate JSON shape: {\"op\":\"eq\",\"field\":\"Mood\",\"value\":\"happy\"}; ops are eq/ne/gt/gte/lt/lte (string-ordered), in ({\"op\":\"in\",\"field\":\"x\",\"values\":[...]}), and/or ({\"op\":\"and\",\"left\":{...},\"right\":{...}}), not ({\"op\":\"not\",\"inner\":{...}}). Returns up to `take` (max 100) entity records. Scope-isolated — only the caller's own entities are visible. An entity type produced only by modules outside the cross-module AI surface returns `{\"error\":\"UnqueryableModule\"}`; no grant fixes that, so do not retry it. Discover entity-type ids via list_data_types, which already lists only queryable producers."
    Parameters = [
        {
            Name = "entityType"
            Type = "string"
            Description = "The entity type to query, e.g. \"Mood\" or \"TrainingDay\"."
            Required = true
            Default = None
        }
        {
            Name = "predicateJson"
            Type = "string"
            Description =
                "Optional predicate as a JSON string (see the tool description for the shape). Omit or pass an empty string to return all entities of the type (subject to `take`)."
            Required = false
            Default = None
        }
        {
            Name = "take"
            Type = "number"
            Description = "Maximum number of records to return (default 100, hard-capped at 100)."
            Required = false
            Default = Some "100"
        }
    ]
    SourceModule = "_platform.ai"
    EmitsActions = None
    Location = ServerResident
    Surface = Both
    IsLiveInterface = false
    ResultBudget = DefaultResultBudget
}

let private executeQueryEntity (ctx: HttpContext) (argsJson: string) : Async<string> = async {
    let scopeId = scopeIdOf ctx

    let parsed =
        try
            use doc = JsonDocument.Parse argsJson
            let root = doc.RootElement

            let entityType = optString root "entityType"

            let take =
                match root.TryGetProperty "take" with
                | true, v when v.ValueKind = JsonValueKind.Number -> v.GetInt32()
                | _ -> 100

            let predicate =
                match root.TryGetProperty "predicateJson" with
                | true, v when v.ValueKind = JsonValueKind.String -> parsePredicateJson (v.GetString())
                | true, v when v.ValueKind = JsonValueKind.Object -> parsePredicate v |> Result.map Some
                | _ -> Ok None

            Ok(entityType, take, predicate)
        with ex ->
            Error(sprintf "arguments were not valid JSON: %s" ex.Message)

    match parsed with
    | Error msg ->
        return
            fableSerialize {|
                error = "InvalidArguments"
                message = msg
            |}
    | Ok(None, _, _) ->
        return
            fableSerialize {|
                error = "InvalidArguments"
                message = "Required argument 'entityType' is missing."
            |}
    | Ok(_, _, Error predErr) ->
        return
            fableSerialize {|
                error = "InvalidPredicate"
                message = predErr
            |}
    | Ok(Some entityType, take, Ok wherePred) ->
        // Phase 36.C — the opt-in gate for the one tool that names a data
        // shape rather than a module. Attribution runs through the data
        // catalogue's producers, which is the only module-attribution an
        // entity type has: `EntityRegistration` carries none (entities are
        // registered app-level by `ServerApp.withEntity`, not per module).
        //
        // So the gate binds exactly where the shard says it does — "an
        // entity type whose only producer module is non-queryable" — and
        // an entity type with NO catalogued producer is passed through
        // unchanged rather than refused. Refusing it was the other
        // candidate and is worse: it would block an opted-in module's own
        // entities whenever it happens not to also declare a `DataType`,
        // with a refusal whose only remedy is unrelated to the opt-in.
        //
        // That leaves a real hole — an opted-OUT module's entities stay
        // reachable here when it declares no `DataType` — and the hole is
        // recorded rather than papered over: closing it needs module
        // attribution on `EntityRegistration`, which is a substrate change
        // of its own, not a line in this gate.
        //
        // Phase 36.D reads the same attribution one step further: the
        // producers that ARE on the AI surface are the modules whose data
        // this read would return, so they are exactly the modules the user
        // is asked about. `Ok []` (no catalogued producer) therefore
        // reaches the consent gate with nothing to name, and is granted for
        // the same reason it passes the 36.C gate.
        let! entityGate = async {
            match ctx.RequestServices.GetService(typeof<IDataCatalog>) with
            | :? IDataCatalog as catalog ->
                let! producers = catalog.GetProducers entityType

                if List.isEmpty producers then
                    return Ok []
                else
                    let isQueryable = aiQueryable ctx
                    let queryable = producers |> List.filter isQueryable

                    if List.isEmpty queryable then
                        return Error(String.Join(", ", producers))
                    else
                        return Ok queryable
            | _ -> return Ok []
        }

        match entityGate with
        | Error producerNames ->
            return
                unqueryableModule
                    producerNames
                    (sprintf
                        "Entity type '%s' is produced only by module(s) that have not opted into the cross-module AI surface (%s), so it cannot be read through the _platform.ai.* tools. This is a deployment-level declaration, not a permission the current user can be granted — do not retry. Use list_accessible_modules to see which modules report queryable: true."
                        entityType
                        producerNames)
        | Ok queryableProducers ->
            // Phase 36.D — the user-consent gate, innermost, asked once per
            // producing module because the read returns data from all of
            // them. The first refusal ends the read.
            let! refusal = consentRefusalForAll ctx queryEntityDef.Name entityType argsJson queryableProducers

            match refusal with
            | Some rendered -> return rendered
            | None ->
                match ctx.RequestServices.GetService(typeof<IEntityStore>) with
                | :? IEntityStore as store ->
                    let cappedTake = min (max take 1) 100

                    let query: EntityQuery<JsonElement> = {
                        EntityType = entityType
                        Where = wherePred
                        OrderBy = None
                        Skip = 0
                        Take = cappedTake
                    }

                    let! result = store.Query<JsonElement>(scopeId, query)

                    match result with
                    | Ok entities ->
                        return
                            fableSerialize {|
                                entityType = entityType
                                count = List.length entities
                                entities = entities
                            |}
                    | Error err ->
                        return
                            fableSerialize {|
                                error = "EntityQueryFailed"
                                entityType = entityType
                                message = EntityError.message err
                            |}
                | _ ->
                    return
                        fableSerialize {|
                            error = "EntityStoreUnavailable"
                            message = "No entity store is registered in this deployment."
                        |}
}

// ─── _platform.ai.list_results ───────────────────────────────────

let private listResultsDef: AIToolDefinition = {
    Name = "_platform.ai.list_results"
    Description =
        "List the persisted analytical results a module has produced in the current user's scope, newest first. Enforces the caller's Read permission on the named module — a module the user can't read returns `{\"error\":\"PermissionDenied\"}` — and the module's AI-queryability opt-in: a module outside the cross-module AI surface returns `{\"error\":\"UnqueryableModule\"}`, which no grant fixes. Each row carries objectId, resultType, version, createdAt, createdBy and dataType (metadata only — fetch a result's content with get_latest_result). Optional dateFrom/dateTo (ISO-8601) filter by creation time; supply both to bound a range. Returns `{\"error\":\"ResultStoreUnavailable\"}` when the deployment has no result store configured."
    Parameters = [
        {
            Name = "moduleName"
            Type = "string"
            Description = "The module whose results to list (as listed by list_accessible_modules)."
            Required = true
            Default = None
        }
        {
            Name = "dateFrom"
            Type = "string"
            Description = "Optional inclusive lower bound on creation time (ISO-8601). Supply together with dateTo."
            Required = false
            Default = None
        }
        {
            Name = "dateTo"
            Type = "string"
            Description = "Optional inclusive upper bound on creation time (ISO-8601). Supply together with dateFrom."
            Required = false
            Default = None
        }
    ]
    SourceModule = "_platform.ai"
    EmitsActions = None
    Location = ServerResident
    Surface = Both
    IsLiveInterface = false
    ResultBudget = DefaultResultBudget
}

let private resultTypeOf (moduleName: string) (objectId: string) : string =
    let prefix = ResultObjectId.modulePrefix moduleName

    if objectId.StartsWith prefix then
        objectId.Substring prefix.Length
    else
        objectId

let private executeListResults (ctx: HttpContext) (argsJson: string) : Async<string> = async {
    let access = reconstructAccess ctx
    let scopeId = scopeIdOf ctx

    let parsed =
        try
            use doc = JsonDocument.Parse argsJson
            let root = doc.RootElement
            Some(optString root "moduleName", optDateTime root "dateFrom", optDateTime root "dateTo")
        with _ ->
            None

    match parsed with
    | None
    | Some(None, _, _) ->
        return
            fableSerialize {|
                error = "InvalidArguments"
                message = "Required argument 'moduleName' is missing."
            |}
    | Some(Some moduleName, dateFrom, dateTo) ->
        // Phase 730 — BOTH halves, and in this order. The permission check
        // asks "may this caller read the module at all"; the grant check
        // asks "is the authority behind that permission entry live". A
        // pending or consent-revoked grant leaves the entry in place, so
        // the first check alone admits it.
        //
        // This site is a data REACH rather than an enumeration, so the
        // refusal goes through `guardToolGrant`, which emits the same
        // `UnconsentedGrantRefused` row the Remoting seam does — an
        // attempt on inert authority is exactly what that event records,
        // and the cross-module family must not be the one path where it
        // happens silently. The rendered error is deliberately identical
        // to the permission refusal: the model is told it may not have
        // this module, never which of the two gates stopped it.
        if
            not (AccessContext.hasPermission moduleName ModulePermission.Read access)
            || not (guardToolGrant ctx access moduleName)
        then
            return
                fableSerialize {|
                    error = "PermissionDenied"
                    targetModule = moduleName
                |}
        // Phase 36.C — third gate, reported LAST and rendered
        // DIFFERENTLY. The caller-side pair above is deliberately
        // indistinguishable to the model; this one is deliberately
        // distinct, because no grant fixes it and a model told
        // "PermissionDenied" would keep trying.
        elif not (aiQueryable ctx moduleName) then
            return unqueryableModule moduleName (notOptedInMessage moduleName)
        else
            // Phase 36.D — the user-consent gate, innermost: the three
            // gates above are the deployment's answers, this one is the
            // user's. Reached only after all three have passed, so a
            // dialog can never name a module the caller may not read.
            let! refusal = consentRefusal ctx listResultsDef.Name moduleName "" argsJson

            match refusal with
            | Some rendered -> return rendered
            | None ->
                match ctx.RequestServices.GetService(typeof<IResultStore>) with
                | :? IResultStore as store ->
                    let dateRange =
                        match dateFrom, dateTo with
                        | Some f, Some t -> Some(f, t)
                        | _ -> None

                    let! results = store.ListResults(scopeId, moduleName, dateRange)

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
                        fableSerialize {|
                            targetModule = moduleName
                            results = rows
                        |}
                | _ ->
                    return
                        fableSerialize {|
                            error = "ResultStoreUnavailable"
                            message = "No result store is configured in this deployment."
                        |}
}

// ─── _platform.ai.get_latest_result ──────────────────────────────

[<Literal>]
let private ContentCharCap = 1_000_000

let private getLatestResultDef: AIToolDefinition = {
    Name = "_platform.ai.get_latest_result"
    Description =
        "Fetch the latest version of a single persisted result by module + resultType, returning its content (decoded as UTF-8 text — typically JSON) plus version metadata. Enforces the caller's Read permission on the module — a module the user can't read returns `{\"error\":\"PermissionDenied\"}` — and the module's AI-queryability opt-in: a module outside the cross-module AI surface returns `{\"error\":\"UnqueryableModule\"}`, which no grant fixes. Returns `{\"error\":\"NotFound\"}` when no result of that type has been saved in the scope. Content is capped at ~1MB (`truncated` flags when the body was cut). Discover available resultTypes for a module via list_results."
    Parameters = [
        {
            Name = "moduleName"
            Type = "string"
            Description = "The module that produced the result."
            Required = true
            Default = None
        }
        {
            Name = "resultType"
            Type = "string"
            Description = "The result-type discriminator (as listed by list_results, e.g. \"q1-sales\")."
            Required = true
            Default = None
        }
    ]
    SourceModule = "_platform.ai"
    EmitsActions = None
    Location = ServerResident
    Surface = Both
    IsLiveInterface = false
    ResultBudget = DefaultResultBudget
}

let private executeGetLatestResult (ctx: HttpContext) (argsJson: string) : Async<string> = async {
    let access = reconstructAccess ctx
    let scopeId = scopeIdOf ctx

    let parsed =
        try
            use doc = JsonDocument.Parse argsJson
            let root = doc.RootElement
            Some(optString root "moduleName", optString root "resultType")
        with _ ->
            None

    match parsed with
    | None
    | Some(None, _) ->
        return
            fableSerialize {|
                error = "InvalidArguments"
                message = "Required argument 'moduleName' is missing."
            |}
    | Some(_, None) ->
        return
            fableSerialize {|
                error = "InvalidArguments"
                message = "Required argument 'resultType' is missing."
            |}
    | Some(Some moduleName, Some resultType) ->
        // Phase 730 — BOTH halves, and in this order. The permission check
        // asks "may this caller read the module at all"; the grant check
        // asks "is the authority behind that permission entry live". A
        // pending or consent-revoked grant leaves the entry in place, so
        // the first check alone admits it.
        //
        // This site is a data REACH rather than an enumeration, so the
        // refusal goes through `guardToolGrant`, which emits the same
        // `UnconsentedGrantRefused` row the Remoting seam does — an
        // attempt on inert authority is exactly what that event records,
        // and the cross-module family must not be the one path where it
        // happens silently. The rendered error is deliberately identical
        // to the permission refusal: the model is told it may not have
        // this module, never which of the two gates stopped it.
        if
            not (AccessContext.hasPermission moduleName ModulePermission.Read access)
            || not (guardToolGrant ctx access moduleName)
        then
            return
                fableSerialize {|
                    error = "PermissionDenied"
                    targetModule = moduleName
                |}
        // Phase 36.C — third gate, reported LAST and rendered
        // DIFFERENTLY. The caller-side pair above is deliberately
        // indistinguishable to the model; this one is deliberately
        // distinct, because no grant fixes it and a model told
        // "PermissionDenied" would keep trying.
        elif not (aiQueryable ctx moduleName) then
            return unqueryableModule moduleName (notOptedInMessage moduleName)
        else
            // Phase 36.D — the user-consent gate, innermost: the three
            // gates above are the deployment's answers, this one is the
            // user's. Reached only after all three have passed, so a
            // dialog can never name a module the caller may not read.
            let! refusal = consentRefusal ctx getLatestResultDef.Name moduleName resultType argsJson

            match refusal with
            | Some rendered -> return rendered
            | None ->
                match ctx.RequestServices.GetService(typeof<IResultStore>) with
                | :? IResultStore as store ->
                    let! outcome = store.GetLatest(scopeId, moduleName, resultType)

                    match outcome with
                    | Ok(meta, bytes) ->
                        let decoded = Encoding.UTF8.GetString bytes
                        let truncated = decoded.Length > ContentCharCap

                        let content =
                            if truncated then
                                decoded.Substring(0, ContentCharCap)
                            else
                                decoded

                        return
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
                    | Error DataObjectError.NotFound ->
                        return
                            fableSerialize {|
                                error = "NotFound"
                                targetModule = moduleName
                                resultType = resultType
                            |}
                    | Error err ->
                        return
                            fableSerialize {|
                                error = "ResultReadFailed"
                                message = dataObjectErrorText err
                            |}
                | _ ->
                    return
                        fableSerialize {|
                            error = "ResultStoreUnavailable"
                            message = "No result store is configured in this deployment."
                        |}
}

// ─── Phase 36.E — what each tool is reaching for ─────────────────
//
// One `describe` per tool, feeding the audit decorator. They live here,
// beside the executors, because each names the SAME argument the executor
// parses: a second parser elsewhere would drift from this file silently,
// and the row would then record a target the tool never touched.
//
// They run BEFORE the executor and must not throw — the decorator guards
// them anyway, but a `describe` that raised would turn a recorded target
// into no target at all, which is a quieter failure than it looks.

/// Best-effort read of one string argument, returning `None` on any
/// malformed body. The executors render `InvalidArguments` for exactly
/// this case and the row still lands — with no target, which is honest:
/// a call that named no module reached no module.
let private argString (argsJson: string) (name: string) : string option =
    try
        use doc = JsonDocument.Parse argsJson
        optString doc.RootElement name
    with _ ->
        None

let private describeEnumeration (_: HttpContext) (_: string) : Async<AICrossModuleAudit.ReadTarget> =
    async.Return AICrossModuleAudit.ReadTarget.enumeration

let private describeNamedModule
    (queryArg: string option)
    (_: HttpContext)
    (argsJson: string)
    : Async<AICrossModuleAudit.ReadTarget> =
    async.Return(
        AICrossModuleAudit.ReadTarget.ofModule
            (argString argsJson "moduleName")
            (queryArg |> Option.bind (argString argsJson))
    )

/// `query_entity` names a data SHAPE rather than a module, so its targets
/// come from the same place the 36.C and 36.D gates take them: the data
/// catalogue's producers for the entity type.
///
/// Deliberately UNFILTERED by the queryability gate, unlike those two.
/// They ask "which producers may be read"; the audit row asks "which
/// modules was this read about", and for a read refused BY that gate the
/// answer is the producers it would have reached. Filtering here would
/// record an opt-in refusal as a read of nothing, which is the one
/// reading that makes the refusal unauditable.
let private describeEntityQuery (ctx: HttpContext) (argsJson: string) : Async<AICrossModuleAudit.ReadTarget> = async {
    let entityType = argString argsJson "entityType"

    match entityType, ctx.RequestServices.GetService(typeof<IDataCatalog>) with
    | Some typeId, (:? IDataCatalog as catalog) ->
        let! producers = catalog.GetProducers typeId

        return {
            Modules = producers |> List.distinct |> List.sort
            QueryKey = entityType
        }
    | _ -> return { Modules = []; QueryKey = entityType }
}

// ─── Registration ────────────────────────────────────────────────

/// The six built-in `_platform.ai.*` cross-module read tools —
/// auto-registered by `composeAI` alongside `NarrativeTools.builtInTools`.
///
/// Registered through `createAuditedTool` (Phase 36.E) so every
/// invocation lands one `CrossModuleRead` audit row whatever its outcome.
/// A seventh tool added to this list cannot skip the trail: the
/// registration shape requires it to declare what it reads.
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