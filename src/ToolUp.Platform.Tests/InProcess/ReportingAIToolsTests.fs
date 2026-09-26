// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.ReportingAIToolsTests

open System
open System.Collections.Concurrent
open System.Text
open System.Text.Json
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.Narrative
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.InMemoryEventStore
open ToolUp.AI
open ToolUp.AI.AIToolRegistry
open ToolUp.Reporting
open ToolUp.Reporting.IReportTemplateStore
open ToolUp.Reporting.RendererRegistry
open ToolUp.Reporting.ReportingAITools

// ─── Phase 577 — the `_platform.reporting` AI tool family ────────────
//
// What is pinned here, per 577.D: RBAC denial (anonymous and an explicit
// permission-map refusal), the missing-placeholder envelope, inline-vs-
// blob routing, composition-gated registration (no Reporting composed ⇒
// no tools), the `ReportRendered` audit row carrying the source module
// and the requesting conversation, the disclosure door on both render
// paths, and the long-render job end to end (queued in the turn, rendered
// to blob by the handler, the user notified with the link).

// ── Doubles ───────────────────────────────────────────────────────

type private CountingTemplateStore(templates: ReportTemplate list) =
    let mutable reads = 0
    member _.Reads = reads

    interface IReportTemplateStore with
        member _.List _ =
            reads <- reads + 1
            async.Return templates

        member _.Get(_, id) =
            reads <- reads + 1
            async.Return(templates |> List.tryFind (fun t -> t.Id = id))

        member _.Save(_, _, template) = async.Return(Ok template)
        member _.Delete(_, _, _) = async.Return(Ok())

type private RecordingChannel() =
    let published = ConcurrentQueue<string * Notification>()
    member _.Published = published |> Seq.toList

    interface INotificationChannel with
        member _.Publish(scopeId, notification) =
            published.Enqueue((scopeId, notification))
            async.Return()

        member _.Subscribe(_, _) = async.Return(Guid.NewGuid())
        member _.Unsubscribe _ = async.Return()

type private RecordingScheduler() =
    let scheduled = ConcurrentQueue<JobRegistration>()
    let triggered = ConcurrentQueue<string * JobId * string>()
    let jobId = Guid.NewGuid()
    member _.JobId = jobId
    member _.Scheduled = scheduled |> Seq.toList
    member _.Triggered = triggered |> Seq.toList

    interface IJobScheduler with
        member _.RegisterHandler(_, _) = ()
        member _.RegisterHandlerAsync(_, _) = async.Return(Ok())

        member _.Schedule registration =
            scheduled.Enqueue registration
            async.Return(Ok jobId)

        member _.Cancel(_, _) = async.Return()
        member _.Disable(_, _) = async.Return()
        member _.Enable(_, _) = async.Return()
        member _.Get(_, _) = async.Return None
        member _.ListJobs _ = async.Return []
        member _.GetRecentRuns(_, _, _) = async.Return []

        member _.TriggerOnce(scopeId, id, byUser) =
            triggered.Enqueue((scopeId, id, byUser))
            async.Return(Ok())

        member _.NotifyEventWritten(_, _, _) = async.Return()

/// Preset-verdict gate: ids absent from `verdicts` deny as `unknown-fact`.
type private PresetGate(verdicts: Map<string, FactDisclosureVerdict>) =
    interface IFactDisclosureGate with
        member _.Check(_: string, _: string, _: FactEgressSurface, factIds: string list) = async {
            return
                factIds
                |> List.distinct
                |> List.map (fun id ->
                    id, (verdicts.TryFind id |> Option.defaultValue (FactNotDisclosable "unknown-fact")))
                |> Map.ofList
        }

        member this.Check(scope: ResolvedScope, principal: string, surface: FactEgressSurface, factIds: string list) =
            (this :> IFactDisclosureGate).Check(scope.ScopeId, principal, surface, factIds)

// ── Fixtures ──────────────────────────────────────────────────────

