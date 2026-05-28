module KnowledgeBase.ServerApiAIContext

open System
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open SharedTypes
open KnowledgeBase.ServerJsonHelpers
open KnowledgeBase.ServerAIContext
open KnowledgeBase.ServerApiDeps

let getAIContext (deps: KnowledgeApiDeps) : Async<AIContextEntry option> = async {
    if AccessContext.isAnonymous deps.AccessContext then
        return None
    else
        return! loadAIContext deps.Storage deps.Scope.Container
}

let setAIContext (deps: KnowledgeApiDeps) (body: string) : Async<Result<AIContextEntry, string>> = async {
    if AccessContext.isAnonymous deps.AccessContext then
        return Error "Standing AI context is not available in Anonymous mode."
    else
        let! gate = deps.EnsureContextWriteAllowed()

        match gate with
        | Error msg -> return Error msg
        | Ok() ->
            let entry: AIContextEntry = {
                Body = body
                UpdatedAt = DateTimeOffset.UtcNow
                UpdatedBy = deps.UserId
            }

            do! saveAIContext deps.Storage deps.Scope.Container entry

            // Audit. Stored under the same scope so the team's
            // event log shows context churn alongside other team
            // activity. Length-only payload — full body lives in
            // the blob; the audit log records the change, not
            // the contents.
            match deps.EventStore with
            | Some es ->
                try
                    let payload =
                        toJson {|
                            UpdatedBy = deps.UserId
                            BodyLength = body.Length
                            Cleared = String.IsNullOrWhiteSpace body
                        |}

                    let evt =
                        Events.create deps.Scope.ScopeId "KnowledgeBase" "AIContextUpdated" payload

                    do! es.Write evt
                with ex ->
                    deps.Logger.Warn(sprintf "[KnowledgeBase] AIContextUpdated event write failed: %s" ex.Message)
            | None -> ()

            do! deps.PublishInventory()
            return Ok entry
}

let refreshAIContext (deps: KnowledgeApiDeps) : Async<unit> = async {
    // Manual trigger for the inventory snapshot. Inventory is
    // published automatically on every mutation; this endpoint
    // exists so a UI button can force a fresh push without
    // requiring a no-op write.
    do! deps.PublishInventory()
}