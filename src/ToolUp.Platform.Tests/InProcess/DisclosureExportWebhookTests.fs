// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.DisclosureExportWebhookTests

open System
open System.Text
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Narrative
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Reporting
open ToolUp.Reporting.IReportTemplateStore
open ToolUp.Reporting.RendererRegistry
open ToolUp.Facts
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 564 — disclosure egress: export door + webhook contract ───
//
// Extends the Phase 525 enforcement to the next enumerable doors: the
// Reporting export door (fact-referencing narrative content checked at
// `FactExport` before rendering — denied values redacted to the
// policy-naming marker via the 525 walk, the output noting withheld
// refs) and the `FactWebhook` surface contract (suppression-on-deny:
// a denied fact is ABSENT from an outbound payload, pinned here before
// the emitter exists). Same discipline as 525: the one
// `IFactDisclosureGate` is consulted; no evaluate logic lives outside
// the gate — the one-predicate invariant is pinned below by handing
// the doors verdicts that contradict any local re-derivation.

// ── Shared doubles ────────────────────────────────────────────────

/// Preset-verdict gate double — records its last call so a test can pin
/// the scope / principal / surface handed to it. Ids absent from
/// `verdicts` deny as `unknown-fact` (the conservative contract the
/// real gate honours).
type private PresetGate(verdicts: Map<string, FactDisclosureVerdict>) =
    let mutable lastCall: (string * string * FactEgressSurface * string list) option =
        None

    member _.LastCall = lastCall

    interface IFactDisclosureGate with
        member _.Check(scopeId, principal, surface, factIds) = async {
            lastCall <- Some(scopeId, principal, surface, factIds)

            return
                factIds
                |> List.distinct
                |> List.map (fun id ->
                    id, (verdicts.TryFind id |> Option.defaultValue (FactNotDisclosable "unknown-fact")))
                |> Map.ofList
        }

type private FixedTemplateStore(templates: ReportTemplate list) =
    interface IReportTemplateStore with
        member _.List _ = async.Return templates

        member _.Get(_, id) =
            async.Return(templates |> List.tryFind (fun t -> t.Id = id))

        member _.Save(_, template) = async.Return(Ok template)
        member _.Delete(_, _) = async.Return(Ok())

let private markdownTemplate: ReportTemplate = {
    Id = "quarterly"
    DisplayName = "Quarterly report"
    Format = TemplateFormat.Markdown
    Body = Encoding.UTF8.GetBytes "# Quarterly\n\n{{summary}}\n"
    Placeholders = [
        {
            Key = "summary"
            DisplayName = "Summary"
            Kind = PlaceholderKind.Text
            Required = true
        }
    ]
    Version = 1
}

let private htmlTemplate: ReportTemplate = {
    Id = "quarterly-html"
    DisplayName = "Quarterly report (HTML)"
    Format = TemplateFormat.Html
    // `_raw` token: the handler projects the narrative to an HTML
    // fragment, so the template injects it unescaped.
    Body = Encoding.UTF8.GetBytes "<article>{{summary_raw}}</article>"
    Placeholders = [
        {
            Key = "summary"
            DisplayName = "Summary"
            Kind = PlaceholderKind.Text
            Required = true
        }
    ]
    Version = 1
}

let private storeBlobOk: ReportApiHandler.StoreBlob =
    fun _ _ _ -> async.Return(Ok("blob-key", 1))

let private noAudit: ReportApiHandler.AuditOnRender = fun _ -> async.Return()

let private templates = [ markdownTemplate; htmlTemplate ]

// Registry built explicitly rather than via
// `ReportingCompose.buildDefaultRegistry` — that helper's unqualified
// `create ()` resolves to `HtmlRenderer.create` (the later `open`), so
// it never registers the Markdown renderer (pre-existing Phase 23
// defect, tracked outside this phase).
let private zeroDepRegistry () : RendererRegistry =
    let registry = RendererRegistry()
    registry.Register(MarkdownRenderer.create ()) |> ignore
    registry.Register(HtmlRenderer.create ()) |> ignore
    registry

