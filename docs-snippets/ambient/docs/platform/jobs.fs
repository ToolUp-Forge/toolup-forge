// Ambient context for `docs/platform/jobs.md`.
//
// The handler example reads the module's own payload DTO, its parser, and
// the unit of work it wraps — the three things a module author supplies
// and the SDK never does. The scheduling examples additionally read a
// composition root's `scheduler` singleton and the values a caller already
// holds when it submits a job (its resolved scope, the acting user, the
// pre-serialised payload). Declared here so the blocks compile as written;
// a block that declares its own `MyJobPayload` / `MyJobHandler` shadows
// this one, which is why they sit in an auto-opened module.

[<AutoOpen>]
module PageAmbient =

    /// The module's own payload DTO, serialised into `JobDefinition.Payload`.
    type MyJobPayload = { DatasourceId: string }

    /// Deserialise the persisted payload string. `Error` is terminal — a
    /// malformed payload does not recover on retry.
    let tryParsePayload (payload: string) : Result<MyJobPayload, string> = failwith "ambient"

    /// The unit of work, with its dependencies already captured.
    let doWork (input: MyJobPayload) : Async<unit> = failwith "ambient"

    /// The module's handler, as "Writing a job handler" declares it. That
    /// block redeclares the type; it is named here so the compose-root
    /// block that *registers* it compiles on its own.
    type MyJobHandler() =
        interface IJobHandler with
            member _.Execute(_ctx: JobContext) : Async<JobResult> = failwith "ambient"

    /// The scheduler singleton the composition root built.
    let scheduler: IJobScheduler = failwith "ambient"

    /// The caller's resolved scope. Every `IJobScheduler` method is
    /// scope-qualified — a `JobId` is only meaningful inside its scope.
    let scopeId: string = failwith "ambient"

    /// The acting principal, stamped into `CreatedBy` / `ScheduledManually`.
    let userId: string = failwith "ambient"

    /// The module's pre-serialised JSON payload string.
    let payloadJson: string = failwith "ambient"

    /// The id `Schedule` returned for the `Manual` job being fired.
    let myJobId: JobId = failwith "ambient"

    /// The daily-summary example's own inputs.
    let teamId: string = failwith "ambient"

    let serialiseTeamPayload (team: string) : string = failwith "ambient"

    /// The dispatch context a handler reports progress against.
    let ctx: JobContext = failwith "ambient"

    /// The data-ingestion remoting surface, and the source a triggered
    /// refresh names.
    let dataIngestionApi: IDataIngestionApi = failwith "ambient"

    let datasourceId: DataSourceId = failwith "ambient"