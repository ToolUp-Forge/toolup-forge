// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.Passkey.PasskeyHost

open System.Text.Json
open Microsoft.AspNetCore.Http
open Giraffe
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Secrets
open ToolUp.AuthProviders.Passkey.PasskeyTypes
open ToolUp.AuthProviders.Passkey.PasskeyStores
open ToolUp.AuthProviders.Passkey.PasskeyRegistrationPolicy
open ToolUp.AuthProviders.Passkey.PasskeyCeremony
open ToolUp.AuthProviders.Passkey.PasskeySessionToken

// ─── Ceremony endpoints ──────────────────────────────────────────────
//
// Four Giraffe routes drive the WebAuthn ceremonies:
//   POST /api/passkey/register/begin      → attestation options
//   POST /api/passkey/register/complete   → verify + persist + sign in
//   POST /api/passkey/assert/begin        → assertion options
//   POST /api/passkey/assert/complete     → verify + clone-detect + sign in
//
// Begin returns the Fido2 options JSON plus a `challengeId`; complete
// echoes `?challenge=<id>` on the query string with the browser's raw
// authenticator response in the body. A successful complete on either
// leg mints the short-lived platform session JWT (`PasskeySessionToken`)
// and emits the ceremony audit rows (GP 6).

/// Per-deployment ceremony runtime — one instance registered as a DI
/// singleton by `PasskeyCompose`. Bundles the config, the Fido2 verifier,
/// the credential + challenge stores, and a lazily-resolved session
/// signing-secret cache (resolved from `ISecretStore` on first use).
type PasskeyRuntime = {
    Config: PasskeyConfig
    Fido2: Fido2NetLib.IFido2
    Credentials: PasskeyCredentialStore
    Challenges: IPasskeyChallengeStore
    SecretCache: string option ref
}

module PasskeyRuntime =
    /// Build the runtime over the deployment's `IBlobStorage`.
    let create (config: PasskeyConfig) (blobs: IBlobStorage) : PasskeyRuntime = {
        Config = config
        Fido2 = buildFido2 config
        Credentials = PasskeyCredentialStore(blobs)
        Challenges = InMemoryPasskeyChallengeStore()
        SecretCache = ref None
    }

// The F#-aware STJ options for the request/response envelopes (Option /
// list / record on the wire).
let private jsonOptions =
    ToolUp.Remoting.Json.SystemTextJson.FableConverters.create ()

