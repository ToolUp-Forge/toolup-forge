module ToolUp.Platform.HomeOverviewApiHandler

open System
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.HealthChecks
open DataManagementTypes

// ─── Phase 171 — Home / Overview API handler ─────────────────────
//
// Aggregates the deployment-at-a-glance overview for the optional
// SDK-built-in Home landing module. Resolves everything lazily from
// DI per request — same idiom as `UsageQueryApiHandler` /
// `HealthMonitorApiHandler`.
//
//   - Tools + counts: `IDataCatalog` enumerates the data-producing
//     modules; `CountObjects` reads the per-type record count for the
//     caller's resolved scope only (GP 4 — another tenant's objects
//     are structurally unreachable).
//   - Active AI: the optional `IActiveAiProbe` DI seam, implemented +
//     registered by `ToolUp.AI` when composed. Absent → `None` (GP 1
//     keeps `Platform.Server` free of any AI dependency; GP 13 keeps
//     it zero-cost when no AI is wired).
//   - Health: a one-line summary of the registered `IHealthCheck`
//     probes, surfaced only to platform admins (least-privilege).
//
// The `[<RequiresClaim "scope">]` marker on `GetOverview` means the
// dispatcher only routes scoped callers here; the no-catalog / no-AI
// branches below are defensive, not the anonymous path.

let private resolveAccessContext (ctx: HttpContext) : AccessContext =
    match ctx.RequestServices.GetService(typeof<AccessContext>) with
    | :? AccessContext as ac -> ac
    | _ ->
        let userId =
            match ctx.Items.TryGetValue "ToolUp.UserId" with
            | true, (:? string as id) -> id
            | _ -> "anonymous"

        AccessContext.unrestricted (AnonymousSession userId)

/// Effective scope — same idiom as `UsageQueryApiHandler.resolveScopeId`.
let private resolveScopeId (accessContext: AccessContext) : string =
    match accessContext.TeamId with
    | Some teamId -> teamId
    | None -> accessContext.UserId

/// Coarse mode label derived from the *resolved subject*, so it
/// reflects what the caller actually sees rather than the raw config.
let private modeLabel (accessContext: AccessContext) : string =
    match accessContext.Subject with
    | AnonymousSession _ -> "Anonymous"
    | TeamMember _ -> "Team"
    | AuthenticatedUser _ -> "Individual"
    | ClaimBearer _ -> "Claim-bearer"

/// Build one `ToolSummary` per data-producing module from the
/// catalog, with scope-correct per-type counts. Empty when no catalog
/// is registered (a deployment with no data-producing modules).
///
/// Per-team exposure gate (Phase 245 tri-state): a module that is
/// `Hidden` or `Unavailable` for the caller's team is dropped, so the
/// Home overview matches the sidebar's visible set. `ModuleExposure`
/// is empty for non-team subjects, so this is a no-op outside team
/// scope.
let private buildTools (ctx: HttpContext) (accessContext: AccessContext) (scopeId: string) : Async<ToolSummary list> =
    match ctx.RequestServices.GetService(typeof<IDataCatalog>) with
    | :? IDataCatalog as catalog -> async {
        let! types = catalog.ListTypes()

        // Resolve producers + scope count for every type once.
        let! perType =
            types
            |> List.map (fun (t: DataTypeInfo) -> async {
                let! producers = catalog.GetProducers t.Id
                let! count = catalog.CountObjects(scopeId, t.Id)
                return (t, producers, count)
            })
            |> Async.Parallel

        // Invert (type → producing modules) into (module → types),
        // preserving the catalog's declaration order within each
        // module.
        return
            perType
            |> Array.collect (fun (t, producers, count) ->
                producers |> List.map (fun m -> (m, (t, count))) |> Array.ofList)
            |> Array.groupBy fst
            |> Array.filter (fun (moduleName, _) -> AccessContext.isModuleExposed moduleName accessContext)
            |> Array.map (fun (moduleName, entries) ->
                let counts =
                    entries
                    |> Array.map (fun (_, (t, count)) -> {
                        TypeId = t.Id
                        DisplayName = t.DisplayName
                        Count = count
                    })
                    |> Array.toList

                {
                    ModuleId = moduleName
                    Name = moduleName
                    DataCounts = counts
                    TotalRecords = counts |> List.sumBy _.Count
                })
            |> Array.toList
      }
    | _ -> async { return [] }

