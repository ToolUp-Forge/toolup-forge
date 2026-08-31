// Ambient context for `src/ToolUp.AI/TECHNICAL_GUIDE.md`.
//
// The guide is a tour of the AI companion's runtime, so nearly every
// block is an excerpt from a composition root, an agent-loop call site,
// or a consumer module that the page never shows in full. What those
// blocks read is always one of three things:
//
//   * the five arguments a caller of `IAIProvider.SendMessage` already
//     holds (`provider` / `msgs` / `tools` / `systemPrompt` / `onStream`
//     / `retryPolicy`), plus the multipart message the Vision section
//     builds in its first block and summarises in its third;
//   * the deployment's own composition-root locals — the platform prompt
//     prefix, the AI mode / client config / module list the client shell
//     is run with;
//   * a CONSUMER module's own domain surface — the guide's worked
//     example is a `MediaOptimisation` module, whose tool declaration,
//     routine, request/result records and Elmish `Msg` belong to the
//     deployment and not to the SDK.
//
// All three are declared here so the blocks compile exactly as a reader
// would copy them, with no `open`-ceremony added to the markdown — and,
// unlike a `skip=fragment` marker, with every `AIToolDefinition` /
// `AIProviderMessage` / `SystemPromptBuilder` / `PeerJobHandle`-class SDK
// name in them held under the gate.
//
// The top-level `open`s are the ones the page's own blocks assume but
// never write: `AIAssistantServerConfig` + `PromptContext` live in
// `ToolUp.AI.SystemPromptBuilder`, `RegisteredTool` + `createTool` in
// `ToolUp.AI.AIToolRegistry`, `IProviderProfile` in
// `ToolUp.Platform.Providers`, and the client-tier blocks need
// `ToolUp.AI.Client` + Feliz + SimpleJson. `Fable.SimpleJson` is opened
// LAST so the export-decoder block's `Json.parseAs` resolves to it.
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Giraffe
open Feliz
open ToolUp.Platform.Providers
open ToolUp.AI
open ToolUp.AI.AIToolRegistry
open ToolUp.AI.SystemPromptBuilder
open ToolUp.AI.Client
open Fable.SimpleJson

[<AutoOpen>]
module PageAmbient =

    // ── The provider call site ────────────────────────────────────
    //
    // The capability-flag and vision-rejection blocks are excerpts from
    // a caller that already holds a resolved provider and the five
    // arguments `IAIProvider.SendMessage` takes.

    /// The deployment's resolved provider, as returned by
    /// `IAIProviderFactory.Resolve`.
    let provider: IAIProvider = failwith "ambient"

    let msgs: AIProviderMessage list = failwith "ambient"

    let tools: AIProviderToolDef list = failwith "ambient"

    let systemPrompt: string option = failwith "ambient"

    let onStream: (string -> unit) option = failwith "ambient"

    let retryPolicy: RetryPolicy = failwith "ambient"

    /// The image bytes a caller already holds — read from an upload, a
    /// blob store or an asset store; the page deliberately does not say
    /// which, only that they never enter an audit blob.
    let receiptBytes: byte[] = failwith "ambient"

    /// The multipart message built in the Vision section's first block
    /// and summarised in its third.
    let multipart: AIProviderMessage = failwith "ambient"

    // ── The client-side AI surfaces ───────────────────────────────

    /// The AI assistant page's own Elmish model. A real SDK type, so the
    /// `AvailableTools` read in the tool-use gating block stays checked.
    let model: AIAssistantUI.Model = failwith "ambient"

    /// The page's own tool-list renderer, in the view the gating block is
    /// excerpted from.
    let renderToolList (available: AIToolDefinition list) : unit = failwith "ambient"

    /// The AI mode / client config / module list the shell is run with,
    /// built earlier in the consumer's own `Client.fs`.
    let aiMode: AIAssistantMode = failwith "ambient"

    let config: ClientConfig = failwith "ambient"

    let modules: ErasedModule list = failwith "ambient"

    /// The companion's own toolbar button, registered from a module-load
    /// `do` block.
    let MyButton () : ReactElement = failwith "ambient"

    // ── The composition root's prompt layer ───────────────────────

    /// The deployment's platform-wide prompt prefix.
    let platformPrefix: string = failwith "ambient"

    /// The team profile the team-private-context builder injects, and the
    /// deployment-owned store it is read from. Both belong to the
    /// deployment: the SDK's own `ITeamStore` carries memberships and
    /// roles, never a domain profile, so the guide's builder is reading a
    /// store the consumer wrote.
    type TeamProfile = {
        Name: string
        Category: string
        Brands: string
    }

    type TeamProfileStore = {
        GetTeamProfile: string -> Async<TeamProfile>
    }

    // ── The worked consumer module ────────────────────────────────
    //
    // `MediaOptimisation` is the guide's example CONSUMER module. Its
    // request / result records, its server routine, its tool declaration
    // and its Elmish `Msg` are all the deployment's own — declared here
    // so the four tool-wiring blocks compile as a reader would copy them.

    type MediaOptimisationRequest = { Budget: decimal }

    type MediaOptimisationResult = { Allocation: (string * decimal) list }

    /// The module's own Elmish message. `ApiCall` is the SDK's
    /// start/finish envelope, so `Finished result` in the decoder block
    /// is the real SDK case rather than a look-alike.
    type Msg = OptimiseMedia of ApiCall<unit, MediaOptimisationResult>

    /// The request the executor parsed out of `argsJson`.
    let request: MediaOptimisationRequest = failwith "ambient"

    /// The deployment's Fable-shaped serialiser (`FableConverters`-backed
    /// in a real app).
    let fableSerialize (value: 'T) : string = failwith "ambient"

    module MediaOptimisation =

        module Server =

            let loadDataTool: AIToolDefinition = failwith "ambient"

            let optimiseCurvesRoutine (req: MediaOptimisationRequest) : MediaOptimisationResult = failwith "ambient"

    // ── The client-resident-companion contract-pack binding ───────

    /// The companion's own allowlist policy, consulted by the authorizer
    /// the contract packs are bound against.
    type MyCompanyPolicy = { Allowlist: Set<string> }

    module MyPolicy =

        let allows (policy: MyCompanyPolicy) (toolName: string) (activeModule: string option) : bool =
            failwith "ambient"

    let myPolicy: MyCompanyPolicy = failwith "ambient"