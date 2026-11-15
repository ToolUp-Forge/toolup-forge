// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.PasskeyRegistrationGateTests

// ─── Passkey registration proves who is enrolling (Phase 1285) ──────
//
// The registration ceremony mints a session for the identity it enrols,
// so the identity is the whole of the security question. Every case here
// drives the four ceremony routes end to end through a TestServer, with
// the real Fido2 options builder and a stub attestation verifier (the
// vendor crypto is not the subject), and speaks only JSON on the wire —
// so each pin reads the behaviour a browser sees rather than a function
// a refactor could route around.

open System
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Giraffe
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Secrets
open ToolUp.Platform.Config.NotificationChannel
open ToolUp.Platform.Config.TeamManagement
open ToolUp.Platform.Scope.Teams.TeamInvitationHandler
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage
open ToolUp.Platform.Infra
open ToolUp.Platform.Config
open ToolUp.Platform.Scope
open ToolUp.Platform.Scope.Teams
open ToolUp.Platform.Testing
open ToolUp.AuthProviders.Passkey.PasskeyTypes
open ToolUp.AuthProviders.Passkey.PasskeyStores
open ToolUp.AuthProviders.Passkey.PasskeyCeremony
open ToolUp.AuthProviders.Passkey.PasskeyHost

// ─── Fixtures ────────────────────────────────────────────────────────

let private jsonOptions = ToolUp.Remoting.Json.SystemTextJson.PlatformJson.options

let private baseConfig =
    PasskeyConfig.create "example.com" "Example" [ "https://example.com" ]

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

/// The real options builder (what the browser is told to demand) over a
/// stub attestation verifier that accepts any response and returns a
/// fresh credential bound to the user the begin leg named.
let private ceremonyFido2 (config: PasskeyConfig) : Fido2NetLib.IFido2 =
    let real = buildFido2 config

    { new Fido2NetLib.IFido2 with
        member _.RequestNewCredential(prms) = real.RequestNewCredential prms
        member _.GetAssertionOptions(prms) = real.GetAssertionOptions prms

        member _.MakeNewCredentialAsync(prms, _: CancellationToken) =
            Task.FromResult(
                Fido2NetLib.Objects.RegisteredPublicKeyCredential(
                    Id = Guid.NewGuid().ToByteArray(),
                    PublicKey = [| 9uy; 9uy |],
                    SignCount = 0u,
                    User = prms.OriginalOptions.User,
                    Transports = [||]
                )
            )

        member _.MakeAssertionAsync(_, _: CancellationToken) =
            Task.FromResult(Unchecked.defaultof<Fido2NetLib.Objects.VerifyAssertionResult>)
    }

let private authProviderFor (user: AuthenticatedUser) : IAuthProvider =
    { new IAuthProvider with
        member _.GetUser(_) = async { return user }
        member _.ValidateRequest(_) = async { return Ok user }
        member _.IsCryptographicallyVerified = true
    }

let private signedIn (userId: string) (email: string option) : AuthenticatedUser = {
    AuthenticatedUser.anonymous with
        UserId = UserId userId
        DisplayName = userId
        Email = email
}

let private platformAdmins (admins: string list) : IPlatformAdminStore =
    { new IPlatformAdminStore with
        member _.IsPlatformAdmin(UserId id) = async { return List.contains id admins }
        member _.AssignPlatformAdmin(_, _) = async { return Error "read-only fixture" }
        member _.RevokePlatformAdmin(_, _) = async { return Error "read-only fixture" }
        member _.ListPlatformAdmins() = async { return admins }
    }

/// One deployment's shared substrate: every replica of it reads the same
/// blobs (credentials, the bootstrap marker), teams, invitations and
/// pending-by-email entries.
type private Deployment = {
    Blobs: IBlobStorage
    Teams: ITeamStore
    ShareTokens: IShareTokenStore
    PendingInvites: IPendingInviteStore
    Secrets: ISecretStore
    Admins: IPlatformAdminStore
}

