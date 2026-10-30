module ToolUp.Platform.Tests.InProcess.AuditCallSiteFireAndForgetTests

// Phase 1026 — every fire-and-forget audit record is caught at its call
// site, so a throwing sink cannot kill the host.
//
// Phase 1024's census found 26 sites that started an `IAuditLog.Record` (or
// an `IAuthAuditHook` / `IUsageLog` write) with a bare `Async.Start`,
// relying only on the interface's "swallow your own failures" promise. The
// shipped sinks keep it; a custom or cloud-backed one that throws inside
// its async terminated the host. Every one of those sites now starts its
// write through `Background.startAudit` (or, for work a service drains at
// stop, `Background.start`).
//
// One representative site per family is driven here, once against healthy
// sinks (the control) and once against a sink that fails in one of three
// ways:
//
//   * `Plain` — the write fails inside plain async code. Started with a bare
//     `Async.Start`, the exception is rethrown on a thread-pool thread and
//     TERMINATES THE HOST, so this arm can only go red in a child process.
//   * `TaskShaped` — the write fails behind a task boundary
//     (`Async.AwaitTask`); started bare it is an unobserved task exception.
//   * `Synchronous` — the write throws before an `Async` exists. Each site
//     evaluates the write eagerly, as before, inside a `try` whose `with`
//     hands the throw to the same warner; unguarded it failed the request.
//
// For every arm: the caller's outcome equals the control's, no unobserved
// task exception carries the arm's marker, and exactly one log entry names
// the write and the exception type — a Warning from `startAudit`, or the
// `Error` `Background.start` logs for the service-lifetime site.
//
// The last list is the regrowth guard: a source scan refusing any bare
// `Async.Start` under `src/ToolUp.Platform.Server/Server/` that its
// allowlist does not name, each entry with its reason.

open System
open System.Collections.Concurrent
open System.IO
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Expecto
open Giraffe
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.Consent.ConsentStateStore
open ToolUp.Platform.FileProcessor
open ToolUp.Platform.Usage
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage
open ToolUp.Platform.Tests.Support
open ToolUp.Platform.Tests.Support.FireAndForgetHarness

type CallSiteSinkDownException(marker: string) =
    inherit Exception(marker)

let private failWith mode marker =
    FireAndForgetHarness.failWith (fun m -> CallSiteSinkDownException m :> exn) mode marker

/// The sinks a site writes to: the audit log, the auth-denial hook and the
/// usage log. Healthy (`None`) or all failing per the mode.
type private Sinks = {
    AuditLog: IAuditLog
    Hook: IAuthAuditHook
    Usage: IUsageLog
}

let private sinks (failure: (FailureMode * string) option) : Sinks =
    let write () : Async<unit> =
        match failure with
        | Some(mode, marker) -> failWith mode marker
        | None -> async { return () }

    {
        AuditLog =
            { new IAuditLog with
                member _.Record(_, _) = write ()
                member _.GetAuditTrail(_, _, _) = async { return [] }
            }
        Hook =
            { new IAuthAuditHook with
                member _.RecordDenial _ = write ()
            }
        Usage =
            { new IUsageLog with
                member _.Record _ = write ()
                member _.Query(_, _, _) = async { return [] }
                member _.Aggregate(_, _) = async { return Map.empty }
            }
    }

/// Warnings and errors, in one queue: the representative site of each
/// family logs one or the other, never both.
let private capturingLogger () =
    let entries = ConcurrentQueue<string>()

    entries,
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn message = entries.Enqueue message

        member _.Error(message, ex) =
            let suffix =
                match ex with
                | Some e ->
                    // The cause, as the Warning formatter sees it, through
                    // an `Async.AwaitTask` boundary's wrapper.
                    let cause = ProviderExceptions.unwrap e
                    sprintf " :: %s: %s" (cause.GetType().Name) cause.Message
                | None -> ""

            entries.Enqueue(message + suffix)
    }

