module ToolUp.AuthProviders.EntraDirectory

open System
open System.Net.Http
open System.Net.Http.Headers
open System.Text.Json
open System.Threading
open Azure.Core
open Azure.Identity
open ToolUp.Platform

// ─── 0.5.7 — Microsoft Graph IUserDirectory companion ───────────────
//
// Implements `IUserDirectory` against the Microsoft Graph `/users`
// endpoint, returning matching directory entries for the SDK's
// `TeamManagerUI` typeahead. Plugs into `ServerApp.withUserDirectory`
// at compose time.
//
// **Authentication model.** App-only via Azure Identity's
// `DefaultAzureCredential`:
//
//   - On Azure (App Service, Container Apps, AKS, etc.) the system /
//     user-assigned managed identity is picked up automatically.
//   - Locally, `DefaultAzureCredential` walks env vars → VS sign-in →
//     Azure CLI (`az login`) → Azure PowerShell. For local dev, run
//     `az login` once; the SDK caches the access token.
//
// The caller identity must hold the `User.ReadBasic.All` (or
// `User.Read.All`) **application** permission on Microsoft Graph and
// the deploying tenant admin must have consented to it. For a managed
// identity, grant the role via:
//
// ```powershell
// $msi = Get-AzADServicePrincipal -DisplayName "<app-service-name>"
// $graph = Get-AzADServicePrincipal -ApplicationId "00000003-0000-0000-c000-000000000000"
// $role = $graph.AppRole | ? { $_.Value -eq "User.ReadBasic.All" }
// New-AzADAppRoleAssignment `
//     -ObjectId $msi.Id `
//     -PrincipalId $msi.Id `
//     -ResourceId $graph.Id `
//     -AppRoleId $role.Id
// ```
//
// **Query shape.** Graph's `$filter=startswith(...)` against multiple
// fields is the most operator-friendly: matches against displayName,
// mail, and userPrincipalName all surface. The `ConsistencyLevel:
// eventual` header is required for `$count` / advanced query semantics
// but isn't strictly needed for plain `startswith` — included anyway
// because it future-proofs the query if we later want `$search`.
//
// **Caching.** A single `HttpClient` + a single `TokenCredential` are
// shared across calls. Azure Identity caches tokens in-process; the
// companion does not add its own cache layer.
//
// **Errors.** Transient Graph 429 / 5xx surface as
// `Error "directory unavailable: …"` — the UI renders the string
// under the typeahead. Empty / blank queries short-circuit upstream
// in `UserDirectoryApiHandler` before reaching this companion.

/// Configuration for `EntraDirectoryUserDirectory`. The only mandatory
/// field is the Graph endpoint — overridable so the companion can run
/// against the Graph national clouds (Government, China) where the
/// default `graph.microsoft.com` host doesn't resolve. The default is
/// the commercial cloud, which covers the overwhelming majority of
/// deployments.
type EntraDirectoryConfig = {
    /// Graph endpoint base — e.g. `https://graph.microsoft.com` (default,
    /// commercial cloud), `https://graph.microsoft.us` (US Gov),
    /// `https://microsoftgraph.chinacloudapi.cn` (China). Trailing slash
    /// optional; the companion normalises.
    GraphEndpoint: string
}

module EntraDirectoryConfig =
    let defaults = {
        GraphEndpoint = "https://graph.microsoft.com"
    }

// ─── Internal: shared HttpClient + token credential ──────────────────

module private GraphState =
    // One HttpClient process-wide; `HttpClientFactory` is the canonical
    // ASP.NET Core idiom but the companion is constructed before DI is
    // wired (it lives in compose-time `withUserDirectory`). The static
    // `HttpClient` is safe because the companion is itself a singleton
    // resolved from DI; one client survives the process lifetime.
    let private httpClient = new HttpClient(Timeout = TimeSpan.FromSeconds 15.0)

    let getClient () : HttpClient = httpClient

    // `DefaultAzureCredential` is also process-wide. Azure Identity
    // caches tokens internally so consecutive `GetTokenAsync` calls
    // within the token lifetime return the cached value without
    // touching IMDS / the STS.
    let private credential = DefaultAzureCredential() :> TokenCredential

    let getCredential () : TokenCredential = credential

