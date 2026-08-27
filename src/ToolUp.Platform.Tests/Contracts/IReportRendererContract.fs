module ToolUp.Platform.Tests.Contracts.IReportRendererContract

open System
open System.Text
open Expecto
open ToolUp.Reporting

// ─── IReportRenderer contract pack ───────────────────────────────────
//
// Per-renderer assertions that every IReportRenderer implementation
// must satisfy. Each renderer binding (MarkdownRenderer / HtmlRenderer
// today; Pdf / Docx / Xlsx / Pptx as their sub-companions land) calls
// `tests "<RendererName>" factory` to bind the pack to its factory.
//
// Coverage:
//   - Format claim matches what the renderer actually accepts
//   - Round-trip: simple text placeholder substitutes correctly
//   - Validation: missing required placeholder → MissingRequiredPlaceholder
//   - Validation: type mismatch → PlaceholderTypeMismatch
//   - Unknown placeholder in template body passes through unchanged
//   - Number / Date placeholders honour their format hints
//   - Render result is deterministic across calls (same input →
//     same output bytes)

let private utf8 (s: string) = Encoding.UTF8.GetBytes s
let private utf8Decode (b: byte[]) = Encoding.UTF8.GetString b

let private mkTemplateBytes
    (format: TemplateFormat)
    (body: byte[])
    (placeholders: PlaceholderSchema list)
    : ReportTemplate =
    {
        Id = "test-template"
        DisplayName = "Test"
        Format = format
        Body = body
        Placeholders = placeholders
        Version = 1
    }