/// A request's context over services that are disposed when the request
/// ends, exactly as the request's own are: a write that fails afterwards
/// finds them gone, so the warning must have been built while it was live.
let private requestContext (configure: IServiceCollection -> unit) (meth: string) (path: string) =
    let services = ServiceCollection()
    configure services
    let provider = services.BuildServiceProvider()
    let ctx = DefaultHttpContext(RequestServices = provider)
    ctx.Request.Method <- meth
    ctx.Request.Path <- PathString path
    ctx.Response.Body <- new MemoryStream()
    ctx, provider

/// One representative call site, and how to reach it.
type private Site = {
    /// The family, as the test name reads.
    Family: string
    /// What the one log entry must contain: the write's name in the
    /// `startAudit` Warning's form, or `Background.start`'s label.
    Names: string
    /// The source the entry names.
    Source: string
    /// Drive the site; the result is the caller-visible outcome.
    Act: Sinks -> ILogger -> Async<string>
}

let private jsonOptions = FableConverters.create ()

let private adAnalytics: Site = {
    Family = "ad analytics (impression)"
    Names = "the AdImpressionRecorded audit write failed"
    Source = "AdAnalyticsApiHandler"
    Act =
        fun sinks logger -> async {
            AdAnalyticsApiHandler.resetObservabilityState ()
            do! CacheReset.invalidateAll ()

            let impression: AdImpression = {
                SlotId = "sidebar-top"
                AdClientId = "ca-pub-test"
                OccurredAt = DateTimeOffset.UtcNow
                PathAtImpression = "/docs/getting-started"
            }

            let ctx, provider =
                requestContext
                    (fun s ->
                        s.AddSingleton<ILogger>(logger) |> ignore
                        s.AddSingleton<IAuditLog>(sinks.AuditLog) |> ignore)
                    "POST"
                    "/api/_platform/ads/impression"

            use _ = provider
            ctx.Request.ContentType <- "application/json"

            ctx.Request.Body <-
                new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(impression, jsonOptions)))

            let handler = choose AdAnalyticsApiHandler.routes
            let! _ = handler (fun c -> Task.FromResult(Some c)) ctx |> Async.AwaitTask
            return sprintf "status %d" ctx.Response.StatusCode
        }
}

let private shareToken: Site = {
    Family = "share token (denial)"
    Names = "the AuthorizationDenied audit write failed"
    Source = "ShareTokenAuthMiddleware"
    Act =
        fun sinks logger -> async {
            let ctx, provider =
                requestContext
                    (fun s ->
                        s.AddSingleton<ILogger>(logger) |> ignore
                        s.AddSingleton<IAuthAuditHook>(sinks.Hook) |> ignore)
                    "GET"
                    "/api/shared"

            use _ = provider
            ctx.Request.QueryString <- QueryString "?token=not-a-real-token"
            let next = RequestDelegate(fun _ -> Task.CompletedTask)
            do! ShareTokenAuth.ShareTokenAuthMiddleware(next).InvokeAsync ctx |> Async.AwaitTask
            return sprintf "status %d" ctx.Response.StatusCode
        }
}

let private serviceAccount: Site = {
    Family = "service account (store)"
    Names = "the ServiceAccountCreated audit write failed"
    Source = "BlobServiceAccountStore"
    Act =
        fun sinks logger -> async {
            let store =
                ServiceAccountStore.BlobServiceAccountStore(InMemoryBlobStorage(), Some sinks.AuditLog, logger)
                :> IServiceAccountStore

            let! created =
                store.Create {
                    DisplayName = "ci-bot"
                    ScopeId = "team-1026"
                    Permissions = Map.ofList [ "reports", [ ModulePermission.Read ] ]
                    CreatedBy = "user-1026"
                }

            return
                match created with
                | Ok account -> sprintf "Ok %s in %s" account.DisplayName account.ScopeId
                | Error e -> sprintf "Error %A" e
        }
}

let private consent: Site = {
    Family = "consent (state store)"
    Names = "the Custom:ConsentGranted audit write failed"
    Source = "InMemoryConsentStateStore"
    Act =
        fun sinks logger -> async {
            let store =
                InMemoryConsentStateStore(Some sinks.AuditLog, logger) :> IConsentStateStore

            let record =
                ConsentRecord.create
                    "subject-1026"
                    (Set.ofList [ Analytics ])
                    Set.empty
                    (DateTimeOffset(2026, 10, 17, 0, 0, 0, TimeSpan.Zero))
                    BannerInteraction

            let! stored = store.Upsert("team-1026", record)

            return
                match stored with
                | Ok r -> sprintf "Ok v%d %A" r.Version r.Granted
                | Error e -> sprintf "Error %s" e
        }
}

