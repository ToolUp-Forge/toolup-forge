// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.GitHubApi

open System
open System.Net
open System.Net.Http
open System.Text.Json
open System.Threading.Tasks

// ─── Narrow GitHub REST facade ───────────────────────────────────────
//
// Every GitHub HTTP call the companion makes lives here — the single
// seam a GitHub-API change would touch (mirrors the companion-authoring
// guide's "narrow facade / one Native.fs" rule for the wire surface).
// Everything above it is provider logic in ordinary managed F#.
//
// Calls are built as per-request `HttpRequestMessage`s (headers set on
// the request, not the client) so a process-wide shared `HttpClient` is
// safe: GitHub *requires* a `User-Agent` on every request and we must
// never bake one caller's bearer onto a shared client's default headers.
//
// No `Octokit` / vendor SDK dependency (GP 1) — GitHub's REST surface is
// permissive enough for BCL `HttpClient` + `System.Text.Json`, keeping
// the companion's dep graph and OSS supply-chain surface minimal.

/// The identity GitHub reports for a validated token. `Id` is the
/// numeric, immutable, never-reused account id — the right stable
/// `UserId`; `Login` (the @handle) can be renamed and must not key
/// identity.
type GitHubUser = {
    Id: int64
    Login: string
    Name: string option
    Email: string option
}

/// Typed failure modes of a GitHub API call. Kept distinct so the
/// provider maps each to the right log level + metric without parsing
/// message strings.
type GitHubApiError =
    /// 401 — GitHub rejected the bearer (revoked / never valid / wrong
    /// token type). The authoritative "this is not a real identity".
    | Unauthorized
    /// 403 — reachable + authenticated but refused: a missing token
    /// scope, SSO-enforcement block, or (with `x-ratelimit-remaining: 0`)
    /// a spent rate-limit budget. Carries GitHub's own message.
    | Forbidden of message: string
    /// Network / transport failure reaching the API — infrastructure
    /// trouble, not a credential problem.
    | NetworkError of message: string
    /// A 2xx body that could not be parsed into the expected shape, or
    /// an unexpected non-2xx status. Carries a short, secret-free note.
    | MalformedResponse of message: string

/// GitHub REST API version pin (the `X-GitHub-Api-Version` header).
/// GitHub honours dated versions; pinning shields the companion from a
/// breaking default-version bump.
[<Literal>]
let private apiVersion = "2022-11-28"

let private trimBase (apiBaseUrl: string) = apiBaseUrl.TrimEnd('/')

/// Build a GET request carrying the bearer + the headers GitHub's API
/// contract requires. `token` rides `Authorization: Bearer` — GitHub
/// accepts both `Bearer <t>` and `token <t>`; Bearer is the modern form.
let private mkRequest (userAgent: string) (token: string) (url: string) : HttpRequestMessage =
    let req = new HttpRequestMessage(HttpMethod.Get, url)

    req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token)
    |> ignore

    req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json")
    |> ignore

    req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", apiVersion)
    |> ignore

    req.Headers.TryAddWithoutValidation("User-Agent", userAgent) |> ignore
    req

let private tryGetString (name: string) (el: JsonElement) : string option =
    match el.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
    | _ -> None

/// Classify a non-success response into the typed error. 401 → the
/// authoritative rejection; 403/429 → forbidden/rate-limited; anything
/// else → unexpected. The body is read for GitHub's `message` field but
/// never echoed wholesale (it can restate request detail).
let private classifyFailure (status: HttpStatusCode) (body: string) : GitHubApiError =
    let message =
        try
            use doc = JsonDocument.Parse body
            tryGetString "message" doc.RootElement |> Option.defaultValue ""
        with _ ->
            ""

    match int status with
    | 401 -> Unauthorized
    | 403
    | 429 -> Forbidden(if message = "" then string status else message)
    | other -> MalformedResponse(sprintf "unexpected HTTP %d from GitHub API" other)