let private ungatedApi () : IReportApi =
    ReportApiHandler.create
        (FixedTemplateStore templates :> IReportTemplateStore)
        (zeroDepRegistry ())
        storeBlobOk
        noAudit
        ReportApiConfig.defaults
        "team-a"

let private gatedApi (gate: IFactDisclosureGate) : IReportApi =
    ReportApiHandler.createWithDisclosureGate
        gate
        "user-1"
        (FixedTemplateStore templates :> IReportTemplateStore)
        (zeroDepRegistry ())
        storeBlobOk
        noAudit
        ReportApiConfig.defaults
        "team-a"

let private renderInline (api: IReportApi) (templateId: TemplateId) (values: Map<string, PlaceholderValue>) = async {
    let! outcome = api.Render(templateId, values)

    match outcome with
    | Ok(RenderedInline(bytes, _)) -> return Encoding.UTF8.GetString bytes
    | other -> return failtestf "expected an inline render, got %A" other
}

let private docOf (elements: NarrativeElement list) : NarrativeDocument = {
    Title = "T"
    Subtitle = None
    Sections = [
        {
            Id = "s"
            Heading = "H"
            Subheading = None
            Elements = elements
        }
    ]
    Provenance = None
    Lang = None
    CanonicalUrl = None
}

/// A narrative citing one disclosable and one denied fact.
let private mixedDoc =
    docOf [
        Paragraph [
            InlineSpan.Text "Revenue was "
            Metric("revenue", "£21,800", Some "fact-open")
            InlineSpan.Text " and margin was "
            Metric("margin", "12%", Some "fact-secret")
        ]
    ]

// ── Surface vocabulary (564.A) ────────────────────────────────────

let surfaceTests =
    testList "Phase 564 egress surfaces" [
        test "FactExport and FactWebhook extend the canonical strings additively" {
            Expect.equal (FactEgressSurface.toString FactExport) "Export" "the export door's audit surface"
            Expect.equal (FactEgressSurface.toString FactWebhook) "Webhook" "the webhook door's audit surface"

            // The Phase 525 vocabulary is untouched — existing verdict
            // consumers are unaffected by the DU extension.
            Expect.equal (FactEgressSurface.toString FactRetrieval) "Retrieval" "525 vocabulary unchanged"
            Expect.equal (FactEgressSurface.toString FactToolResult) "ToolResult" "525 vocabulary unchanged"

            Expect.equal
                (FactEgressSurface.toString FactNarrativePublication)
                "NarrativePublication"
                "525 vocabulary unchanged"
        }
    ]

// ── Export door (564.B) ───────────────────────────────────────────