let private files: Site = {
    Family = "files (data-store reset)"
    Names = "the DataStoreReset audit write failed"
    Source = "FileManagement"
    Act =
        fun sinks logger -> async {
            let ctx, provider =
                requestContext
                    (fun s ->
                        s.AddSingleton<ILogger>(logger) |> ignore
                        s.AddSingleton<IAuditLog>(sinks.AuditLog) |> ignore
                        s.AddSingleton<DataType list>([]) |> ignore)
                    "POST"
                    "/api/files/reset"

            use _ = provider
            // A user of its own per run, so no other test's session store
            // is reset and none is shared.
            ctx.Items["ToolUp.UserId"] <- sprintf "user-1026-%s" (Guid.NewGuid().ToString "N")
            let! reset = (FileManagement.fileManagementApi ctx).ResetDataStore()
            return sprintf "%A" reset
        }
}

let private memorySecrets () : Secrets.ISecretStore =
    let entries = ConcurrentDictionary<string * string, string>()

    { new Secrets.ISecretStore with
        member _.GetSecret(scopeId, key) = async {
            match entries.TryGetValue((scopeId, key)) with
            | true, v -> return Some v
            | _ -> return None
        }

        member _.SetSecret(scopeId, key, value) = async {
            entries[(scopeId, key)] <- value
            return Ok()
        }

        member _.DeleteSecret(scopeId, key) = async {
            entries.TryRemove((scopeId, key)) |> ignore
            return Ok()
        }

        member _.ListKeys scopeId = async {
            return
                entries.Keys
                |> Seq.filter (fun (s, _) -> s = scopeId)
                |> Seq.map snd
                |> List.ofSeq
        }
    }

let private oauth: Site = {
    Family = "OAuth (provider connect)"
    Names = "the OAuthConnected audit write failed"
    Source = "ProviderOAuthConnect"
    Act =
        fun sinks logger -> async {
            let post: ProviderOAuthFlow.OAuthTokenPost =
                fun _ _ -> async { return Ok """{"access_token":"at-1","refresh_token":"rt-1","expires_in":3600}""" }

            let secrets = memorySecrets ()
            let flow = ClaudeOAuth.createWith post secrets ClaudeOAuth.defaults
            let profiles = ProviderOAuthTestSupport.memoryProviderProfile ()

            let scope: StorageScope = {
                ScopeId = "team-1026"
                Container = "team-team-1026"
                Persist = true
            }

            let credentials: OAuthCredentials = {
                RefreshToken = "rt-0"
                AccessToken = None
                ExpiresAt = None
                IdToken = None
            }

            // The OAuth callback passes its request-bound warner; this is
            // the same formatter over the capturing logger.
            let warn =
                Background.writeFailureLogger "ProviderOAuthConnect" (Some logger) "" "the connect"

            let! connected =
                ProviderOAuthConnect.completeConnectObserved
                    warn
                    profiles
                    secrets
                    (Some sinks.AuditLog)
                    scope
                    "user-1026"
                    flow
                    (OAuthCorrelationKey.providerEntry "anthropic")
                    credentials
                    (DateTime(2026, 10, 17, 0, 0, 0, DateTimeKind.Utc))

            let! profile = profiles.Get scope

            let labels =
                profile
                |> Option.map (fun p -> p.Entries |> List.map (fun e -> e.Label))
                |> Option.defaultValue []

            return sprintf "%A %A" connected labels
        }
}

let private emailEnvelope () : EmailEnvelope = {
    Recipients = [ RecipientId.User "user-1026" ]
    Content = InlineEmail("Subject", "Body", None)
    CorrelationId = None
}

