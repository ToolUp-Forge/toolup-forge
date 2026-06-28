module ToolUp.Platform.Tests.InProcess.HydrationParityTests

open Expecto
open ToolUp.Platform.Testing

// ─── Phase 203 — hydration-parity conformance harness ────────────────
//
// Pins the `HydrationParity` normalisation + diff contract: the shipped
// divergence-class fixtures (attribute order, boolean/void elements,
// adjacent text nodes, whitespace-significant content, event-handler
// nodes) MUST all reach `Parity`, and the deliberately-mismatched
// fixture MUST `Divergence` with a readable, node-naming message. The
// React behaviour itself is browser-verified per the acceptance criteria
// — this pack guards the F# normaliser the consumer's CI captures feed.

let private fixturesParity =
    HydrationParity.divergenceClassFixtures
    |> List.map (fun fx ->
        testCase (sprintf "divergence class: %s — SSR and CSR normalise identically" fx.Name)
        <| fun () ->
            match HydrationParity.check fx.Ssr fx.Csr with
            | HydrationParity.Parity -> ()
            | HydrationParity.Divergence msg -> failtestf "fixture %A should reach parity but diverged: %s" fx.Name msg)

let private mismatchDiverges =
    testCase "deliberate mismatch — check returns a Divergence naming the divergent node"
    <| fun () ->
        let fx = HydrationParity.mismatchedFixture

        match HydrationParity.check fx.Ssr fx.Csr with
        | HydrationParity.Parity -> failtest "mismatched fixture must NOT reach parity"
        | HydrationParity.Divergence msg ->
            // The diff must localise the failure (a node index) and name
            // the divergent content so the build log is actionable.
            Expect.stringContains msg "node #" "divergence message must cite a node index"
            Expect.stringContains msg "Two" "divergence message must name the SSR side's divergent text"
            Expect.stringContains msg "Three" "divergence message must name the CSR side's divergent text"

let private normaliseIdempotent =
    testCase "normalise — twice is byte-stable (idempotent canonical form)"
    <| fun () ->
        for fx in HydrationParity.divergenceClassFixtures do
            let once = HydrationParity.normalise fx.Csr
            let twice = HydrationParity.normalise once
            Expect.equal twice once (sprintf "normalise must be idempotent for %A" fx.Name)

let private normaliseConverges =
    testCase "normalise — both sides of a parity fixture produce the same canonical string"
    <| fun () ->
        for fx in HydrationParity.divergenceClassFixtures do
            Expect.equal
                (HydrationParity.normalise fx.Ssr)
                (HydrationParity.normalise fx.Csr)
                (sprintf "SSR and CSR must canonicalise identically for %A" fx.Name)

let private selfParity =
    testCase "check — a fragment is always parity with itself"
    <| fun () ->
        let sample = HydrationParity.mismatchedFixture.Ssr
        Expect.equal (HydrationParity.check sample sample) HydrationParity.Parity "a fragment must hydrate-match itself"

let private orderingDiffDetected =
    testCase "check — a structurally different element (not just whitespace) is caught"
    <| fun () ->
        // An attribute VALUE difference is structural and must not be
        // normalised away — only order / form / whitespace is.
        let ssr = "<input name=\"email\" type=\"text\">"
        let csr = "<input name=\"email\" type=\"email\">"

        match HydrationParity.check ssr csr with
        | HydrationParity.Parity -> failtest "an attribute-value difference must be a divergence, not parity"
        | HydrationParity.Divergence msg -> Expect.stringContains msg "input" "divergence must name the <input> node"

[<Tests>]
let tests =
    testList "Phase 203 — hydration-parity conformance harness" [
        testList "divergence-class fixtures reach parity" fixturesParity
        testList "diff + normalisation" [
            mismatchDiverges
            normaliseIdempotent
            normaliseConverges
            selfParity
            orderingDiffDetected
        ]
    ]