/// One-line health summary — platform admins only.
let private buildHealth (ctx: HttpContext) (accessContext: AccessContext) : Async<string option> =
    if AccessContext.canModifyPlatformConfig accessContext then
        async {
            let probes = ctx.RequestServices.GetServices<IHealthCheck>() |> List.ofSeq

            if probes.IsEmpty then
                return None
            else
                let! runs = probes |> List.map HealthCheckRunner.runOne |> Async.Parallel
                let healthy = runs |> Array.filter (fun r -> r.Status = "Healthy") |> Array.length
                return Some(sprintf "%d/%d checks healthy" healthy runs.Length)
        }
    else
        async { return None }

/// Active-AI summary via the optional `IActiveAiProbe` DI seam.
let private buildActiveAi (ctx: HttpContext) (scopeId: string) : Async<ActiveAiSummary option> =
    match ctx.RequestServices.GetService(typeof<IActiveAiProbe>) with
    | :? IActiveAiProbe as probe -> probe.Describe scopeId
    | _ -> async { return None }

/// Phase 217 — scope-correct widget data via the optional
/// `IHomeWidgetDataProvider` DI seam. Resolved as a collection: every
/// registered provider runs in parallel for the caller's scope and the
/// maps are merged into one bag (contributor-namespaced keys avoid
/// collisions). Empty when no provider is composed (GP 13) — the
/// overview is then the byte-for-byte Phase 171 shape.
let private buildWidgetData (ctx: HttpContext) (scopeId: string) : Async<Map<string, string>> =
    let providers =
        ctx.RequestServices.GetServices<IHomeWidgetDataProvider>() |> List.ofSeq

    if providers.IsEmpty then
        async { return Map.empty }
    else
        async {
            let! maps = providers |> List.map (fun p -> p.Describe scopeId) |> Async.Parallel
            return maps |> Array.collect Map.toArray |> Map.ofArray
        }

// ─── Phase 217 — per-user recents/pinning persistence ────────────
//
// Persisted through the existing per-user config/store seam
// (`IConfigStore`) under a reserved module key, in the caller's *user*
// scope — so recents/pinning is per-user and never leaks across users,
// even in Team mode (GP 4). Stored as two `String` fields, each holding
// a JSON-encoded id list (the config store's `String` validator stores
// a JSON-quoted string, so the list is JSON-encoded into that string).

/// Reserved config module key for the Home recents/pinning document.
let private pinningModuleKey = "_sdk.home.pinning"

/// Recents cap — the most-recently-visited N tools are kept.
let private recentsCap = 8

[<Literal>]
let private pinnedField = "pinned"

[<Literal>]
let private recentField = "recent"

/// Two `String` fields, each defaulting to an empty JSON array string.
let private pinningSchema: ModuleConfigSchema = {
    Fields = [
        {
            Key = pinnedField
            DisplayName = "Pinned tools"
            Description = None
            Kind = ConfigFieldKind.String None
            Required = false
            DefaultJson = "\"[]\""
        }
        {
            Key = recentField
            DisplayName = "Recent tools"
            Description = None
            Kind = ConfigFieldKind.String None
            Required = false
            DefaultJson = "\"[]\""
        }
    ]
}

/// Per-*user* scope for the pinning document — keyed by the user id for
/// every non-anonymous subject (a team member's recents follow the user,
/// never the team), `None` for anonymous callers (no durable per-user
/// store).
let private userPinningScope (accessContext: AccessContext) : StorageScope option =
    match accessContext.Subject with
    | AnonymousSession _ -> None
    | _ ->
        Some {
            ScopeId = accessContext.UserId
            Container = $"user-{accessContext.UserId}"
            Persist = true
        }