let private transactionalDelivery: Site = {
    Family = "service lifetime (transactional delivery)"
    Names = "delivery_unhandled"
    Source = "[TransactionalDispatcher]"
    Act =
        fun sinks logger -> async {
            let sink =
                TransactionalDispatcherTests.FakeSink(
                    NotificationKind.SinkKind.Email,
                    "Fake",
                    ConcurrentQueue [ SinkResult.Delivered(Some "msg-1026") ]
                )

            let prefs = Map.ofList [ ConfigKeys.NotificationPrefsKeys.EmailEnabled, "true" ]

            // Stopping cancels the deliveries in flight, so the stop waits
            // until the delivery's audit write has run (and failed).
            let written = TaskCompletionSource()

            let auditLog =
                { new IAuditLog with
                    member _.Record(scopeId, event) = async {
                        try
                            return! sinks.AuditLog.Record(scopeId, event)
                        finally
                            written.TrySetResult() |> ignore
                    }

                    member _.GetAuditTrail(a, b, c) = sinks.AuditLog.GetAuditTrail(a, b, c)
                }

            let dispatcher =
                new TransactionalDispatcher.TransactionalDispatcher(
                    [ sink :> INotificationSink ],
                    TransactionalDispatcherTests.FakeConfigStore(prefs) :> IConfigStore,
                    auditLog,
                    logger,
                    {
                        TransactionalRetryPolicy.defaults with
                            MaxAttempts = 1
                            InitialBackoff = TimeSpan.FromMilliseconds 1.0
                            MaxBackoff = TimeSpan.FromMilliseconds 1.0
                    },
                    Tracing.NoOpActivitySink() :> Tracing.IActivitySink
                )

            do! dispatcher.StartAsync CancellationToken.None |> Async.AwaitTask

            try
                let channel =
                    TransactionalDispatcher.DispatchingNotificationChannel(
                        NotificationChannel.InMemoryNotificationChannel(None),
                        dispatcher
                    )
                    :> INotificationChannel

                do! channel.Publish("team-1026", TransactionalEmail(emailEnvelope ()))

                let! winner =
                    Task.WhenAny(sink.FirstCallTask, Task.Delay(TimeSpan.FromSeconds 10.0))
                    |> Async.AwaitTask

                let! _ =
                    Task.WhenAny(written.Task, Task.Delay(TimeSpan.FromSeconds 10.0))
                    |> Async.AwaitTask

                do! Async.Sleep 200

                return
                    if obj.ReferenceEquals(winner, sink.FirstCallTask) then
                        "delivered to the sink"
                    else
                        "never reached the sink"
            finally
                dispatcher.StopAsync(CancellationToken.None) |> ignore
        }
}

let private sites = [
    adAnalytics
    shareToken
    serviceAccount
    consent
    files
    oauth
    transactionalDelivery
]

let private arm (site: Site) (mode: FailureMode) =
    testCaseAsync (sprintf "%s: a %A failure is caught, logged once, and changes nothing" site.Family mode)
    <| async {
        watchUnobserved ()
        let marker = sprintf "callsite-down-%s" (Guid.NewGuid().ToString "N")
        let _, controlLogger = capturingLogger ()
        let! control = site.Act (sinks None) controlLogger

        let entries, logger = capturingLogger ()
        let! outcome = site.Act (sinks (Some(mode, marker))) logger

        Expect.equal outcome control "the caller's outcome is the healthy sinks'"

        let! logged =
            settleOn 100 (fun () ->
                entries.ToArray()
                |> List.ofArray
                |> List.filter (fun e -> e.Contains site.Names))

        let! leaked = leakedFor marker
        Expect.isEmpty leaked "no unobserved task exception carries this write's failure"

        match logged with
        | [ entry ] ->
            Expect.stringContains entry "CallSiteSinkDownException" "the entry names the exception type"
            Expect.stringContains entry site.Source "the entry names its source"
        | other ->
            failtestf
                "expected exactly one entry naming %s, got %A (outcome %s; every entry %A)"
                site.Names
                other
                outcome
                (entries.ToArray())
    }

// ─── The regrowth guard ─────────────────────────────────────────────

/// A bare `Async.Start` the guard admits: the file, a fragment found on the
/// call's line or in the ten lines above it (what identifies THIS call in
/// its file), and why it is safe without `Background`.
type private Admitted = {
    File: string
    Anchor: string
    Reason: string
}

