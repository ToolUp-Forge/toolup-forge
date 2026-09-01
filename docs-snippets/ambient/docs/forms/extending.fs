// Ambient context for `docs/forms/extending.md`.
//
// The page teaches extension points, so each block is an excerpt from a
// consumer's own code: the validator / guard / action it declared two
// blocks earlier and is now registering, the domain services those
// resolve out of `ctx.Services`, the durable ledger and the distributed
// rate limiter a deployment supplies. None of them are SDK types —
// they are exactly what the page's own program provides — so they are
// declared once here rather than re-shown in every block.
open ToolUp.Platform.AI
open ToolUp.Forms.FormValidator
open ToolUp.Forms.IWorkflowEngine
open ToolUp.Forms.IActionLedger

[<AutoOpen>]
module PageAmbient =

    /// A domain service a guard resolves per invocation from
    /// `ctx.Services` — deliberately not an SDK interface.
    type ICreditCheckApi =
        abstract GetCreditScore: companyName: string -> Async<float>

    /// The consumer's own mail service. Email delivery is NOT an SDK
    /// seam; it rides `INotificationChannel` / `INotificationSink` or a
    /// service of the deployment's own, registered in DI.
    type IMyEmailService =
        abstract Send: string -> string -> string -> Async<unit>

    /// A durable ledger a deployment wires in place of the in-memory
    /// default. The page's own skeleton shows the shape; this stands in
    /// for it where a later block only registers the thing.
    type PostgresActionLedger(connectionString: string) =
        interface IActionLedger with
            member _.Record entry = failwith "ambient"
            member _.Lookup(submissionId, transitionId, actionName) = failwith "ambient"
            member _.MarkSucceeded(submissionId, transitionId, actionName) = failwith "ambient"
            member _.MarkFailed(submissionId, transitionId, actionName, reason) = failwith "ambient"

    /// A distributed rate limiter shipped by someone other than us — the
    /// shape `withShareTokenRateLimiter` and the conformance pack are
    /// both written against.
    module MyCompany =
        type RedisShareTokenRateLimiter(connectionString: string) =
            interface IShareTokenRateLimiter with
                member _.Admit(scopeId, tokenId, rate) = failwith "ambient"
                member _.IsDistributed = true

    /// The custom validator declared in the page's first block.
    let blockListValidator: CustomValidator = failwith "ambient"

    /// The two guards declared under "Workflow guards".
    let hasProposalAttached: WorkflowGuard = failwith "ambient"

    let creditCheckPassed: WorkflowGuard = failwith "ambient"

    /// The two actions declared under "Workflow actions".
    let sendWelcomeEmail: WorkflowAction = failwith "ambient"

    let kickoffOnboardingJob: WorkflowAction = failwith "ambient"

    /// The body an action renders for its welcome mail.
    let welcomeEmailBody (submission: Submission) : string = failwith "ambient"

    /// Whatever the analyser actually asks the model — the page is about
    /// the seam, not about the prompt.
    let summariseSentiment (provider: IAIProvider) (texts: string list) : Async<string> = failwith "ambient"

    /// Connection strings the deployment already holds.
    let connStr: string = failwith "ambient"

    let redisConnString: string = failwith "ambient"

    let testConnString: string = failwith "ambient"