module ToolUp.Platform.Tests.InProcess.UnreducedViewPreviewTests

open System.IO
open Expecto
open Feliz
open ToolUp.Platform
open Toolup.Samples.ToyTreeBinding.ToyNode

// ─── Phase 298 — live preview of an unreduced composition's view subtrees ──
//
// The loop-speed unlock: render a partial (in-progress, not-yet-compiled)
// composition's typed-tree view subtrees live (CSR) against a Phase 264
// `HostBindingSources` projection, WITHOUT a built solution. A composition
// is in progress by definition, so an unresolved binding degrades to a
// labelled placeholder — never a throw. The resolution DECISION is a pure
// tier-neutral function, so the .NET runner drives it directly (the Feliz
// rendering + throw-safety are Fable-verified on MinimalClient). Proofs:
//
//   1. A subtree whose required bindings all resolve against a projected
//      source renders (`PreviewOutcome.Rendered`).
//   2. A subtree with an unresolved required binding degrades to a
//      `PreviewOutcome.Placeholder` naming the missing key — a pure decision
//      that never throws.
//   3. Re-evaluating after EDITING a subtree needs no rebuild: adding a
//      required binding flips the outcome with no compilation (loop-speed).
//   4. A whole partial composition previews as a per-subtree outcome list —
//      some rendered, some placeheld — against one projection.
//   5. The preview's binding namespace IS the Phase 264 read-side: the toy
//      (a stranger tree language) resolves a `Bind` against the same sources.
//   6. The seam source carries no banned OSS vocabulary (GP 1 grep-guard).

let private repoRoot () =
    let asmDir =
        Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)
    // bin/Debug/net10.0 → ToolUp.Platform.Tests → src → toolup-forge
    Path.GetFullPath(Path.Combine(asmDir, "..", "..", "..", "..", ".."))

/// A projection of an in-progress composition's host state. `count` +
/// `label` are projected; `total` is deliberately absent (a binding the
/// author has referenced but not yet wired — the in-progress case).
let private partialSources: HostBindingSources = {
    QueryResults = Map.ofList [ "count", box 3; "label", box "draft" ]
    State = Map.ofList [ "expanded", box true ]
}

/// A view subtree the preview never actually renders in the .NET runner
/// (Feliz is Fable-only) — `Render` is `Html.none`, so the pure `outcome`
/// decision is what's under test.
let private subtree (label: string) (required: string list) : UnreducedViewSubtree = {
    Label = label
    RequiredBindings = required
    Render = fun _ -> Html.none
}

// ─── 1 + 2. Resolve → Rendered; unresolved → safe placeholder ─────────

let private outcomeTests =
    testList "Phase 298 — subtree preview outcome (pure, never throws)" [
        testCase "a subtree whose required bindings all resolve renders"
        <| fun _ ->
            let s = subtree "counter" [ "count"; "label" ]

            Expect.equal
                (UnreducedViewPreview.outcome s partialSources)
                PreviewOutcome.Rendered
                "every required binding resolves → the subtree renders live"

        testCase "a subtree with an unresolved binding degrades to a labelled placeholder"
        <| fun _ ->
            let s = subtree "totals" [ "count"; "total" ] // 'total' is not yet projected

            match UnreducedViewPreview.outcome s partialSources with
            | PreviewOutcome.Placeholder missing ->
                Expect.equal missing [ "total" ] "the placeholder names exactly the unresolved binding"
            | PreviewOutcome.Rendered -> failtest "an unresolved binding must degrade to a placeholder, not render"

        testCase "the preview decision is pure — an unresolved binding never throws"
        <| fun _ ->
            // A subtree whose Render WOULD throw, but whose required binding
            // is unresolved, is never rendered — outcome is a pure decision.
            let s = {
                Label = "boom"
                RequiredBindings = [ "total" ]
                Render = fun _ -> failwith "must never be invoked for an unresolved subtree"
            }

            Expect.equal
                (UnreducedViewPreview.outcome s partialSources)
                (PreviewOutcome.Placeholder [ "total" ])
                "an unresolved subtree degrades without ever invoking Render"
    ]

// ─── 3. Editing a subtree re-previews with no rebuild ─────────────────

