module ToolUp.Forms.Tests.PublicFormUseLimitTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.EntityTypes
open ToolUp.Platform.Infra.IEntityStore
open ToolUp.Platform.Scope
open ToolUp.Platform.Testing
open ToolUp.Forms
open ToolUp.Forms.FormSchema
open ToolUp.Forms.FormSubmission
open ToolUp.Forms.FormApi
open ToolUp.Forms.PublicFormApi
open ToolUp.Forms.IFormStore
open ToolUp.Forms.FormStore
open ToolUp.Forms.FormApiHandler
open ToolUp.Forms.FormsCompose
open ToolUp.Forms.Tests.InProcess.InMemoryEntityStore
open ToolUp.Forms.Tests.InProcess.InMemoryAuditLog

// ─── A public form link is used as many times as it says ────────────
//
// `SubmitWithToken` reserves the link's use before it stores the
// submission, so concurrent submissions on a single-use link store
// exactly one. A submission that fails to store returns the use; if the
// return itself fails, the use stays spent and the link fails closed.
// Issued links carry a default per-window rate limit.

let private scope = "survey-scope"
let private schemaId = "survey"

let private silentLogger: ILogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

let private publishable: FormSchema = {
    FormSchema.create schemaId "Survey" [] with
        Visibility = Publishable
}

let private newShareTokenStore () : IShareTokenStore =
    ShareTokenStore.create
        (Fakes.standardBlobStorage ())
        (Fakes.standardSecretStore ())
        (InMemoryAuditLog() :> IAuditLog)
        silentLogger

/// A form store with a publishable schema in `scope`.
let private newFormStore () : IFormStore =
    let store = FormStore(InMemoryEntityStore() :> IEntityStore) :> IFormStore

    match
        store.SaveSchema(scope, EntityPrincipal.ofPrincipal "form-admin", publishable)
        |> Async.RunSynchronously
    with
    | Ok _ -> store
    | Error err -> failtestf "SaveSchema: %A" err

/// Counts the submissions stored, and fails `SaveSubmission` while `failing` is set.
type private CountingFormStore(inner: IFormStore) =
    let mutable stored = 0
    member val Failing = false with get, set
    member _.Stored = stored

    interface IFormStore with
        member _.SaveSchema(s, p, schema) = inner.SaveSchema(s, p, schema)
        member _.GetSchema(s, id, v) = inner.GetSchema(s, id, v)
        member _.ListSchemas s = inner.ListSchemas s
        member _.DeleteSchema(s, p, id) = inner.DeleteSchema(s, p, id)

        member this.SaveSubmission(s, submission) = async {
            if this.Failing then
                return Error(FormError.StorageFailed "the submission could not be written")
            else
                let! result = inner.SaveSubmission(s, submission)

                if Result.isOk result then
                    Interlocked.Increment(&stored) |> ignore

                return result
        }

        member _.GetSubmission(s, id) = inner.GetSubmission(s, id)
        member _.ListSubmissions(s, q) = inner.ListSubmissions(s, q)
        member _.DeleteSubmission(s, p, id) = inner.DeleteSubmission(s, p, id)

/// Holds every `Validate` until `expected` have been asked (or a bounded
/// wait lapses), so every submitter sees the link unspent before any of
/// them goes further. A handler that stores before it reserves stores all
/// of them; one that reserves first stores the limit.
type private HoldValidations(inner: IShareTokenStore, expected: int) =
    let mutable asked = 0
    let allAsked = TaskCompletionSource<unit>()

    interface IShareTokenStore with
        member _.Issue req = inner.Issue req

        member _.Validate token = async {
            let! result = inner.Validate token

            if Interlocked.Increment(&asked) >= expected then
                allAsked.TrySetResult() |> ignore

            let! _ =
                Task.WhenAny(allAsked.Task, Task.Delay(TimeSpan.FromSeconds 10.0))
                |> Async.AwaitTask

            return result
        }

        member _.MarkUsed(s, t) = inner.MarkUsed(s, t)
        member _.ReleaseUse(s, t) = inner.ReleaseUse(s, t)
        member _.Revoke(s, t, a) = inner.Revoke(s, t, a)
        member _.ListByResource(s, k, r) = inner.ListByResource(s, k, r)
        member _.ListByIssuer(s, i) = inner.ListByIssuer(s, i)

