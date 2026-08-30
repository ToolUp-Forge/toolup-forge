// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.GoogleDirectory

open System
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open ToolUp.Platform
open ToolUp.Platform.Secrets
open ToolUp.AuthProviders.GoogleDirectoryAuth

// ─── Google Workspace IUserDirectory companion ───────────────────────
//
// Implements `IUserDirectory` against Google Workspace, the direct
// analogue of the Microsoft Graph `EntraDirectory` companion:
//
//   * `SearchUsers` → `GET /admin/directory/v1/users?query=…`
//     (Admin SDK Directory API). Powers the SDK's invitation-form
//     typeahead. Needs the
//     `admin.directory.user.readonly` scope granted to the service
//     account by domain-wide delegation.
//
//   * `ResolveUsers` → `GET /admin/directory/v1/users/{userKey}`, one
//     per id, bounded-concurrency. Reverse batch lookup
//     (id → display name + email) for the Platform-Management admin
//     tables. Same scope as `SearchUsers`. Ids the directory does not
//     recognise return 404 and are skipped, never surfaced as errors —
//     the caller joins by `UserId` and renders the raw id for anything
//     missing.
//
//   * `NotifyInvitation` → `POST /gmail/v1/users/me/messages/send`
//     (Gmail API), with the token impersonating `SenderUserId`, so the
//     From: line is the configured invitations mailbox. Needs the
//     `gmail.send` scope, delegated for that one mailbox. When
//     `SenderUserId = None` the call returns `Error` and the
//     team-invite handler silently skips notification — the invite
//     still lands via the existing pending-by-email store.
//
// **Authentication model.** Service-account JSON key + *domain-wide
// delegation* (DWD), the only application-scoped identity Google
// Workspace offers for these APIs. Two halves have to be in place, and
// they are configured in different consoles:
//
//   1. A service account in a Google Cloud project, with a JSON key,
//      and the Admin SDK API + Gmail API enabled on that project.
//   2. In the Workspace **admin** console (Security → Access and data
//      control → API controls → Domain-wide delegation), the service
//      account's numeric client ID authorised for the exact scope
//      strings this companion uses.
//
// A token is then minted per capability by impersonating a user: the
// directory read impersonates `ImpersonatedAdmin` (a Workspace admin —
// only an admin may list the directory), and the mail send impersonates
// `SenderUserId`. The whole exchange lives in `GoogleDirectoryAuth`;
// see that module's header for the JWT-grant mechanics. Nothing here
// touches a private key.
//
// **Credentials come through `ISecretStore`, never the environment.**
// `create` takes the store and a `(scopeId, key)` coordinate; the
// service-account JSON is read from it on first use and memoised. A
// DWD key can impersonate any user in the domain, so it belongs behind
// the deployment's audited secret backend rather than in a process env
// var or on the container filesystem — which is also why this companion
// ships no `fromEnv`, unlike `EntraDirectory` (whose managed-identity
// model has no key material to place).
//
// **Local development.** There is no `az login`-shaped shortcut here:
// Google's user-credential ADC flow cannot impersonate, so it cannot
// serve a domain-wide-delegation grant. The local story is therefore
// the same as production with a narrower blast radius — mint a
// *separate* service account against a test Workspace domain, delegate
// only `admin.directory.user.readonly`, and put its JSON in whatever
// `ISecretStore` the dev composition wires (the file-backed store under
// an encrypted master key is the usual choice). Leave `SenderUserId`
// unset locally and the mail path is inert. See README.
//
// **Query shape.** Google's Directory API `query` parameter takes
// `field:value` prefix terms, and multiple terms are ANDed — there is
// no OR. Matching display name OR email, which is what an operator
// typing into a typeahead means, is therefore two requests
// (`name:'…'` and `email:'…'`) merged and de-duplicated by id. That is
// a mechanical difference from `EntraDirectory`, which expresses the
// same intent as one OData `$filter` with `or` clauses; the observable
// behaviour is the same. See `runQuery` below.
//
// **Caching.** A single `HttpClient` is shared process-wide; the token
// provider caches one bearer per (subject, scopes) pair and refreshes
// ahead of expiry.
//
// **Errors.** Directory failures — including a credential that will not
// load and a delegation that was never granted — surface as
// `Error "directory unavailable: …"`. Mail-send failures surface as
// `Error "notification unavailable: …"`; `TeamInvitationHandler`
// swallows the error and the pending-invite store entry remains
// authoritative.

