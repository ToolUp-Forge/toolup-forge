// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 772 — server-side egress policy: the outbound-call seam.
///
/// Four claims the phase makes, each probed where it can fail:
///
///   • a deny is refused BEFORE the socket opens — the inner handler is
///     never reached, and the typed exception is what the caller sees;
///   • a permit is byte-identical — the request the inner handler
///     receives through the platform factory is the request a bare
///     `HttpClient` would have handed it;
///   • the audit row carries the ORIGIN and the component, never the
///     path or the query string;
///   • a verified composition with no grants denies everything outbound
///     and says so at boot.
///
/// The enforcement module is process-wide by design (the header note in
/// `EgressPolicyHandler.fs` says why), so every probe that installs a
/// binding runs under `testSequenced` and restores the permit-all default
/// in `finally`. The pure probes need no such care and run in parallel.
module EgressPolicyTests

open System
open System.Net
open System.Net.Http
open System.Threading.Tasks
open Expecto
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.DeploymentVerification

// ─── Doubles ───────────────────────────────────────────────────────────

/// Records every audit row with its scope.
type private RecordingAudit() =
    let recorded = ResizeArray<string * AuditEvent>()

    member _.Events = lock recorded (fun () -> List.ofSeq recorded)

    interface IAuditLog with
        member _.Record(scopeId, audit) = async { lock recorded (fun () -> recorded.Add((scopeId, audit))) }

        member _.GetAuditTrail(_, _, _) = async { return recorded |> Seq.map snd |> List.ofSeq }

/// A primary handler that never opens a socket: it records every request
/// it was handed and answers with an echo of it, so "the inner handler
/// was reached" and "what it was handed" are both observable.
type private RecordingHandler() =
    inherit HttpMessageHandler()
    let seen = ResizeArray<HttpRequestMessage * string>()

    /// Every (request, body) this handler received, in order.
    member _.Seen = lock seen (fun () -> List.ofSeq seen)

    override _.SendAsync(request, _cancellationToken) = task {
        let! body =
            match request.Content with
            | null -> Task.FromResult ""
            | content -> content.ReadAsStringAsync()

        lock seen (fun () -> seen.Add((request, body)))
        let response = new HttpResponseMessage(HttpStatusCode.OK)
        response.Content <- new StringContent(sprintf "%s %O %s" request.Method.Method request.RequestUri body)
        return response
    }

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

let private origin (url: string) = EgressDestination.parse url

let private moduleA = ComponentId.ofModule "reports"
let private moduleB = ComponentId.ofModule "billing"

let private grantsForA: EgressGrantSignature =
    Map.ofList [ moduleA, EgressGrant.ofOrigins [ "https://api.example.com" ] ]

let private decide (binding: EgressPolicyBinding) componentId url surface =
    binding.Policy.Decide(componentId, origin url, surface)

let private isPermit =
    function
    | EgressVerdict.Permit -> true
    | EgressVerdict.Deny _ -> false

/// Send through `client` and hand back the raised exception, if any.
let private sendCatching (client: HttpClient) (request: HttpRequestMessage) : Async<Result<HttpResponseMessage, exn>> = async {
    let! outcome = client.SendAsync request |> Async.AwaitTask |> Async.Catch

    // `Async.AwaitTask` surfaces a faulted task's exception wrapped in an
    // `AggregateException`; a `task` / C# `await` caller sees the inner one.
    // Unwrap so the probes assert on what the handler actually raised.
    let rec unwrap (e: exn) =
        match e with
        | :? AggregateException as a when a.InnerExceptions.Count = 1 -> unwrap a.InnerExceptions[0]
        | e -> e

    return
        match outcome with
        | Choice1Of2 response -> Ok response
        | Choice2Of2 e -> Error(unwrap e)
}

let private deniedRows (audit: RecordingAudit) =
    audit.Events
    |> List.choose (fun (scope, event) ->
        match event with
        | EgressDenied p -> Some(scope, p)
        | _ -> None)

/// Run `body` from a clean enforcement state and return to it afterwards,
/// whatever happens.
let private isolated (body: unit -> Async<unit>) : Async<unit> = async {
    EgressEnforcement.reset ()

    try
        return! body ()
    finally
        EgressEnforcement.reset ()
}

// ─── The pure half ─────────────────────────────────────────────────────

