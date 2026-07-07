module ToolUp.Platform.Tests.InProcess.HostingSeamComponentIdTests

open System.IO
open Expecto
open ToolUp.Platform
open Toolup.Samples.ToyTreeBinding.ToyNode

// ─── Phase 299 — owning ComponentId on the hosting seam ────────────────
//
// The forge half of the identity bridge. Four proofs:
//   1. A tagged host carries its owning `ComponentId` (Phase 279).
//   2. An interaction event attributes to the owning component — the
//      Phase 297 usage-export correlation edge.
//   3. A binding resolution (the toy's `Bind` node, Phase 264 read-side)
//      attributes to the owning component — the composition ↔ view resolve.
//   4. An UNTAGGED host attributes to `None` — byte-for-byte the pre-299
//      behaviour (GP 11). Plus the OSS grep-guard.

let private repoRoot () =
    let assemblyDir =
        Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)

    Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

let private salesId = ComponentId.ofModule "sales"

// ─── 1. Ownership carries the id ──────────────────────────────────────

let private ownershipTests =
    testList "Phase 299 — ownership carries the owning ComponentId" [
        testCase "ofComponent tags the host with its owning id"
        <| fun _ ->
            let ownership = HostOwnership.ofComponent salesId
            Expect.equal (HostOwnership.owner ownership) (Some salesId) "owner is the module's component id"

        testCase "untagged owner is None (pre-299)"
        <| fun _ -> Expect.equal (HostOwnership.owner HostOwnership.untagged) None "an untagged host has no owner"
    ]

// ─── 2. An interaction event attributes to the owner ──────────────────

let private eventAttributionTests =
    testList "Phase 299 — interaction event attribution (Phase 297 edge)" [
        testCase "a tagged host attributes an interaction event to its owning component"
        <| fun _ ->
            let ownership = HostOwnership.ofComponent salesId
            // A toy interaction event — the "stranger tree language" raising
            // an event the host must attribute back to the owning module.
            let event = NavigateTo "home"
            let owner, carried = HostOwnership.attribute ownership event

            Expect.equal owner (Some salesId) "the event attributes to the owning component"
            Expect.equal carried event "the event itself is carried unchanged"

        testCase "an untagged host attributes the same event to None (GP 11)"
        <| fun _ ->
            let owner, carried =
                HostOwnership.attribute HostOwnership.untagged (NavigateTo "home")

            Expect.equal owner None "no owning component on an untagged host"
            Expect.equal carried (NavigateTo "home") "the event is still carried"
    ]

// ─── 3. A binding resolution attributes to the owner ──────────────────

let private bindingAttributionTests =
    testList "Phase 299 — binding-resolution attribution (Phase 264 resolve)" [
        testCase "a resolved binding attributes to the owning component"
        <| fun _ ->
            let ownership = HostOwnership.ofComponent salesId
            let sources = HostBindingSources.ofQueryResults (Map.ofList [ "count", box 42 ])

            // Resolve the toy's Bind node against the host projection, then
            // attribute the resolution across the composition ↔ view boundary.
            let resolved = HostBindingSources.tryResolve "count" sources
            let owner, value = HostOwnership.attribute ownership resolved

            Expect.equal owner (Some salesId) "the binding resolution attributes to the owning component"
            Expect.equal value (Some(box 42)) "the resolved value is carried"

        testCase "the toy tree resolves + lowers under an owning host"
        <| fun _ ->
            // End-to-end: a hosted (toy) tree resolves its binding against the
            // host projection; the host that owns it is identified, so the
            // rendered subtree maps back to its owning component.
            let ownership = HostOwnership.ofComponent salesId
            let sources = HostBindingSources.ofQueryResults (Map.ofList [ "count", box 7 ])
            let tree = Element("p", [ Text "count: "; Bind "count" ])
            let html = tree |> resolve sources |> lowerToHtml

            Expect.stringContains html "count: 7" "the hosted tree resolved its binding"
            Expect.equal (HostOwnership.owner ownership) (Some salesId) "and the host is attributable to its owner"
    ]

// ─── 4. OSS grep-guard ────────────────────────────────────────────────

let private ossTests =
    testList "Phase 299 — OSS boundary" [
        testCase "the seam source carries no banned OSS vocabulary"
        <| fun _ ->
            let path =
                Path.Combine(repoRoot (), "src", "ToolUp.Platform.Client", "Client", "HostStateProjection.fs")

            Expect.isTrue (File.Exists path) (sprintf "expected the seam file at %s" path)
            NeutralityTokens.assertNoBannedTokens path (File.ReadAllText path)
            NeutralityTokens.skipUnlessExternalSource ()
    ]

let tests =
    testList "HostingSeamComponentId (Phase 299)" [
        ownershipTests
        eventAttributionTests
        bindingAttributionTests
        ossTests
    ]