/// Decode an id list from a stored `String`-field value. The persisted
/// value is a JSON-quoted string whose content is a JSON array of ids;
/// any decode failure degrades to an empty list (never throws).
let private decodeIds (rawQuoted: string) : string list =
    try
        let inner = System.Text.Json.JsonSerializer.Deserialize<string> rawQuoted
        System.Text.Json.JsonSerializer.Deserialize<string[]> inner |> List.ofArray
    with _ -> []

/// Encode an id list for a `String` config field. The config-store
/// validator requires the field value to itself be a JSON string, so
/// the id array is serialised to JSON array text and that text is then
/// JSON-quoted — `["a","b"]` ⇒ `"[\"a\",\"b\"]"` — the exact shape
/// `decodeIds` reverses on read.
let private encodeIds (ids: string list) : string =
    let arrayText = System.Text.Json.JsonSerializer.Serialize(List.toArray ids)
    System.Text.Json.JsonSerializer.Serialize arrayText

let private resolveConfigStore (ctx: HttpContext) : IConfigStore option =
    match ctx.RequestServices.GetService(typeof<IConfigStore>) with
    | :? IConfigStore as cs -> Some cs
    | _ -> None

/// Read the caller's recents/pinning state. `empty` when anonymous, no
/// config store, or nothing stored yet.
let private loadPinning (ctx: HttpContext) (accessContext: AccessContext) : Async<HomePinningState> =
    match resolveConfigStore ctx, userPinningScope accessContext with
    | Some store, Some scope -> async {
        let! raw = store.GetRaw(scope, pinningModuleKey)

        let ids key =
            raw |> Map.tryFind key |> Option.map decodeIds |> Option.defaultValue []

        return {
            Pinned = ids pinnedField
            Recent = ids recentField
        }
      }
    | _ -> async { return HomePinningState.empty }

/// Persist the caller's recents/pinning state, then return it. A write
/// failure (or anonymous / no-store) is swallowed — the in-memory state
/// is still returned so the client reflects the user's action.
let private savePinning
    (ctx: HttpContext)
    (accessContext: AccessContext)
    (state: HomePinningState)
    : Async<HomePinningState> =
    match resolveConfigStore ctx, userPinningScope accessContext with
    | Some store, Some scope -> async {
        let values =
            Map [ pinnedField, encodeIds state.Pinned; recentField, encodeIds state.Recent ]

        let! _ = store.SetRaw(scope, pinningModuleKey, values, pinningSchema)
        return state
      }
    | _ -> async { return state }

/// Build the `IHomeOverviewApi` Fable.Remoting handler.
let homeOverviewApi (ctx: HttpContext) : IHomeOverviewApi =
    let accessContext = resolveAccessContext ctx
    let scopeId = resolveScopeId accessContext

    {
        GetOverview =
            fun () -> async {
                let! tools = buildTools ctx accessContext scopeId
                let! activeAi = buildActiveAi ctx scopeId
                let! health = buildHealth ctx accessContext
                let! widgetData = buildWidgetData ctx scopeId

                return {
                    Tools = tools
                    ActiveAi = activeAi
                    Deployment = {
                        Mode = modeLabel accessContext
                        Health = health
                    }
                    GeneratedAt = DateTime.UtcNow
                    // Phase 217 — merged from every registered
                    // `IHomeWidgetDataProvider`; empty (the default) when
                    // none is composed (GP 13).
                    WidgetData = widgetData
                }
            }

        // Phase 217 — per-user recents/pinning.
        GetPinning = fun () -> loadPinning ctx accessContext

        RecordVisit =
            fun moduleId -> async {
                let! current = loadPinning ctx accessContext
                // Most-recent-first, deduped, bounded.
                let recent =
                    moduleId :: (current.Recent |> List.filter (fun m -> m <> moduleId))
                    |> List.truncate recentsCap

                return! savePinning ctx accessContext { current with Recent = recent }
            }

        SetPinned =
            fun req -> async {
                let! current = loadPinning ctx accessContext

                let pinned =
                    if req.Pinned then
                        if List.contains req.ModuleId current.Pinned then
                            current.Pinned
                        else
                            current.Pinned @ [ req.ModuleId ]
                    else
                        current.Pinned |> List.filter (fun m -> m <> req.ModuleId)

                return! savePinning ctx accessContext { current with Pinned = pinned }
            }
    }