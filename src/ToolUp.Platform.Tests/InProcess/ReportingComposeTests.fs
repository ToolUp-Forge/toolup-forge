module ToolUp.Platform.Tests.InProcess.ReportingComposeTests

open Expecto
open ToolUp.Reporting

// Regression pack for `ReportingCompose.buildDefaultRegistry`.
//
// The defect this exists to catch: `ReportingCompose` opened BOTH
// `MarkdownRenderer` and `HtmlRenderer`, each of which exposes a
// `create ()`. The later `open` shadowed the earlier, so the
// unqualified `create ()` intended to build the Markdown renderer
// resolved to `HtmlRenderer.create`. The "default" registry therefore
// registered the HTML renderer twice, and `Render` on a Markdown
// template failed with `NoRendererForFormat Markdown`.
//
// Nothing about that is visible to the type checker — both factories
// return `IReportRenderer`, so the wrong call compiles cleanly — and
// nothing about it is visible in review either, because the call site
// carried a `// MarkdownRenderer` comment asserting the very thing that
// was false. The only durable guard is an assertion that the default
// registry actually resolves a renderer FOR EACH format it claims to
// cover, which is what this pack does.

/// The formats `buildDefaultRegistry` is contracted to cover with the
/// zero-dependency in-process renderers. `Pdf` / `Docx` / `Xlsx` /
/// `Pptx` are deliberately absent — they ship in sub-companions and are
/// registered by the consumer via `ReportingCompose.withRenderer`.
let private zeroDepFormats = [ Markdown; Html ]

/// The load-bearing case: a resolvable renderer for EVERY zero-dep
/// format. Under the shadowing bug `TryResolve Markdown` returned None
/// and this failed naming Markdown.
let private perFormatTests =
    zeroDepFormats
    |> List.map (fun format ->
        testCase (sprintf "resolves a renderer for %A" format) (fun () ->
            let registry = ReportingCompose.buildDefaultRegistry ()

            match registry.TryResolve format with
            | None ->
                failtestf
                    "buildDefaultRegistry registered no renderer for %A. Registered formats: %A. A renderer factory was almost certainly resolved to the wrong module (see this file's header)."
                    format
                    (registry.SupportedFormats())
            | Some renderer ->
                Expect.contains
                    renderer.SupportedFormats
                    format
                    (sprintf
                        "the renderer registered under %A must itself declare support for %A — resolving the wrong factory yields a renderer keyed under a format it does not serve"
                        format
                        format)))

let private coverageTests = [
    testCase "covers every zero-dep format" (fun () ->
        let registry = ReportingCompose.buildDefaultRegistry ()
        let supported = registry.SupportedFormats()

        let missing =
            zeroDepFormats |> List.filter (fun f -> not (List.contains f supported))

        Expect.isEmpty
            missing
            (sprintf
                "buildDefaultRegistry must cover every zero-dep format. Missing: %A. Registered: %A."
                missing
                supported))

    // Registering one renderer twice would silently satisfy a naive
    // "two Register calls happened" check, so assert the defaults are
    // two DISTINCT renderer registrations.
    testCase "registers distinct renderers, not one renderer twice" (fun () ->
        let registry = ReportingCompose.buildDefaultRegistry ()
        let renderers = registry.AllRenderers()

        Expect.hasLength
            renderers
            2
            (sprintf
                "expected the Markdown and Html renderers as two distinct registrations, got %d: %A"
                (List.length renderers)
                (renderers |> List.map _.SupportedFormats)))
]

[<Tests>]
let tests =
    testList "ReportingCompose.buildDefaultRegistry" (perFormatTests @ coverageTests)