let private freshDeployment (admins: string list) : Deployment =
    let teamBlobs = InMemoryBlobStorage() :> IBlobStorage
    let secrets = Fakes.TestSecretStore() :> ISecretStore

    {
        Blobs = InMemoryBlobStorage() :> IBlobStorage
        Teams = TeamStore(teamBlobs, InMemoryNotificationChannel(None) :> INotificationChannel) :> ITeamStore
        ShareTokens = ShareTokenStore.create (InMemoryBlobStorage()) secrets (NoOpAuditLog()) silentLogger
        PendingInvites =
            ToolUp.Platform.Scope.Teams.InMemoryPendingInviteStore(InMemoryBlobStorage(), silentLogger)
            :> IPendingInviteStore
        Secrets = secrets
        Admins = platformAdmins admins
    }

/// One replica: its own process (its own challenge store), the
/// deployment's shared substrate.
let private replica (deployment: Deployment) (config: PasskeyConfig) (user: AuthenticatedUser) =
    let runtime: PasskeyRuntime = {
        Config = config
        Fido2 = ceremonyFido2 config
        Credentials = PasskeyCredentialStore(deployment.Blobs)
        Challenges = InMemoryPasskeyChallengeStore()
        SecretCache = ref None
    }

    let host =
        Host
            .CreateDefaultBuilder()
            .ConfigureWebHostDefaults(fun webHost ->
                webHost
                    .UseTestServer()
                    .ConfigureServices(fun (services: IServiceCollection) ->
                        services.AddGiraffe() |> ignore
                        services.AddSingleton<PasskeyRuntime>(runtime) |> ignore
                        services.AddSingleton<IAuthProvider>(authProviderFor user) |> ignore
                        services.AddSingleton<ISecretStore>(deployment.Secrets) |> ignore
                        services.AddSingleton<IBlobStorage>(deployment.Blobs) |> ignore
                        services.AddSingleton<ITeamStore>(deployment.Teams) |> ignore
                        services.AddSingleton<IShareTokenStore>(deployment.ShareTokens) |> ignore
                        services.AddSingleton<IPendingInviteStore>(deployment.PendingInvites) |> ignore
                        services.AddSingleton<IPlatformAdminStore>(deployment.Admins) |> ignore)
                    .Configure(fun (app: IApplicationBuilder) -> app.UseGiraffe routes)
                |> ignore)
            .Build()

    host.Start()
    host.GetTestClient(), runtime, host

let private post (client: HttpClient) (path: string) (body: string) : HttpStatusCode * string =
    let req =
        new HttpRequestMessage(
            HttpMethod.Post,
            path,
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        )

    let resp = client.SendAsync req |> Async.AwaitTask |> Async.RunSynchronously
    resp.StatusCode, resp.Content.ReadAsStringAsync() |> Async.AwaitTask |> Async.RunSynchronously

/// The begin leg's status and body, and — when begin admitted the caller
/// — the complete leg's status and the session it minted.
type private Enrolment = {
    BeginStatus: HttpStatusCode
    BeginBody: string
    Session: SessionTokenResponse option
    CompleteStatus: HttpStatusCode option
}

let private enrol (client: HttpClient) (body: string) : Enrolment =
    let beginStatus, beginBody = post client "/api/passkey/register/begin" body

    if beginStatus <> HttpStatusCode.OK then
        {
            BeginStatus = beginStatus
            BeginBody = beginBody
            Session = None
            CompleteStatus = None
        }
    else
        let options =
            JsonSerializer.Deserialize<CeremonyOptionsResponse>(beginBody, jsonOptions)

        let status, completeBody =
            post client $"/api/passkey/register/complete?challenge={options.ChallengeId}" "{}"

        {
            BeginStatus = beginStatus
            BeginBody = beginBody
            CompleteStatus = Some status
            Session =
                if status = HttpStatusCode.OK then
                    Some(JsonSerializer.Deserialize<SessionTokenResponse>(completeBody, jsonOptions))
                else
                    None
        }