let private destinationTests =
    testList "EgressDestination — origin only, never the URL" [
        test "a full URL reduces to its lowercase origin with the default port elided" {
            let d =
                EgressDestination.tryParse "HTTPS://Api.Example.com:443/v1/secret?token=abc#frag"

            Expect.equal (d |> Option.map EgressDestination.origin) (Some "https://api.example.com") "origin only"
        }

        test "a non-default port is kept; a default one is dropped" {
            Expect.equal
                (EgressDestination.tryParse "http://host:8080/x"
                 |> Option.map EgressDestination.origin)
                (Some "http://host:8080")
                "explicit non-default port kept"

            Expect.equal
                (EgressDestination.tryParse "http://host:80/x"
                 |> Option.map EgressDestination.origin)
                (Some "http://host")
                "default http port elided"
        }

        test "userinfo never reaches the origin" {
            Expect.equal
                (EgressDestination.tryParse "https://user:pw@host/x"
                 |> Option.map EgressDestination.origin)
                (Some "https://host")
                "credential stripped"
        }

        test "a bracketed IPv6 literal keeps its colons and its port" {
            Expect.equal
                (EgressDestination.tryParse "https://[::1]:8443/x"
                 |> Option.map EgressDestination.origin)
                (Some "https://[::1]:8443")
                "ipv6 origin"
        }

        test "no scheme, no host, or a non-numeric port is None rather than a guess" {
            Expect.isNone (EgressDestination.tryParse "no-scheme/path") "no scheme"
            Expect.isNone (EgressDestination.tryParse "https:///path") "no host"
            Expect.isNone (EgressDestination.tryParse "https://host:notaport/") "bad port"
            Expect.isNone (EgressDestination.tryParse null) "null"
        }

        test "ofParts agrees with tryParse" {
            Expect.equal
                (EgressDestination.ofParts "HTTPS" "Api.Example.com" (Some 443))
                (EgressDestination.tryParse "https://api.example.com/")
                "same origin either way"
        }

        test "parse raises on an unparseable URL rather than narrowing a grant silently" {
            Expect.throwsT<ArgumentException> (fun () -> EgressDestination.parse "nope" |> ignore) "raises"
        }
    ]

let private policyTests =
    testList "EgressPolicy — bind resolves profile stance + grants" [
        test "not mandatory, nothing declared: permit-all, posture unenforced (GP 11)" {
            let b = EgressPolicy.bind false None
            Expect.equal b.Posture EgressPosture.Unenforced "posture"
            Expect.isTrue (isPermit (decide b moduleB "https://anywhere.example" EgressSurface.Other)) "permits"

            Expect.isTrue
                (isPermit (decide b EgressPolicy.platformComponent "http://[::1]:9/x" EgressSurface.Webhook))
                "permits all"
        }

        test "mandatory, nothing declared: deny-all, and the boot sentence says so" {
            let b = EgressPolicy.bind true None
            Expect.equal b.Posture EgressPosture.DenyAll "posture"

            Expect.isFalse
                (isPermit (decide b moduleA "https://api.example.com" EgressSurface.AIProvider))
                "denies declared-looking origin"

            Expect.isFalse
                (isPermit (decide b EgressPolicy.platformComponent "https://x.example" EgressSurface.Other))
                "denies the platform too"

            Expect.stringContains (EgressPosture.describe b.Posture) "refused before the socket opens" "says so"
            Expect.stringContains (EgressPosture.describe b.Posture) "deny-all" "names the posture"
        }

        test "mandatory with grants: declared origin permitted, other origin and undeclared component denied" {
            let b = EgressPolicy.bind true (Some grantsForA)
            Expect.equal b.Posture (EgressPosture.Declared(1, true)) "posture"

            Expect.isTrue
                (isPermit (decide b moduleA "https://api.example.com/v1/x?q=1" EgressSurface.AIProvider))
                "declared origin"

            Expect.isFalse (isPermit (decide b moduleA "https://other.example" EgressSurface.AIProvider)) "other origin"

            Expect.isFalse
                (isPermit (decide b moduleB "https://api.example.com" EgressSurface.AIProvider))
                "undeclared component"
        }

        test "not mandatory with grants: undeclared component stays unrestricted; declared one is bound" {
            let b = EgressPolicy.bind false (Some grantsForA)
            Expect.equal b.Posture (EgressPosture.Declared(1, false)) "posture"

            Expect.isTrue
                (isPermit (decide b moduleB "https://anywhere.example" EgressSurface.Other))
                "undeclared unrestricted"

            Expect.isFalse (isPermit (decide b moduleA "https://other.example" EgressSurface.Other)) "declared bound"
        }

        test "a deny reason names component, origin, surface and grant — and never the path or query" {
            let b = EgressPolicy.bind true (Some grantsForA)

            match decide b moduleA "https://other.example/private/path?secret=1" EgressSurface.Notification with
            | EgressVerdict.Permit -> failtest "expected a deny"
            | EgressVerdict.Deny reason ->
                Expect.stringContains reason "module:reports" "component"
                Expect.stringContains reason "https://other.example" "origin"
                Expect.stringContains reason "notification" "surface"
                Expect.stringContains reason "declared{https://api.example.com}" "grant"
                Expect.isFalse (reason.Contains "/private") "no path"
                Expect.isFalse (reason.Contains "secret") "no query"
        }

        test "EgressGrant renders deterministically and keeps 'declares nothing' distinct from 'unrestricted'" {
            Expect.equal (EgressGrant.render UnrestrictedEgress) "unrestricted" "unrestricted"
            Expect.equal (EgressGrant.render (EgressGrant.ofOrigins [])) "declared{}" "declares nothing"

            Expect.equal
                (EgressGrant.render (EgressGrant.ofOrigins [ "https://b.example"; "https://a.example" ]))
                "declared{https://a.example,https://b.example}"
                "ordinal order"

            Expect.isFalse (EgressGrant.isDeclared UnrestrictedEgress) "not declared"
            Expect.isTrue (EgressGrant.isDeclared (EgressGrant.ofOrigins [])) "declared, empty"
        }
    ]

