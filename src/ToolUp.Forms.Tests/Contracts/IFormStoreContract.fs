module ToolUp.Forms.Tests.Contracts.IFormStoreContract

open System
open Expecto
open ToolUp.Platform.EntityTypes
open ToolUp.Platform.EntityQueryTypes
open ToolUp.Platform.IEntityStore
open ToolUp.Platform.Tests.Support.PrincipalRecordingEntityStore
open ToolUp.Platform.EntityQuery
open ToolUp.Forms.FormSchema
open ToolUp.Forms.FormSubmission
open ToolUp.Forms.IFormStore

// ─── IFormStore contract pack ─────────────────────────────────────
//
// Framework-agnostic test pack: takes a factory that produces a
// fresh `(IFormStore, scopeId-A, scopeId-B)` triple per test and
// exercises the public contract.
//
// Default impl bound by `FormStoreTests.fs` over the test project's
// in-memory `IEntityStore` stub. Future distributed companions
// (Akka.NET / Orleans) would bind the same pack against their own
// factory and prove portability — same shape as
// `IBookingSchedulerContract` / `IEntityStoreContract` packs.

type StoreFactory = unit -> IFormStore * string * string

/// The caller the shape tests pass on every mutating member. The
/// shape tests do not observe it — `principalTests` does.
let private caller = EntityPrincipal.ofPrincipal "form-admin"

let private makeSchema (id: FormSchemaId) : FormSchema = {
    Id = id
    Type = FormSchema.entityType
    Version = 1
    DisplayName = id
    Description = None
    Fields = [
        {
            Key = "title"
            DisplayName = "Title"
            Description = None
            Kind = TextField None
            Required = true
            Validators = []
        }
    ]
    Visibility = Internal
}

let private makeSubmission (id: SubmissionId) (formId: FormSchemaId) (submittedBy: string) : Submission = {
    Id = id
    Type = Submission.entityType
    Version = 1
    FormId = formId
    SchemaVersion = 1
    SubmittedAt = DateTimeOffset.UtcNow
    Author = AuthenticatedUser submittedBy
    Values = Map [ "title", TextValue "test" ]
    State = Submitted
    WorkflowId = None
}

let tests (label: string) (factory: StoreFactory) =
    testList (sprintf "IFormStore contract — %s" label) [

        testAsync "SaveSchema then GetSchema returns the schema" {
            let store, scope, _ = factory ()
            let schema = makeSchema "f1"
            let! save = store.SaveSchema(scope, caller, schema)
            Expect.isOk save "save ok"

            let! got = store.GetSchema(scope, "f1", None)

            match got with
            | Ok s -> Expect.equal s.DisplayName "f1" "round-tripped"
            | Error e -> failwithf "expected schema, got %A" e
        }

        testAsync "SaveSchema bumps Version on second save with same Id" {
            let store, scope, _ = factory ()
            let schema = makeSchema "f1"
            let! first = store.SaveSchema(scope, caller, schema)
            let! second = store.SaveSchema(scope, caller, schema)

            match first, second with
            | Ok s1, Ok s2 ->
                Expect.equal s1.Version 1 "first is v1"
                Expect.equal s2.Version 2 "second is v2"
            | _ -> failwith "expected both saves to succeed"
        }

        testAsync "SaveSubmission then GetSubmission returns the submission" {
            let store, scope, _ = factory ()
            let submission = makeSubmission "s1" "f1" "user-a"
            let! save = store.SaveSubmission(scope, submission)
            Expect.isOk save "save ok"

            let! got = store.GetSubmission(scope, "s1")

            match got with
            | Ok s ->
                Expect.equal s.FormId "f1" "round-tripped FormId"
                Expect.equal s.Author (AuthenticatedUser "user-a") "round-tripped Author"
            | Error e -> failwithf "expected submission, got %A" e
        }

        testAsync "ListSubmissions filtered by FormId returns matches" {
            let store, scope, _ = factory ()
            do! store.SaveSubmission(scope, makeSubmission "s1" "form-A" "user") |> Async.Ignore
            do! store.SaveSubmission(scope, makeSubmission "s2" "form-A" "user") |> Async.Ignore
            do! store.SaveSubmission(scope, makeSubmission "s3" "form-B" "user") |> Async.Ignore

            let query =
                forType<Submission> Submission.entityType
                |> where (Eq(Submission.indexFormId, "form-A"))

            let! r = store.ListSubmissions(scope, query)

            match r with
            | Ok results ->
                Expect.equal results.Length 2 "two form-A submissions"
                Expect.all results (fun s -> s.FormId = "form-A") "all match form-A"
            | Error e -> failwithf "query failed: %A" e
        }

        testAsync "ListSubmissions filtered by Author returns matches" {
            let store, scope, _ = factory ()

            do!
                store.SaveSubmission(scope, makeSubmission "s1" "form-A" "alice")
                |> Async.Ignore

            do! store.SaveSubmission(scope, makeSubmission "s2" "form-A" "bob") |> Async.Ignore

            do!
                store.SaveSubmission(scope, makeSubmission "s3" "form-A" "alice")
                |> Async.Ignore

            let query =
                forType<Submission> Submission.entityType
                |> where (Eq(Submission.indexAuthor, SubmissionAuthor.indexValueForUser "alice"))

            let! r = store.ListSubmissions(scope, query)

            match r with
            | Ok results ->
                Expect.equal results.Length 2 "two alice submissions"
                Expect.all results (fun s -> s.Author = AuthenticatedUser "alice") "all match alice"
            | Error e -> failwithf "query failed: %A" e
        }

        testAsync "ListSubmissions filtered by State returns matches" {
            let store, scope, _ = factory ()

            let draftSub = {
                makeSubmission "s1" "form-A" "user" with
                    State = Draft
            }

            let submittedSub = makeSubmission "s2" "form-A" "user"
            do! store.SaveSubmission(scope, draftSub) |> Async.Ignore
            do! store.SaveSubmission(scope, submittedSub) |> Async.Ignore

            let query =
                forType<Submission> Submission.entityType
                |> where (Eq(Submission.indexState, "Draft"))

            let! r = store.ListSubmissions(scope, query)

            match r with
            | Ok results ->
                Expect.equal results.Length 1 "one draft submission"
                Expect.equal results[0].Id "s1" "matches draft id"
            | Error e -> failwithf "query failed: %A" e
        }

        testAsync "Scope isolation: scope A submissions invisible from scope B" {
            let store, scopeA, scopeB = factory ()

            do!
                store.SaveSubmission(scopeA, makeSubmission "s1" "form-A" "user")
                |> Async.Ignore

            let! gotFromB = store.GetSubmission(scopeB, "s1")
            Expect.isError gotFromB "scope B cannot read scope A's submission"

            let query = forType<Submission> Submission.entityType
            let! listFromB = store.ListSubmissions(scopeB, query)

            match listFromB with
            | Ok [] -> () // expected
            | Ok xs -> failwithf "expected empty, got %d" xs.Length
            | Error e -> failwithf "query failed: %A" e
        }

        testAsync "DeleteSubmission removes head; subsequent Get returns NotFound" {
            let store, scope, _ = factory ()
            do! store.SaveSubmission(scope, makeSubmission "s1" "form-A" "user") |> Async.Ignore

            let! del = store.DeleteSubmission(scope, caller, "s1")
            Expect.isOk del "delete ok"

            let! got = store.GetSubmission(scope, "s1")

            match got with
            | Error(FormError.NotFound _) -> ()
            | other -> failwithf "expected NotFound, got %A" other
        }

        testAsync "DeleteSchema is idempotent (deleting non-existent returns Ok)" {
            let store, scope, _ = factory ()
            let! r1 = store.DeleteSchema(scope, caller, "never-existed")
            Expect.isOk r1 "first delete idempotent"

            let! r2 = store.DeleteSchema(scope, caller, "never-existed")
            Expect.isOk r2 "second delete idempotent"
        }
    ]
