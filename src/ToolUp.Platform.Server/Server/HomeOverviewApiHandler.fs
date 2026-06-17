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
let private buildTools (ctx: HttpContext) (scopeId: string) : Async<ToolSummary list> =
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

/// Build the `IHomeOverviewApi` Fable.Remoting handler.
let homeOverviewApi (ctx: HttpContext) : IHomeOverviewApi =
    let accessContext = resolveAccessContext ctx
    let scopeId = resolveScopeId accessContext

    {
        GetOverview =
            fun () -> async {
                let! tools = buildTools ctx scopeId
                let! activeAi = buildActiveAi ctx scopeId
                let! health = buildHealth ctx accessContext

                return {
                    Tools = tools
                    ActiveAi = activeAi
                    Deployment = {
                        Mode = modeLabel accessContext
                        Health = health
                    }
                    GeneratedAt = DateTime.UtcNow
                }
            }
    }