module ToolUp.Platform.Tests.InProcess.ComponentLifecycleTests

open Expecto
open ToolUp.Platform

// ─── Phase 291 — component lifecycle ordering by id ───────────────────
//
// Covers the acceptance shape: a declared "secret-store before
// audit-sink" ordering initialises in order and disposes in reverse; an
// undeclared deployment matches prior registration-order behaviour
// (GP 11); a cyclic order fails at compose with a readable error.

let private secretStore = ComponentId.forCompanionSlot "ISecretStore"
let private auditSink = ComponentId.forCompanionImpl "IAuditSink" "splunk"
let private storage = ComponentId.forCompanionSlot "IBlobStorage"

let tests =
    testList "ComponentLifecycle" [

        // ── a declared order initialises in order, disposes in reverse ─
        testCase "a declared 'secret-store before audit-sink' order initialises in order"
        <| fun _ ->
            let order =
                ComponentLifecycle.ofComponents [ auditSink; secretStore ] // registration puts the sink first
                |> ComponentLifecycle.before secretStore auditSink // but declare the dependency

            Expect.equal
                (ComponentLifecycle.initSequence order)
                (Ok [ secretStore; auditSink ])
                "the declared edge overrides registration order — secret store first"

        testCase "dispose is the reverse of init"
        <| fun _ ->
            let order =
                ComponentLifecycle.ofComponents [ auditSink; secretStore ]
                |> ComponentLifecycle.before secretStore auditSink

            Expect.equal
                (ComponentLifecycle.disposeSequence order)
                (Ok [ auditSink; secretStore ])
                "the audit sink disposes before the secret store it depends on"

        testCase "runInit / runDispose apply the effect in the resolved order"
        <| fun _ ->
            let order =
                ComponentLifecycle.ofComponents [ auditSink; secretStore ]
                |> ComponentLifecycle.before secretStore auditSink

            let initLog = System.Collections.Generic.List<string>()
            let disposeLog = System.Collections.Generic.List<string>()

            let init =
                ComponentLifecycle.runInit (fun c -> initLog.Add(ComponentId.value c)) order

            let dispose =
                ComponentLifecycle.runDispose (fun c -> disposeLog.Add(ComponentId.value c)) order

            Expect.equal init (Ok()) "init runs cleanly"
            Expect.equal dispose (Ok()) "dispose runs cleanly"

            Expect.equal
                (List.ofSeq initLog)
                [ ComponentId.value secretStore; ComponentId.value auditSink ]
                "init effect applied secret-store-first"

            Expect.equal
                (List.ofSeq disposeLog)
                [ ComponentId.value auditSink; ComponentId.value secretStore ]
                "dispose effect applied in reverse"

        // ── GP 11: an undeclared order = registration order ───────────
        testCase "an undeclared order resolves to registration order (GP 11)"
        <| fun _ ->
            let order = ComponentLifecycle.ofComponents [ secretStore; storage; auditSink ]

            Expect.equal
                (ComponentLifecycle.initSequence order)
                (Ok [ secretStore; storage; auditSink ])
                "no edges → exactly registration order"

        testCase "an unconstrained component keeps its registration position (stable sort)"
        <| fun _ ->
            // Only secretStore→auditSink is constrained; storage floats but
            // stays where it was registered.
            let order =
                ComponentLifecycle.ofComponents [ storage; auditSink; secretStore ]
                |> ComponentLifecycle.before secretStore auditSink

            Expect.equal
                (ComponentLifecycle.initSequence order)
                (Ok [ storage; secretStore; auditSink ])
                "storage keeps its leading position; the constrained pair orders around it"

        // ── a cyclic order is rejected at compose ─────────────────────
        testCase "a cyclic order fails with a readable Error"
        <| fun _ ->
            let order =
                ComponentLifecycle.ofComponents [ secretStore; auditSink ]
                |> ComponentLifecycle.before secretStore auditSink
                |> ComponentLifecycle.before auditSink secretStore

            match ComponentLifecycle.initSequence order with
            | Ok seq -> failtestf "expected a cycle error, got %A" seq
            | Error message -> Expect.stringContains message "cyclic" "the error names the cycle"

        testCase "ensureAcyclic raises at compose on a cycle, passes on an acyclic order"
        <| fun _ ->
            let cyclic =
                ComponentLifecycle.ofComponents [ secretStore; auditSink ]
                |> ComponentLifecycle.before secretStore auditSink
                |> ComponentLifecycle.before auditSink secretStore

            Expect.throwsC (fun () -> ComponentLifecycle.ensureAcyclic "composition" cyclic) (fun ex ->
                Expect.stringContains ex.Message "composition" "the compose context is named")

            let acyclic =
                ComponentLifecycle.ofComponents [ secretStore; auditSink ]
                |> ComponentLifecycle.before secretStore auditSink

            ComponentLifecycle.ensureAcyclic "composition" acyclic
            Expect.isTrue true "an acyclic order does not raise"
    ]