/// Configuration for `GoogleDirectoryUserDirectory`.
///
/// The two identity fields are the ones a deployment must get right:
/// `ImpersonatedAdmin` is *who the directory reads run as*, and
/// `SenderUserId` is *who the invitation email comes from*. Both are
/// Workspace user addresses in the domain the service account is
/// delegated over.
type GoogleDirectoryConfig = {
    /// Admin SDK Directory API base — `https://admin.googleapis.com`
    /// by default. Overridable for a private egress proxy or a test
    /// double. Trailing slash optional; the companion normalises.
    DirectoryEndpoint: string
    /// Gmail API base — `https://gmail.googleapis.com` by default.
    /// Same override rationale as `DirectoryEndpoint`.
    GmailEndpoint: string
    /// OAuth 2.0 token endpoint. A real key file names Google's own
    /// endpoint here too, so the two are resolved by treating that
    /// value as "unspecified": a key naming a NON-default `token_uri`
    /// wins (it is the audience the key was minted against), and
    /// anything else defers to this field. Override it for a private
    /// egress proxy or a test double.
    TokenEndpoint: string
    /// The Workspace primary domain the directory query is scoped to,
    /// e.g. `"example.com"`. Required — an unscoped Directory API
    /// query spans the whole customer account, which for a reseller
    /// or multi-domain tenant is not what the operator meant.
    Domain: string
    /// Workspace address of the ADMIN the service account impersonates
    /// for directory reads, e.g. `"admin@example.com"`. Required: the
    /// Directory API refuses a non-admin subject. Prefer a dedicated,
    /// least-privilege admin role (User Management, read-only) over a
    /// super-admin.
    ImpersonatedAdmin: string
    /// Workspace address of the mailbox outbound `NotifyInvitation`
    /// emails are sent FROM. `None` disables `NotifyInvitation` (the
    /// companion still does directory search). Wire a dedicated
    /// service account such as `invites@example.com` so the From:
    /// header matches the brand voice of the email.
    SenderUserId: string option
    /// `ISecretStore` scope the service-account JSON is stored under.
    /// `"_platform"` — the reserved platform scope — is the right
    /// answer for a deployment-wide credential; a per-tenant
    /// directory would use that tenant's scope id.
    CredentialScopeId: string
    /// `ISecretStore` key the service-account JSON is stored under.
    /// The stored value is the key file's contents verbatim, not a
    /// path to it.
    CredentialSecretKey: string
}

module GoogleDirectoryConfig =
    /// Endpoint + credential-coordinate defaults. `Domain` and
    /// `ImpersonatedAdmin` are deliberately left empty — there is no
    /// sensible default for "which Workspace domain", and a companion
    /// that silently queried the wrong one would be worse than one
    /// that refuses at preflight.
    let defaults = {
        DirectoryEndpoint = "https://admin.googleapis.com"
        GmailEndpoint = "https://gmail.googleapis.com"
        TokenEndpoint = DefaultTokenEndpoint
        Domain = ""
        ImpersonatedAdmin = ""
        SenderUserId = None
        CredentialScopeId = "_platform"
        CredentialSecretKey = "google_directory_service_account"
    }

// ─── Internal: shared HttpClient ─────────────────────────────────────

module private GoogleDirectoryState =
    // One HttpClient process-wide, for the same reason `EntraDirectory`
    // holds one: the companion is constructed at compose time, before
    // DI is wired, so `IHttpClientFactory` is not reachable. The
    // companion is itself a singleton, so one client survives the
    // process lifetime.
    let private httpClient = new HttpClient(Timeout = TimeSpan.FromSeconds 15.0)

    let getClient () : HttpClient = httpClient

// ─── Internal: Directory API response shape ──────────────────────────

// PUBLIC, not `private`, for the same reason `EntraDirectory`'s Graph
// records are: `[<CLIMutable>]` on a private record generates a
// non-public parameterless constructor, and STJ's reflection
// deserialiser rejects it at runtime with "Deserialization of types
// without a parameterless constructor … is not supported". The types
// stay module-scoped in every practical sense; they are simply
// reachable by the serialiser.
[<CLIMutable>]
type GoogleUserName = {
    fullName: string
    givenName: string
    familyName: string
}

[<CLIMutable>]
type GoogleUser = {
    id: string
    primaryEmail: string
    name: GoogleUserName
}

[<CLIMutable>]
type GoogleUsersResponse = { users: GoogleUser array }

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

let private base64Url (bytes: byte[]) =
    Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