/// A store whose `ReleaseUse` always fails.
type private FailingRelease(inner: IShareTokenStore) =
    interface IShareTokenStore with
        member _.Issue req = inner.Issue req
        member _.Validate token = inner.Validate token
        member _.MarkUsed(s, t) = inner.MarkUsed(s, t)

        member _.ReleaseUse(_, _) = async {
            return Error(ShareTokenError.StorageFailed "the claim could not be written")
        }

        member _.Revoke(s, t, a) = inner.Revoke(s, t, a)
        member _.ListByResource(s, k, r) = inner.ListByResource(s, k, r)
        member _.ListByIssuer(s, i) = inner.ListByIssuer(s, i)

let private issue (store: IShareTokenStore) (useLimit: int option) : ShareToken =
    let request: ShareTokenIssueRequest = {
        ScopeId = scope
        ResourceKind = ResourceKind
        ResourceId = schemaId
        AttributedHandle = Some "respondent@example.test"
        IssuedBy = "form-admin"
        ExpiresAt = None
        UseLimit = Some useLimit
        RateLimit = None
    }

    match store.Issue request |> Async.RunSynchronously with
    | Ok issued -> issued
    | Error err -> failtestf "Issue: %A" err

let private publicApi (tokens: IShareTokenStore) (forms: IFormStore) : IPublicFormApi =
    PublicFormApiHandler.publicFormApi
        tokens
        forms
        FormValidator.emptyRegistry
        (InMemoryAuditLog() :> IAuditLog)
        (InMemoryShareTokenRateLimiter.create ())
        silentLogger
        (DefaultHttpContext())

let private submit (api: IPublicFormApi) (token: string) =
    api.SubmitWithToken { Token = token; Values = Map.empty }

let private usedCount (tokens: IShareTokenStore) =
    match tokens.ListByResource(scope, ResourceKind, schemaId) |> Async.RunSynchronously with
    | [ claim ] -> claim.UsedCount
    | other -> failtestf "expected one claim, got %d" other.Length

