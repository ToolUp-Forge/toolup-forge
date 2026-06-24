module ToolUp.AuthProviders.EntraDirectory

open System
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open Azure.Core
open Azure.Identity
open ToolUp.Platform

// ─── 0.5.7 — Microsoft Graph IUserDirectory companion ───────────────
//
// Implements `IUserDirectory` against Microsoft Graph:
//
//   * `SearchUsers` → `GET /users?$filter=startswith(displayName,'q') …`
//     Requires `User.ReadBasic.All` (or `User.Read.All`) application
//     permission. Powers the SDK's invitation-form typeahead.
//
//   * `ResolveUsers` → `POST /directoryObjects/getByIds`
//     Reverse batch lookup (id → display name + email) for the
//     Platform-Management admin tables. Same `User.ReadBasic.All`
//     permission as SearchUsers; up to 1000 ids per request.
//
//   * `NotifyInvitation` → `POST /users/{senderOid}/sendMail`
//     Requires `Mail.Send` application permission, scoped to the
//     mailbox identified by `SenderUserId`. Sends a branded
//     "you've been invited to <team>" email to the invitee.
//     When `SenderUserId = None`, the call returns `Error` and the
//     team-invite handler silently skips notification — the invite
//     still lands via the existing pending-by-email store.
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
// Both permissions are application-scoped — the calling identity
// (typically a managed identity) gets the role assignment in Entra
// once; tenant admin consent is required. See README for the
// PowerShell snippet.
//
// **Query shape.** Graph's `$filter=startswith(...)` against
// `displayName`, `mail`, and `userPrincipalName` is the most
// operator-friendly: matches against any of the three surface.
// The `ConsistencyLevel: eventual` header future-proofs the request
// for `$count` / `$search` semantics we may want later.
//
// **Caching.** A single `HttpClient` + a single `TokenCredential` are
// shared across calls. Azure Identity caches tokens in-process; the
// companion does not add its own cache layer.
//
// **Errors.** Transient Graph 429 / 5xx surface as
// `Error "directory unavailable: …"`. Mail-send failures surface as
// `Error "notification unavailable: …"` — `TeamInvitationHandler`
// swallows the error and the pending-invite store entry remains
// authoritative.

/// Configuration for `EntraDirectoryUserDirectory`. Two fields:
///
/// - `GraphEndpoint` — Graph base URL. Defaults to commercial cloud;
///   national clouds (US Gov, China) override.
/// - `SenderUserId` — Entra `oid` of the mailbox outbound invitation
///   emails are sent FROM. `None` disables `NotifyInvitation` (the
///   companion still does directory search). Wire a dedicated service
///   account such as `invites@yourdomain.example` so the From: header
///   matches the brand voice of the email.
type EntraDirectoryConfig = {
    /// Graph endpoint base — e.g. `https://graph.microsoft.com` (default,
    /// commercial cloud), `https://graph.microsoft.us` (US Gov),
    /// `https://microsoftgraph.chinacloudapi.cn` (China). Trailing slash
    /// optional; the companion normalises.
    GraphEndpoint: string
    /// Entra `oid` of the mailbox that outbound `NotifyInvitation`
    /// emails are sent FROM. Set to the object id of a service-account
    /// mailbox the managed identity has `Mail.Send` over. `None`
    /// disables `NotifyInvitation` (the substrate's other capability —
    /// `SearchUsers` — is unaffected).
    SenderUserId: string option
}