/// Gmail sends RFC 2822, not a JSON message model — so the subject
/// header is always RFC 2047 base64-encoded rather than conditionally
/// so. A team name with an accent, an em dash, or a non-Latin script
/// would otherwise ride raw 8-bit bytes in a header field, which
/// receiving agents are entitled to mangle; encoding unconditionally
/// costs a few bytes and removes the branch that would only ever be
/// exercised by the users least likely to report it.
let private encodeSubjectHeader (subject: string) =
    "=?UTF-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes subject) + "?="

/// Strip the characters that would break out of the Directory API's
/// single-quoted query literal. Google documents no escape sequence
/// for `query`, so the only sound handling is removal — a typeahead
/// prefix containing a quote or a backslash is not a search anyone
/// meant to run.
let private sanitiseQueryTerm (s: string) =
    s |> String.filter (fun c -> c <> '\'' && c <> '\\')

let private buildRawMessage (sender: string) (n: InvitationNotification) : string =
    let html = renderInvitationHtml n
    let subject = sprintf "You've been invited to %s on %s" n.TeamName n.AppName

    // CRLF line endings and a blank line before the body: RFC 2822 is
    // explicit that the separator is CRLF CRLF, and Gmail rejects a
    // bare-LF message with a 400 that names nothing useful.
    let message =
        String.Join(
            "\r\n",
            [
                sprintf "From: %s" sender
                sprintf "To: %s" n.Email
                sprintf "Subject: %s" (encodeSubjectHeader subject)
                "MIME-Version: 1.0"
                "Content-Type: text/html; charset=\"UTF-8\""
                ""
                html
            ]
        )

    base64Url (Encoding.UTF8.GetBytes message)

let private buildSendPayload (sender: string) (n: InvitationNotification) : string =
    sprintf """{"raw":%s}""" (JsonSerializer.Serialize(buildRawMessage sender n))

// ─── IUserDirectory implementation ───────────────────────────────────