// ─── Phase 814 — the principal on the seam call is the principal on the row ──
//
// `mkEntityStore` builds the `IEntityStore` the binding tests against;
// `mkStore` composes the `IFormStore` under test over an entity store
// the pack hands it. The pack decorates the entity store to observe
// what the seam passes down, so the claim is exactly the seam's: the
// caller on `SaveSchema` / `DeleteSchema` / `DeleteSubmission` reaches
// the store unchanged, and the host principal is never substituted.
let principalTests (label: string) (mkEntityStore: unit -> IEntityStore) (mkStore: IEntityStore -> IFormStore) =
    let setup () =
        let recording = PrincipalRecordingEntityStore(mkEntityStore ())
        let store = mkStore (recording :> IEntityStore)
        let scope = "team-814-" + Guid.NewGuid().ToString("N").Substring(0, 8)
        store, recording, scope

    testList (sprintf "IFormStore principal contract (Phase 814) — %s" label) [

        testAsync "the caller on SaveSchema and DeleteSchema is the principal the store receives" {
            let store, recording, scope = setup ()
            let admin = EntityPrincipal.ofPrincipal "alice"

            let delegated =
                EntityPrincipal.ofPrincipal "bob" |> EntityPrincipal.onBehalfOf "alice"

            let! saved = store.SaveSchema(scope, admin, makeSchema "f1")
            Expect.isOk saved "save ok"

            let! deleted = store.DeleteSchema(scope, delegated, "f1")
            Expect.isOk deleted "delete ok"

            Expect.equal
                (recording.Calls |> List.map (fun c -> c.Member, c.ScopeId, c.Principal))
                [ "Save", scope, admin; "Delete", scope, delegated ]
                "each seam call reaches the entity store with exactly the principal the caller passed"
        }

        testAsync "a submission deleted by its invited respondent carries the prefix-tagged respondent identity" {
            let store, recording, scope = setup ()

            let respondent = InvitedRespondent("tok-1", Some "invitee")

            let respondentPrincipal =
                EntityPrincipal.ofPrincipal (SubmissionAuthor.toIndexValue respondent)

            let submission = {
                makeSubmission "s1" "form-A" "unused" with
                    Author = respondent
            }

            do! store.SaveSubmission(scope, submission) |> Async.Ignore
            let! deleted = store.DeleteSubmission(scope, respondentPrincipal, "s1")
            Expect.isOk deleted "delete ok"

            Expect.equal
                recording.Principals
                [ respondentPrincipal; respondentPrincipal ]
                "the row for the creation (806) and the row for the deletion (814) name the same prefix-tagged respondent"

            Expect.isFalse
                (recording.Principals |> List.exists (fun p -> p = EntityPrincipal.system))
                "the store never substitutes the host principal for the caller"
        }
    ]