let private summaryPlaceholder: PlaceholderSchema = {
    Key = "summary"
    DisplayName = "Summary"
    Kind = PlaceholderKind.Text
    Required = true
}

let private quarterly: ReportTemplate = {
    Id = "quarterly"
    DisplayName = "Quarterly report"
    Format = TemplateFormat.Markdown
    Body = Encoding.UTF8.GetBytes "# Quarterly {{year}}\n\n{{summary}}\n"
    Placeholders = [
        summaryPlaceholder
        {
            Key = "year"
            DisplayName = "Year"
            Kind = PlaceholderKind.Number None
            Required = false
        }
    ]
    Version = 1
}

let private wordTemplate: ReportTemplate = {
    Id = "board-pack"
    DisplayName = "Board pack"
    Format = TemplateFormat.Docx
    Body = [||]
    Placeholders = [ summaryPlaceholder ]
    Version = 1
}

let private registry () : RendererRegistry =
    let r = RendererRegistry()
    r.Register(MarkdownRenderer.create ()) |> ignore
    r.Register(HtmlRenderer.create ()) |> ignore
    r

type private Harness = {
    Deps: ReportingAIToolDeps
    Store: CountingTemplateStore
    Blobs: ConcurrentQueue<string * byte[] * string>
    DeploymentAudits: ConcurrentQueue<ReportApiHandler.ReportRenderedAudit>
}

let private harnessWith (configure: ReportingAIToolDeps -> ReportingAIToolDeps) : Harness =
    let store = CountingTemplateStore [ quarterly; wordTemplate ]
    let blobs = ConcurrentQueue<string * byte[] * string>()
    let audits = ConcurrentQueue<ReportApiHandler.ReportRenderedAudit>()

    let storeBlob: ReportApiHandler.StoreBlob =
        fun scope bytes mime ->
            blobs.Enqueue((scope, bytes, mime))
            async.Return(Ok($"reports/{blobs.Count}", 3))

    let deps =
        {
            ReportingAIToolDeps.create store (registry ()) storeBlob (fun key version ->
                $"/api/files/{key}?v={version}") with
                AuditOnRender =
                    fun a ->
                        audits.Enqueue a
                        async.Return()
        }
        |> configure

    {
        Deps = deps
        Store = store
        Blobs = blobs
        DeploymentAudits = audits
    }

let private harness () = harnessWith id

let private conversationId = Guid("11111111-2222-3333-4444-555555555555")

type private Services = {
    Provider: IServiceProvider
    Events: InMemoryEventStore
    Channel: RecordingChannel
    Narratives: INarrativeStore
}

let private servicesWith (extra: IServiceCollection -> unit) : Services =
    let events = InMemoryEventStore()
    let channel = RecordingChannel()
    let narratives = InMemoryNarrativeStore() :> INarrativeStore
    let s = ServiceCollection()
    s.AddSingleton<IEventStore>(events) |> ignore
    s.AddSingleton<INotificationChannel>(channel) |> ignore
    s.AddSingleton<INarrativeStore>(narratives) |> ignore
    extra s

    {
        Provider = s.BuildServiceProvider() :> IServiceProvider
        Events = events
        Channel = channel
        Narratives = narratives
    }

/// A background tool context the way `createBackgroundContext` builds it:
/// the middleware items copied forward, plus the conversation stamp.
let private signedIn (services: Services) (activeModule: string option) : HttpContext =
    let ctx = DefaultHttpContext()
    ctx.RequestServices <- services.Provider
    ctx.Items["ToolUp.UserId"] <- box "user-1"

    ctx.Items["ToolUp.StorageScope"] <-
        box {
            ScopeId = "team-a"
            Container = "team-team-a"
            Persist = true
        }

    ctx.Items["ToolUp.Subject"] <- box (TeamMember("user-1", "team-a"))
    ctx.Items[AIConsentDispatch.ItemsKeys.ConversationId] <- box conversationId

    activeModule
    |> Option.iter (fun m -> ctx.Items[AICrossModuleAudit.ItemsKeys.ActiveModule] <- box m)

    ctx :> HttpContext

