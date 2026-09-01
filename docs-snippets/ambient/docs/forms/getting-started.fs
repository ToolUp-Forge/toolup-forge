// Ambient context for `docs/forms/getting-started.md`.
//
// The page is a walkthrough, so its blocks are excerpts from four
// different files of one consumer app: the module's `SharedTypes.fs`
// (the schema + the workflow the later blocks reference by name), the
// server composition root (the auth provider and module list it already
// holds), and the client's `ClientModel.fs` / `ClientView.fs` (the
// Elmish `Model` / `Msg` whose `update` arms the submit and transition
// blocks are). None of those are SDK types; they are what the page's own
// program provides, declared once here so each block compiles exactly as
// a reader would copy it.
open ToolUp.Elmish
open ToolUp.Forms.Workflow
open ToolUp.Forms.FormApi

[<AutoOpen>]
module PageAmbient =

    /// The client module's Elmish messages. The submit and transition
    /// blocks are single arms of its `update`.
    type Msg =
        | SubmitLead of Map<string, FieldValue>
        | SubmissionSucceeded of Submission
        | SubmissionFailed of FormError
        | TransitionWorkflow of SubmissionId * TransitionEvent
        | TransitionSucceeded of Submission
        | TransitionFailed of FormError

    /// The client module's Elmish model.
    type Model = { Submissions: Submission list }

    /// The schema declared in step 2, as the later blocks reach it —
    /// through the module it was declared in.
    module MyModule =
        module SharedTypes =
            let leadCaptureSchema: FormSchema = failwith "ambient"

    /// The workflow declared in step 3.
    let leadWorkflow: WorkflowDefinition = failwith "ambient"

    /// The publishable survey schema declared in step 8.
    let npsSchema: FormSchema = failwith "ambient"

    /// What the composition root already holds by the time it reaches
    /// `FormsServerApp`.
    let authProvider: IAuthProvider = failwith "ambient"

    let modules: ServerModule list = failwith "ambient"

    /// The forms API a server-side caller issues tokens through.
    let formsApi: IFormApi = failwith "ambient"

    /// The `update` arm's own inputs.
    let model: Model = failwith "ambient"

    let msg: Msg = failwith "ambient"