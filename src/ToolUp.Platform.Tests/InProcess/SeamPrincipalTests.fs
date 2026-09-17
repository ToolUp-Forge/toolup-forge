// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.SeamPrincipalTests

// ─── Phase 814 — the module seams carry their caller ────────────────
//
// Phase 806 put the principal on `IEntityStore`'s type, and every caller
// with a principal in reach passed it — but four module seams carried no
// caller at all, so nine call sites behind them passed a comment-marked
// `EntityPrincipal.system`: the row said "the host" for a form schema an
// admin deleted, a report template a team member saved, a booking
// resource an operator registered, a page an author published. Phase
// 814 threads the principal through those seams. Two of the four have a
// contract pack in their own test project (`IFormStoreContract`,
// `IBookingSchedulerContract`), and their principal cases live there.
// The other two are bound here: the narrative publisher against the real
// blob-backed store with a capturing audit log (the literal claim — the
// lifecycle row names the caller), and the report-template store against
// a recording decorator (its record cannot pass the real store's shape
// validation — see the note on that case).
//
// The third section is the guard that keeps the next identity-less seam
// from arriving silently: a source-reading test over `src/**/*.fs`, the
// shape `ConstantTimeCompareTests.implementationPinTests` uses, that
// refuses a new `EntityPrincipal.system` call site outside the declared
// set and refuses the type's pre-814 spelling anywhere. Its classifier
// is pure and pinned in both directions, so the guard is known to fire
// before it is trusted. (The old spelling is assembled at runtime below
// rather than written here, because this file is inside the scan.)

open System
open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.EntityTypes
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.IEntityStore
open ToolUp.Platform.DataObjectStore
open ToolUp.Platform.EntityStore
open ToolUp.Platform.Narrative
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage
open ToolUp.Platform.Tests.Contracts.IEntityStoreContract
open ToolUp.Platform.Tests.Support.PrincipalRecordingEntityStore
open ToolUp.PublicRendering
open ToolUp.Reporting
open ToolUp.Reporting.IReportTemplateStore

// ── Section A — the narrative publisher stamps the caller on the page row ──

let private mkAuditedPublisher () =
    let log = CapturingAuditLog()
    let blob = InMemoryBlobStorage() :> IBlobStorage
    let dos = DataObjectStore(blob) :> IDataObjectStore
    let registry = EntityRegistry()
    registry.Register<PublicPageEntity>(PublicPageEntity.registration)

    let store =
        BlobEntityStore(dos, blob, registry, Some(log :> IAuditLog)) :> IEntityStore

    let publisher =
        PublicRenderingNarrativePagePublisher.create
            store
            [ LayoutName "page" ]
            None
            NarrativePublishGuardrails.defaults
            None

    publisher, log

let private narrativeTests =
    testList "INarrativePagePublisher" [

        testCaseAsync "the publishing caller is the principal on the page's lifecycle row"
        <| async {
            let publisher, log = mkAuditedPublisher ()
            let author = EntityPrincipal.ofPrincipal "author-814"

            let! outcome =
                publisher.PublishAsync(
                    author,
                    "launch",
                    None,
                    None,
                    Some "page",
                    OverwriteExisting,
                    Narrative.create "Launch"
                )

            match outcome with
            | PublishSucceeded slug -> Expect.equal slug "launch" "published at the requested slug"
            | PublishFailed reason -> failtestf "expected the publish to succeed; got %s" reason

            let rows =
                log.Lifecycle PublicPageEntity.PublicScope
                |> List.map (fun (case, p) -> case, p.UserId, p.OnBehalfOf)

            Expect.equal rows [ "created", "author-814", None ] "one lifecycle row, naming the caller — never the host"
        }

        testCaseAsync "a delegated publish records the on-behalf-of subject on the row"
        <| async {
            let publisher, log = mkAuditedPublisher ()

            let editor =
                EntityPrincipal.ofPrincipal "editor" |> EntityPrincipal.onBehalfOf "author"

            let! _ =
                publisher.PublishAsync(
                    editor,
                    "note",
                    None,
                    None,
                    Some "page",
                    OverwriteExisting,
                    Narrative.create "Note"
                )

            let rows =
                log.Lifecycle PublicPageEntity.PublicScope
                |> List.map (fun (case, p) -> case, p.UserId, p.OnBehalfOf)

            Expect.equal rows [ "created", "editor", Some "author" ] "principal and delegation both reach the row"
        }
    ]

// ── Section B — the report-template store passes the caller through ──
//
// `ReportTemplate` carries no `Type` field, so the real store's shape
// validation (`tryGetEntityFields`) refuses it before any row is written
// — a standing defect of the reporting store against the blob-backed
// entity store, not something this phase introduces or fixes. The seam's
// own claim — the caller on `Save` / `Delete` reaches the entity store
// unchanged — is observed through the recording decorator over a stub
// that accepts every write.

