// Ambient context for `docs/platform/jobs.md`.
//
// The handler example reads the module's own payload DTO, its parser, and
// the unit of work it wraps — the three things a module author supplies
// and the SDK never does. Declared here so the block compiles as written;
// a block that declares its own `MyJobPayload` shadows this one, which is
// why they sit in an auto-opened module.

[<AutoOpen>]
module PageAmbient =

    /// The module's own payload DTO, serialised into `JobDefinition.Payload`.
    type MyJobPayload = { DatasourceId: string }

    /// Deserialise the persisted payload string. `Error` is terminal — a
    /// malformed payload does not recover on retry.
    let tryParsePayload (payload: string) : Result<MyJobPayload, string> = failwith "ambient"

    /// The unit of work, with its dependencies already captured.
    let doWork (input: MyJobPayload) : Async<unit> = failwith "ambient"