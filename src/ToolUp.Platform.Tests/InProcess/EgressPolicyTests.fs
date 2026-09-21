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
// Phase 796 — the fact-disclosure surface vocabulary the pinned snapshot
// mirrors. `ToolUp.Facts` stays fully qualified at its few use sites
// rather than opened: it is the tier ABOVE `ToolUp.Platform` in the
// reference graph — which is the whole reason the egress seam holds a
// mirror instead of naming `TaintLabel` — and spelling it out at the
// call site is what keeps that direction legible in the probes.
open ToolUp.Platform.VectorKnowledgeTypes

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
    binding.Policy.Decide(componentId, origin url, surface, EgressLabel.clean)

/// Phase 796 — the same decision over an explicit payload label.
let private decideLabelled (binding: EgressPolicyBinding) componentId url surface label =
    binding.Policy.Decide(componentId, origin url, surface, label)

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

            // Phase 796 — this probe is about the COMPONENT axis, and a
            // mandatory profile now also refuses a payload whose lineage
            // was never computed, so the chain claims a computed label to
            // isolate the axis under test. Without it the permitted leg
            // would be refused for the other reason entirely, and the
            // probe would pass for the wrong one.
            let get () =
                EgressLabelContext.runLabelled EgressLabel.clean (fun () ->
                    sendCatching client (new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/")))

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
    EgressLabelVocabulary = DisclosurePolicyRefSnapshot.Version
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
                DeniedLabel = "labelled{party-a}"
                DeniedReason = "r"
            }

            let report, section = sectionFor (Some(integrity EgressPosture.DenyAll [ denial ]))
            Expect.equal (VerificationSectionVerdict.label section.Verdict) "verified" "verdict"
            Expect.stringContains (VerificationSectionVerdict.detail section.Verdict) "1 refusal(s)" "count"

            Expect.contains
                section.Findings
                "refused: module:_platform -> https://api.example.com (ai-provider surface, labelled{party-a})"
                "the refusal line names origin, component and payload label"

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

// The seam's contract pack, bound by both shipped implementations (GP 12
// treats a portable interface as unproven until a second implementation
// runs the same pack).
let private contractTests =
    testList "IEgressPolicy contract — both shipped policies" [
        ToolUp.Platform.Tests.Contracts.IEgressPolicyContract.tests "PermitAll" EgressPolicy.permitAll

        ToolUp.Platform.Tests.Contracts.IEgressPolicyContract.tests
            "DeclaredDestinations"
            (EgressPolicy.declaredDestinations (DeclaredDestinations Set.empty) grantsForA)

        // Phase 796 — the decorator is a third implementation of the same
        // seam and is held to the same laws, including on the coordinate
        // it was added to read.
        ToolUp.Platform.Tests.Contracts.IEgressPolicyContract.tests
            "RequireClearedLabel over DeclaredDestinations"
            (EgressPolicy.requireClearedLabel (EgressPolicy.declaredDestinations UnrestrictedEgress grantsForA))
    ]

// ─── Phase 796 — the payload's label, and one ref vocabulary ───────────
//
// Three claims, each probed where it can fail:
//
//   • `Unlabelled` and `clean` are DIFFERENT values, and stay different
//     through every accessor and through the join — the whole phase
//     rests on "nobody computed a lineage" not collapsing into "a
//     lineage was computed and carries nothing";
//   • the gates decide on the label: a payload at a DECLARED origin is
//     still refused under a mandatory profile when its lineage was never
//     computed, and the advisory profile is byte-for-byte unchanged
//     (GP 11);
//   • the pinned ref vocabulary matches the live surfaces, with a go-red
//     control proving the check has teeth rather than agreeing with
//     itself.

let private labelTests =
    testList "EgressLabel — the mirror and its order" [
        test "unlabelled is not clean, and the difference survives every accessor" {
            Expect.notEqual EgressLabel.unlabelled EgressLabel.clean "the two are distinct values"
            Expect.isFalse (EgressLabel.isLabelled EgressLabel.unlabelled) "unlabelled carries no computation"
            Expect.isTrue (EgressLabel.isLabelled EgressLabel.clean) "clean is a computed answer"
            Expect.isFalse (EgressLabel.isClean EgressLabel.unlabelled) "unlabelled is NOT clean"
            Expect.isTrue (EgressLabel.isClean EgressLabel.clean) "clean is clean"

            // Both read as carrying nothing — which is why `isLabelled`
            // has to be consulted first, and why `policyRefs` alone can
            // never be read as evidence that nothing reaches the payload.
            Expect.isEmpty (EgressLabel.policyRefs EgressLabel.unlabelled) "no refs"
            Expect.isEmpty (EgressLabel.policyRefs EgressLabel.clean) "no refs"
            Expect.isFalse (EgressLabel.contains "party-a" EgressLabel.unlabelled) "knows nothing either way"
        }

        test "join is absorbing on unlabelled and set union on computed labels" {
            let a = EgressLabel.ofPolicyRef "party-a"
            let b = EgressLabel.ofPolicyRef "party-b"

            Expect.equal (EgressLabel.join a b) (EgressLabel.ofPolicyRefs [ "party-a"; "party-b" ]) "union"
            Expect.equal (EgressLabel.join a EgressLabel.clean) a "clean is the identity on computed labels"

            // The fail-open this rules out: an uncomputed source joined
            // into a computed one must not vanish.
            Expect.equal (EgressLabel.join a EgressLabel.unlabelled) EgressLabel.unlabelled "absorbing on the right"
            Expect.equal (EgressLabel.join EgressLabel.unlabelled a) EgressLabel.unlabelled "absorbing on the left"

            Expect.equal
                (EgressLabel.joinAll [ a; EgressLabel.unlabelled; b ])
                EgressLabel.unlabelled
                "one uncomputed source makes the whole payload uncomputed"

            Expect.equal (EgressLabel.joinAll []) EgressLabel.clean "nothing joined is nothing carried"
        }

        test "the mirror agrees with TaintLabel ref for ref, and bottom projects to clean" {
            let taint = ToolUp.Facts.TaintLabel.ofPolicyRefs [ "party-b"; "party-a" ]
            let mirrored = ToolUp.Facts.TaintLabel.toEgressLabel taint

            Expect.equal
                (EgressLabel.policyRefs mirrored)
                (ToolUp.Facts.TaintLabel.policyRefs taint)
                "the two carry the same refs"

            // `bottom` is a COMPUTED lineage carrying nothing, so it must
            // project to `clean` and never to `unlabelled` — a surface
            // holding a `TaintLabel` at all has computed one.
            Expect.equal
                (ToolUp.Facts.TaintLabel.toEgressLabel ToolUp.Facts.TaintLabel.bottom)
                EgressLabel.clean
                "bottom projects to clean"

            // The join commutes with the projection, which is what makes
            // the mirror faithful rather than merely similarly shaped.
            let other = ToolUp.Facts.TaintLabel.ofPolicyRef "party-c"

            Expect.equal
                (ToolUp.Facts.TaintLabel.toEgressLabel (ToolUp.Facts.TaintLabel.join taint other))
                (EgressLabel.join mirrored (ToolUp.Facts.TaintLabel.toEgressLabel other))
                "projecting a join is joining the projections"
        }

        test "render is stable, deterministic, and tells the two empties apart" {
            Expect.equal (EgressLabel.render EgressLabel.unlabelled) "unlabelled" "unlabelled"
            Expect.equal (EgressLabel.render EgressLabel.clean) "labelled{}" "clean"

            Expect.equal
                (EgressLabel.render (EgressLabel.ofPolicyRefs [ "b"; "a" ]))
                "labelled{a,b}"
                "refs sorted ordinally"
        }
    ]

let private labelledGateTests =
    testList "the gates consult the label" [
        test "requireClearedLabel refuses both ways a label fails to clear, and defers when it does" {
            let policy = EgressPolicy.requireClearedLabel EgressPolicy.permitAll
            let destination = origin "https://api.example.com"

            let denialFor label =
                match policy.Decide(moduleA, destination, EgressSurface.Webhook, label) with
                | EgressVerdict.Deny reason ->
                    Expect.stringContains reason "https://api.example.com" "the refusal names the origin"
                    Expect.isFalse (reason.Contains "?") "never a query string"
                    reason
                | EgressVerdict.Permit -> failtestf "%s was permitted" (EgressLabel.render label)

            // Two failures, two remedies, two distinguishable sentences.
            Expect.stringContains
                (denialFor EgressLabel.unlabelled)
                "carries no disclosure label"
                "an uncomputed lineage says so"

            let restricted = denialFor (EgressLabel.ofPolicyRef "party-a")
            Expect.stringContains restricted "no declassification has cleared" "an uncleared lineage says so"
            Expect.stringContains restricted "labelled{party-a}" "and names the refs — names, never values"

            Expect.isTrue
                (isPermit (policy.Decide(moduleA, destination, EgressSurface.Webhook, EgressLabel.clean)))
                "a CLEARED payload defers to the inner policy"
        }

        test "a restricted-labelled value cannot leave through a DECLARED origin under Verified" {
            // The phase's headline acceptance: the destination is
            // permitted, and the call is still refused, because the gate
            // now sees WHAT is leaving and not only where it is going.
            // The only way past it is a declassification, which lowers
            // the label to `clean` before it ever reaches this seam.
            let binding = EgressPolicy.bind true (Some grantsForA)

            let decide label =
                decideLabelled binding moduleA "https://api.example.com" EgressSurface.Webhook label

            Expect.isTrue (isPermit (decide EgressLabel.clean)) "a cleared payload crosses a declared origin"

            for blocked in
                [
                    EgressLabel.unlabelled
                    EgressLabel.ofPolicyRef "party-a"
                    EgressLabel.ofPolicyRefs [ "party-a"; "party-b" ]
                ] do
                match decide blocked with
                | EgressVerdict.Deny _ -> ()
                | EgressVerdict.Permit -> failtestf "a declared origin carried %s out" (EgressLabel.render blocked)
        }

        test "the refusal holds at EVERY egress surface, not only the one it was probed at" {
            // Acceptance asks for this per surface, and a policy that
            // happened to bound only one would pass every probe above.
            let binding = EgressPolicy.bind true (Some grantsForA)

            for surface in
                [
                    EgressSurface.ModuleHandler
                    EgressSurface.AIProvider
                    EgressSurface.AuthProvider
                    EgressSurface.Notification
                    EgressSurface.Webhook
                    EgressSurface.Other
                ] do
                let at label =
                    decideLabelled binding moduleA "https://api.example.com" surface label

                Expect.isTrue (isPermit (at EgressLabel.clean)) (sprintf "%A: a cleared payload crosses" surface)

                match at (EgressLabel.ofPolicyRef "party-a") with
                | EgressVerdict.Deny _ -> ()
                | EgressVerdict.Permit -> failtestf "%A let a restricted-labelled payload out" surface
        }

        test "the advisory profile is unchanged by the label (GP 11)" {
            for binding in [ EgressPolicy.bind false None; EgressPolicy.bind false (Some grantsForA) ] do
                for label in [ EgressLabel.unlabelled; EgressLabel.clean; EgressLabel.ofPolicyRef "party-a" ] do
                    Expect.equal
                        (decideLabelled binding moduleA "https://api.example.com" EgressSurface.Webhook label)
                        (decideLabelled
                            binding
                            moduleA
                            "https://api.example.com"
                            EgressSurface.Webhook
                            EgressLabel.clean)
                        "the label changes no verdict where declaration is advisory"

            // And a component the advisory signature leaves unrestricted
            // still reaches an undeclared origin, unlabelled payload and
            // all — the pre-796 behaviour, byte for byte.
            match
                decideLabelled
                    (EgressPolicy.bind false (Some grantsForA))
                    moduleB
                    "https://other.example"
                    EgressSurface.Webhook
                    EgressLabel.unlabelled
            with
            | EgressVerdict.Permit -> ()
            | EgressVerdict.Deny r -> failtestf "an unrestricted component was refused: %s" r
        }

        test "the classification gate holds the same bar at its own boundary" {
            let inner = EgressGate.permissiveEgressPolicy
            let policy = EgressGate.requireClearedLabel inner
            let unlabelledCtx = EgressContext.create EgressBoundary.ExportPayload "recipient"

            let restrictedCtx =
                unlabelledCtx |> EgressContext.withLabel (EgressLabel.ofPolicyRef "party-a")

            let clearedCtx = unlabelledCtx |> EgressContext.withLabel EgressLabel.clean

            Expect.equal unlabelledCtx.Label EgressLabel.unlabelled "create leaves the crossing unlabelled"

            for boundary in [ EgressBoundary.ExportPayload; EgressBoundary.RpcResponse ] do
                for level in [ Public; Confidential; Financial; Pii ] do
                    let at (ctx: EgressContext) =
                        policy level { ctx with Boundary = boundary }

                    Expect.equal (at unlabelledCtx) EgressDecision.Block "an uncomputed lineage is blocked"
                    Expect.equal (at restrictedCtx) EgressDecision.Block "an uncleared lineage is blocked"

                    Expect.equal
                        (at clearedCtx)
                        (inner level { clearedCtx with Boundary = boundary })
                        "a cleared crossing defers to the inner policy"
        }

        testCaseAsync "a field whose label does not clear is dropped and audited; a cleared one is not"
        <| async {
            let classifier =
                DefaultFieldClassifier.create [ FieldClassification.create "Customer" "Email" Pii ]

            let fields = Map.ofList [ "Email", "a@b.com"; "Unclassified", "x" ]
            let policy = EgressGate.requireClearedLabel EgressGate.permissiveEgressPolicy

            let auditUnlabelled = RecordingAudit()
            let ctx = EgressContext.create EgressBoundary.ExportPayload "recipient"
            let! blocked = EgressGate.apply classifier policy auditUnlabelled ctx "Customer" fields

            Expect.isFalse (blocked.ContainsKey "Email") "the classified field is dropped, never marker-substituted"
            Expect.equal (blocked.TryFind "Unclassified") (Some "x") "an unclassified field is untouched"
            Expect.isNonEmpty auditUnlabelled.Events "the refusal is audited"

            let auditLabelled = RecordingAudit()
            let clearedCtx = ctx |> EgressContext.withLabel EgressLabel.clean
            let! passed = EgressGate.apply classifier policy auditLabelled clearedCtx "Customer" fields

            Expect.equal passed fields "a cleared label passes the permissive inner policy through unchanged"
            Expect.isEmpty auditLabelled.Events "and audits nothing"
        }

        testCaseAsync "the ambient label reaches the decision, the ledger and the report"
        <| isolated (fun () -> async {
            EgressEnforcement.install (EgressPolicy.bind true (Some grantsForA))

            use client =
                PlatformHttpClient.createWith EgressSurface.Webhook (new RecordingHandler())

            // A DECLARED origin claimed by the component that declares
            // it, so the only thing left that can refuse this is the
            // label the surface claimed for the chain.
            let emit label =
                EgressComponentContext.runAs moduleA (fun () ->
                    EgressLabelContext.runLabelled label (fun () ->
                        sendCatching client (new HttpRequestMessage(HttpMethod.Post, "https://api.example.com/emit"))))

            let! outcome = emit EgressLabel.unlabelled

            match outcome with
            | Ok _ -> failtest "an unlabelled payload left through a declared origin"
            | Error e ->
                Expect.isTrue (e :? EgressDeniedException) "the typed refusal"
                Expect.stringContains e.Message "carries no disclosure label" "naming the label"

            let projected = EgressEnforcement.deploymentVerificationEvidence "verified"

            Expect.equal
                (projected.EgressDenials |> List.map _.DeniedLabel)
                [ "unlabelled" ]
                "the ledger names the label"

            Expect.equal
                projected.EgressLabelVocabulary
                DisclosurePolicyRefSnapshot.Version
                "the projection names the pinned vocabulary"

            // The same call with a computed label goes through, which is
            // what makes the refusal above about the label and not about
            // anything else in the composition.
            let! permitted = emit EgressLabel.clean
            Expect.isOk permitted "a computed label crosses the same declared origin"
        })

        test "an unclaimed chain reads unlabelled, and a claim does not escape it" {
            Expect.equal (EgressLabelContext.current ()) EgressLabel.unlabelled "nothing claimed"

            EgressLabelContext.runLabelled (EgressLabel.ofPolicyRef "party-a") (fun () -> async {
                Expect.equal
                    (EgressLabelContext.current ())
                    (EgressLabel.ofPolicyRef "party-a")
                    "the claim is visible inside"

                EgressLabelContext.joinIn (EgressLabel.ofPolicyRef "party-b")

                Expect.equal
                    (EgressLabelContext.current ())
                    (EgressLabel.ofPolicyRefs [ "party-a"; "party-b" ])
                    "joinIn accumulates"
            })
            |> Async.RunSynchronously

            Expect.equal (EgressLabelContext.current ()) EgressLabel.unlabelled "the prior claim is restored"
        }
    ]

// ─── One vocabulary, pinned — and the drift check that proves it ───────

/// Every way a pinned list and the live one disagree, as readable
/// sentences. Returned rather than asserted so the check can be bound
/// twice: to the real snapshot and asserted empty, and to a deliberately
/// drifted one to prove it fires. A drift check that only ever agrees
/// with itself is not a check.
let private vocabularyDrift (what: string) (pinned: string list) (live: string list) : string list = [
    for missing in live |> List.filter (fun l -> not (List.contains l pinned)) do
        yield sprintf "the live %s '%s' is absent from the pinned snapshot" what missing

    for stale in pinned |> List.filter (fun p -> not (List.contains p live)) do
        yield sprintf "the pinned snapshot names the %s '%s', which no live case produces" what stale

    if List.length pinned = List.length live && pinned <> live then
        yield sprintf "the pinned %s order %A differs from the live order %A" what pinned live
]

/// The canonical string of every case of a nullary-case DU, in
/// declaration order — read off the LIVE type by reflection, so a case
/// added to it cannot be missed by a hand-maintained list.
let private liveCases<'T> (render: 'T -> string) : string list =
    Microsoft.FSharp.Reflection.FSharpType.GetUnionCases typeof<'T>
    |> Array.map (fun case -> Microsoft.FSharp.Reflection.FSharpValue.MakeUnion(case, [||]) :?> 'T |> render)
    |> List.ofArray

let private snapshotTests =
    testList "DisclosurePolicyRefSnapshot — one vocabulary across the tiers" [
        test "the pinned fact-egress surfaces match the live DU" {
            let live = liveCases<FactEgressSurface> FactEgressSurface.toString

            Expect.isEmpty
                (vocabularyDrift "fact-egress surface" DisclosurePolicyRefSnapshot.factEgressSurfaces live)
                "add the surface to the snapshot and bump DisclosurePolicyRefSnapshot.Version in the same commit"
        }

        test "the pinned outbound-call surfaces match the live DU" {
            let live = liveCases<EgressSurface> EgressSurface.label

            Expect.isEmpty
                (vocabularyDrift "egress surface" DisclosurePolicyRefSnapshot.egressSurfaces live)
                "add the surface to the snapshot and bump DisclosurePolicyRefSnapshot.Version in the same commit"
        }

        test "the reserved policy refs are the ones the platform actually emits" {
            // `Internal` is emitted by the one disclosure predicate, so
            // the snapshot's claim about it is checked against that
            // predicate rather than against another list.
            match
                ToolUp.Facts.DisclosureEgress.evaluate
                    ToolUp.Facts.DisclosurePolicyResolver.denyUnknown
                    FactRetrieval
                    ToolUp.Facts.Internal
            with
            | FactNotDisclosable policyRef ->
                Expect.contains DisclosurePolicyRefSnapshot.reservedPolicyRefs policyRef "the snapshot names it"
            | FactDisclosable -> failtest "the Internal classification became disclosable"

            Expect.contains DisclosurePolicyRefSnapshot.reservedPolicyRefs "unknown-fact" "the unresolvable-id ref"

            Expect.equal
                DisclosurePolicyRefSnapshot.UnlabelledToken
                (EgressLabel.render EgressLabel.unlabelled)
                "the snapshot's unlabelled token is what the renderer produces"
        }

        test "the drift check has teeth — a deliberately drifted snapshot fails it" {
            let live = liveCases<EgressSurface> EgressSurface.label

            // A case dropped from the mirror: the shape a composition
            // tier takes when the runtime tier grows a surface it has
            // not adopted.
            let dropped =
                DisclosurePolicyRefSnapshot.egressSurfaces
                |> List.filter (fun s -> s <> "webhook")

            Expect.isNonEmpty (vocabularyDrift "egress surface" dropped live) "a dropped case is caught"

            // A case the mirror invented, which no live type produces.
            let invented = DisclosurePolicyRefSnapshot.egressSurfaces @ [ "carrier-pigeon" ]
            Expect.isNonEmpty (vocabularyDrift "egress surface" invented live) "an invented case is caught"

            // Same membership, different order — the mirror is
            // field-for-field, so a reordering is drift too.
            let reordered = List.rev DisclosurePolicyRefSnapshot.egressSurfaces
            Expect.isNonEmpty (vocabularyDrift "egress surface" reordered live) "a reordering is caught"

            // The control in the other direction: the REAL snapshot
            // passes the very check the three above fail.
            Expect.isEmpty
                (vocabularyDrift "egress surface" DisclosurePolicyRefSnapshot.egressSurfaces live)
                "the real snapshot is clean under the same check"
        }
    ]

let tests =
    testList "Phase 772 / 796 — server-side egress policy, checked against the payload's label" [
        destinationTests
        policyTests
        handlerTests
        reportTests
        evidenceTests
        contractTests
        labelTests
        labelledGateTests
        snapshotTests
    ]