let private sessionUser (e: Enrolment) : string option = e.Session |> Option.map _.UserId.Value

/// A team with `owner` as its Owner.
let private provisionTeam (teams: ITeamStore) (owner: string) : Async<string> = async {
    let teamId = "t-" + Guid.NewGuid().ToString("N").Substring(0, 8)
    let! _ = teams.CreateTeam(TeamId teamId, "Test Team")
    let! _ = teams.AddMember(TeamId teamId, UserId owner, Owner)
    return teamId
}

let private ownerContext (userId: string) : HttpContext =
    let services = ServiceCollection()
    services.AddScoped<AccessContext>(fun _ -> AccessContexts.user userId) |> ignore
    let ctx = DefaultHttpContext() :> HttpContext
    ctx.RequestServices <- services.BuildServiceProvider()
    AccessContexts.stampUserId ctx.Items userId
    ctx

let private inviteConfig = {
    ServerConfig.defaults with
        Surfaces = Surfaces.team
        PublicBaseUrl = Some "https://app.example.com"
}

/// Issue an invitation link as the team's owner; returns its token (the
/// secret the link carries).
let private issueInvite
    (deployment: Deployment)
    (teamId: string)
    (owner: string)
    (role: TeamRole)
    (maxUses: int)
    (expiresIn: TimeSpan)
    : Async<string> =
    async {
        let api =
            teamInvitationApi deployment.ShareTokens deployment.Teams (NoOpAuditLog()) inviteConfig (ownerContext owner)

        match!
            api.IssueInvite {
                TeamId = teamId
                Role = role
                ExpiresIn = Some expiresIn
                EmailHint = Some "invitee@example.com"
                MaxUses = Some maxUses
            }
        with
        | Ok issued -> return issued.InviteUrl.Substring(issued.InviteUrl.LastIndexOf('/') + 1)
        | Error e -> return failwithf "issue failed: %s" e
    }

/// A pending-by-email invitation for `email` — what the email-keyed path
/// knows. Knowing the address is all a caller needs to match it.
let private pendingByEmail (deployment: Deployment) (email: string) (teamId: string) : Async<unit> = async {
    let entry: PendingInviteByEmail = {
        TeamId = teamId
        Role = Member
        ExpiresAt = DateTime.UtcNow.AddDays 1.0
        InviterUserId = "owner"
        IssuedAt = DateTime.UtcNow
    }

    match! deployment.PendingInvites.Upsert(email, entry) with
    | Ok() -> ()
    | Error e -> failwithf "pending invite failed: %A" e
}

/// Enrol a first passkey for `userId` through a signed-in session.
let private enrolSignedIn (deployment: Deployment) (userId: string) : unit =
    let client, _, host = replica deployment baseConfig (signedIn userId None)

    try
        let e = enrol client "{}"
        Expect.equal e.CompleteStatus (Some HttpStatusCode.OK) $"fixture: {userId} enrols from a session"
    finally
        host.Dispose()

let private onReplica
    (client: HttpClient, runtime: PasskeyRuntime, host: IHost)
    (f: HttpClient -> PasskeyRuntime -> unit)
    =
    try
        f client runtime
    finally
        host.Dispose()

let private anonymous = AuthenticatedUser.anonymous

let private openRegistration = {
    baseConfig with
        AllowOpenRegistration = true
}

// ─── Tests ───────────────────────────────────────────────────────────