/// Every bare `Async.Start` under `src/ToolUp.Platform.Server/Server/` at
/// Phase 1026. Phase 1024's census classed 15 SAFE; the 16th is the helper
/// itself. Adding a call means adding an entry here with its reason — or,
/// for any write, using `Background.startAudit` / `Background.start`.
let private admitted: Admitted list = [
    {
        File = "Background.fs"
        Anchor = "let internal startAudit"
        Reason = "the helper itself: the started work is wrapped in Async.Catch"
    }
    {
        File = "Compose/ComposeTeamRuntime.fs"
        Anchor = "GrantNotificationObserver("
        Reason = "passed as the observer's schedule; each publish carries its own try/with"
    }
    {
        File = "DefaultDeployPipeline.fs"
        Anchor = "Wrap the fire so any unhandled throw"
        Reason = "the started pipeline is wrapped in try/with; emitState swallows its own failures"
    }
    {
        File = "Files/FileManagement.fs"
        Anchor = "eprintfn \"%s: %O\" msg ex"
        Reason = "post-save hooks after upload, each wrapped in safeHook's try/with"
    }
    {
        File = "Files/FileManagement.fs"
        Anchor = "Post-save hook failed on retry"
        Reason = "post-save hooks on retry, each wrapped in safeHook's try/with"
    }
    {
        File = "Files/FileManagement.fs"
        Anchor = "The audit row above already recorded the fact."
        Reason = "the store-reset channel publish runs under its own try/with"
    }
    {
        File = "GrantPolicyGuard.fs"
        Anchor = "GrantPolicyRefused payload"
        Reason = "log.Record inside the started async's try/with"
    }
    {
        File = "GrantPolicyGuard.fs"
        Anchor = "GrantRecorded payload"
        Reason = "log.Record inside the started async's try/with"
    }
    {
        File = "GrantConsentStore.fs"
        Anchor = "resolveLive store verifier auditLog Async.Start"
        Reason = "passed as resolveLive's schedule; emit wraps log.Record in try/with"
    }
    {
        File = "GrantConsentStore.fs"
        Anchor = "resolveLive consentStore verifier auditLog Async.Start"
        Reason = "passed as resolveLive's schedule; emit wraps log.Record in try/with"
    }
    {
        File = "IEdgeCache.fs"
        Anchor = "Phase 740 — resolved once per purge"
        Reason = "purgeWithRetry catches each attempt"
    }
    {
        File = "RemotingHelpers.fs"
        Anchor = "GrantConsentStore.guardDispatchWithConsent"
        Reason = "passed as guardDispatchWithConsent's schedule; both emit paths use try/with"
    }
    {
        File = "Scope/AdminMutationApproval.fs"
        Anchor = "let schedule (work: Async<unit>) = Async.Start work"
        Reason = "the ceremony's schedule; every use goes through emit's try/with"
    }
    {
        File = "Remoting/Jobs.fs"
        Anchor = "The work runs under the job's own token"
        Reason = "the job runner's work runs under try/with recording Failed / Cancelled"
    }
    {
        File = "Remoting/Streaming.fs"
        Anchor = "let channel = Channel.CreateUnbounded<'T>()"
        Reason = "runProducer runs under try/with; a fault completes the channel"
    }
    {
        File = "WebhookDispatcher.fs"
        Anchor = "Async.Start(emitAudit event.ScopeId \"WebhookEventDropped\""
        Reason = "emitAudit wraps eventStore.Write in try/with and logs the failure"
    }
]

/// `Async.Start` as a whole word: never `StartAsTask`, `StartImmediate`,
/// `StartChild` or `StartWithContinuations`.
let private bareStart = Regex(@"\bAsync\.Start\b(?!\w)", RegexOptions.Compiled)

/// The code on a line, without a trailing `//` comment. A line that is
/// only a comment (`//` or `///`) carries no code.
let private codeOf (line: string) =
    let trimmed = line.TrimStart()

    if trimmed.StartsWith "//" then
        ""
    else
        match line.IndexOf "//" with
        | -1 -> line
        | i -> line.Substring(0, i)