module EntraDirectoryConfig =
    let defaults = {
        GraphEndpoint = "https://graph.microsoft.com"
        SenderUserId = None
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

// 0.5.12 — STJ requires a PUBLIC parameterless constructor.
// `type private` on the record makes `[<CLIMutable>]`'s generated
// ctor non-public, and `JsonSerializer.Deserialize` rejects it with
// `Deserialization of types without a parameterless constructor ...
// is not supported. Type 'GraphUsersResponse'`. Dropping `private`
// keeps the type module-internal (still not exported from the .fs
// file's namespace surface) but gives STJ a public ctor to reach.
[<CLIMutable>]
type GraphUser = {
    id: string
    displayName: string
    mail: string
    userPrincipalName: string
}

[<CLIMutable>]
type GraphUsersResponse = { value: GraphUser array }

// ─── Internal: invitation-email rendering ────────────────────────────

let private roleLabel (role: TeamRole) =
    match role with
    | Owner -> "Owner"
    | Admin -> "Admin"
    | Member -> "Member"

let private renderInvitationHtml (n: InvitationNotification) : string =
    let enc (s: string) = WebUtility.HtmlEncode s

    let inviterLine =
        match n.InviterName with
        | Some name -> sprintf "<p>%s has invited you" (enc name)
        | None -> "<p>You've been invited"

    sprintf
        """<html><body style="font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif; color: #222;">
%s to join the <strong>%s</strong> team on <strong>%s</strong> as a <strong>%s</strong>.</p>
<p><a href="%s" style="display: inline-block; padding: 10px 18px; background: #6b21a8; color: white; text-decoration: none; border-radius: 6px;">Open %s</a></p>
<p style="color: #666; font-size: 12px;">Sign in with your work account to accept this invitation. If the button doesn't work, paste this URL into your browser: %s</p>
</body></html>"""
        inviterLine
        (enc n.TeamName)
        (enc n.AppName)
        (roleLabel n.Role)
        n.RedirectUrl
        (enc n.AppName)
        n.RedirectUrl

let private buildSendMailPayload (n: InvitationNotification) : string =
    // Build the Graph `sendMail` request body by hand. Hand-rolling
    // (rather than reaching for a JSON object DOM) keeps the
    // dependency surface to just `System.Text.Json`. The HTML body
    // and free-form fields are escaped via `JsonEncodedText` /
    // `JsonSerializer.Serialize` so quotes / backslashes round-trip
    // cleanly through Graph.
    let encStr (s: string) = JsonSerializer.Serialize s
    let html = renderInvitationHtml n
    let subject = sprintf "You've been invited to %s on %s" n.TeamName n.AppName

    sprintf
        """{"message":{"subject":%s,"body":{"contentType":"HTML","content":%s},"toRecipients":[{"emailAddress":{"address":%s}}]},"saveToSentItems":false}"""
        (encStr subject)
        (encStr html)
        (encStr n.Email)

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

    let acquireGraphToken () = async {
        let! ct = Async.CancellationToken
        let request = TokenRequestContext scopes

        let! token =
            GraphState.getCredential().GetTokenAsync(request, ct).AsTask()
            |> Async.AwaitTask

        return token.Token
    }

    let nonEmpty (s: string) =
        if String.IsNullOrWhiteSpace s then None else Some s

    interface IUserDirectory with
        member _.SearchUsers(query: string, take: int) = async {
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
                    let! token = acquireGraphToken ()

                    let safeQuery = escapeODataLiteral trimmed

                    let filter =
                        sprintf
                            "startswith(displayName,'%s') or startswith(mail,'%s') or startswith(userPrincipalName,'%s')"
                            safeQuery
                            safeQuery
                            safeQuery

                    // 0.5.11 — `$count=true` is REQUIRED on the Users
                    // collection when `$filter` combines multiple
                    // `startswith` clauses with `or`. Without it, Graph
                    // applies only the first clause and silently returns
                    // an empty (or partial) array — the response is 200
                    // OK but the operator sees an empty dropdown when
                    // typing an email or UPN prefix. Per Graph "advanced
                    // query" docs, the trio is: `$count=true` +
                    // `ConsistencyLevel: eventual` header + the OR
                    // filter. The header was already set; the count
                    // param was the missing piece.
                    let url =
                        sprintf
                            "%s/v1.0/users?$count=true&$top=%d&$select=id,displayName,mail,userPrincipalName&$filter=%s"
                            normalisedEndpoint
                            take
                            (Uri.EscapeDataString filter)

                    use request = new HttpRequestMessage(HttpMethod.Get, url)
                    request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)
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
                                |> List.choose (fun u ->
                                    match nonEmpty u.id with
                                    | None ->
                                        // Defensive — Graph always returns id, but
                                        // a malformed response is safer to drop
                                        // than to surface a blank-id row.
                                        None
                                    | Some uid ->
                                        let email =
                                            match nonEmpty u.mail with
                                            | Some _ as m -> m
                                            | None -> nonEmpty u.userPrincipalName

                                        Some {
                                            UserId = uid
                                            DisplayName = nonEmpty u.displayName
                                            Email = email
                                        })

                        return Ok summaries
            with
            | :? OperationCanceledException -> return Error "directory request cancelled"
            | ex -> return Error(sprintf "directory unavailable: %s" ex.Message)
        }

        member _.ResolveUsers(ids: string list) = async {
            // Reverse lookup via Graph `directoryObjects/getByIds` — the
            // canonical batch id→object endpoint. Accepts up to 1000 ids
            // per request; we chunk defensively in case a caller passes
            // more. Needs only `User.ReadBasic.All` (already required for
            // SearchUsers) — `getByIds` returns the same basic profile
            // fields. Ids the directory doesn't recognise are simply
            // absent from the response; we never surface them as errors.
            try
                let! ct = Async.CancellationToken

                let distinctIds = ids |> List.choose nonEmpty |> List.distinct

                if List.isEmpty distinctIds then
                    return Ok []
                else
                    let! token = acquireGraphToken ()
                    let client = GraphState.getClient ()
                    let url = sprintf "%s/v1.0/directoryObjects/getByIds" normalisedEndpoint

                    // Graph caps getByIds at 1000 ids/request; chunk to be safe.
                    let chunks = distinctIds |> List.chunkBySize 1000

                    let encStr (s: string) = JsonSerializer.Serialize s

                    let mutable failure: string option = None
                    let collected = ResizeArray<UserSummary>()
                    let mutable remaining = chunks

                    while failure.IsNone && not (List.isEmpty remaining) do
                        let chunk = List.head remaining
                        remaining <- List.tail remaining

                        // Restrict to user objects so groups / service
                        // principals sharing an id space don't surface.
                        let idsJson = chunk |> List.map encStr |> String.concat ","

                        let payload = sprintf """{"ids":[%s],"types":["user"]}""" idsJson

                        use request = new HttpRequestMessage(HttpMethod.Post, url)
                        request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)
                        request.Content <- new StringContent(payload, Encoding.UTF8, "application/json")

                        let! response = client.SendAsync(request, ct) |> Async.AwaitTask

                        if not response.IsSuccessStatusCode then
                            let! body = response.Content.ReadAsStringAsync ct |> Async.AwaitTask

                            failure <-
                                Some(
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
                                JsonSerializer
                                    .DeserializeAsync<GraphUsersResponse>(bodyStream, jsonOptions, ct)
                                    .AsTask()
                                |> Async.AwaitTask

                            if not (isNull (box parsed)) && not (isNull (box parsed.value)) then
                                parsed.value
                                |> Array.iter (fun u ->
                                    match nonEmpty u.id with
                                    | None -> ()
                                    | Some uid ->
                                        let email =
                                            match nonEmpty u.mail with
                                            | Some _ as m -> m
                                            | None -> nonEmpty u.userPrincipalName

                                        collected.Add {
                                            UserId = uid
                                            DisplayName = nonEmpty u.displayName
                                            Email = email
                                        })

                    match failure with
                    | Some err -> return Error err
                    | None -> return Ok(List.ofSeq collected)
            with
            | :? OperationCanceledException -> return Error "directory request cancelled"
            | ex -> return Error(sprintf "directory unavailable: %s" ex.Message)
        }

        member _.NotifyInvitation(notification: InvitationNotification) = async {
            match config.SenderUserId with
            | None ->
                // The companion was wired without a mail-send mailbox.
                // Surface the reason so an operator inspecting logs can
                // either ignore it (invites still land via the pending-
                // store) or wire a sender id to enable email.
                return Error "notification disabled: EntraDirectoryConfig.SenderUserId not set"
            | Some sender ->
                try
                    let! ct = Async.CancellationToken
                    let! token = acquireGraphToken ()

                    let url = sprintf "%s/v1.0/users/%s/sendMail" normalisedEndpoint sender

                    use request = new HttpRequestMessage(HttpMethod.Post, url)
                    request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)
                    let payload = buildSendMailPayload notification

                    request.Content <- new StringContent(payload, Encoding.UTF8, "application/json")

                    let client = GraphState.getClient ()
                    let! response = client.SendAsync(request, ct) |> Async.AwaitTask

                    if response.IsSuccessStatusCode then
                        return Ok()
                    else
                        let! body = response.Content.ReadAsStringAsync ct |> Async.AwaitTask

                        return
                            Error(
                                sprintf
                                    "notification unavailable: %d %s — %s"
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
                with
                | :? OperationCanceledException -> return Error "notification request cancelled"
                | ex -> return Error(sprintf "notification unavailable: %s" ex.Message)
        }