/// Bind the pack with an explicit template-body builder AND output
/// projection. Container formats whose template is not raw text
/// (DOCX / XLSX — a zip package carrying the placeholder tokens in
/// document text) supply `buildBody`, which wraps the pack's textual
/// fixture bodies in a minimal valid container, and `projectOutput`,
/// which extracts the rendered text back out so the substitution /
/// format-hint / determinism assertions run against content.
let testsWithBody
    (name: string)
    (factory: unit -> IReportRenderer)
    (format: TemplateFormat)
    (buildBody: string -> byte[])
    (projectOutput: byte[] -> string)
    =
    let mkTemplate (format: TemplateFormat) (body: string) (placeholders: PlaceholderSchema list) =
        mkTemplateBytes format (buildBody body) placeholders

    testList $"{name} — IReportRenderer contract" [
        testCaseAsync "Renderer claims the expected format"
        <| async {
            let r = factory ()
            Expect.contains r.SupportedFormats format $"renderer must claim format {format}"
        }

        testCaseAsync "Simple text placeholder substitutes"
        <| async {
            let r = factory ()

            let template =
                mkTemplate format "Hello {{name}}!" [
                    {
                        Key = "name"
                        DisplayName = "Name"
                        Kind = Text
                        Required = true
                    }
                ]

            let values = Map [ "name", TextValue "World" ]
            let! result = r.Render(template, values)

            match result with
            | Result.Error e -> failtestf "Expected Ok; got Error: %s" (RenderError.toMessage e)
            | Result.Ok bytes ->
                let rendered = projectOutput bytes
                Expect.stringContains rendered "World" "value substituted"
                Expect.isFalse (rendered.Contains "{{name}}") "placeholder consumed"
        }

        testCaseAsync "Missing required placeholder → MissingRequiredPlaceholder"
        <| async {
            let r = factory ()

            let template =
                mkTemplate format "Hello {{name}}!" [
                    {
                        Key = "name"
                        DisplayName = "Name"
                        Kind = Text
                        Required = true
                    }
                ]

            let! result = r.Render(template, Map.empty)

            match result with
            | Result.Error(MissingRequiredPlaceholder "name") -> ()
            | Result.Error e -> failtestf "Expected MissingRequiredPlaceholder; got %A" e
            | Result.Ok _ -> failtest "Expected Error; got Ok"
        }

        testCaseAsync "Type mismatch → PlaceholderTypeMismatch"
        <| async {
            let r = factory ()

            let template =
                mkTemplate format "{{count}}" [
                    {
                        Key = "count"
                        DisplayName = "Count"
                        Kind = Number None
                        Required = true
                    }
                ]

            // Supply a TextValue against a Number-shaped schema
            let values = Map [ "count", TextValue "not-a-number" ]
            let! result = r.Render(template, values)

            match result with
            | Result.Error(PlaceholderTypeMismatch("count", _, _)) -> ()
            | Result.Error e -> failtestf "Expected PlaceholderTypeMismatch; got %A" e
            | Result.Ok _ -> failtest "Expected Error; got Ok"
        }

        testCaseAsync "Number placeholder honours format hint"
        <| async {
            let r = factory ()

            let template =
                mkTemplate format "{{n}}" [
                    {
                        Key = "n"
                        DisplayName = "N"
                        Kind = Number(Some "F2")
                        Required = true
                    }
                ]

            let values = Map [ "n", NumberValue 3.14159 ]
            let! result = r.Render(template, values)

            match result with
            | Result.Error e -> failtestf "Expected Ok; got Error: %s" (RenderError.toMessage e)
            | Result.Ok bytes ->
                let rendered = projectOutput bytes
                Expect.stringContains rendered "3.14" "format hint applied"
        }

        testCaseAsync "Date placeholder honours format hint"
        <| async {
            let r = factory ()

            let template =
                mkTemplate format "{{d}}" [
                    {
                        Key = "d"
                        DisplayName = "D"
                        Kind = Date(Some "yyyy-MM-dd")
                        Required = true
                    }
                ]

            let date = DateTimeOffset(2026, 5, 12, 0, 0, 0, TimeSpan.Zero)
            let values = Map [ "d", DateValue date ]
            let! result = r.Render(template, values)

            match result with
            | Result.Error e -> failtestf "Expected Ok; got Error: %s" (RenderError.toMessage e)
            | Result.Ok bytes ->
                let rendered = projectOutput bytes
                Expect.stringContains rendered "2026-05-12" "format hint applied"
        }

        testCaseAsync "Unknown placeholder in template body passes through"
        <| async {
            let r = factory ()
            // Schema declares only `name`; body references `{{unknown}}`
            let template =
                mkTemplate format "Hi {{name}}, also {{unknown}}" [
                    {
                        Key = "name"
                        DisplayName = "Name"
                        Kind = Text
                        Required = true
                    }
                ]

            let values = Map [ "name", TextValue "Alice" ]
            let! result = r.Render(template, values)

            match result with
            | Result.Error e -> failtestf "Expected Ok; got Error: %s" (RenderError.toMessage e)
            | Result.Ok bytes ->
                let rendered = projectOutput bytes
                Expect.stringContains rendered "Alice" "name substituted"
                // Unknown token survives — author's signal that
                // schema/template drifted apart
                Expect.stringContains rendered "{{unknown}}" "unknown token preserved"
        }

        testCaseAsync "Render is deterministic"
        <| async {
            let r = factory ()

            let template =
                mkTemplate format "{{a}} - {{b}}" [
                    {
                        Key = "a"
                        DisplayName = "A"
                        Kind = Text
                        Required = true
                    }
                    {
                        Key = "b"
                        DisplayName = "B"
                        Kind = Number None
                        Required = true
                    }
                ]

            let values = Map [ "a", TextValue "X"; "b", NumberValue 42.0 ]

            let! r1 = r.Render(template, values)
            let! r2 = r.Render(template, values)

            match r1, r2 with
            | Result.Ok b1, Result.Ok b2 ->
                // Projected-content determinism: byte-identical for
                // text renderers (utf8Decode is lossless), content-
                // identical for binary formats whose container embeds
                // a creation timestamp.
                Expect.equal (projectOutput b1) (projectOutput b2) "deterministic across calls"
            | _ -> failtest "Both renders should succeed"
        }
    ]

/// Bind the pack with an explicit output projection over raw-text
/// template bodies. Binary-OUTPUT renderers whose template body is
/// still text (the HTML→PDF renderer) supply a text extraction so the
/// substitution / format-hint / determinism assertions run against the
/// rendered *content* — raw PDF bytes carry compressed streams +
/// creation-timestamp metadata, so neither substring search nor byte
/// equality is meaningful there.
let testsWith
    (name: string)
    (factory: unit -> IReportRenderer)
    (format: TemplateFormat)
    (projectOutput: byte[] -> string)
    =
    testsWithBody name factory format utf8 projectOutput

/// Bind the pack for text-shaped renderers (output bytes are UTF-8
/// text). The original entry point — existing bindings are unchanged.
let tests (name: string) (factory: unit -> IReportRenderer) (format: TemplateFormat) =
    testsWith name factory format utf8Decode