let private getService<'T> (ctx: HttpContext) : 'T =
    ctx.RequestServices.GetService(typeof<'T>) :?> 'T

let private tryGetService<'T> (ctx: HttpContext) : 'T option =
    match ctx.RequestServices.GetService(typeof<'T>) with
    | null -> None
    | svc -> Some(svc :?> 'T)

let private writeJson (statusCode: int) (value: obj) : HttpHandler =
    fun (_: HttpFunc) (ctx: HttpContext) ->
        ctx.SetStatusCode statusCode
        ctx.SetContentType "application/json"
        ctx.WriteStringAsync(JsonSerializer.Serialize(value, jsonOptions))

let private writeError (statusCode: int) (message: string) : HttpHandler =
    writeJson statusCode {| error = message |}

/// Resolve (and cache) the HS256 session signing secret from
/// `ISecretStore`.
let private resolveSecret (runtime: PasskeyRuntime) (ctx: HttpContext) : Async<string> = async {
    match runtime.SecretCache.Value with
    | Some s -> return s
    | None ->
        let secrets = getService<ISecretStore> ctx
        let! secret = resolveSigningSecret secrets
        runtime.SecretCache.Value <- Some secret
        return secret
}

/// The request's resolved principal (anonymous when unauthenticated),
/// via the composed `IAuthProvider` (lenient `GetUser`).
let private currentUser (ctx: HttpContext) : Async<AuthenticatedUser> = async {
    let auth = getService<IAuthProvider> ctx
    return! auth.GetUser(RequestContextBuilder.ofHttpContext ctx)
}

let private auditEmit (ctx: HttpContext) (scopeId: string) (event: AuditEvent) : Async<unit> = async {
    match tryGetService<IAuditLog> ctx with
    | Some auditLog -> return! auditLog.Record(scopeId, event)
    | None -> return ()
}

let private credIdPrefix (credentialIdB64Url: string) : string =
    if credentialIdB64Url.Length <= 12 then
        credentialIdB64Url
    else
        credentialIdB64Url.Substring(0, 12)

let private mintSession
    (runtime: PasskeyRuntime)
    (ctx: HttpContext)
    (record: PasskeyCredentialRecord)
    : Async<SessionTokenResponse> =
    async {
        let! secret = resolveSecret runtime ctx

        let token =
            mint secret runtime.Config {
                UserId = record.UserId
                DisplayName = record.DisplayName
                Email = record.Email
            }

        return {
            Token = token
            ExpiresInSeconds = runtime.Config.SessionTokenTtlSeconds
            UserId = record.UserId
        }
    }

// ─── Handlers ────────────────────────────────────────────────────────

let private registerBeginHandler: HttpHandler =
    fun (next: HttpFunc) (ctx: HttpContext) -> task {
        let runtime = getService<PasskeyRuntime> ctx
        let! body = ctx.ReadBodyFromRequestAsync()

        let request =
            try
                JsonSerializer.Deserialize<RegisterBeginRequest>(body, jsonOptions)
            with _ -> {
                Username = None
                DisplayName = None
                Email = None
                BootstrapToken = None
            }

        let! user = currentUser ctx |> Async.StartAsTask
        let pendingStore = tryGetService<IPendingInviteStore> ctx
        let! resolved = resolveIdentity runtime.Config user pendingStore request |> Async.StartAsTask

        match resolved with
        | Error e -> return! writeError 403 e next ctx
        | Ok(grant, identity) ->
            let! begun =
                beginRegistration runtime.Fido2 runtime.Credentials runtime.Challenges runtime.Config grant identity
                |> Async.StartAsTask

            match begun with
            | Ok options -> return! writeJson 200 options next ctx
            | Error e -> return! writeError 400 e next ctx
    }

let private registerCompleteHandler: HttpHandler =
    fun (next: HttpFunc) (ctx: HttpContext) -> task {
        let runtime = getService<PasskeyRuntime> ctx

        match ctx.TryGetQueryStringValue "challenge" with
        | None -> return! writeError 400 "Missing ?challenge query parameter." next ctx
        | Some challengeId ->
            match runtime.Challenges.TryTake challengeId with
            | None ->
                return! writeError 400 "Passkey challenge is unknown or expired — start registration again." next ctx
            | Some pending ->
                let! body = ctx.ReadBodyFromRequestAsync()

                let! completed =
                    completeRegistration runtime.Fido2 runtime.Credentials pending body
                    |> Async.StartAsTask

                match completed with
                | Error e -> return! writeError 400 e next ctx
                | Ok record ->
                    do!
                        auditEmit
                            ctx
                            PasskeyConfig.PlatformContainer
                            (PasskeyCredentialRegistered {
                                UserId = record.UserId
                                CredentialIdPrefix = credIdPrefix record.CredentialId
                                Grant = pending.Grant |> Option.defaultValue "Unknown"
                            })
                        |> Async.StartAsTask
                        :> System.Threading.Tasks.Task

                    // Enrolment implies authentication of this ceremony —
                    // sign the user in immediately (443 acceptance:
                    // end-to-end sign-in with a platform-created passkey).
                    let! session = mintSession runtime ctx record |> Async.StartAsTask

                    do!
                        auditEmit
                            ctx
                            PasskeyConfig.PlatformContainer
                            (UserLoggedIn {
                                UserId = record.UserId
                                AuthProvider = "Passkey"
                            })
                        |> Async.StartAsTask
                        :> System.Threading.Tasks.Task

                    return! writeJson 200 session next ctx
    }

let private assertBeginHandler: HttpHandler =
    fun (next: HttpFunc) (ctx: HttpContext) -> task {
        let runtime = getService<PasskeyRuntime> ctx
        let! body = ctx.ReadBodyFromRequestAsync()

        let request =
            try
                JsonSerializer.Deserialize<AssertionBeginRequest>(body, jsonOptions)
            with _ -> { Username = None }

        let! begun =
            beginAssertion runtime.Fido2 runtime.Credentials runtime.Challenges runtime.Config request.Username
            |> Async.StartAsTask

        match begun with
        | Ok options -> return! writeJson 200 options next ctx
        | Error e -> return! writeError 400 e next ctx
    }

let private assertCompleteHandler: HttpHandler =
    fun (next: HttpFunc) (ctx: HttpContext) -> task {
        let runtime = getService<PasskeyRuntime> ctx

        match ctx.TryGetQueryStringValue "challenge" with
        | None -> return! writeError 400 "Missing ?challenge query parameter." next ctx
        | Some challengeId ->
            match runtime.Challenges.TryTake challengeId with
            | None -> return! writeError 400 "Passkey challenge is unknown or expired — start sign-in again." next ctx
            | Some pending ->
                let! body = ctx.ReadBodyFromRequestAsync()

                let! completed =
                    completeAssertion runtime.Fido2 runtime.Credentials pending body
                    |> Async.StartAsTask

                match completed with
                | Error e -> return! writeError 401 e next ctx
                | Ok record ->
                    let! session = mintSession runtime ctx record |> Async.StartAsTask

                    do!
                        auditEmit
                            ctx
                            PasskeyConfig.PlatformContainer
                            (UserLoggedIn {
                                UserId = record.UserId
                                AuthProvider = "Passkey"
                            })
                        |> Async.StartAsTask
                        :> System.Threading.Tasks.Task

                    return! writeJson 200 session next ctx
    }

/// The passkey ceremony routes. Mounted by `PasskeyCompose.run` onto the
/// SDK's route chain via `ComposeExtensions.Handlers`. Every handler
/// resolves its `PasskeyRuntime` + substrate services per-request from
/// `ctx.RequestServices`.
let routes: HttpHandler =
    choose [
        POST >=> route "/api/passkey/register/begin" >=> registerBeginHandler
        POST >=> route "/api/passkey/register/complete" >=> registerCompleteHandler
        POST >=> route "/api/passkey/assert/begin" >=> assertBeginHandler
        POST >=> route "/api/passkey/assert/complete" >=> assertCompleteHandler
    ]