/// An `IEntityStore` that accepts every write and holds nothing: the
/// smallest inner store the recording decorator can sit over.
type private AcceptingEntityStore() =
    interface IEntityStore with
        member _.Save<'T>(_, _, _: 'T) = async {
            return
                Ok {
                    Id = "t"
                    Type = "ReportTemplate"
                    Version = 1
                }
        }

        member _.SaveIfVersion<'T>(_, _, _: 'T, expected) = async {
            return
                Ok {
                    Id = "t"
                    Type = "ReportTemplate"
                    Version = expected + 1
                }
        }

        member _.Get<'T>(_, entityType, entityId) : Async<Result<'T, EntityError>> = async {
            return Error(NotFound(entityType, entityId))
        }

        member _.GetVersion<'T>(_, entityType, entityId, _) : Async<Result<'T, EntityError>> = async {
            return Error(NotFound(entityType, entityId))
        }

        member _.ListVersions<'T>(_, _, _) : Async<EntityRef<'T> list> = async { return [] }
        member _.Delete(_, _, _, _) = async { return Ok() }
        member _.DeleteIfVersion(_, _, _, _, _) = async { return Ok() }

        member _.FindByIndex<'T>(_, _, _, _) : Async<Result<EntityRef<'T> list, EntityError>> = async { return Ok [] }

        member _.Count(_, _) = async { return 0 }
        member _.ListAll<'T>(_, _, _, _) : Async<EntityRef<'T> list> = async { return [] }
        member _.Query<'T>(_, _) : Async<Result<'T list, EntityError>> = async { return Ok [] }

let private template: ReportTemplate = {
    Id = "quarterly"
    DisplayName = "Quarterly"
    Format = Markdown
    Body = Text.Encoding.UTF8.GetBytes "# {{title}}"
    Placeholders = []
    Version = 0
}