// ─── The enforcing half ────────────────────────────────────────────────

let private handlerTests =
    testSequenced
    <| testList "EgressPolicyHandler + PlatformHttpClient — the seam binds" [
        testCaseAsync "a deny is refused before the socket opens: inner handler never reached, typed exception raised"
        <| isolated (fun () -> async {
            let audit = RecordingAudit()
            EgressEnforcement.bindAudit audit
            EgressEnforcement.install (EgressPolicy.bind true None)
            let inner = new RecordingHandler()
            use client = PlatformHttpClient.createWith EgressSurface.AIProvider inner

            let! outcome =
                sendCatching
                    client
                    (new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/v1/secret?token=abc"))

            match outcome with
            | Ok _ -> failtest "expected the call to be refused"
            | Error(:? EgressDeniedException as denied) ->
                Expect.equal
                    (EgressDestination.origin denied.Destination)
                    "https://api.example.com"
                    "origin on the exception"

                Expect.equal
                    denied.Component
                    EgressPolicy.platformComponent
                    "attributed to the platform when nothing claimed"

                Expect.equal denied.Surface EgressSurface.AIProvider "surface"
                Expect.isFalse (denied.Reason.Contains "/v1/secret") "reason carries no path"
                Expect.isFalse (denied.Reason.Contains "token") "reason carries no query"
            | Error other ->
                failtestf "expected EgressDeniedException, got %s: %s" (other.GetType().Name) other.Message

            Expect.isEmpty inner.Seen "the inner handler was never reached — the request never left the process"
            Expect.equal (EgressEnforcement.denialCount ()) 1 "one refusal ledgered"
        })

        testCaseAsync "the audit row carries origin and component, never the path or query"
        <| isolated (fun () -> async {
            let audit = RecordingAudit()
            EgressEnforcement.bindAudit audit
            EgressEnforcement.install (EgressPolicy.bind true (Some grantsForA))

            use client =
                PlatformHttpClient.createWith EgressSurface.Webhook (new RecordingHandler())

            let! _ =
                EgressComponentContext.runAs moduleA (fun () ->
                    sendCatching
                        client
                        (new HttpRequestMessage(HttpMethod.Post, "https://hooks.example.com:8443/deliver/42?sig=xyz")))

            match deniedRows audit with
            | [ scope, row ] ->
                Expect.equal scope EgressEnforcement.AuditScope "reserved platform scope"
                Expect.equal row.Component "module:reports" "the claimed component"
                Expect.equal row.Origin "https://hooks.example.com:8443" "origin, with its non-default port"
                Expect.equal row.Surface "webhook" "surface label"
                Expect.isFalse (row.Reason.Contains "/deliver") "no path on the row"
                Expect.isFalse (row.Reason.Contains "sig=") "no query on the row"
            | rows -> failtestf "expected exactly one EgressDenied row, got %d" rows.Length

            match EgressEnforcement.recentDenials () with
            | [ record ] ->
                Expect.equal record.DeniedComponent moduleA "ledger component"
                Expect.equal record.DeniedOrigin "https://hooks.example.com:8443" "ledger origin"
            | records -> failtestf "expected one ledgered refusal, got %d" records.Length
        })

        testCaseAsync "a permit is byte-identical: the inner handler receives what a bare HttpClient would hand it"
        <| isolated (fun () -> async {
            // The permit-all default is installed by `isolated`'s reset.
            let bare = new RecordingHandler()
            let platform = new RecordingHandler()
            use bareClient = new HttpClient(bare)
            use platformClient = PlatformHttpClient.createWith EgressSurface.Other platform

            let request () =
                let r =
                    new HttpRequestMessage(HttpMethod.Post, "https://api.example.com/v1/items?page=2")

                r.Headers.Add("X-Probe", "772")
                r.Headers.Authorization <- Headers.AuthenticationHeaderValue("Bearer", "k")
                r.Content <- new StringContent("{\"a\":1}", Text.Encoding.UTF8, "application/json")
                r

            let! bareResponse = bareClient.SendAsync(request ()) |> Async.AwaitTask
            let! platformResponse = platformClient.SendAsync(request ()) |> Async.AwaitTask
            let! bareBody = bareResponse.Content.ReadAsStringAsync() |> Async.AwaitTask
            let! platformBody = platformResponse.Content.ReadAsStringAsync() |> Async.AwaitTask

            match bare.Seen, platform.Seen with
            | [ b, bBody ], [ p, pBody ] ->
                Expect.equal p.Method b.Method "method"
                Expect.equal p.RequestUri b.RequestUri "uri, path and query intact"
                Expect.equal (string p.Headers) (string b.Headers) "request headers"
                Expect.equal (string p.Content.Headers) (string b.Content.Headers) "content headers"
                Expect.equal pBody bBody "body bytes"
            | _ -> failtest "each inner handler should have seen exactly one request"

            Expect.equal platformBody bareBody "response bytes"
            Expect.equal (EgressEnforcement.denialCount ()) 0 "nothing refused"
            Expect.isEmpty (EgressEnforcement.recentDenials ()) "nothing ledgered"
        })

        testCaseAsync "the ambient component flows into the decision and is restored afterwards"
        <| isolated (fun () -> async {
            EgressEnforcement.install (EgressPolicy.bind true (Some grantsForA))

            use client =
                PlatformHttpClient.createWith EgressSurface.ModuleHandler (new RecordingHandler())

            let get () =
                sendCatching client (new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/"))

            let! asA = EgressComponentContext.runAs moduleA get
            Expect.isOk asA "module:reports declares api.example.com — permitted"

            let! asPlatform = get ()

            match asPlatform with
            | Error(:? EgressDeniedException as denied) ->
                Expect.equal denied.Component EgressPolicy.platformComponent "back to the platform after runAs"
            | _ -> failtest "the undeclared platform component should be refused under a mandatory profile"

            Expect.isNone (EgressComponentContext.tryCurrent ()) "no claim leaks past runAs"
        })

        testCaseAsync "the DI-named client runs under the same installed policy"
        <| isolated (fun () -> async {
            let audit = RecordingAudit()
            let services = ServiceCollection()
            ComposeRuntimeServices.registerPlatformHttpClient services audit silentLogger
            EgressEnforcement.install (EgressPolicy.bind true None)
            use provider = services.BuildServiceProvider()
            let factory = provider.GetRequiredService<IHttpClientFactory>()
            use client = factory.CreateClient PlatformHttpClient.Name

            let! outcome = sendCatching client (new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/x"))

            match outcome with
            | Error(:? EgressDeniedException as denied) ->
                Expect.equal
                    denied.Surface
                    EgressSurface.ModuleHandler
                    "the named client is the module-handler surface"
            | _ -> failtest "the named client should consult the installed deny-all binding"

            Expect.equal (deniedRows audit).Length 1 "registration bound the audit log"
        })

        testCaseAsync "a verified composition with no grants says so at boot"
        <| isolated (fun () -> async {
            let logged = ResizeArray<string>()

            let logger =
                { new ILogger with
                    member _.Debug _ = ()
                    member _.Info m = logged.Add m
                    member _.Warn _ = ()
                    member _.Error(_, _) = ()
                }

            EgressEnforcement.install (EgressPolicy.bind true None)
            ComposeRuntimeServices.registerPlatformHttpClient (ServiceCollection()) (RecordingAudit()) logger

            Expect.exists
                (List.ofSeq logged)
                (fun m -> m.Contains "deny-all" && m.Contains "refused before the socket opens")
                "the boot log states the deny-all posture"
        })
    ]

// ─── The report's eleventh section ─────────────────────────────────────

let private integrity posture denials = {
    EgressProfile = "verified"
    EgressPosture = posture
    EgressComponents = [
        {
            EgressComponent = moduleA
            EgressDeclaredGrant = EgressGrant.ofOrigins [ "https://api.example.com" ]
        }
    ]
    EgressDenials = denials
    EgressDenialsSinceBoot = List.length denials
}

let private sectionFor (egress: EgressIntegrity option) =
    let report =
        DeploymentVerificationEvidence.none
        |> DeploymentVerificationEvidence.withEgress egress
        |> fun e -> DeploymentVerificationReport.buildReport e "probe" DateTime.UnixEpoch
        |> Async.RunSynchronously

    report, report.Sections |> List.find (fun s -> s.Id = EgressSection)

let private reportTests =
    testList "DeploymentVerificationReport — the egress section" [
        test "nothing supplied reads not-composed, and the not-proved statement stands unnarrowed" {
            let report, section = sectionFor None
            Expect.equal (VerificationSectionVerdict.label section.Verdict) "not-composed" "verdict"

            let statement =
                report.NotProved
                |> List.find (fun s -> s.Id = "egress-covers-the-platform-factory")

            Expect.isNone statement.Narrowing "nothing composed narrows nothing"
        }

        test "the permit-all default reads observed, and says it bounds nothing" {
            let _, section = sectionFor (Some(integrity EgressPosture.Unenforced []))
            Expect.equal (VerificationSectionVerdict.label section.Verdict) "observed" "verdict"
            Expect.stringContains (VerificationSectionVerdict.detail section.Verdict) "nothing is bounded" "honest"
        }

        test "deny-all under a verified profile reads verified and counts its refusals" {
            let denial = {
                DeniedComponent = EgressPolicy.platformComponent
                DeniedOrigin = "https://api.example.com"
                DeniedSurface = "ai-provider"
                DeniedReason = "r"
            }

            let report, section = sectionFor (Some(integrity EgressPosture.DenyAll [ denial ]))
            Expect.equal (VerificationSectionVerdict.label section.Verdict) "verified" "verdict"
            Expect.stringContains (VerificationSectionVerdict.detail section.Verdict) "1 refusal(s)" "count"

            Expect.contains
                section.Findings
                "refused: module:_platform -> https://api.example.com (ai-provider surface)"
                "the refusal line names origin and component"

            Expect.contains section.Findings "module:reports: declared{https://api.example.com}" "the grant line"

            let statement =
                report.NotProved
                |> List.find (fun s -> s.Id = "egress-covers-the-platform-factory")

            Expect.isSome statement.Narrowing "a composed section narrows the statement"
        }

        test "declared grants read verified when undeclared components are refused, observed when they are not" {
            let _, mandatory = sectionFor (Some(integrity (EgressPosture.Declared(1, true)) []))
            Expect.equal (VerificationSectionVerdict.label mandatory.Verdict) "verified" "mandatory"

            let _, advisory = sectionFor (Some(integrity (EgressPosture.Declared(1, false)) []))
            Expect.equal (VerificationSectionVerdict.label advisory.Verdict) "observed" "advisory"

            Expect.stringContains
                (VerificationSectionVerdict.detail advisory.Verdict)
                "partial"
                "says the bound is partial"
        }

        test "the section is APPENDED after the remoting-decoder section" {
            let report, _ = sectionFor None
            let ids = report.Sections |> List.map _.Id
            Expect.equal (List.last ids) EgressSection "last"
            Expect.equal ids[ids.Length - 2] RemotingDecoderSection "after the tenth"
        }
    ]

let private evidenceTests =
    testSequenced
    <| testList "EgressEnforcement.deploymentVerificationEvidence — derived from what is installed" [
        testCaseAsync "the projection carries the installed posture, grants and ledger"
        <| isolated (fun () -> async {
            EgressEnforcement.install (EgressPolicy.bind true (Some grantsForA))

            use client =
                PlatformHttpClient.createWith EgressSurface.Other (new RecordingHandler())

            let! _ = sendCatching client (new HttpRequestMessage(HttpMethod.Get, "https://other.example/x"))

            let projected = EgressEnforcement.deploymentVerificationEvidence "verified"
            Expect.equal projected.EgressProfile "verified" "profile label"
            Expect.equal projected.EgressPosture (EgressPosture.Declared(1, true)) "posture"
            Expect.equal (projected.EgressComponents |> List.map _.EgressComponent) [ moduleA ] "components"
            Expect.equal projected.EgressDenialsSinceBoot 1 "count"
            Expect.equal (projected.EgressDenials |> List.map _.DeniedOrigin) [ "https://other.example" ] "ledger"
        })
    ]

let tests =
    testList "Phase 772 — server-side egress policy" [
        destinationTests
        policyTests
        handlerTests
        reportTests
        evidenceTests
    ]