let private anonymous (services: Services) : HttpContext =
    let ctx = DefaultHttpContext()
    ctx.RequestServices <- services.Provider
    ctx.Items["ToolUp.UserId"] <- box "sess-9"
    ctx.Items["ToolUp.Subject"] <- box (AnonymousSession "sess-9")
    ctx :> HttpContext

let private executorOf (deps: ReportingAIToolDeps) (name: string) =
    tools deps |> List.find (fun (def, _) -> def.Name = name) |> snd

let private call (deps: ReportingAIToolDeps) (name: string) (ctx: HttpContext) (args: string) : JsonElement =
    let json = executorOf deps name ctx args |> Async.RunSynchronously
    (JsonDocument.Parse json).RootElement

let private str (el: JsonElement) (name: string) = el.GetProperty(name).GetString()

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

let private reportedEvents (services: Services) : ModuleEvent list =
    (services.Events :> IEventStore).ReadBySource("team-a", SourceModule)
    |> Async.RunSynchronously

// ── Composition-gated registration ────────────────────────────────

let registrationTests =
    testList "Phase 577 _platform.reporting registration" [
        test "no Reporting composed ⇒ no reporting tools (GP 13)" {
            let app = ServerApp.empty

            Expect.isFalse
                (app.AITools |> List.exists (fun (def, _) -> def.SourceModule = SourceModule))
                "a deployment that never composes the family surfaces none of it"
        }

        test "withReportingAITools contributes exactly the two tools, under the reserved source" {
            let app = withReportingAITools (harness ()).Deps ServerApp.empty

            let names =
                app.AITools
                |> List.filter (fun (def, _) -> def.SourceModule = SourceModule)
                |> List.map (fun (def, _) -> def.Name)
                |> List.sort

            Expect.equal names [ ListTemplatesToolName; RenderReportToolName ] "both tools, nothing else"

            Expect.isTrue
                (isSdkReservedToolSource SourceModule)
                "the source is SDK-reserved, so the per-module RBAC filter does not hide the family"
        }

        test "the composeAI pickup registers them beside the built-ins without a name collision" {
            let app = withReportingAITools (harness ()).Deps ServerApp.empty

            let builtIns =
                (NarrativeTools.builtInTools @ PlatformAITools.builtIn)
                |> List.map _.Definition.Name
                |> Set.ofList

            let reporting = app.AITools |> List.map (fun (def, _) -> def.Name) |> Set.ofList

            Expect.isEmpty (Set.intersect builtIns reporting) "no reporting tool shadows an SDK built-in"

            let registry = AIToolRegistry()
            registry.RegisterAll(app.AITools |> List.map (fun (def, exec) -> createTool def exec))

            Expect.isSome (registry.FindByName RenderReportToolName) "render_report reaches the AI tool registry"
            Expect.isSome (registry.FindByName ListTemplatesToolName) "list_report_templates reaches it too"
        }

        test "every tool declares its effects (the verified profile refuses undeclared tools)" {
            for def, _ in tools (harness ()).Deps do
                Expect.notEqual def.Effects UndeclaredEffects $"{def.Name} declares its effects"
        }

        test "the items keys this companion reads are the ones the AI server stamps" {
            // Spelled twice because this package does not reference the AI
            // server; pinned equal here, where both are in scope.
            Expect.equal ItemsKeys.ConversationId AIConsentDispatch.ItemsKeys.ConversationId "conversation id key"
            Expect.equal ItemsKeys.ActiveModule AICrossModuleAudit.ItemsKeys.ActiveModule "active module key"
        }
    ]

// ── RBAC ──────────────────────────────────────────────────────────