/// `GET {base}/user` — resolve the identity behind a bearer. The 200
/// body is `{ id, login, name, email, … }`; `name` / `email` are
/// nullable (email is null unless the user made it public). Success here
/// *is* the identity proof — GitHub only returns 200 for a token it
/// minted.
let getUser
    (httpClient: HttpClient)
    (userAgent: string)
    (apiBaseUrl: string)
    (token: string)
    : Async<Result<GitHubUser, GitHubApiError>> =
    async {
        try
            use req = mkRequest userAgent token (trimBase apiBaseUrl + "/user")
            let! resp = httpClient.SendAsync req |> Async.AwaitTask
            let! body = resp.Content.ReadAsStringAsync() |> Async.AwaitTask

            if not resp.IsSuccessStatusCode then
                return Error(classifyFailure resp.StatusCode body)
            else
                try
                    use doc = JsonDocument.Parse body
                    let root = doc.RootElement

                    match root.TryGetProperty "id" with
                    | true, idEl when idEl.ValueKind = JsonValueKind.Number ->
                        return
                            Ok {
                                Id = idEl.GetInt64()
                                Login = tryGetString "login" root |> Option.defaultValue ""
                                Name = tryGetString "name" root
                                Email = tryGetString "email" root
                            }
                    | _ -> return Error(MalformedResponse "GitHub /user response missing numeric 'id'")
                with :? JsonException ->
                    return Error(MalformedResponse "GitHub /user response was not valid JSON")
        with
        | :? HttpRequestException as ex -> return Error(NetworkError ex.Message)
        | :? TaskCanceledException -> return Error(NetworkError "request to GitHub API timed out")
    }

/// `GET {base}/user/emails` — best-effort primary-verified email lookup
/// when `/user` returned none. Requires the token's `user:email` scope;
/// a 403 (scope absent) is folded to `Ok None` so the caller degrades
/// silently rather than failing the whole validation over an optional
/// field.
let getPrimaryEmail
    (httpClient: HttpClient)
    (userAgent: string)
    (apiBaseUrl: string)
    (token: string)
    : Async<Result<string option, GitHubApiError>> =
    async {
        try
            use req = mkRequest userAgent token (trimBase apiBaseUrl + "/user/emails")
            let! resp = httpClient.SendAsync req |> Async.AwaitTask
            let! body = resp.Content.ReadAsStringAsync() |> Async.AwaitTask

            if resp.StatusCode = HttpStatusCode.Forbidden then
                // Token lacks the `user:email` scope — expected when the
                // deployment didn't request it. Optional enrichment, so
                // this is a `None`, not a failure.
                return Ok None
            elif not resp.IsSuccessStatusCode then
                return Error(classifyFailure resp.StatusCode body)
            else
                try
                    use doc = JsonDocument.Parse body

                    let primary =
                        doc.RootElement.EnumerateArray()
                        |> Seq.tryFind (fun el ->
                            (match el.TryGetProperty "primary" with
                             | true, p -> p.ValueKind = JsonValueKind.True
                             | _ -> false)
                            && (match el.TryGetProperty "verified" with
                                | true, v -> v.ValueKind = JsonValueKind.True
                                | _ -> false))
                        |> Option.bind (tryGetString "email")

                    return Ok primary
                with :? JsonException ->
                    return Error(MalformedResponse "GitHub /user/emails response was not valid JSON")
        with
        | :? HttpRequestException as ex -> return Error(NetworkError ex.Message)
        | :? TaskCanceledException -> return Error(NetworkError "request to GitHub API timed out")
    }

/// `GET {base}/user/memberships/orgs/{org}` — is the *authenticated*
/// user an active member of `org`? 200 with `state = "active"` ⇒ member;
/// 200 `state = "pending"` (invited, not accepted) ⇒ not yet a member;
/// 403 / 404 ⇒ not a member (or the token lacks `read:org`). Requires
/// the `read:org` scope to see private memberships.
let checkOrgMembership
    (httpClient: HttpClient)
    (userAgent: string)
    (apiBaseUrl: string)
    (token: string)
    (org: string)
    : Async<Result<bool, GitHubApiError>> =
    async {
        try
            let url = trimBase apiBaseUrl + "/user/memberships/orgs/" + Uri.EscapeDataString org
            use req = mkRequest userAgent token url
            let! resp = httpClient.SendAsync req |> Async.AwaitTask
            let! body = resp.Content.ReadAsStringAsync() |> Async.AwaitTask

            match resp.StatusCode with
            | HttpStatusCode.OK ->
                try
                    use doc = JsonDocument.Parse body
                    let state = tryGetString "state" doc.RootElement
                    return Ok(state = Some "active")
                with :? JsonException ->
                    return Error(MalformedResponse "GitHub membership response was not valid JSON")
            | HttpStatusCode.Forbidden
            | HttpStatusCode.NotFound ->
                // Not a member, or the token cannot see the membership.
                // Either way the user is not an admitted active member.
                return Ok false
            | HttpStatusCode.Unauthorized -> return Error Unauthorized
            | other -> return Error(MalformedResponse(sprintf "unexpected HTTP %d checking org membership" (int other)))
        with
        | :? HttpRequestException as ex -> return Error(NetworkError ex.Message)
        | :? TaskCanceledException -> return Error(NetworkError "request to GitHub API timed out")
    }