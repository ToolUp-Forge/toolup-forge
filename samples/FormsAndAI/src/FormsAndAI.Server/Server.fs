// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module FormsAndAI.Server

open ToolUp.Platform
open ToolUp.Platform.IEntityStore
open ToolUp.Platform.Server
open ToolUp.Forms.FormSchema
open ToolUp.Forms.Workflow
open ToolUp.Forms.IWorkflowEngine
open ToolUp.AI
open ToolUp.AI.SystemPromptBuilder
open ToolUp.AI.DefaultAIProviderFactory

// Phase 1h reference composition root — Forms AND AI on the same
// `ServerApp` pipeline via the additive `withForms` / `withAI`
// extensions. See `samples/FormsAndAI/README.md` for the demo's scope
// (compile-target sample; not directly runnable without
// `TOOLUP_ENTITY_STORE` enabled + a real `IAIProvider` companion).

// ─── Forms-side substrate ─────────────────────────────────────────

/// Minimal Form schema (no fields — the sample exists to demonstrate
/// composition, not validation). Real apps carry a real `Fields`
/// list — see `docs/forms/`.
let private demoSchema: FormSchema = FormSchema.create "demo-form" "Demo form" []

/// Minimal workflow with one transition `new` --submit--> `submitted`.
/// The transition's `Action = Some "stampSubmission"` looks up the
/// action registered below; the engine threads a `WorkflowContext`
/// carrying the resolved `IServiceProvider` into it per Phase 1h.
let private demoWorkflow: WorkflowDefinition = {
    Id = "demo-workflow"
    InitialState = "new"
    Transitions = [
        {
            From = "new"
            Event = "submit"
            To = "submitted"
            Guard = None
            Action = Some "stampSubmission"
        }
    ]
}

/// Workflow action demonstrating the Phase 1h guard/action DI access.
/// Resolves `IEntityStore` from `ctx.Services` and writes a small
/// audit-shaped entity each time the transition fires — proving the
/// action sees the same DI container as every other handler in the
/// app (including the AI agent loop registered alongside via
/// `withAI`).
let private stampSubmission: WorkflowAction =
    fun ctx -> async {
        // Resolve services per-invocation — never capture transient
        // services at compose time. The cast is safe because the SDK
        // always registers `IEntityStore` (concrete impl varies per
        // `ServerConfig.EntityStore`); deployments running without
        // an entity store would crash here at first transition,
        // which is the documented requirement of the Forms companion.
        let entityStore = ctx.Services.GetService(typeof<IEntityStore>) :?> IEntityStore
        // In a real action the entity write goes here. For the
        // sample we keep the body trivial — the point is the DI
        // resolution shape, not the persistence call.
        ignore entityStore
        return ()
    }

// ─── AI-side substrate ────────────────────────────────────────────

/// Empty no-op factory. Real deployments use
/// `DefaultAIProviderFactory.create` with a wired AIProviders
/// companion + IPlatformAIKeyStore; here we only care that AI's
/// compose layer wires up cleanly alongside Forms's. Calls to
/// `IAIProviderFactory.Resolve` against this factory return
/// `Error NoProviderConfigured` — boots clean, fails clearly at
/// first request.
let private aiFactory: IAIProviderFactory = DefaultAIProviderFactory.empty

/// Minimal AI assistant config. `Branding` is the client-visible
/// shell; `SystemPrompt = None` skips system-prompt assembly entirely
/// (the agent runs against just the user turn).
let private aiAssistantConfig: AIAssistantServerConfig = {
    Branding = {
        Name = "Demo Assistant"
        Icon = "/svg/chart.svg"
        ShowSidePanel = false
    }
    SystemPrompt = None
    MaxHistoryMessages = None
}

// ─── Composition root ─────────────────────────────────────────────

[<EntryPoint>]
let main _ =
    let logger = ConsoleLogger.fromEnv ()
    let config = ServerConfig.fromEnv logger ServerConfigOverrides.empty

    // Local file storage for the blob substrate the `IProviderProfile`
    // store sits on top of. Sample-only — production deployments wire
    // S3 / Azure / GCS via `BlobStorageEnv.fromEnv` and the
    // corresponding cloud companion.
    let blobStorage =
        LocalFileStorage.LocalFileStorage("./.local-blobs") :> BlobStorage.IBlobStorage

    // Canonical BYOK provider-profile store. Registered as the
    // `IProviderProfile` DI singleton so the AI factory + settings
    // handler resolve it; also passed to `withAI` below as the
    // required AI dependency.
    let providerProfile = BlobProviderProfile.create blobStorage

    // The Phase 1h pipeline. Note the single `ServerApp.empty |> ... |>
    // ServerApp.run` shape — no terminal `FormsServerApp.run` or
    // `AIServerApp.run`. Pre-Phase-1h, a deployment that wanted both
    // Forms and AI had to pick one root and drop the other.
    ServerApp.empty
    |> ServerApp.withConfig config
    |> ServerApp.withLogger logger
    |> ServerApp.withStorage blobStorage
    |> ToolUp.Forms.FormsCompose.withForms (fun f ->
        f
        |> ToolUp.Forms.FormsCompose.FormsServerApp.withFormSchema demoSchema
        |> ToolUp.Forms.FormsCompose.FormsServerApp.withWorkflow demoWorkflow
        |> ToolUp.Forms.FormsCompose.FormsServerApp.withAction "stampSubmission" stampSubmission)
    |> ToolUp.AI.AICompose.withAI aiFactory providerProfile (fun ai ->
        ai |> ToolUp.AI.AICompose.AIServerApp.withAIConfig aiAssistantConfig)
    // |> ToolUp.Forms.FormsCompose.withForms (fun f -> f)
    //   ^ Phase 1h conflict validator — uncomment to see the
    //     compose-time failure:
    //       "ToolUp.Forms: companion already composed on this
    //        ServerApp pipeline. ..."
    //     instead of the pre-Phase-1h cascading
    //     duplicate-entity-registration / double-mounted-route errors.
    |> ServerApp.run