let rbacTests =
    testList "Phase 577 _platform.reporting RBAC" [
        test "an anonymous caller is refused by both tools before any store read" {
            let h = harness ()
            let services = servicesWith ignore
            let ctx = anonymous services

            let listed = call h.Deps ListTemplatesToolName ctx "{}"
            Expect.equal (str listed "error") "NotAuthorised" "list refuses an anonymous caller"

            let rendered =
                call h.Deps RenderReportToolName ctx """{"template_id":"quarterly","values":{"summary":"x"}}"""

            Expect.equal (str rendered "error") "NotAuthorised" "render refuses an anonymous caller"
            Expect.equal h.Store.Reads 0 "the refusal precedes every template read"
            Expect.isEmpty h.Blobs "and nothing was rendered"
        }

        test "an explicit permission-map entry without Read refuses the family" {
            let h = harness ()
            let services = servicesWith ignore
            let ctx = signedIn services None

            ctx.Items["ToolUp.ModulePermissions"] <- box (Map.ofList [ SourceModule, [ ModulePermission.SchemaOnly ] ])

            let listed = call h.Deps ListTemplatesToolName ctx "{}"
            Expect.equal (str listed "error") "NotAuthorised" "a deliberate deny is honoured"
        }

        test "a permission map that does not name the family leaves it reachable" {
            let h = harness ()
            let services = servicesWith ignore
            let ctx = signedIn services None

            ctx.Items["ToolUp.ModulePermissions"] <- box (Map.ofList [ "Pricing", [ ModulePermission.Read ] ])

            let listed = call h.Deps ListTemplatesToolName ctx "{}"
            Expect.equal (listed.GetProperty("templates").GetArrayLength()) 2 "RBAC on other modules does not hide it"
        }
    ]

// ── list_report_templates ─────────────────────────────────────────

let listTests =
    testList "Phase 577 list_report_templates" [
        test "each template carries id / name / format / renderability / placeholder schema" {
            let h = harness ()
            let services = servicesWith ignore
            let listed = call h.Deps ListTemplatesToolName (signedIn services None) "{}"
            let entries = listed.GetProperty("templates").EnumerateArray() |> Seq.toList

            let q = entries |> List.find (fun e -> str e "id" = "quarterly")
            Expect.equal (str q "name") "Quarterly report" "name"
            Expect.equal (str q "format") "markdown" "format"
            Expect.isTrue (q.GetProperty("renderable").GetBoolean()) "markdown is rendered by the zero-dep default"

            let placeholders = q.GetProperty("placeholders").EnumerateArray() |> Seq.toList
            let summary = placeholders |> List.find (fun p -> str p "key" = "summary")
            Expect.isTrue (summary.GetProperty("required").GetBoolean()) "required flag"
            Expect.equal (str (summary.GetProperty "schema") "kind") "text" "value kind"

            let year = placeholders |> List.find (fun p -> str p "key" = "year")
            Expect.equal (str (year.GetProperty "schema") "kind") "number" "number kind"

            let board = entries |> List.find (fun e -> str e "id" = "board-pack")

            Expect.isFalse
                (board.GetProperty("renderable").GetBoolean())
                "a format with no composed renderer is listed honestly as not renderable"
        }
    ]

// ── render_report ─────────────────────────────────────────────────