[<Tests>]
let tests =
    testList "Phase 1285 — passkey registration proves who is enrolling" [

        testCaseAsync "an anonymous registration naming a victim and an invited email is refused at register/begin"
        <| async {
            let deployment = freshDeployment [ "victim" ]
            let! teamId = provisionTeam deployment.Teams "victim"
            do! pendingByEmail deployment "invitee@example.com" teamId

            onReplica (replica deployment baseConfig anonymous) (fun client _ ->
                let e =
                    enrol client """{"username":"victim","email":"invitee@example.com","displayName":"Victim"}"""

                Expect.notEqual
                    (sessionUser e)
                    (Some "victim")
                    "no ceremony may sign an anonymous caller in as the victim"

                Expect.equal e.BeginStatus HttpStatusCode.Forbidden "the request is refused at register/begin")
        }

        testCaseAsync "open registration refuses a user id that already holds a passkey"
        <| async {
            let deployment = freshDeployment []
            enrolSignedIn deployment "victim"

            onReplica (replica deployment openRegistration anonymous) (fun client runtime ->
                let e = enrol client """{"username":"victim"}"""
                Expect.equal e.BeginStatus HttpStatusCode.Forbidden "an existing passkey holder's id is refused"

                let creds = runtime.Credentials.ListByUserId "victim" |> Async.RunSynchronously
                Expect.equal creds.Length 1 "no second credential was enrolled for the victim")
        }

        testCaseAsync "open registration refuses a user id that is a team principal"
        <| async {
            let deployment = freshDeployment []
            let! _ = provisionTeam deployment.Teams "victim"

            onReplica (replica deployment openRegistration anonymous) (fun client _ ->
                let e = enrol client """{"username":"victim"}"""
                Expect.equal e.BeginStatus HttpStatusCode.Forbidden "a team member's id is refused")
        }

        testCaseAsync "open registration refuses a user id that is a platform principal"
        <| async {
            let deployment = freshDeployment [ "root" ]

            onReplica (replica deployment openRegistration anonymous) (fun client _ ->
                let e = enrol client """{"username":"root"}"""
                Expect.equal e.BeginStatus HttpStatusCode.Forbidden "a platform admin's id is refused")
        }

        testCaseAsync "a bootstrap enrolment for an existing principal is refused"
        <| async {
            let deployment = freshDeployment [ "root" ]

            let cfg = {
                baseConfig with
                    BootstrapToken = Some "first-run-secret"
            }

            onReplica (replica deployment cfg anonymous) (fun client _ ->
                let e = enrol client """{"username":"root","bootstrapToken":"first-run-secret"}"""
                Expect.equal e.BeginStatus HttpStatusCode.Forbidden "the bootstrap token does not enrol an existing id")
        }

        testCaseAsync "open registration still enrols a new user id"
        <| async {
            let deployment = freshDeployment []

            onReplica (replica deployment openRegistration anonymous) (fun client _ ->
                let e = enrol client """{"username":"newcomer"}"""
                Expect.equal (sessionUser e) (Some "newcomer") "a fresh id enrols and is signed in")
        }

        testCaseAsync "an invited user with the link enrols and joins the team with the invited role"
        <| async {
            let deployment = freshDeployment []
            let! teamId = provisionTeam deployment.Teams "owner"
            let! token = issueInvite deployment teamId "owner" Admin 1 (TimeSpan.FromDays 1.0)

            onReplica (replica deployment baseConfig anonymous) (fun client _ ->
                let e = enrol client $$"""{"username":"owner","inviteToken":"{{token}}"}"""

                match sessionUser e with
                | None -> failtestf "the invitee was not enrolled: %A %s" e.BeginStatus e.BeginBody
                | Some userId ->
                    Expect.notEqual userId "owner" "the identity comes from the invitation, never the body's username"

                    let role =
                        deployment.Teams.GetMemberRole(TeamId teamId, UserId userId)
                        |> Async.RunSynchronously

                    Expect.equal role (Some Admin) "the invitee joined the team with the invited role"

                let ownerRole =
                    deployment.Teams.GetMemberRole(TeamId teamId, UserId "owner")
                    |> Async.RunSynchronously

                Expect.equal ownerRole (Some Owner) "the named user's membership is untouched")
        }

        testCaseAsync "an invitation link is redeemed once: a second enrolment on a single-use link is refused"
        <| async {
            let deployment = freshDeployment []
            let! teamId = provisionTeam deployment.Teams "owner"
            let! token = issueInvite deployment teamId "owner" Member 1 (TimeSpan.FromDays 1.0)
            // The address the link was sent to also has a pending-by-email
            // entry: knowing it must not stand in for the spent link.
            do! pendingByEmail deployment "invitee@example.com" teamId

            onReplica (replica deployment baseConfig anonymous) (fun client _ ->
                let body =
                    $$"""{"username":"first","inviteToken":"{{token}}","email":"invitee@example.com"}"""

                let first = enrol client body
                Expect.isSome first.Session "the first enrolment on the link succeeds"

                let second =
                    enrol client $$"""{"username":"second","inviteToken":"{{token}}","email":"invitee@example.com"}"""

                Expect.isNone second.Session "the spent link enrols nobody"
                Expect.equal second.BeginStatus HttpStatusCode.Forbidden "the spent link is refused at register/begin")
        }

        testCaseAsync "an expired invitation link is refused"
        <| async {
            let deployment = freshDeployment []
            let! teamId = provisionTeam deployment.Teams "owner"
            let! token = issueInvite deployment teamId "owner" Member 1 (TimeSpan.FromMilliseconds 1.0)
            do! pendingByEmail deployment "invitee@example.com" teamId
            do! Async.Sleep 50

            onReplica (replica deployment baseConfig anonymous) (fun client _ ->
                let e =
                    enrol client $$"""{"username":"late","inviteToken":"{{token}}","email":"invitee@example.com"}"""

                Expect.isNone e.Session "an expired link enrols nobody"
                Expect.equal e.BeginStatus HttpStatusCode.Forbidden "the expired link is refused at register/begin")
        }

        testCaseAsync "a signed-in user's passkey takes its email from the session, never the body"
        <| async {
            let deployment = freshDeployment []

            onReplica (replica deployment baseConfig (signedIn "carol" None)) (fun client runtime ->
                let e = enrol client """{"email":"ceo@example.com"}"""
                Expect.equal (sessionUser e) (Some "carol") "the session drives the identity"

                let creds = runtime.Credentials.ListByUserId "carol" |> Async.RunSynchronously
                Expect.equal (creds |> List.map _.Email) [ None ] "the body's email was not adopted")
        }

        testCaseAsync "an authenticated user still adds a second passkey, with open registration on or off"
        <| async {
            let deployment = freshDeployment []
            let! _ = provisionTeam deployment.Teams "carol"
            enrolSignedIn deployment "carol"

            for cfg in [ baseConfig; openRegistration ] do
                onReplica (replica deployment cfg (signedIn "carol" (Some "carol@example.com"))) (fun client _ ->
                    let e = enrol client "{}"

                    Expect.equal
                        (sessionUser e)
                        (Some "carol")
                        $"carol adds a passkey from her session (open registration = {cfg.AllowOpenRegistration})")

            let creds =
                PasskeyCredentialStore(deployment.Blobs).ListByUserId "carol"
                |> Async.RunSynchronously

            Expect.equal creds.Length 3 "carol holds three passkeys"
        }

        testCaseAsync "the bootstrap token is consumed on first use, on this replica and on every other"
        <| async {
            let deployment = freshDeployment []

            let cfg = {
                baseConfig with
                    BootstrapToken = Some "first-run-secret"
            }

            let replicaA = replica deployment cfg anonymous
            let replicaB = replica deployment cfg anonymous

            onReplica replicaA (fun clientA _ ->
                onReplica replicaB (fun clientB _ ->
                    let first =
                        enrol clientA """{"username":"admin","bootstrapToken":"first-run-secret"}"""

                    Expect.equal (sessionUser first) (Some "admin") "the first use enrols the first credential"

                    let again =
                        enrol clientA """{"username":"admin2","bootstrapToken":"first-run-secret"}"""

                    Expect.isNone again.Session "a second use on the same replica enrols nobody"
                    Expect.equal again.BeginStatus HttpStatusCode.Forbidden "and is refused at register/begin"

                    let elsewhere =
                        enrol clientB """{"username":"admin3","bootstrapToken":"first-run-secret"}"""

                    Expect.isNone elsewhere.Session "a second use on another replica enrols nobody"
                    Expect.equal elsewhere.BeginStatus HttpStatusCode.Forbidden "and is refused at register/begin"))
        }

        testCaseAsync "the bootstrap token is single-winner when two ceremonies race it"
        <| async {
            let deployment = freshDeployment []

            let cfg = {
                baseConfig with
                    BootstrapToken = Some "first-run-secret"
            }

            onReplica (replica deployment cfg anonymous) (fun client _ ->
                // Both begin before either completes: the gate cannot have
                // seen a consumed marker, so the complete leg must decide.
                let beginA, bodyA =
                    post
                        client
                        "/api/passkey/register/begin"
                        """{"username":"a","bootstrapToken":"first-run-secret"}"""

                let beginB, bodyB =
                    post
                        client
                        "/api/passkey/register/begin"
                        """{"username":"b","bootstrapToken":"first-run-secret"}"""

                Expect.equal (beginA, beginB) (HttpStatusCode.OK, HttpStatusCode.OK) "both begins are admitted"

                let complete (body: string) =
                    let options = JsonSerializer.Deserialize<CeremonyOptionsResponse>(body, jsonOptions)
                    fst (post client $"/api/passkey/register/complete?challenge={options.ChallengeId}" "{}")

                let outcomes = [ complete bodyA; complete bodyB ]

                Expect.equal
                    (outcomes |> List.filter ((=) HttpStatusCode.OK) |> List.length)
                    1
                    $"exactly one ceremony is signed in on the bootstrap token: %A{outcomes}")
        }

        testCaseAsync "two ceremonies racing to enrol the same new user id cannot both be signed in as it"
        <| async {
            let deployment = freshDeployment []

            onReplica (replica deployment openRegistration anonymous) (fun client _ ->
                let beginA, bodyA =
                    post client "/api/passkey/register/begin" """{"username":"newbie"}"""

                let beginB, bodyB =
                    post client "/api/passkey/register/begin" """{"username":"newbie"}"""

                Expect.equal (beginA, beginB) (HttpStatusCode.OK, HttpStatusCode.OK) "both begins see a new id"

                let complete (body: string) =
                    let options = JsonSerializer.Deserialize<CeremonyOptionsResponse>(body, jsonOptions)
                    fst (post client $"/api/passkey/register/complete?challenge={options.ChallengeId}" "{}")

                let outcomes = [ complete bodyA; complete bodyB ]

                Expect.equal
                    (outcomes |> List.filter ((=) HttpStatusCode.OK) |> List.length)
                    1
                    $"exactly one ceremony enrols the new id: %A{outcomes}")
        }

        testCaseAsync "user verification is required by default on registration and on sign-in"
        <| async {
            let deployment = freshDeployment []

            onReplica (replica deployment openRegistration anonymous) (fun client _ ->
                let regStatus, regBody =
                    post client "/api/passkey/register/begin" """{"username":"uv"}"""

                Expect.equal regStatus HttpStatusCode.OK "register/begin admitted"
                let reg = JsonSerializer.Deserialize<CeremonyOptionsResponse>(regBody, jsonOptions)
                Expect.stringContains reg.OptionsJson "\"userVerification\":\"required\"" "registration demands UV"

                let asStatus, asBody = post client "/api/passkey/assert/begin" "{}"
                Expect.equal asStatus HttpStatusCode.OK "assert/begin admitted"

                let assertion =
                    JsonSerializer.Deserialize<CeremonyOptionsResponse>(asBody, jsonOptions)

                Expect.stringContains assertion.OptionsJson "\"userVerification\":\"required\"" "sign-in demands UV")
        }
    ]