// ─── Internal: Graph response shape ──────────────────────────────────

[<CLIMutable>]
type private GraphUser = {
    id: string
    displayName: string
    mail: string
    userPrincipalName: string
}

[<CLIMutable>]
type private GraphUsersResponse = { value: GraphUser array }

// ─── IUserDirectory implementation ───────────────────────────────────

/// Graph-backed `IUserDirectory`. One instance per deployment;
/// registered via `ServerApp.withUserDirectory`.
type EntraDirectoryUserDirectory(config: EntraDirectoryConfig) =
    let normalisedEndpoint = config.GraphEndpoint.TrimEnd('/')

    let scopes = [| sprintf "%s/.default" normalisedEndpoint |]

    let jsonOptions =
        let o = JsonSerializerOptions()
        o.PropertyNameCaseInsensitive <- true
        o

    // Escape single-quotes inside the $filter literal per OData rules —
    // double them up. Graph rejects unescaped quotes with a 400.
    let escapeODataLiteral (s: string) = s.Replace("'", "''")

    interface IUserDirectory with
        member _.Search (query: string) (maxResults: int) = async {
            try
                let! ct = Async.CancellationToken
                let trimmed = query.Trim()

                // Minimum prefix length — Graph rate-limits aggressively
                // on single-char prefixes and the result set is too
                // broad to be useful anyway. The UI's debounce typically
                // suppresses this, but enforcing it server-side too
                // protects against a client that bypasses the debounce.
                if trimmed.Length < 2 then
                    return Ok []
                else
                    // Token acquisition. Synchronous from the caller's
                    // perspective; Azure Identity returns from cache when
                    // the token is fresh.
                    let tokenRequest = TokenRequestContext scopes

                    let! token =
                        GraphState.getCredential().GetTokenAsync(tokenRequest, ct).AsTask()
                        |> Async.AwaitTask

                    let safeQuery = escapeODataLiteral trimmed

                    let filter =
                        sprintf
                            "startswith(displayName,'%s') or startswith(mail,'%s') or startswith(userPrincipalName,'%s')"
                            safeQuery
                            safeQuery
                            safeQuery

                    let url =
                        sprintf
                            "%s/v1.0/users?$top=%d&$select=id,displayName,mail,userPrincipalName&$filter=%s"
                            normalisedEndpoint
                            maxResults
                            (Uri.EscapeDataString filter)

                    use request = new HttpRequestMessage(HttpMethod.Get, url)
                    request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token.Token)
                    // ConsistencyLevel: eventual is required for $count
                    // / $search; harmless on plain $filter requests but
                    // future-proofs the call if we later want $search.
                    request.Headers.Add("ConsistencyLevel", "eventual")

                    let client = GraphState.getClient ()
                    let! response = client.SendAsync(request, ct) |> Async.AwaitTask

                    if not response.IsSuccessStatusCode then
                        let! body = response.Content.ReadAsStringAsync ct |> Async.AwaitTask

                        return
                            Error(
                                sprintf
                                    "directory unavailable: %d %s — %s"
                                    (int response.StatusCode)
                                    (if isNull response.ReasonPhrase then
                                         ""
                                     else
                                         response.ReasonPhrase)
                                    (if isNull body then
                                         ""
                                     else
                                         body.Substring(0, min 200 body.Length))
                            )
                    else
                        let! bodyStream = response.Content.ReadAsStreamAsync ct |> Async.AwaitTask

                        let! parsed =
                            JsonSerializer.DeserializeAsync<GraphUsersResponse>(bodyStream, jsonOptions, ct).AsTask()
                            |> Async.AwaitTask

                        let summaries =
                            if isNull (box parsed) || isNull (box parsed.value) then
                                []
                            else
                                parsed.value
                                |> Array.toList
                                |> List.map (fun u ->
                                    let displayName =
                                        if String.IsNullOrWhiteSpace u.displayName then
                                            (if isNull u.userPrincipalName then
                                                 "(unknown)"
                                             else
                                                 u.userPrincipalName)
                                        else
                                            u.displayName

                                    let email =
                                        if not (String.IsNullOrWhiteSpace u.mail) then
                                            u.mail
                                        elif not (String.IsNullOrWhiteSpace u.userPrincipalName) then
                                            u.userPrincipalName
                                        else
                                            ""

                                    {
                                        UserId = (if isNull u.id then "" else u.id)
                                        DisplayName = displayName
                                        Email = email
                                    })
                                // Drop entries that arrived without an oid — defensive;
                                // Graph always returns id, but a malformed response is
                                // safer to drop than to surface a blank-id row.
                                |> List.filter (fun s -> s.UserId <> "")

                        return Ok summaries
            with
            | :? OperationCanceledException -> return Error "directory request cancelled"
            | ex -> return Error(sprintf "directory unavailable: %s" ex.Message)
        }