let renderTests =
    testList "Phase 577 render_report" [
        test "a missing required placeholder is a recoverable envelope naming the key" {
            let h = harness ()
            let services = servicesWith ignore

            let result =
                call
                    h.Deps
                    RenderReportToolName
                    (signedIn services None)
                    """{"template_id":"quarterly","values":{"year":2026}}"""

            Expect.equal (str result "error") "MissingPlaceholders" "typed error"

            let missing =
                result.GetProperty("missing").EnumerateArray()
                |> Seq.map _.GetString()
                |> Seq.toList

            Expect.equal missing [ "summary" ] "names exactly the missing key"
            Expect.isTrue (result.GetProperty("placeholders").GetArrayLength() > 0) "and re-states the schema"
            Expect.isEmpty h.Blobs "nothing was rendered"
        }

        test "a value of the wrong kind and an unknown key are refused, naming the key" {
            let h = harness ()
            let services = servicesWith ignore
            let ctx = signedIn services None

            let wrongKind =
                call
                    h.Deps
                    RenderReportToolName
                    ctx
                    """{"template_id":"quarterly","values":{"summary":"s","year":"soon"}}"""

            Expect.equal (str wrongKind "error") "InvalidValues" "kind mismatch"
            Expect.stringContains (str wrongKind "message") "year" "names the key"

            let unknown =
                call
                    h.Deps
                    RenderReportToolName
                    ctx
                    """{"template_id":"quarterly","values":{"summary":"s","colour":"red"}}"""

            Expect.stringContains (str unknown "message") "colour" "an unknown key is named"
        }

        test "a small Markdown render comes back inline as text (inline routing)" {
            let h = harness ()
            let services = servicesWith ignore

            let result =
                call
                    h.Deps
                    RenderReportToolName
                    (signedIn services None)
                    """{"template_id":"quarterly","values":{"summary":"Revenue rose.","year":2026}}"""

            Expect.equal (str result "delivery") "inline" "inline"
            Expect.stringContains (str result "content") "Revenue rose." "the rendered text"
            Expect.stringContains (str result "content") "2026" "the number placeholder"
            Expect.isEmpty h.Blobs "no blob for an inline render"
        }

        test "above the inline budget the render goes to blob with a download link and a toast (blob routing)" {
            let h = harnessWith (fun d -> { d with InlineTextBudget = 8 })
            let services = servicesWith ignore

            let result =
                call
                    h.Deps
                    RenderReportToolName
                    (signedIn services None)
                    """{"template_id":"quarterly","values":{"summary":"Revenue rose."}}"""

            Expect.equal (str result "delivery") "download" "blob"
            Expect.equal (str result "downloadLink") "/api/files/reports/1?v=3" "the deployment's link"
            Expect.equal h.Blobs.Count 1 "exactly one blob written, through the deployment's StoreBlob"

            let notices = services.Channel.Published

            Expect.exists
                notices
                (fun (user, n) ->
                    user = "user-1"
                    && (match n with
                        | Notification.SystemMessage(_, text) -> text.Contains "Quarterly report"
                        | _ -> false))
                "the toast the shell renders"

            Expect.isFalse
                (notices
                 |> List.exists (fun (_, n) ->
                     match n with
                     | Notification.JobCompleted _ -> true
                     | _ -> false))
                "no JobCompleted without a job - the link is in the tool result the user is reading"
        }

        test "a successful render writes ReportRendered under _platform.reporting with the conversation id" {
            let h = harness ()
            let services = servicesWith ignore

            call
                h.Deps
                RenderReportToolName
                (signedIn services None)
                """{"template_id":"quarterly","values":{"summary":"s"}}"""
            |> ignore

            Expect.equal h.DeploymentAudits.Count 1 "the deployment's own audit callback still runs"

            match reportedEvents services with
            | [ e ] ->
                Expect.equal e.SourceModule SourceModule "source module"
                Expect.equal e.EventType ReportRenderedEventType "event type"
                let payload = (JsonDocument.Parse e.Payload).RootElement

                Expect.stringContains
                    (payload.GetProperty("ConversationId").GetRawText())
                    (string conversationId)
                    "conversation id"

                Expect.equal (str payload "TemplateId") "quarterly" "template"
                Expect.equal (str payload "RequestedBy") "user-1" "requester"
            | other -> failtestf "expected one ReportRendered row, got %A" other
        }

        test "an unknown template is TemplateNotFound" {
            let h = harness ()
            let services = servicesWith ignore

            let result =
                call h.Deps RenderReportToolName (signedIn services None) """{"template_id":"nope"}"""

            Expect.equal (str result "error") "TemplateNotFound" "typed"
        }
    ]