// ─── Public entry points ─────────────────────────────────────────────

/// Construct an `IUserDirectory` from an explicit
/// `EntraDirectoryConfig`. The underlying Graph HTTP client is lazily
/// initialised on the first `SearchUsers` / `NotifyInvitation` call;
/// no startup work, no eager auth probe.
let create (config: EntraDirectoryConfig) : IUserDirectory =
    EntraDirectoryUserDirectory config :> IUserDirectory

/// Default-cloud convenience — equivalent to
/// `create EntraDirectoryConfig.defaults`. Suitable for any deployment
/// targeting the commercial Microsoft Graph cloud
/// (`https://graph.microsoft.com`). `NotifyInvitation` is disabled
/// (returns `Error`) because no `SenderUserId` is supplied; use
/// `create` with `{ EntraDirectoryConfig.defaults with SenderUserId = Some "<oid>" }`
/// or `fromEnv` when invitation emails are wanted.
let fromManagedIdentity () : IUserDirectory = create EntraDirectoryConfig.defaults

/// Environment-driven factory. Reads:
///
/// * `TOOLUP_ENTRA_DIRECTORY_ENABLED` — `1` / `true` to opt in.
/// * `TOOLUP_ENTRA_DIRECTORY_GRAPH_ENDPOINT` — Graph base URL override
///   (defaults to commercial cloud).
/// * `TOOLUP_ENTRA_DIRECTORY_SENDER_OID` — Entra `oid` of the mailbox
///   that outbound `NotifyInvitation` emails are sent FROM. When unset
///   the companion's `NotifyInvitation` returns `Error` and the
///   team-invite handler skips the email step (pending-store entry
///   still lands).
///
/// Returns `None` when neither `_ENABLED` is `1` nor `_GRAPH_ENDPOINT`
/// is set — composition roots that read this should fall back to
/// wiring the typeahead without a directory companion (the SDK
/// handler short-circuits to `Ok []` and the UI degrades gracefully).
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

    let senderOid =
        match Environment.GetEnvironmentVariable "TOOLUP_ENTRA_DIRECTORY_SENDER_OID" with
        | null
        | "" -> None
        | v -> Some v

    let baseEndpoint =
        explicitEndpoint
        |> Option.defaultValue EntraDirectoryConfig.defaults.GraphEndpoint

    match enabled, explicitEndpoint with
    | false, None -> None
    | _ ->
        Some(
            create {
                GraphEndpoint = baseEndpoint
                SenderUserId = senderOid
            }
        )

// ─── Phase 9c portability audit (six rules) ──────────────────────────
//
// 1. Identity by value — both `IUserDirectory.SearchUsers` and
//    `NotifyInvitation` operate over plain records / strings.
// 2. Async at every boundary — every method returns `Async<_>`; HTTP
//    Tasks bridged via `Async.AwaitTask`.
// 3. Retry as data — none expressed by this companion. The Graph
//    `HttpClient` could be wrapped in a Polly retry handler by the
//    composition root if needed; the companion stays a pure
//    pass-through so failure modes surface as `Error` strings the
//    caller (the team-invite handler or the typeahead UI) can render
//    or swallow as appropriate.
// 4. Stateless between calls — the cached HttpClient + TokenCredential
//    are shared across calls; no per-call state survives. Distributed-
//    safe: every node with the same managed identity / app role
//    produces identical results.
// 5. No cross-shard ordering — Graph returns results in its own natural
//    order; the UI sorts by displayName for stable rendering. Mail
//    delivery ordering is the SMTP / Graph mailer's concern.
// 6. Precision at the lower bound — N/A; `IUserDirectory` has no
//    timing semantics.