/// Every bare start in `source`, as (1-based line, the window the anchor is
/// looked for in: the call's line and the ten above it).
let private bareStartsIn (source: string) : (int * string) list =
    let lines = source.Replace("\r\n", "\n").Split '\n'

    [
        for i in 0 .. lines.Length - 1 do
            if bareStart.IsMatch(codeOf lines[i]) then
                let first = max 0 (i - 10)
                i + 1, String.Join("\n", lines[first..i])
    ]

/// The sites in one file the allowlist does not admit, and the entries for
/// that file that admitted nothing (a stale entry is refused too, so the
/// list only ever names real calls).
let private judge (file: string) (source: string) =
    let entries = admitted |> List.filter (fun a -> a.File = file)
    let starts = bareStartsIn source

    let unadmitted =
        starts
        |> List.filter (fun (_, window) ->
            entries |> List.filter (fun a -> window.Contains a.Anchor) |> List.length <> 1)
        |> List.map fst

    let stale =
        entries
        |> List.filter (fun a ->
            starts
            |> List.filter (fun (_, window) -> window.Contains a.Anchor)
            |> List.length
            <> 1)

    unadmitted, stale

let private serverSourceRoot () =
    let rec up (dir: DirectoryInfo) =
        if isNull dir then
            failtest "src/ToolUp.Platform.Server/Server was not found above the test binary"
        else
            let candidate =
                Path.Combine(dir.FullName, "src", "ToolUp.Platform.Server", "Server")

            if Directory.Exists candidate then
                candidate
            else
                up dir.Parent

    up (DirectoryInfo AppContext.BaseDirectory)

let private guard =
    testList "the regrowth guard" [
        test "no bare Async.Start under Server/ outside the reasoned allowlist" {
            let root = serverSourceRoot ()
            let mutable seen = 0

            let findings = [
                for path in Directory.EnumerateFiles(root, "*.fs", SearchOption.AllDirectories) do
                    let file = Path.GetRelativePath(root, path).Replace('\\', '/')
                    let source = File.ReadAllText path
                    seen <- seen + (bareStartsIn source).Length
                    let unadmitted, stale = judge file source

                    for line in unadmitted do
                        sprintf
                            "%s:%d — a bare Async.Start: start the write through Background.startAudit / Background.start, or admit it here with its reason"
                            file
                            line

                    for entry in stale do
                        sprintf "%s — the entry anchored on %A no longer names exactly one call" file entry.Anchor
            ]

            // Every allowlisted file was actually read: an entry whose file
            // is gone is stale too.
            let missing =
                admitted
                |> List.filter (fun a -> not (File.Exists(Path.Combine(root, a.File))))
                |> List.map (fun a -> sprintf "%s — the admitted file does not exist" a.File)

            match findings @ missing with
            | [] -> ()
            | refused ->
                failtestf
                    "every bare Async.Start is admitted, and every admission is live:%s%s"
                    Environment.NewLine
                    (String.concat Environment.NewLine refused)

            Expect.equal seen admitted.Length "the scan saw exactly the admitted calls (it is not vacuous)"
        }

        test "the scanner sees a bare start and ignores StartAsTask, StartImmediate and comments" {
            let source =
                String.concat "\n" [
                    "let a () = log.Record(s, e) |> Async.Start"
                    "let b () = Async.StartAsTask(work) |> ignore"
                    "let c () = Async.StartImmediate work"
                    "// a comment naming Async.Start is not a call"
                    "/// nor is a doc comment naming `Async.Start`"
                    "let d () = Async.StartChild work // Async.Start in a trailing comment"
                    "    Async.Start("
                ]

            Expect.equal (bareStartsIn source |> List.map fst) [ 1; 7 ] "only the two real bare starts are seen"

            let unadmitted, _ = judge "NotAdmitted.fs" source
            Expect.equal unadmitted [ 1; 7 ] "a file with no entries admits none of its bare starts"
        }

        test "every admission carries a reason" {
            for entry in admitted do
                Expect.isFalse
                    (String.IsNullOrWhiteSpace entry.Reason)
                    (sprintf "%s %A has a reason" entry.File entry.Anchor)
        }
    ]

let tests =
    testList "Phase 1026 — every fire-and-forget audit record is caught at its call site" [
        for site in sites do
            for mode in [ Plain; TaskShaped; Synchronous ] do
                arm site mode

        guard
    ]