// ── Narrative sourcing + the disclosure door ──────────────────────

let narrativeTests =
    testList "Phase 577 render_report narrative sourcing" [
        test "narrative_placeholder with no id uses the on-screen page's latest narrative" {
            let h = harness ()
            let services = servicesWith ignore

            services.Narratives.Publish("team-a", "Pricing", None, docOf [ Paragraph [ InlineSpan.Text "old text" ] ])
            |> Async.RunSynchronously
            |> ignore

            services.Narratives.Publish(
                "team-a",
                "Pricing",
                None,
                docOf [ Paragraph [ InlineSpan.Text "on-screen text" ] ]
            )
            |> Async.RunSynchronously
            |> ignore

            services.Narratives.Publish("team-a", "Churn", None, docOf [ Paragraph [ InlineSpan.Text "other page" ] ])
            |> Async.RunSynchronously
            |> ignore

            let result =
                call
                    h.Deps
                    RenderReportToolName
                    (signedIn services (Some "Pricing"))
                    """{"template_id":"quarterly","narrative_placeholder":"summary"}"""

            Expect.equal (str result "delivery") "inline" "rendered"
            Expect.stringContains (str result "content") "on-screen text" "the active page's latest narrative"
            Expect.isFalse ((str result "content").Contains "other page") "not another page's"
        }

        test "narrative_id selects that narrative" {
            let h = harness ()
            let services = servicesWith ignore

            let id =
                services.Narratives.Publish("team-a", "Churn", None, docOf [ Paragraph [ InlineSpan.Text "by id" ] ])
                |> Async.RunSynchronously

            let result =
                call
                    h.Deps
                    RenderReportToolName
                    (signedIn services (Some "Pricing"))
                    $$"""{"template_id":"quarterly","narrative_placeholder":"summary","narrative_id":"{{id}}"}"""

            Expect.stringContains (str result "content") "by id" "the named narrative"
        }

        test "no page and no id is a recoverable NarrativeUnavailable" {
            let h = harness ()
            let services = servicesWith ignore

            let result =
                call
                    h.Deps
                    RenderReportToolName
                    (signedIn services None)
                    """{"template_id":"quarterly","narrative_placeholder":"summary"}"""

            Expect.equal (str result "error") "NarrativeUnavailable" "typed and recoverable"
        }

        test "the 564 disclosure door redacts a denied fact on the in-turn path" {
            let h = harness ()

            let services =
                servicesWith (fun s ->
                    s.AddSingleton<IFactDisclosureGate>(PresetGate(Map.ofList [ "fact-open", FactDisclosable ]))
                    |> ignore)

            services.Narratives.Publish(
                "team-a",
                "Pricing",
                None,
                docOf [
                    Paragraph [
                        Metric("revenue", "£21,800", Some "fact-open")
                        InlineSpan.Text " / "
                        Metric("margin", "12.3%", Some "fact-secret")
                    ]
                ]
            )
            |> Async.RunSynchronously
            |> ignore

            let result =
                call
                    h.Deps
                    RenderReportToolName
                    (signedIn services (Some "Pricing"))
                    """{"template_id":"quarterly","narrative_placeholder":"summary"}"""

            let content = str result "content"
            Expect.stringContains content "£21,800" "a disclosable value renders"
            Expect.isFalse (content.Contains "12.3%") "a denied value never leaves"
            Expect.stringContains content "Withheld values" "the withheld section the door appends"
        }
    ]

// ── The long-render path (577.C) ──────────────────────────────────

let private deferMarkdown (d: ReportingAIToolDeps) = {
    d with
        LongRender = {
            LongRenderPolicy.defaults with
                DeferredFormats = [ TemplateFormat.Markdown ]
        }
}

