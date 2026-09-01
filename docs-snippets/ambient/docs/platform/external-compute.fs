// Ambient context for `docs/platform/external-compute.md`.
//
// The page teaches a BROKER seam, so almost every block is an excerpt
// from something it never shows in full: the deployment's service
// collection at the point compose registers the dispatcher, the backend
// companion's own transport calls, the clean-room collaborators the
// gated-output pipeline is composed from, the running job whose
// `ctx.Progress` a handler reports through, and the page's own
// `MyComputeBackend` companion the conformance binding drives.
//
// Declared here so the blocks compile exactly as a reader would copy
// them, with no `open`-ceremony added to the markdown.
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform.BlobStorage
open ToolUp.InterPlatform

[<AutoOpen>]
module PageAmbient =

    /// The page's own backend companion's configuration — a placeholder for
    /// whatever a real companion's `create` takes.
    type MyComputeConfig = { Endpoint: string }

    /// The deployment's service collection, at the point compose is
    /// registering the seam.
    let services: IServiceCollection = failwith "ambient"

    /// The dispatcher a deployment composed, the raw companion beneath it,
    /// and the decorator stack's inner backend. Three names because the
    /// page's blocks are each written from a different vantage point.
    let myDispatcher: IExternalComputeDispatcher = failwith "ambient"

    let myBackend: IExternalComputeDispatcher = failwith "ambient"

    let backend: IExternalComputeDispatcher = failwith "ambient"

    let dispatcher: IExternalComputeDispatcher = failwith "ambient"

    /// The blob store `MemoizedComputeDispatcher` persists its memo into.
    let blobStorage: IBlobStorage = failwith "ambient"

    /// The tenant scope and the pre-serialised payload a submitting caller
    /// already holds.
    let scopeId: string = failwith "ambient"

    let payload: string = failwith "ambient"

    /// The spec / handle a gated-output pipeline is holding an answer for,
    /// and the bytes the isolated worker produced.
    let spec: ExternalWorkSpec = failwith "ambient"

    let handle: ExternalHandle = failwith "ambient"

    let workerOutput: string = failwith "ambient"

    /// The clean-room collaborators `GatedComputeDeps` is composed from.
    let broker: ICleanRoomBroker = failwith "ambient"

    let template: CleanRoomTemplate = failwith "ambient"

    let auditSink: PeerCleanRoomDecisionPayload -> Async<unit> = failwith "ambient"

    /// The three calls a backend companion's own transport makes, and the
    /// webhook registration its callback-capable arm performs.
    let submitToBackend
        (scopeId: string)
        (spec: ExternalWorkSpec)
        : Async<Result<ExternalHandle, ExternalComputeError>> =
        failwith "ambient"

    let pollBackend (handle: ExternalHandle) : Async<ExternalOutcome> = failwith "ambient"

    let cancelBackend (handle: ExternalHandle) : Async<unit> = failwith "ambient"

    let setWebhook (nativeRef: string) (callbackPath: string) (secret: string) : Async<unit> = failwith "ambient"

    /// The ed25519 arm a deployment composes from a crypto companion, since
    /// the BCL ships no Ed25519 primitive (GP 1).
    let myEd25519Verify (publicKey: string) (payload: string) (signature: string) : Result<unit, string> =
        failwith "ambient"

    /// The worker-key registry the signed-outcome policy is created against.
    let myWorkerKeyRegistry: IWorkerKeyRegistry = failwith "ambient"

    /// The running job a handler reports progress from. The scheduler
    /// synthesises this per dispatch; a handler never constructs one.
    let ctx: JobContext = failwith "ambient"

    let config: MyComputeConfig = failwith "ambient"

    /// The page's own dispatcher companion, as the conformance-binding
    /// block calls it. `forceStatus` is that companion's own way of
    /// driving a unit to an outcome — the `Drive` seam the pack needs.
    module MyComputeBackend =
        let create (config: MyComputeConfig) : IExternalComputeDispatcher = failwith "ambient"

        let forceStatus
            (backend: IExternalComputeDispatcher)
            (handle: ExternalHandle)
            (outcome: ExternalOutcome)
            : Async<unit> =
            failwith "ambient"