let private editLoopTests =
    testList "Phase 298 — an edited subtree re-previews with no rebuild" [
        testCase "adding a required binding flips the outcome (pure re-eval, no compilation)"
        <| fun _ ->
            let before = subtree "chart" [ "count" ]

            Expect.equal
                (UnreducedViewPreview.outcome before partialSources)
                PreviewOutcome.Rendered
                "renders before the edit"

            // The author edits the subtree to bind a not-yet-projected key.
            let after = {
                before with
                    RequiredBindings = [ "count"; "total" ]
            }

            match UnreducedViewPreview.outcome after partialSources with
            | PreviewOutcome.Placeholder [ "total" ] -> ()
            | other -> failtestf "the edited subtree should re-preview as a placeholder; got %A" other
    ]

// ─── 4. A whole partial composition previews as a per-subtree outcome ──

let private compositionTests =
    testList "Phase 298 — a partial composition previews per subtree" [
        testCase "outcomes reports each subtree's live/placeholder state against one projection"
        <| fun _ ->
            let subtrees = [
                subtree "counter" [ "count" ] // resolves
                subtree "summary" [ "label" ] // resolves
                subtree "totals" [ "total" ] // unresolved → placeholder
            ]

            let results = UnreducedViewPreview.outcomes subtrees partialSources

            Expect.equal (List.length results) 3 "every subtree contributes an outcome"
            Expect.equal (List.tryItem 0 results) (Some("counter", PreviewOutcome.Rendered)) "counter renders"
            Expect.equal (List.tryItem 1 results) (Some("summary", PreviewOutcome.Rendered)) "summary renders"

            Expect.equal
                (List.tryItem 2 results)
                (Some("totals", PreviewOutcome.Placeholder [ "total" ]))
                "totals degrades to a placeholder — the composition is in progress"
    ]

// ─── 5. The preview binding namespace is the Phase 264 read-side (toy) ─

let private toyParityTests =
    testList "Phase 298 — preview resolves against the Phase 264 read-side" [
        testCase "the toy (a stranger tree) resolves a Bind against the same projection the preview uses"
        <| fun _ ->
            // The preview considers 'count' resolved; the toy — rendering the
            // SAME projection — binds it to the projected value. One namespace.
            let s = subtree "counter" [ "count" ]

            Expect.equal
                (UnreducedViewPreview.outcome s partialSources)
                PreviewOutcome.Rendered
                "preview sees count resolved"

            let tree = Element("p", [ Text "count: "; Bind "count" ])
            let html = tree |> resolve partialSources |> lowerToHtml
            Expect.stringContains html "count: 3" "the toy resolves the same projected binding the preview gates on"

        testCase "an unresolved binding renders the toy's stable miss marker (never throws)"
        <| fun _ ->
            let s = subtree "totals" [ "total" ]

            match UnreducedViewPreview.outcome s partialSources with
            | PreviewOutcome.Placeholder _ -> ()
            | PreviewOutcome.Rendered -> failtest "the preview must placehold the unresolved subtree"

            // The toy, rendering the same unresolved key, degrades visibly too.
            let html = Bind "total" |> resolve partialSources |> lowerToHtml
            Expect.stringContains html "{unbound:total}" "the unresolved binding degrades visibly on both sides"
    ]

// ─── 6. OSS grep-guard ────────────────────────────────────────────────

let private boundaryTests =
    testList "Phase 298 — OSS boundary" [
        testCase "the preview seam source carries no banned OSS vocabulary (GP 1)"
        <| fun _ ->
            let path =
                Path.Combine(repoRoot (), "src", "ToolUp.Platform.Client", "Client", "UnreducedViewPreview.fs")

            Expect.isTrue (File.Exists path) $"expected seam file at {path}"
            let contents = (File.ReadAllText path).ToLowerInvariant()
            Expect.isFalse (contents.Contains "fuaran") $"{path} must carry no Fuaran token (GP 1)"
    ]

let tests =
    testList "Phase 298 — unreduced composition view preview" [
        outcomeTests
        editLoopTests
        compositionTests
        toyParityTests
        boundaryTests
    ]