namespace ToolUp.AI

open System
open System.Text.Json
open System.Threading
open Microsoft.AspNetCore.Http
open ToolUp.Platform

/// The caller an AI door serves, read from the principal `ScopeResolutionMiddleware`
/// stamped once (`AccessContext.ItemsKey`). The one typed read of who is asking and
/// in which scope: every AI handler and tool reads it here, and a request with no
/// resolved principal refuses instead of falling back to a scope of its own.
[<RequireQualifiedAccess>]
module AIRequest =
    /// The principal the request's scoped factory yields, or `None`.
    let private ofServices (ctx: HttpContext) : AccessContext option =
        AccessContext.tryResolve ctx.RequestServices

    /// The request's principal: the scoped DI value when the factory ran, else
    /// the one stamped on its items (a background context carries it there).
    /// `None` when neither holds one.
    let access (ctx: HttpContext) : AccessContext option =
        match ofServices ctx with
        | Some access -> Some access
        | None -> AccessContext.ofItems ctx.Items

    /// The request's principal, or `AccessContext.unresolved` — which grants
    /// nothing — when none was resolved. `site` names the door that serves it.
    let accessOrUnresolved (site: string) (ctx: HttpContext) : AccessContext =
        access ctx |> Option.defaultWith (fun () -> AccessContext.unresolved site)

    /// The storage scope a principal's AI reads and writes land in: the scope
    /// its resolution derived, else its own user container (a principal the
    /// deployment placed in no shared scope still has its own). There is no
    /// scope for a request with no principal: the caller refuses.
    let scopeOf (access: AccessContext) : StorageScope =
        match access.StorageScope with
        | Some scope -> scope
        | None -> {
            ScopeId = access.UserId
            Container = ScopeKey.container (ScopeKey.User(UserId access.UserId))
            Persist = true
          }

    /// The principal `ScopeResolutionMiddleware` stamped on the request's
    /// items, else the scoped DI value: the source of the request's identity
    /// and scope. (`access` reads the DI value first, the source of its
    /// authority; in a composed request the two are one principal.)
    let principal (ctx: HttpContext) : AccessContext option =
        match AccessContext.ofItems ctx.Items with
        | Some stamped -> Some stamped
        | None -> ofServices ctx

    /// The storage scope of the request's principal (`scopeOf`), or `None`
    /// when no principal was resolved.
    let scope (ctx: HttpContext) : StorageScope option = principal ctx |> Option.map scopeOf

    /// The request principal's identity, or `None` when none was resolved.
    let callerId (ctx: HttpContext) : string option = principal ctx |> Option.map _.UserId

    /// Carry `request`'s principal, its grant and consent verdicts and its
    /// resolved scope onto `background`, a context with its own services that
    /// outlives the request (the agent loop's). Each is read through its typed
    /// accessor and written once; nothing is re-derived and nothing minted: a
    /// request with no principal carries none, and is served unresolved.
    let carry (request: HttpContext) (background: HttpContext) : unit =
        AccessContext.ofItems request.Items
        |> Option.iter (AccessContext.stamp background.Items)

        background.Items[ToolUp.Platform.Scope.GrantPolicyGuard.ModuleGrantsItemsKey] <-
            box (ToolUp.Platform.Scope.GrantPolicyGuard.grantsFromItems request.Items)

        background.Items[ToolUp.Platform.Scope.GrantConsentStore.ModuleGrantConsentsItemsKey] <-
            box (ToolUp.Platform.Scope.GrantConsentStore.consentVerdictsFromItems request.Items)

        ToolUp.Platform.Scope.StorageScopeResolver.ScopeResolution.carry
            background
            (ToolUp.Platform.Scope.StorageScopeResolver.ScopeResolution.forRequest request)

/// The stream a tool call can ask its user on: the turn's conversation, its
/// task and its emitter. Present inside a live turn; absent for a call that is
/// not one (a probe, a contract pack), where a prompting gate has no one to ask.
type PromptChannel = {
    ConversationId: Guid
    TaskId: Guid
    Emit: AIStreamEvent -> unit
}

/// The agent loop's SSE emitter, wrapped in a nominal type so the request-items
/// carry holds a value of its own type rather than a bare function.
type StreamEmitter = { Emit: AIStreamEvent -> unit }

/// What a built-in tool's typed executor runs with: the request, the caller,
/// the caller's scope and the turn's prompt channel, read once by the loop.
type ToolContext = {
    /// The request the turn runs in, for its services.
    Request: HttpContext
    /// The caller, as the turn's gates judged it.
    Access: AccessContext
    /// The scope the caller's reads and writes land in (`AIRequest.scopeOf`).
    Scope: StorageScope
    /// The turn's prompt channel, when the call is part of a live turn.
    Channel: PromptChannel option
    /// The module and page the user was on.
    ActiveModule: string option
    ActivePage: string option
    /// The turn's cancellation.
    Cancel: CancellationToken
}

/// A tool call's arguments: the JSON the model wrote, parsed once.
type ToolArgs = {
    /// The arguments as the model wrote them.
    Json: string
    /// The parsed arguments object.
    Root: JsonElement
}