/// Google Workspace-backed `IUserDirectory`. One instance per
/// deployment; registered via `ServerApp.withUserDirectory`.
///
/// The `HttpClient` argument exists so the companion can be driven
/// against a stub transport in tests; production composition goes
/// through `create`, which supplies the shared process-wide client.
type GoogleDirectoryUserDirectory(config: GoogleDirectoryConfig, secrets: ISecretStore, http: HttpClient) =
    let directoryBase = config.DirectoryEndpoint.TrimEnd('/')
    let gmailBase = config.GmailEndpoint.TrimEnd('/')

    let jsonOptions =
        let o = JsonSerializerOptions()
        o.PropertyNameCaseInsensitive <- true
        o

    let loadKey () = async {
        let! stored = secrets.GetSecret(config.CredentialScopeId, config.CredentialSecretKey)

        match stored with
        | None ->
            return
                Result.Error(
                    sprintf
                        "no service-account credential at secret '%s/%s'"
                        config.CredentialScopeId
                        config.CredentialSecretKey
                )
        | Some json ->
            match parseServiceAccountJson json with
            | Result.Ok key ->
                // A key file that omits `token_uri` inherits the
                // configured endpoint rather than the module default,
                // so an endpoint override reaches the token exchange
                // too.
                return
                    Result.Ok {
                        key with
                            TokenUri =
                                if key.TokenUri = DefaultTokenEndpoint then
                                    config.TokenEndpoint
                                else
                                    key.TokenUri
                    }
            | Result.Error e -> return Result.Error e
    }

    let tokens = TokenProvider(http, loadKey)

    let nonEmpty (s: string) =
        if String.IsNullOrWhiteSpace s then None else Some s

    let directoryToken () =
        tokens.GetAsync(config.ImpersonatedAdmin, [ DirectoryReadonlyScope ])

    /// Bounded prefix of a failure body. Google's error envelopes lead
    /// with the reason, so 200 characters is enough to name the cause
    /// and short enough not to paste a directory page into a log.
    let describeFailure (verb: string) (status: HttpStatusCode) (body: string) =
        sprintf
            "%s: %d — %s"
            verb
            (int status)
            (if isNull body then
                 ""
             else
                 body.Substring(0, min 200 body.Length))

    let toSummary (u: GoogleUser) =
        match nonEmpty u.id with
        | None ->
            // Defensive — the Directory API always returns an id, but
            // a blank-id row in a typeahead is worse than a dropped
            // one.
            None
        | Some uid ->
            let displayName =
                if isNull (box u.name) then
                    None
                else
                    match nonEmpty u.name.fullName with
                    | Some _ as full -> full
                    | None ->
                        match nonEmpty u.name.givenName, nonEmpty u.name.familyName with
                        | Some g, Some f -> Some(g + " " + f)
                        | Some g, None -> Some g
                        | None, Some f -> Some f
                        | None, None -> None

            Some {
                UserId = uid
                DisplayName = displayName
                Email = nonEmpty u.primaryEmail
            }

    /// One Directory API `users.list` call for a single `field:value`
    /// query term.
    let runQuery (token: string) (query: string) (take: int) = async {
        let! ct = Async.CancellationToken

        let url =
            sprintf
                "%s/admin/directory/v1/users?domain=%s&maxResults=%d&projection=basic&viewType=admin_view&query=%s"
                directoryBase
                (Uri.EscapeDataString config.Domain)
                take
                (Uri.EscapeDataString query)

        use request = new HttpRequestMessage(HttpMethod.Get, url)
        request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)

        use! response = http.SendAsync(request, ct) |> Async.AwaitTask

        if not response.IsSuccessStatusCode then
            let! body = response.Content.ReadAsStringAsync ct |> Async.AwaitTask
            return Result.Error(describeFailure "directory unavailable" response.StatusCode body)
        else
            let! bodyStream = response.Content.ReadAsStreamAsync ct |> Async.AwaitTask

            let! parsed =
                JsonSerializer.DeserializeAsync<GoogleUsersResponse>(bodyStream, jsonOptions, ct).AsTask()
                |> Async.AwaitTask

            if isNull (box parsed) || isNull (box parsed.users) then
                return Result.Ok []
            else
                return Result.Ok(parsed.users |> Array.toList)
    }

    interface IUserDirectory with
        member _.SearchUsers(query: string, take: int) = async {
            try
                let trimmed = sanitiseQueryTerm (query.Trim())

                // Minimum prefix length. The Directory API is happy to
                // serve a one-character prefix, but the result set is
                // too broad to be useful and the request is pure cost.
                // The UI's debounce usually suppresses it; enforcing it
                // server-side protects against a client that does not.
                if trimmed.Length < 2 then
                    return Ok []
                else
                    match! directoryToken () with
                    | Result.Error e -> return Error(sprintf "directory unavailable: %s" e)
                    | Result.Ok token ->
                        // Two terms, because Google ANDs them within one
                        // `query`. `name:` matches given/family/full
                        // name as a prefix; `email:` matches the primary
                        // address and its aliases.
                        let! byName = runQuery token (sprintf "name:'%s'" trimmed) take
                        let! byEmail = runQuery token (sprintf "email:'%s'" trimmed) take

                        match byName, byEmail with
                        | Result.Error e, _
                        | _, Result.Error e -> return Error e
                        | Result.Ok named, Result.Ok mailed ->
                            let summaries =
                                named @ mailed
                                |> List.choose toSummary
                                |> List.distinctBy _.UserId
                                |> List.truncate take

                            return Ok summaries
            with
            | :? OperationCanceledException -> return Error "directory request cancelled"
            | ex -> return Error(sprintf "directory unavailable: %s" ex.Message)
        }

        member _.ResolveUsers(ids: string list) = async {
            // Per-id `users.get` rather than a batch endpoint: the
            // Directory API has no id-list read, and its batch HTTP
            // endpoint was retired. The cost is N small requests under
            // bounded concurrency — these tables hold a handful of ids.
            // Ids the directory does not recognise (deleted users, ids
            // minted by a different provider) return 404 and are
            // skipped, per the `IUserDirectory` contract.
            try
                let distinctIds = ids |> List.choose nonEmpty |> List.distinct

                if List.isEmpty distinctIds then
                    return Ok []
                else
                    match! directoryToken () with
                    | Result.Error e -> return Error(sprintf "directory unavailable: %s" e)
                    | Result.Ok token ->
                        let! ct = Async.CancellationToken

                        let resolveOne (id: string) = async {
                            let url =
                                sprintf
                                    "%s/admin/directory/v1/users/%s?projection=basic&viewType=admin_view"
                                    directoryBase
                                    (Uri.EscapeDataString id)

                            use request = new HttpRequestMessage(HttpMethod.Get, url)
                            request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)

                            use! response = http.SendAsync(request, ct) |> Async.AwaitTask

                            if int response.StatusCode = 404 then
                                return Result.Ok None
                            elif not response.IsSuccessStatusCode then
                                let! body = response.Content.ReadAsStringAsync ct |> Async.AwaitTask
                                return Result.Error(describeFailure "directory unavailable" response.StatusCode body)
                            else
                                let! bodyStream = response.Content.ReadAsStreamAsync ct |> Async.AwaitTask

                                let! parsed =
                                    JsonSerializer.DeserializeAsync<GoogleUser>(bodyStream, jsonOptions, ct).AsTask()
                                    |> Async.AwaitTask

                                if isNull (box parsed) then
                                    return Result.Ok None
                                else
                                    return Result.Ok(toSummary parsed)
                        }

                        // Cap fan-out so a large team does not open
                        // dozens of simultaneous Directory API requests.
                        let! results = Async.Parallel(distinctIds |> List.map resolveOne, maxDegreeOfParallelism = 8)

                        let firstError =
                            results
                            |> Array.tryPick (function
                                | Result.Error e -> Some e
                                | _ -> None)

                        match firstError with
                        | Some err -> return Error err
                        | None ->
                            return
                                Ok(
                                    results
                                    |> Array.choose (function
                                        | Result.Ok(Some s) -> Some s
                                        | _ -> None)
                                    |> Array.toList
                                )
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
                // store) or wire a sender to enable email.
                return Error "notification disabled: GoogleDirectoryConfig.SenderUserId not set"
            | Some sender ->
                try
                    let! ct = Async.CancellationToken
                    // The mail token impersonates the SENDER, not the
                    // directory admin — Gmail sends as whoever the token
                    // impersonates, so this is what puts the invitations
                    // mailbox on the From: line.
                    match! tokens.GetAsync(sender, [ GmailSendScope ]) with
                    | Result.Error e -> return Error(sprintf "notification unavailable: %s" e)
                    | Result.Ok token ->
                        let url = sprintf "%s/gmail/v1/users/me/messages/send" gmailBase

                        use request = new HttpRequestMessage(HttpMethod.Post, url)
                        request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)

                        request.Content <-
                            new StringContent(buildSendPayload sender notification, Encoding.UTF8, "application/json")

                        use! response = http.SendAsync(request, ct) |> Async.AwaitTask

                        if response.IsSuccessStatusCode then
                            return Ok()
                        else
                            let! body = response.Content.ReadAsStringAsync ct |> Async.AwaitTask
                            return Error(describeFailure "notification unavailable" response.StatusCode body)
                with
                | :? OperationCanceledException -> return Error "notification request cancelled"
                | ex -> return Error(sprintf "notification unavailable: %s" ex.Message)
        }