let exportDoorTests =
    testList "Phase 564 export door" [

        testCaseAsync "a disclosable fact's value renders into the export; the gate is consulted at FactExport"
        <| async {
            let gate = PresetGate(Map.ofList [ "fact-open", FactDisclosable ])

            let doc = docOf [ Paragraph [ Metric("revenue", "£21,800", Some "fact-open") ] ]

            let! rendered = renderInline (gatedApi gate) "quarterly" (Map.ofList [ "summary", NarrativeValue doc ])

            Expect.stringContains rendered "£21,800" "the disclosable value renders"
            Expect.isFalse (rendered.Contains "Withheld values") "nothing withheld ⇒ no note"

            match gate.LastCall with
            | Some(scopeId, principal, surface, ids) ->
                Expect.equal scopeId "team-a" "checked in the rendering scope"
                Expect.equal principal "user-1" "the exporting principal is handed to the gate"
                Expect.equal surface FactExport "checked at the export surface"
                Expect.equal ids [ "fact-open" ] "every cited fact is checked"
            | None -> failtest "the gate was never consulted"
        }

        testCaseAsync
            "a denied fact's value is absent — redacted to the policy-naming marker — and the output notes the withheld ref"
        <| async {
            let gate =
                PresetGate(Map.ofList [ "fact-open", FactDisclosable; "fact-secret", FactNotDisclosable "Internal" ])

            let! rendered = renderInline (gatedApi gate) "quarterly" (Map.ofList [ "summary", NarrativeValue mixedDoc ])

            Expect.isFalse (rendered.Contains "12%") "the denied value never reaches the export"
            Expect.stringContains rendered "not disclosable under policy Internal" "the marker names the policy"
            Expect.stringContains rendered "£21,800" "the disclosable value still renders"
            Expect.stringContains rendered "Withheld values" "the output notes what was withheld"
            Expect.stringContains rendered "fact-secret" "the note names the ref (ids are content hashes, not values)"

            Expect.stringContains
                rendered
                "computed, but not disclosable under policy Internal"
                "the canonical refusal wording rides the note"
        }

        testCaseAsync "the HTML export door redacts identically through the _raw injection path"
        <| async {
            let gate =
                PresetGate(Map.ofList [ "fact-open", FactDisclosable; "fact-secret", FactNotDisclosable "Internal" ])

            let! rendered =
                renderInline (gatedApi gate) "quarterly-html" (Map.ofList [ "summary", NarrativeValue mixedDoc ])

            Expect.isFalse (rendered.Contains "12%") "the denied value never reaches the HTML export"
            Expect.stringContains rendered "not disclosable under policy Internal" "the marker names the policy"
            Expect.stringContains rendered "£21,800" "the disclosable value still renders"
            Expect.stringContains rendered "<article>" "the narrative injects as an HTML fragment, not escaped text"
        }

        testCaseAsync "every deny is audited by the gate with the Export surface (GP 6)"
        <| async {
            let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let store = BlobFactStore.create (InMemoryBlobStorage()) events
            let gate = FactDisclosureGate.create store events
            let scope = "team-a"

            let draft: FactDraft = {
                Subject = {
                    Hierarchy = "geography"
                    Path = [ "uk" ]
                }
                Metric = MetricRef "margin"
                Value = Scalar 100m
                Period = {
                    From = DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)
                    To = DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)
                    Label = Some "Q2-2026"
                }
                Method = Computed("rollup", "1", "p0")
                Evidence = {
                    ResultRef = None
                    InputHashes = [ "hashA" ]
                    TriggerRef = None
                }
                Confidence = None
                Disclosure = Internal
            }

            let fact =
                match store.Assert(scope, draft) |> Async.RunSynchronously with
                | Ok fact -> fact
                | Error e -> failtestf "assert failed: %s" e

            let doc = docOf [ Paragraph [ Metric("margin", "12%", Some fact.FactId) ] ]

            let! rendered = renderInline (gatedApi gate) "quarterly" (Map.ofList [ "summary", NarrativeValue doc ])

            Expect.isFalse (rendered.Contains "12%") "the Internal fact's value is withheld from the export"

            let! rows = events.ReadBySource(scope, FactEvents.SourceModule)

            let denies =
                rows |> List.filter (fun e -> e.EventType = DisclosureEvents.DeniedType)

            Expect.equal denies.Length 1 "exactly one deny row for the one denied fact"

            let payload = denies.Head.Payload
            Expect.stringContains payload "Export" "the deny is audited at the Export surface"
            Expect.stringContains payload fact.FactId "the audited row names the fact id"
            Expect.stringContains payload "Internal" "the audited row names the policy"
            Expect.stringContains payload "user-1" "the audited row names the principal"
            Expect.isFalse (payload.Contains "12%") "the denied value never rides the audit row"
        }

        testCaseAsync "no narrative content ⇒ the gated handler renders byte-identically and never consults the gate"
        <| async {
            let gate = PresetGate Map.empty
            let values = Map.ofList [ "summary", TextValue "plain prose" ]

            let! gated = renderInline (gatedApi gate) "quarterly" values
            let! ungated = renderInline (ungatedApi ()) "quarterly" values

            Expect.equal gated ungated "a fact-less render is untouched by the door (GP 11)"
            Expect.isNone gate.LastCall "no narrative values ⇒ the gate is never consulted"
        }

        testCaseAsync "all-disclosable narrative ⇒ gated output identical to the gate-less handler (plan D17)"
        <| async {
            let gate =
                PresetGate(Map.ofList [ "fact-open", FactDisclosable; "fact-secret", FactDisclosable ])

            let values = Map.ofList [ "summary", NarrativeValue mixedDoc ]

            let! gated = renderInline (gatedApi gate) "quarterly" values
            let! ungated = renderInline (ungatedApi ()) "quarterly" values

            Expect.equal gated ungated "a deployment without classified facts renders byte-identically"
        }

        testCaseAsync "the door honours the gate's verdict verbatim — no evaluate logic outside the gate"
        <| async {
            // The reporting layer holds no `Disclosure` knowledge: hand it
            // verdicts that contradict any local re-derivation and pin
            // that the verdict — not a re-evaluated classification —
            // decides. Deny an id the deployment would call surfaceable ⇒
            // withheld; allow the "secret" id ⇒ disclosed.
            let contrarian =
                PresetGate(
                    Map.ofList [
                        "fact-open", FactNotDisclosable "custom-hold"
                        "fact-secret", FactDisclosable
                    ]
                )

            let! rendered =
                renderInline (gatedApi contrarian) "quarterly" (Map.ofList [ "summary", NarrativeValue mixedDoc ])

            Expect.isFalse (rendered.Contains "£21,800") "a gate-denied fact is withheld, whatever its classification"
            Expect.stringContains rendered "not disclosable under policy custom-hold" "the gate's policy ref is named"
            Expect.stringContains rendered "12%" "a gate-allowed fact is disclosed — the door re-evaluates nothing"
        }
    ]