module ToolArgs =
    /// Parse `json` into arguments. An empty body is the empty object; a body
    /// that is not a JSON object is a refusal naming what it was.
    let parse (json: string) : Result<ToolArgs, string> =
        let text = if String.IsNullOrWhiteSpace json then "{}" else json

        try
            use doc = JsonDocument.Parse text

            if doc.RootElement.ValueKind = JsonValueKind.Object then
                Ok {
                    Json = text
                    Root = doc.RootElement.Clone()
                }
            else
                Error $"arguments must be a JSON object, not {doc.RootElement.ValueKind}"
        with ex ->
            Error $"arguments were not valid JSON: {ex.Message}"

/// Building a `ToolContext`, and the request-items carry a consumer's string
/// executor still reads.
module ToolContext =
    /// `HttpContext.Items` keys of the consumer compatibility carry. A consumer
    /// tool written against `HttpContext -> string -> Async<string>` reaches the
    /// turn's conversation and emitter only through these; the loop stamps them
    /// for such a tool (`stamp`) and reads nothing back from them itself.
    module ItemsKeys =
        /// `Guid` — the conversation the current turn belongs to.
        [<Literal>]
        let ConversationId = "ToolUp.AI.ConversationId"

        /// `Guid` — the `AITask` the current turn belongs to.
        [<Literal>]
        let TaskId = "ToolUp.AI.TaskId"

        /// `StreamEmitter` — the turn's emitter.
        [<Literal>]
        let StreamEmitter = "ToolUp.AI.StreamEmitter"

        /// `string` — the module the user was on when they asked.
        [<Literal>]
        let ActiveModule = "ToolUp.AI.ActiveModule"

    let private guidItem (ctx: HttpContext) (key: string) : Guid option =
        match ctx.Items.TryGetValue key with
        | true, (:? Guid as value) -> Some value
        | _ -> None

    /// The prompt channel a request carries on its items, or `None`.
    let channelOfItems (ctx: HttpContext) : PromptChannel option =
        match guidItem ctx ItemsKeys.ConversationId, ctx.Items.TryGetValue ItemsKeys.StreamEmitter with
        | Some conversationId, (true, (:? StreamEmitter as emitter)) ->
            Some {
                ConversationId = conversationId
                TaskId = guidItem ctx ItemsKeys.TaskId |> Option.defaultValue Guid.Empty
                Emit = emitter.Emit
            }
        | _ -> None

    /// The context a string executor's request stands for: the principal
    /// stamped on it (or the unresolved one), its scope, and the carry.
    let ofRequest (ctx: HttpContext) : ToolContext =
        let access =
            AIRequest.principal ctx
            |> Option.defaultWith (fun () -> AccessContext.unresolved "ToolContext")

        {
            Request = ctx
            Access = access
            Scope = AIRequest.scopeOf access
            Channel = channelOfItems ctx
            ActiveModule =
                match ctx.Items.TryGetValue ItemsKeys.ActiveModule with
                | true, (:? string as m) when not (String.IsNullOrWhiteSpace m) -> Some m
                | _ -> None
            ActivePage = None
            Cancel = CancellationToken.None
        }

    /// Stamp `tool`'s channel and active module onto its request's items, for
    /// a consumer executor that reads them there. The one write of the carry.
    let stamp (tool: ToolContext) : unit =
        let items = tool.Request.Items

        match tool.Channel with
        | Some channel ->
            items[ItemsKeys.ConversationId] <- box channel.ConversationId
            items[ItemsKeys.TaskId] <- box channel.TaskId
            items[ItemsKeys.StreamEmitter] <- box ({ Emit = channel.Emit }: StreamEmitter)
        | None -> ()

        tool.ActiveModule
        |> Option.iter (fun m -> items[ItemsKeys.ActiveModule] <- box m)

    /// Publish a `Notification.ModuleAction` to the caller's per-user
    /// notification stream: the client shell's `ModuleActionReceived` handler
    /// gates on `AccessibleModules`, decodes with the target module's
    /// `ActionDecoder` and dispatches the `Msg`. The tool's text result still
    /// reaches the chat; this is the client-action half beside it.
    ///
    /// `moduleId` is the target module's `Definition.Id`; `actionKey` the
    /// discriminator its decoder matches (declared on
    /// `AIToolDefinition.EmitsActions`); `payloadJson` the serialised payload.
    /// Best-effort: no channel composed, or no resolved caller, is a no-op, and a transport failure is swallowed.
    let emitAction (ctx: HttpContext) (moduleId: string) (actionKey: string) (payloadJson: string) : Async<unit> = async {
        match ctx.RequestServices.GetService(typeof<INotificationChannel>), AIRequest.principal ctx with
        | :? INotificationChannel as channel, Some access when not (String.IsNullOrEmpty access.UserId) ->
            try
                do! channel.Publish(access.UserId, Notification.ModuleAction(moduleId, actionKey, payloadJson))
            with _ ->
                ()
        | _ -> ()
    }