let private reportingTests =
    testList "IReportTemplateStore" [

        testCaseAsync "the caller on Save and Delete is the principal the entity store receives"
        <| async {
            let recording = PrincipalRecordingEntityStore(AcceptingEntityStore())
            let store = EntityBackedTemplateStore.create (recording :> IEntityStore)
            let member' = EntityPrincipal.ofPrincipal "member"

            let delegated =
                EntityPrincipal.ofPrincipal "admin" |> EntityPrincipal.onBehalfOf "member"

            let! saved = store.Save("team-a", member', template)
            Expect.isOk saved "save"

            let! deleted = store.Delete("team-a", delegated, "quarterly")
            Expect.isOk deleted "delete"

            Expect.equal
                (recording.Calls |> List.map (fun c -> c.Member, c.ScopeId, c.Principal))
                [ "Save", "team-a", member'; "Delete", "team-a", delegated ]
                "each seam call reaches the entity store with exactly the principal the caller passed"

            Expect.isFalse
                (recording.Principals |> List.exists (fun p -> p = EntityPrincipal.system))
                "the store never substitutes the host principal for the caller"
        }
    ]

// ── Section C — the guard: no new identity-less seam, no old spelling ──

/// Code only — everything from a `//` onward is dropped, so a doc
/// comment that NAMES `EntityPrincipal.system` (the interface docs say
/// "never substitutes it"; the sweep's says why it may pass it) is not
/// read as a call site. A `//` inside a string literal truncates that
/// line early, which can only hide a match on a line that also carries
/// the token — a shape no site has.
let private codeOnly (source: string) : string =
    source.Split('\n')
    |> Array.map (fun line ->
        match line.IndexOf "//" with
        | -1 -> line
        | i -> line.Substring(0, i))
    |> String.concat "\n"

/// The type's pre-814 name, assembled so this file does not itself carry
/// the token the guard refuses.
let private oldSpelling = "Entity" + "Actor"

/// A repo-relative `/`-separated path, the form the allow-list and the
/// failure messages use.
let private normalise (repoRelative: string) =
    repoRelative.Replace('\\', '/').TrimStart('/')

/// The files that may call `EntityPrincipal.system` in production code:
/// its definition, and the scheduled publish sweep — the one seam where
/// no caller exists to name (its comment says why). Test projects are
/// exempt: a pack passes the host principal deliberately, to prove the
/// row then says exactly that.
let private allowedSystemSites =
    set [
        "src/ToolUp.Platform.Core/Shared/EntityTypes.fs"
        "src/ContentAuthoring/Server/ContentLifecycle.fs"
    ]

let private isTestProject (path: string) =
    let p = normalise path

    p.Contains ".Tests/"
    || p.Contains "/Tests/"
    || p.StartsWith "src/ToolUp.Platform.Testing/"

/// The classifier, pure: `(repo-relative path, source)` pairs in, the
/// offending paths out. Two findings, reported separately because they
/// have different remedies.
let classify
    (files: (string * string) list)
    : {|
          SystemSites: string list
          OldSpelling: string list
      |}
    =
    let systemSites =
        files
        |> List.filter (fun (path, source) ->
            let p = normalise path

            not (isTestProject p)
            && not (Set.contains p allowedSystemSites)
            && (codeOnly source).Contains "EntityPrincipal.system")
        |> List.map (fst >> normalise)

    let oldSpelling =
        files
        |> List.filter (fun (_, source) -> source.Contains oldSpelling)
        |> List.map (fst >> normalise)

    {|
        SystemSites = systemSites
        OldSpelling = oldSpelling
    |}

let private repoRoot () =
    let assemblyDir =
        Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)

    Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

/// Every `.fs` under `src/`, outside build output.
let private sourceFiles () =
    let root = repoRoot ()
    let src = Path.Combine(root, "src")

    Directory.EnumerateFiles(src, "*.fs", SearchOption.AllDirectories)
    |> Seq.filter (fun p ->
        let n = p.Replace('\\', '/')
        not (n.Contains "/obj/" || n.Contains "/bin/"))
    |> Seq.map (fun p -> p.Substring(root.Length), File.ReadAllText p)
    |> List.ofSeq

let private classifierFixtures =
    let handler = "src/ToolUp.Forms.Server/Server/FormApiHandler.fs"

    testList "classifier" [
        test "a planted EntityPrincipal.system in a handler is reported" {
            let planted =
                "let save () = entityStore.Save<FormSchema>(scopeId, EntityPrincipal.system, schema)"

            let verdict = classify [ handler, planted ]
            Expect.equal verdict.SystemSites [ handler ] "the planted site is named"
        }

        test "the same token in a comment is not a call site" {
            let commented =
                "// a handler never passes EntityPrincipal.system\nlet save () = entityStore.Save<FormSchema>(scopeId, principal, schema)"

            let verdict = classify [ handler, commented ]
            Expect.isEmpty verdict.SystemSites "a comment naming the token is documentation, not a site"
        }

        test "the definition, the sweep and a test project are allowed" {
            let call = "let x = EntityPrincipal.system"

            let verdict =
                classify [
                    "src/ToolUp.Platform.Core/Shared/EntityTypes.fs",
                    "let system: EntityPrincipal = { Principal = SystemPrincipal; OnBehalfOf = None; Replay = None }"
                    "src/ContentAuthoring/Server/ContentLifecycle.fs", call
                    "src/ToolUp.Forms.Tests/Contracts/IFormStoreContract.fs", call
                ]

            Expect.isEmpty verdict.SystemSites "the declared set passes"
        }

        test "the old spelling is reported wherever it appears, comments and tests included" {
            let verdict =
                classify [
                    "src/ToolUp.Platform.Server/Server/IEntityStore.fs", "// see " + oldSpelling
                    "src/ToolUp.Forms.Tests/InProcess/FormStoreTests.fs",
                    "let a = " + oldSpelling + ".ofPrincipal \"x\""
                    "src/ToolUp.Forms.Server/Server/FormStore.fs", "let a = EntityPrincipal.ofPrincipal \"x\""
                ]

            Expect.equal
                verdict.OldSpelling
                [
                    "src/ToolUp.Platform.Server/Server/IEntityStore.fs"
                    "src/ToolUp.Forms.Tests/InProcess/FormStoreTests.fs"
                ]
                "both spellings of the old name are named; the new one is not"
        }
    ]

let private guardTests =
    testList "guard" [
        testCase "no EntityPrincipal.system call site outside the definition, the scheduled sweep and the test packs"
        <| fun _ ->
            let files = sourceFiles ()
            Expect.isGreaterThan (List.length files) 100 "sanity: the source scan found the SDK source tree"

            let verdict = classify files

            Expect.isEmpty
                verdict.SystemSites
                "a production seam passes `EntityPrincipal.system` — thread the caller's principal through the seam instead (Phase 814); if the site genuinely has no caller to name, say why in its comment and add it to `allowedSystemSites` here"

        testCase "the pre-814 spelling of the principal type does not survive anywhere under src"
        <| fun _ ->
            let verdict = classify (sourceFiles ())

            Expect.isEmpty
                verdict.OldSpelling
                (sprintf
                    "the type was renamed `EntityPrincipal` in Phase 814 and `%s` is not an alias — nothing released ever carried it"
                    oldSpelling)
    ]

[<Tests>]
let tests =
    testList "Phase 814 — seam principals" [ narrativeTests; reportingTests; classifierFixtures; guardTests ]