// ── Webhook surface contract (564.C) ──────────────────────────────

let webhookContractTests =
    testList "Phase 564 FactWebhook contract" [

        testCaseAsync "suppression-on-deny: a denied fact is absent — never annotated; order is preserved"
        <| async {
            let gate =
                PresetGate(
                    Map.ofList [
                        "fact-a", FactDisclosable
                        "fact-b", FactNotDisclosable "Internal"
                        "fact-c", FactDisclosable
                    ]
                )

            let! permitted = FactWebhookEgress.permittedFactIds gate "team-a" "user-1" [ "fact-a"; "fact-b"; "fact-c" ]

            Expect.equal permitted [ "fact-a"; "fact-c" ] "the denied id is simply absent, order preserved"

            match gate.LastCall with
            | Some(scopeId, principal, surface, ids) ->
                Expect.equal scopeId "team-a" "checked in the emitting scope"
                Expect.equal principal "user-1" "the emitting principal is handed to the gate"
                Expect.equal surface FactWebhook "checked at the webhook surface"
                Expect.equal ids [ "fact-a"; "fact-b"; "fact-c" ] "every payload fact is checked"
            | None -> failtest "the gate was never consulted"
        }

        testCaseAsync "fail-closed: an id without an affirmative verdict is suppressed"
        <| async {
            let gate = PresetGate(Map.ofList [ "fact-a", FactDisclosable ])
            let! permitted = FactWebhookEgress.permittedFactIds gate "team-a" "user-1" [ "fact-a"; "no-such-fact" ]

            Expect.equal permitted [ "fact-a" ] "an unknown id never fails open"
        }

        testCaseAsync "an empty payload never consults the gate"
        <| async {
            let gate = PresetGate Map.empty
            let! permitted = FactWebhookEgress.permittedFactIds gate "team-a" "user-1" []

            Expect.equal permitted [] "empty in, empty out"
            Expect.isNone gate.LastCall "no fact ids ⇒ no gate call"
        }

        testCaseAsync "a deny through the real gate is audited with the Webhook surface (GP 6)"
        <| async {
            let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let store = BlobFactStore.create (InMemoryBlobStorage()) events
            let gate = FactDisclosureGate.create store events
            let scope = "team-a"

            let! permitted = FactWebhookEgress.permittedFactIds gate scope "user-1" [ "no-such-fact" ]
            Expect.equal permitted [] "an unresolvable id is suppressed"

            let! rows = events.ReadBySource(scope, FactEvents.SourceModule)

            let denies =
                rows |> List.filter (fun e -> e.EventType = DisclosureEvents.DeniedType)

            Expect.equal denies.Length 1 "the deny is audited"
            Expect.stringContains denies.Head.Payload "Webhook" "the deny is audited at the Webhook surface"
        }
    ]

let tests =
    testList "Phase 564 disclosure export + webhook doors" [ surfaceTests; exportDoorTests; webhookContractTests ]