// ─── Public entry points ─────────────────────────────────────────────

/// Construct an `IUserDirectory` from an `EntraDirectoryConfig`. The
/// underlying Graph HTTP client is lazily initialised on the first
/// `Search` call; no startup work, no eager auth probe.
let create (config: EntraDirectoryConfig) : IUserDirectory =
    EntraDirectoryUserDirectory config :> IUserDirectory

/// Default-cloud convenience — equivalent to
/// `create EntraDirectoryConfig.defaults`. Suitable for any deployment
/// targeting the commercial Microsoft Graph cloud
/// (`https://graph.microsoft.com`); national-cloud deployments use
/// `create` with an overridden `GraphEndpoint`.
let fromManagedIdentity () : IUserDirectory = create EntraDirectoryConfig.defaults

/// Read the Graph endpoint from `TOOLUP_ENTRA_DIRECTORY_GRAPH_ENDPOINT`
/// and construct the companion. Returns `None` when the env var is
/// unset / empty AND no `TOOLUP_ENTRA_DIRECTORY_ENABLED=1` opt-in is
/// set — composition roots that read this should fall back to wiring
/// the typeahead without a directory companion (the SDK handler
/// short-circuits to `Ok []` and the UI degrades gracefully). When
/// `TOOLUP_ENTRA_DIRECTORY_ENABLED=1` is set without an explicit
/// endpoint, the commercial cloud default is used.
let fromEnv () : IUserDirectory option =
    let enabled =
        match Environment.GetEnvironmentVariable "TOOLUP_ENTRA_DIRECTORY_ENABLED" with
        | "1"
        | "true"
        | "TRUE" -> true
        | _ -> false

    let explicitEndpoint =
        match Environment.GetEnvironmentVariable "TOOLUP_ENTRA_DIRECTORY_GRAPH_ENDPOINT" with
        | null
        | "" -> None
        | url -> Some url

    match enabled, explicitEndpoint with
    | false, None -> None
    | _, Some endpoint -> Some(create { GraphEndpoint = endpoint })
    | true, None -> Some(fromManagedIdentity ())

// ─── Phase 9c portability audit (six rules) ──────────────────────────
//
// 1. Identity by value — `IUserDirectory.Search` returns
//    `Result<UserSummary list, string>`; UserSummary carries only
//    strings.
// 2. Async at every boundary — every method returns `Async<_>`; HTTP
//    Tasks bridged via `Async.AwaitTask`.
// 3. Retry as data — none expressed by this companion. The Graph
//    `HttpClient` could be wrapped in a Polly retry handler by the
//    composition root if needed; the companion stays a pure pass-through
//    so failure modes surface as `Error` strings the UI renders.
// 4. Stateless between calls — the cached HttpClient + TokenCredential
//    are shared across calls; no per-call state survives. Distributed-
//    safe: every node with the same managed identity / app role
//    produces identical results.
// 5. No cross-shard ordering — Graph returns results in its own natural
//    order; the UI sorts by displayName for stable rendering.
// 6. Precision at the lower bound — N/A; `IUserDirectory` has no
//    timing semantics.