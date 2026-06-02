module ToolUp.AI.ToolContext

open System
open Microsoft.AspNetCore.Http
open ToolUp.Platform

/// Helpers available to AI tool executors — the `HttpContext -> argsJson -> Async<resultJson>`
/// functions registered under `AIToolRegistry`. Tools take their JSON
/// result through the agent loop; this module gives them side channels
/// that don't travel through the model (module-targeted actions,
/// structured notifications, …).
///
/// All helpers are best-effort: if the backing infrastructure (notification
/// channel, resolved user id) is absent the helper silently no-ops rather
/// than raising. Tool authors treat them as "try to notify the client;
/// if we can't, the JSON return is still the source of truth".
/// Publish a `Notification.ModuleAction` to the caller's per-user
/// notification stream. The shell's `ModuleActionReceived` handler on
/// the client picks it up, gates on `AccessibleModules`, calls the
/// target module's `ActionDecoder`, and dispatches the decoded `Msg`
/// against that module's state.
///
/// The tool's text result still flows through the agent loop in the
/// chat — `emitAction` is the Level 2 (client-action) half of the
/// two-tier pattern, not a replacement for the Level 1 (chat-only)
/// return value. Tools that also want the user to see the numbers as
/// text simply include them in the returned JSON.
///
/// Arguments:
///   ctx          — the tool's `HttpContext`, used to resolve the
///                  `INotificationChannel` from DI and the authenticated
///                  `userId` from `ctx.Items["ToolUp.UserId"]`
///                  (populated by `ScopeResolutionMiddleware` and
///                  copied into the background context by
///                  `AIAssistantHandler.createBackgroundContext`).
///   moduleId     — target module's `Definition.Id`. Must match the
///                  key the client shell routes against.
///   actionKey    — module-owned discriminator the module's
///                  `ActionDecoder` matches on (e.g.
///                  `"apply-optimised-budget"`). Declared on
///                  `AIToolDefinition.EmitsActions` for discoverability.
///   payloadJson  — already-serialised JSON string the decoder parses.
///                  Using a string (not `obj`) avoids the bare-STJ-vs-
///                  `FableConverters` DU pitfall — tool authors pick
///                  their own serialiser per payload.
///
/// Silently no-ops when the notification channel isn't registered in DI
/// (unit tests, pared-down test harnesses) or the user id is absent.
/// Handler exceptions are caught inside `INotificationChannel.Publish`
/// per its contract — this helper never surfaces a publish error to
/// the tool.
let emitAction (ctx: HttpContext) (moduleId: string) (actionKey: string) (payloadJson: string) : Async<unit> = async {
    let channelBox = ctx.RequestServices.GetService(typeof<INotificationChannel>)

    match channelBox with
    | :? INotificationChannel as channel ->
        match ctx.Items.TryGetValue "ToolUp.UserId" with
        | true, (:? string as userId) when not (String.IsNullOrEmpty userId) && userId <> "anonymous" ->
            try
                do! channel.Publish(userId, Notification.ModuleAction(moduleId, actionKey, payloadJson))
            with _ ->
                // Transport-layer publish failures are swallowed.
                // The tool's returned JSON still reaches the user
                // through the chat; a dropped side-channel event
                // is a UX quality issue, not a functional break.
                ()
        | _ -> ()
    | _ -> ()
}