// ─── Public entry points ─────────────────────────────────────────────

/// Construct an `IUserDirectory` over Google Workspace. The
/// service-account credential is read from `secrets` on the first
/// directory call, not at composition — a deployment that composes the
/// companion and never uses the typeahead does no secret-store I/O and
/// no token exchange (GP 13). To fail fast on a missing or
/// undelegated credential instead, register the companion's
/// `GoogleDirectoryConfigValidator` and let preflight abort startup.
let create (secrets: ISecretStore) (config: GoogleDirectoryConfig) : IUserDirectory =
    GoogleDirectoryUserDirectory(config, secrets, GoogleDirectoryState.getClient ()) :> IUserDirectory

/// As `create`, but over a caller-supplied `HttpClient`. Present for
/// tests driving the companion against a stub transport, and for a
/// deployment that needs its own handler chain (a corporate egress
/// proxy, a Polly retry policy). The caller owns the client's
/// lifetime.
let createWithClient (http: HttpClient) (secrets: ISecretStore) (config: GoogleDirectoryConfig) : IUserDirectory =
    GoogleDirectoryUserDirectory(config, secrets, http) :> IUserDirectory

// ─── Portability audit (six rules) ───────────────────────────────────
//
// 1. Identity by value — every `IUserDirectory` member operates over
//    plain records and strings; no live handle to Google's API, no
//    token on the surface.
// 2. Async at every boundary — every method returns `Async<_>`; HTTP
//    Tasks bridged via `Async.AwaitTask`.
// 3. Retry as data — none expressed by this companion. A deployment
//    wanting retry supplies its own handler chain through
//    `createWithClient`; the companion stays a pass-through so failure
//    modes surface as `Error` strings the caller (the team-invite
//    handler or the typeahead UI) can render or swallow.
// 4. Stateless between calls — the shared HttpClient, the memoised
//    service-account key and the per-(subject, scopes) token cache are
//    all process-local derivations of the same stored credential every
//    node reads. Distributed-safe: any node with the same secret store
//    produces identical results.
// 5. No cross-shard ordering — the Directory API returns results in its
//    own order, and merging the two query legs does not impose one; the
//    UI sorts by displayName for stable rendering. Mail delivery
//    ordering is Gmail's concern.
// 6. Precision at the lower bound — N/A; `IUserDirectory` has no
//    timing semantics.