let tests =
    testList "Public form links — the use is reserved before the submission is stored" [

        testCaseAsync "fifty concurrent submissions on a single-use link store exactly one"
        <| async {
            let inner = newShareTokenStore ()
            let tokens = HoldValidations(inner, 50) :> IShareTokenStore
            let forms = CountingFormStore(newFormStore ())
            let issued = issue inner (Some 1)
            let api = publicApi tokens forms

            let! outcomes = List.init 50 (fun _ -> submit api issued.Token) |> Async.Parallel

            let accepted = outcomes |> Array.filter Result.isOk |> Array.length
            Expect.equal accepted 1 "one submission is accepted"
            Expect.equal forms.Stored 1 "one submission is stored"
            Expect.equal (usedCount inner) 1 "one use is spent"

            for outcome in outcomes do
                match outcome with
                | Ok _
                | Error(FormError.NotFound("token", "use-limit-exceeded")) -> ()
                | Error other -> failtestf "a refused submission is use-limit-exceeded, got %A" other
        }

        testCaseAsync "a submission that fails to store returns the use"
        <| async {
            let tokens = newShareTokenStore ()
            let forms = CountingFormStore(newFormStore ())
            let issued = issue tokens (Some 1)
            let api = publicApi tokens forms

            forms.Failing <- true

            match! submit api issued.Token with
            | Error(FormError.StorageFailed _) -> ()
            | other -> failtestf "the failed store is reported, got %A" other

            Expect.equal (usedCount tokens) 0 "the use is returned"

            forms.Failing <- false
            let! retried = submit api issued.Token
            Expect.isOk retried "the respondent submits again on the same link"
            Expect.equal forms.Stored 1 "the retry is stored"
            Expect.equal (usedCount tokens) 1 "the retry spends the use"
        }

        testCaseAsync "when the use cannot be returned it stays spent and the link fails closed"
        <| async {
            let inner = newShareTokenStore ()
            let tokens = FailingRelease(inner) :> IShareTokenStore
            let forms = CountingFormStore(newFormStore ())
            let issued = issue inner (Some 1)
            let api = publicApi tokens forms

            forms.Failing <- true

            match! submit api issued.Token with
            | Error(FormError.StorageFailed _) -> ()
            | other -> failtestf "the failed store is reported, got %A" other

            Expect.equal (usedCount inner) 1 "the use stays spent"

            forms.Failing <- false

            match! submit api issued.Token with
            | Error(FormError.NotFound("token", "use-limit-exceeded")) -> ()
            | other -> failtestf "the link fails closed, got %A" other

            Expect.equal forms.Stored 0 "nothing is stored"
        }

        testCaseAsync "a returned use on a three-use link is reserved again, never twice"
        <| async {
            let tokens = newShareTokenStore ()
            let forms = CountingFormStore(newFormStore ())
            let issued = issue tokens (Some 3)
            let api = publicApi tokens forms

            forms.Failing <- true
            let! _ = submit api issued.Token
            forms.Failing <- false

            let! outcomes = List.init 12 (fun _ -> submit api issued.Token) |> Async.Parallel

            Expect.equal (outcomes |> Array.filter Result.isOk |> Array.length) 3 "the limit holds"
            Expect.equal forms.Stored 3 "three submissions are stored"
            Expect.equal (usedCount tokens) 3 "three uses are spent"
        }

        testList "issued links carry a default rate limit" [

            testCase "the default is 30 uses per rolling minute, and it passes issue-time validation"
            <| fun () ->
                let expected: ShareTokenRateLimit = {
                    MaxUses = 30
                    Window = TimeSpan.FromMinutes 1.0
                }

                Expect.equal FormsOptions.defaultPublicLinkRateLimit expected "the stated default"

                Expect.equal
                    (FormsOptions.create ()).PublicLinkRateLimit
                    (Some expected)
                    "a deployment that says nothing gets the default"

                Expect.isOk (ShareTokenTypes.validateRateLimit (Some expected)) "valid at issue"

            testCase "a deployment replaces the default, or opts out"
            <| fun () ->
                let rate: ShareTokenRateLimit = {
                    MaxUses = 5
                    Window = TimeSpan.FromMinutes 10.0
                }

                let options = FormsOptions.create ()

                Expect.equal
                    (FormsOptions.withPublicLinkRateLimit (Some rate) options).PublicLinkRateLimit
                    (Some rate)
                    "the deployment's rate"

                Expect.equal
                    (FormsOptions.withPublicLinkRateLimit None options).PublicLinkRateLimit
                    None
                    "no per-window limit"

            testCaseAsync "IssueTokens stamps the default on every link"
            <| async {
                let tokens = newShareTokenStore ()
                let forms = newFormStore ()
                let ctx = DefaultHttpContext()
                AccessContexts.stampUserId ctx.Items scope
                AccessContexts.stampSubject ctx.Items (Subject.AuthenticatedUser scope)

                let api =
                    formApi
                        forms
                        Unchecked.defaultof<_>
                        FormValidator.emptyRegistry
                        (InMemoryAuditLog() :> IAuditLog)
                        Map.empty
                        (Some tokens)
                        (Some "https://forms.example.test")
                        None
                        None
                        AnalyserCache.noOpCache
                        []
                        (FormsOptions.create ()).PublicLinkRateLimit
                        ctx

                let! issued =
                    api.IssueTokens {
                        SchemaId = schemaId
                        Recipients = [
                            {
                                Handle = "respondent@example.test"
                                DisplayName = None
                            }
                        ]
                        ExpiresAt = None
                        UseLimit = None
                    }

                match issued with
                | Ok [ _ ] -> ()
                | other -> failtestf "IssueTokens: %A" other

                let! claims = tokens.ListByResource(scope, ResourceKind, schemaId)

                Expect.equal
                    (claims |> List.map _.RateLimit)
                    [ Some FormsOptions.defaultPublicLinkRateLimit ]
                    "the issued link carries the default"
            }
        ]
    ]