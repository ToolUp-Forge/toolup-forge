module ToolUp.Platform.UsageQueryApiHandler

open System
open System.Globalization
open System.Text
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.TeamManagement
open ToolUp.Platform.Usage

// ─── Phase 9d — Usage admin API handler ──────────────────────────
//
// Production-safe Owner / Admin Fable.Remoting surface that surfaces
// the billing-relevant subset of `IUsageLog`. Mirrors `HealthMonitor-
// ApiHandler` for RBAC shape:
//
//   - Anonymous: short-circuited with `Error` — Anonymous deployments
//     have no role concept and exposing usage to every visitor is a
//     reconnaissance gift (cost telemetry leaks tenant size).
//   - Team / MultiTeam: Owner / Admin only.
//   - Individual / AuthenticatedEphemeral: any authenticated user
//     sees their own scope's usage.
//
// **GP 4 enforcement.** `accessContext.ScopeId` is the only scope the
// handler reads from. Caller-supplied date ranges and resource-kind
// filters are pass-throughs to `IUsageLog.Query` / `Aggregate`; the
// caller cannot override the scope. Cross-team isolation is
// structural — `IUsageLog`'s blob layout keys on `ScopeId` and never
// enumerates outside the requested scope.

let private resolveAccessContext (ctx: HttpContext) : AccessContext =
    match ctx.RequestServices.GetService(typeof<AccessContext>) with
    | :? AccessContext as ac -> ac
    | _ ->
        let userId =
            match ctx.Items.TryGetValue "ToolUp.UserId" with
            | true, (:? string as id) -> id
            | _ -> "anonymous"

        let teamId =
            match ctx.Items.TryGetValue "ToolUp.StorageScope" with
            | true, (:? StorageScope as s) when s.Container.StartsWith "team-" -> Some s.ScopeId
            | _ -> None

        AccessContext.unrestricted (AnonymousSession userId)

let private ensureReadAllowed (ctx: HttpContext) (accessContext: AccessContext) : Async<Result<unit, string>> = async {
    match accessContext.Subject with
    | AnonymousSession _ -> return Error "Usage metering is not available in this mode."
    | TeamMember(userId, teamId) ->
        match ctx.RequestServices.GetService(typeof<ITeamStore>) with
        | :? ITeamStore as ts ->
            let! role = ts.GetMemberRole(teamId, userId)

            match role with
            | Some r when TeamRoles.canWriteTeamConfig r -> return Ok()
            | Some r ->
                return Error $"Only team owners and admins can view usage. Your role: {TeamRoles.displayName r}."
            | None -> return Error "You are not a member of this team."
        | _ -> return Error "Team management is not available in this deployment."
    | AuthenticatedUser _
    | ClaimBearer _ -> return Ok()
}

/// Resolve the caller's effective scope. Same idiom as
/// `WebhookApiHandler.scopeId` / `ConfigHandler` — uses the resolved
/// `StorageScope` from `AccessContext` and falls back to the user id
/// for non-team modes.
let private resolveScopeId (ctx: HttpContext) (accessContext: AccessContext) : string =
    match accessContext.TeamId with
    | Some teamId -> teamId
    | None -> accessContext.UserId

/// Build the `IUsageQueryApi` Fable.Remoting handler. Resolves
/// `IUsageLog` and `AccessContext` lazily from DI per request.
let usageQueryApi (ctx: HttpContext) : IUsageQueryApi =
    let accessContext = resolveAccessContext ctx
    let scopeId = resolveScopeId ctx accessContext

    let resolveStore () =
        match ctx.RequestServices.GetService(typeof<IUsageLog>) with
        | :? IUsageLog as store -> Some store
        | _ -> None

    let withGate (work: IUsageLog -> Async<'T>) (fallback: 'T) = async {
        let! gate = ensureReadAllowed ctx accessContext

        match gate with
        | Error _ -> return fallback
        | Ok() ->
            match resolveStore () with
            | None -> return fallback
            | Some store -> return! work store
    }

    let mapAggregate (m: Map<string, decimal>) : UsageAggregateRow list =
        m
        |> Map.toList
        |> List.map (fun (k, v) -> { Bucket = k; Quantity = v })
        |> List.sortBy _.Bucket

    let formatDate (ts: DateTime) =
        ts.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)

    let formatOrigin (o: ProviderOrigin option) =
        match o with
        | Some TenantBYOK -> "TenantBYOK"
        | Some PlatformManaged -> "PlatformManaged"
        | None -> ""

    let escapeCsvField (raw: string) =
        if raw.Contains '"' || raw.Contains ',' || raw.Contains '\n' then
            "\"" + raw.Replace("\"", "\"\"") + "\""
        else
            raw

    let renderMetadata (m: Map<string, string>) =
        if Map.isEmpty m then
            ""
        else
            m
            |> Map.toList
            |> List.map (fun (k, v) -> sprintf "%s=%s" k v)
            |> String.concat ";"

    let renderCsv (records: UsageRecord list) : byte[] =
        let header = "RecordId,ScopeId,ResourceKind,Quantity,Unit,Origin,Timestamp,Metadata"

        let lines =
            records
            |> List.sortBy _.Timestamp
            |> List.map (fun r ->
                [
                    r.RecordId.ToString "N"
                    r.ScopeId
                    r.ResourceKind
                    r.Quantity.ToString(CultureInfo.InvariantCulture)
                    r.Unit
                    formatOrigin r.Origin
                    formatDate r.Timestamp
                    renderMetadata r.Metadata
                ]
                |> List.map escapeCsvField
                |> String.concat ",")

        let body = (header :: lines) |> String.concat "\r\n"
        Encoding.UTF8.GetBytes(body + "\r\n")

    {
        Query =
            fun (resourceKind, range) ->
                withGate
                    (fun store ->
                        let r = range |> Option.map (fun rng -> rng.From, rng.To)
                        store.Query(scopeId, resourceKind, r))
                    []

        Aggregate =
            fun grouping ->
                withGate
                    (fun store -> async {
                        let! agg = store.Aggregate(scopeId, grouping)
                        return mapAggregate agg
                    })
                    []

        ExportCsv =
            fun range ->
                withGate
                    (fun store -> async {
                        let r = range |> Option.map (fun rng -> rng.From, rng.To)
                        let! records = store.Query(scopeId, None, r)
                        return renderCsv records
                    })
                    [||]
    }