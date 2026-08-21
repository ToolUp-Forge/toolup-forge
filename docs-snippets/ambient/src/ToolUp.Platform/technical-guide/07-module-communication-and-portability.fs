// Ambient context for `src/ToolUp.Platform/technical-guide/07-module-communication-and-portability.md`.
//
// The typed-handler example speaks for an analytics module the page never
// shows: its cross-module request / response contract and the store the
// handler reads. Both are the reader's own types — GP 10 puts them in a
// shared-types project, not in the SDK — so they are declared here.

[<AutoOpen>]
module PageAmbient =

    type LatestAnalysisReq = { DatasetId: string }

    type LatestAnalysisResp = {
        Summary: string
        ComputedAt: DateTime
    }

    type AnalysisRecord = {
        Summary: string
        ComputedAt: DateTime
    }

    type AnalysisStore =
        abstract LoadLatest: teamId: string option * datasetId: string -> Async<AnalysisRecord>

    let store: AnalysisStore = failwith "ambient"