let longRenderTests =
    testList "Phase 577 long-render job" [
        test "the policy measures input size and honours always-deferred formats" {
            Expect.isFalse
                (LongRenderPolicy.defers LongRenderPolicy.defaults TemplateFormat.Markdown 1024L)
                "small input stays in the turn"

            Expect.isTrue
                (LongRenderPolicy.defers LongRenderPolicy.defaults TemplateFormat.Markdown (300L * 1024L))
                "input over the threshold defers"

            Expect.isTrue
                (LongRenderPolicy.defers
                    {
                        LongRenderPolicy.never with
                            DeferredFormats = [ Pdf ]
                    }
                    Pdf
                    1L)
                "a listed format always defers"

            Expect.isFalse
                (LongRenderPolicy.defers LongRenderPolicy.never TemplateFormat.Markdown Int64.MaxValue)
                "never means never"

            Expect.equal
                (estimateInputBytes quarterly (Map.ofList [ "summary", TextValue "abcd" ]))
                (int64 quarterly.Body.Length + 4L)
                "the estimate is the template body plus the supplied values"
        }

        test "a deferred render is queued as a Manual job, triggered, and answered with the handle at once" {
            let h = harnessWith deferMarkdown
            let scheduler = RecordingScheduler()

            let services =
                servicesWith (fun s -> s.AddSingleton<IJobScheduler>(scheduler) |> ignore)

            let result =
                call
                    h.Deps
                    RenderReportToolName
                    (signedIn services None)
                    """{"template_id":"quarterly","values":{"summary":"big"}}"""

            Expect.equal (str result "status") "queued" "queued"
            Expect.equal (result.GetProperty("jobId").GetGuid()) scheduler.JobId "the handle"
            Expect.isEmpty h.Blobs "nothing rendered in the turn"

            match scheduler.Scheduled with
            | [ r ] ->
                Expect.equal r.Handler RenderJobHandlerName "handler"
                Expect.equal r.Trigger Manual "Manual trigger"
                Expect.equal r.ScopeId "team-a" "the caller's scope"

                let payload = RenderJobPayload.decode r.Payload |> Option.get
                Expect.equal payload.UserId "user-1" "the requester rides the payload"
                Expect.equal payload.ConversationId (Some conversationId) "so does the conversation"
            | other -> failtestf "expected one registration, got %A" other

            Expect.equal scheduler.Triggered [ "team-a", scheduler.JobId, "user-1" ] "fired once, by the requester"
        }

        test "the handler renders to blob, reports progress, notifies with the link, and audits with the job id" {
            let h = harnessWith deferMarkdown
            let scheduler = RecordingScheduler()

            let services =
                servicesWith (fun s -> s.AddSingleton<IJobScheduler>(scheduler) |> ignore)

            call
                h.Deps
                RenderReportToolName
                (signedIn services None)
                """{"template_id":"quarterly","values":{"summary":"big"}}"""
            |> ignore

            let registration = scheduler.Scheduled |> List.exactlyOne

            let jobCtx: JobContext = {
                JobId = scheduler.JobId
                ScopeId = "team-a"
                AccessContext = AccessContext.unrestricted (TeamMember("user-1", "team-a"))
                Attempt = 1
                Trigger = Manual
                TriggerSource = ScheduledManually "user-1"
                ScheduledAt = DateTime.UtcNow
                RunningAt = DateTime.UtcNow
                Payload = registration.Payload
                DeadLetterDestination = None
            }

            let outcome =
                (renderJobHandler h.Deps services.Provider).Execute jobCtx
                |> Async.RunSynchronously

            Expect.equal outcome Success "the job succeeds"
            Expect.equal h.Blobs.Count 1 "rendered to blob even though the output is tiny"

            Expect.exists
                services.Channel.Published
                (fun (user, n) ->
                    user = "user-1"
                    && (match n with
                        | Notification.JobCompleted(id, "Succeeded", Some link) ->
                            id = scheduler.JobId && link = "/api/files/reports/1?v=3"
                        | _ -> false))
                "the user is told, with the link, keyed on the job"

            match reportedEvents services with
            | [ e ] ->
                let payload = (JsonDocument.Parse e.Payload).RootElement
                Expect.stringContains (payload.GetProperty("JobId").GetRawText()) (string scheduler.JobId) "job id"

                Expect.stringContains
                    (payload.GetProperty("ConversationId").GetRawText())
                    (string conversationId)
                    "conversation id"
            | other -> failtestf "expected one ReportRendered row, got %A" other
        }

        test "the door runs BEFORE enqueueing: the job payload never carries a denied value" {
            let h = harnessWith deferMarkdown
            let scheduler = RecordingScheduler()

            let services =
                servicesWith (fun s ->
                    s.AddSingleton<IJobScheduler>(scheduler) |> ignore
                    s.AddSingleton<IFactDisclosureGate>(PresetGate Map.empty) |> ignore)

            services.Narratives.Publish(
                "team-a",
                "Pricing",
                None,
                docOf [ Paragraph [ Metric("margin", "12.3%", Some "fact-secret") ] ]
            )
            |> Async.RunSynchronously
            |> ignore

            call
                h.Deps
                RenderReportToolName
                (signedIn services (Some "Pricing"))
                """{"template_id":"quarterly","narrative_placeholder":"summary"}"""
            |> ignore

            let registration = scheduler.Scheduled |> List.exactlyOne
            Expect.isFalse (registration.Payload.Contains "12.3%") "the persisted payload is already redacted"
        }

        test "a failed job render is permanent and tells the user" {
            let h = harness ()
            let services = servicesWith ignore

            let jobCtx: JobContext = {
                JobId = Guid.NewGuid()
                ScopeId = "team-a"
                AccessContext = AccessContext.unrestricted (TeamMember("user-1", "team-a"))
                Attempt = 1
                Trigger = Manual
                TriggerSource = ScheduledManually "user-1"
                ScheduledAt = DateTime.UtcNow
                RunningAt = DateTime.UtcNow
                Payload =
                    RenderJobPayload.encode {
                        TemplateId = "gone"
                        ScopeId = "team-a"
                        UserId = "user-1"
                        ConversationId = None
                        Values = Map.empty
                    }
                DeadLetterDestination = None
            }

            match
                (renderJobHandler h.Deps services.Provider).Execute jobCtx
                |> Async.RunSynchronously
            with
            | PermanentFailure _ -> ()
            | other -> failtestf "expected PermanentFailure, got %A" other

            Expect.exists
                services.Channel.Published
                (fun (_, n) ->
                    match n with
                    | Notification.SystemMessage(SystemMessageLevel.Warning, _) -> true
                    | _ -> false)
                "the failure is announced"
        }

        test "no scheduler composed: a deferrable render still happens, in the turn (GP 13)" {
            let h = harnessWith deferMarkdown
            let services = servicesWith ignore

            let result =
                call
                    h.Deps
                    RenderReportToolName
                    (signedIn services None)
                    """{"template_id":"quarterly","values":{"summary":"s"}}"""

            Expect.equal (str result "status") "rendered" "rendered"
            Expect.stringContains (str result "note") "no background job scheduler" "and says why it did not defer"
        }

        test "the job payload round-trips a narrative value" {
            let payload: RenderJobPayload = {
                TemplateId = "quarterly"
                ScopeId = "team-a"
                UserId = "user-1"
                ConversationId = Some conversationId
                Values =
                    Map.ofList [
                        "summary", NarrativeValue(docOf [ Paragraph [ InlineSpan.Text "round trip" ] ])
                        "year", NumberValue 2026.0
                    ]
            }

            Expect.equal (RenderJobPayload.decode (RenderJobPayload.encode payload)) (Some payload) "lossless"
            Expect.isNone (RenderJobPayload.decode "not json") "garbage decodes to None, never throws"
        }
    ]

let tests =
    testList "Phase 577 _platform.reporting AI tools" [
        registrationTests
        rbacTests
        listTests
        renderTests
        narrativeTests
        longRenderTests
    ]