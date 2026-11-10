// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The three documentation gates: `VerifyDocSnippets`, `VerifySourceCitations`
/// and `VerifyUploadResults`.
///
/// Each gate reads the tree and returns its census and, when it refuses, the
/// findings and the verdict; its lines go to a `Sink`. The driver
/// (`Build.fs`) binds the sink to its trace and turns the result into a
/// `Verdict`; `ToolUp.Platform.Build.Tests` (`DocGatesTests.fs`) holds
/// each arm in both directions over fixture trees. FAKE-free and
/// source-linked into both, so the gate and its proofs run one
/// implementation. The baselines are read through `SurfaceDiff.parse`.
module ToolUp.Forge.DocGates

open System
open System.IO
open System.Text.RegularExpressions
open ToolUp.Platform
open ToolUp.Platform.Tests.Contracts

// ---- VerifyDocSnippets: compile-checked documentation snippets ----
//
// Nothing detected a doc snippet naming an API the SDK no longer
// has. The docs teach `fsharp` fenced blocks a reader copies
// verbatim; when a phase renames or removes the thing a snippet
// calls, the snippet silently becomes a lie that compiles nowhere
// and fails at the reader's first build. Two live instances were
// found minutes apart by a human noticing, which is not a process.
//
// Usage: `dotnet run --project Build.fsproj -- VerifyDocSnippets`
//        `… -- VerifyDocSnippets --update-baseline`  (see below)
//
// ── What is in scope ────────────────────────────────────
//
// IN SCOPE BY DEFAULT: every ```fsharp block under `docs/**` and
// `src/ToolUp.Platform/technical-guide/**`. The escape is a marker
// in the fence INFO STRING — ```fsharp skip=<reason> — drawn from a
// CLOSED set (`docSnippetSkipReasons`). An unknown reason, a bare
// `skip`, or any other unrecognised attribute FAILS the target, so
// the escape cannot be widened by typo or by invention; widening it
// is a visible diff to this file.
//
// The info string is metadata, not content: every Markdown renderer
// takes the FIRST word as the language, so `fsharp skip=fragment`
// still highlights as F# and the marker is invisible to a reader.
// That is what makes an in-band marker acceptable here — it adds no
// ceremony to the code a reader copies.
//
// TWO tree-level exclusions, and they are the same class:
// point-in-time documents whose code deliberately reflects a state
// other than the current surface.
//
//   `docs/migrations/**` — a migration doc's job is to show the
//   RETIRED shape beside its replacement, usually in the same block.
//   Compiling it is category-incorrect: the old shape must not
//   compile, that is the point of the page.
//
//   `docs/design/**` — a design record states what was PROPOSED.
//   Where implementation diverged from the proposal, rewriting the
//   blocks against the shipped surface would destroy exactly the
//   value the document has (it would no longer record what was
//   argued for), and marking them individually would mean widening
//   the closed skip set to cover "historically accurate".
//
// Both are tree exclusions rather than per-block markers precisely
// because a marker on every block of such a page would be the easy
// opt-out that gets reached for. Widening this list is a visible
// diff to this file and needs the same argument: the tree's code is
// point-in-time BY DESIGN, not merely inconvenient to fix.
//
// ── How a fragment declares its context ─────────────────
//
// Snippets are excerpts, not programs, and the docs must not grow
// `open`-ceremony a reader then copies. So context is supplied
// OUT-OF-BAND, in four layers, none of which touch the markdown:
//
//   1. `docSnippetPreamble` — the ambient opens any ToolUp source
//      file has.
//   2. `docSnippetTreePreamble` — per doc tree; a page under
//      `docs/rag/` is read in the context of the RAG package.
//   3. Per-page AMBIENT PREAMBLE — an optional F# file at
//      `docs-snippets/ambient/<doc path>.fs` (the doc tree mirrored,
//      `.md` -> `.fs`), inlined verbatim into every generated block
//      of that page. See below.
//   4. Page accumulation — `open` lines declared in an EARLIER
//      block of the same page apply to later ones, which is how a
//      reader reads a page top to bottom.
//
// ── The per-page ambient preamble, and what it is FOR ───────────
//
// A large share of doc blocks are not drifted and not prose: they
// are excerpts of a composition root the page never shows in full,
// reading locals a reader is expected to already have in scope —
// `config`, `providerProfile`, `secretStore`, `authProvider`, an
// Elmish `Model`, a page-local `loadCampaign`. Before this layer
// existed the only honest classification for those was
// `skip=fragment`, which buys silence: the block's SDK names are
// then checked by nothing, so the next rename rots it invisibly —
// the exact drift class this target exists to catch, in the blocks
// the target cannot see.
//
// The ambient file declares those locals ONCE per page, out of
// band, so the block compiles as written and every SDK name in it
// is checked. It is ordinary F# (opens, type declarations, `let`
// bindings — `failwith "ambient"` is the conventional body, since
// nothing here runs), and it is inlined ahead of the block under
// its own `#line` directive, so an error INSIDE an ambient file is
// reported against that file and lands in the unattributable bucket
// — a harness fault, which is exactly what it is.
//
// The rule for what may go in one: an ambient declaration stands in
// for something the PAGE's own surrounding program would provide.
// It must never redeclare an SDK name — that would fake the surface
// the block is supposed to be checked against, turning the gate
// into a mirror. When in doubt, name the binding after the doc's
// own prose and give it the SDK type, so the type is still checked.
//
// SHAPE of an ambient file — `open`s at the top, everything else
// inside one auto-opened module:
//
//     open ToolUp.PublicRendering
//
//     [<AutoOpen>]
//     module PageAmbient =
//         type Campaign = { Name: string }
//         let loadCampaign (ctx: CallContext) (c: string) = failwith "ambient"
//
// The file is inlined VERBATIM — no reordering — so the `#line`
// attribution stays exact. The two halves are both load-bearing:
// top-level `open`s must be visible to the block (an `open` nested
// inside the module would not be), while the declarations must NOT,
// because a page routinely introduces a type in its first block and
// reads it from its fifth. Flat declarations would collide with that
// first block; auto-opened ones are simply SHADOWED by it, so the
// page teaches the type once and every later block still compiles.
//
// See `docs-snippets/ambient/README.md`.
//
// Each block then compiles as its own module in its own generated
// file. One file per BLOCK, not per page, is load-bearing: F#
// abandons a file at its first parse error, so a single malformed
// block in a shared file would mask every later block on the page.
//
// What this therefore gates is "every SDK name a snippet uses still
// exists, with the shape shown" — not "this block is a runnable
// program". That is the drift class, and it is the one that bites
// readers.
//
// ── Where the failure points ────────────────────────────
//
// Each generated file carries an F# `# <line> "<abs path to the
// .md>"` directive, so the COMPILER ITSELF reports at the markdown
// file and line — `docs/platform/auth.md(88,13): error FS1129: …`.
// No error-message rewriting, and the location survives every
// downstream tool that understands compiler output. The summary
// below additionally groups by file and block ordinal.
//
// ── The baseline, and why it is not a way of going green ────────
//
// The corpus was measured before the gate was designed: of 655
// in-scope blocks, 231 already named at least one API the SDK does
// not have. Fixing all of them is a docs project, not this phase,
// and marking them `skip=` would be a lie — `skip` means "not
// checkable", not "checkable and currently wrong". They are instead
// recorded in `docs-snippets/known-drift.txt`, keyed by a hash of
// the block's own text.
//
// The baseline is a RATCHET, not a mute button, and it fails in
// BOTH directions:
//   * any failing block absent from the baseline fails the target
//     (new drift cannot land);
//   * any baseline entry that now compiles ALSO fails the target,
//     demanding its removal (the baseline can only shrink).
// Because the key is the block's content hash, editing a baselined
// block invalidates its entry — so a broken snippet cannot be
// quietly rewritten into a differently-broken one.
//
// Same posture as the public-API approval baseline, and
// the same posture the gate took when `verify-all` was not green
// on Linux: record the evidence, gate what is true today, and let
// the number only fall.
//
// `--update-baseline` REWRITES the file from the current run. It is
// for the first seeding and for wholesale re-measurement after a
// deliberate corpus change; it is never part of making CI pass.
let docSnippetRoots = [ "docs"; "src/ToolUp.Platform/technical-guide" ]
let docSnippetExcludedTrees = [ "docs/migrations"; "docs/design" ]

// ── The PACKED teaching surfaces, and the contributor
//    guide ────────────────────────────────────────────────────────
//
// `docs/**` is the site. It is not the whole of what a consumer
// reads, and for a companion package it is frequently not what they
// read FIRST. `Directory.Build.props` auto-includes every
// `src/**/README.md` into its own nupkg, so a companion's README
// ships INSIDE the artefact and is the page a consumer lands on from
// nuget.org — the first, and for most of the long-tail companions the
// only, teaching surface they will ever see. It was the one teaching
// surface with no ratchet at all, which is exactly where the
// docs burn-down found the same drift classes it had just cleared out
// of `docs/**` sitting untouched.
//
// Three name-scoped additions rather than a `src` tree root, because
// `src/**` is source, not documentation: a bare tree root would sweep
// in 72 CHANGELOGs, ten LICENSEs and a `node_modules` tree, none of
// which is teaching material and the last of which is not even ours.
//
//   `src/**/README.md`          — packed into the nupkg; see above.
//   `src/**/TECHNICAL_GUIDE.md` — the per-package deep-dive the
//                                 README links to. Same audience, same
//                                 packing convention, and the largest
//                                 single block counts in the estate
//                                 (`ToolUp.AI` alone teaches 22).
//   `CLAUDE.md`                 — the CONTRIBUTOR guide. A different
//                                 audience from the other two, and
//                                 deliberately in scope anyway: its
//                                 module sample is the canonical
//                                 four-file shape every consumer
//                                 module is copied from, and it taught
//                                 `Icon = "/svg/chart.svg"` against a
//                                 `ReactElement` field for as long as
//                                 that field has been typed. A guide
//                                 that mis-teaches the shape is not
//                                 less costly for addressing a
//                                 contributor; it is more.
//
// `bin` / `obj` / `node_modules` are pruned by name during the walk.
// The first two are build output (a packed README is copied into
// `bin/` on its way into the nupkg, so an unpruned walk double-counts
// every one of them); the third is vendored third-party content whose
// prose is not ours to hold to our surface.
let docSnippetSrcRoot = "src"
let docSnippetSrcFileNames = set [ "README.md"; "TECHNICAL_GUIDE.md" ]
let docSnippetPrunedDirs = set [ "bin"; "obj"; "node_modules" ]
let docSnippetLooseFiles = [ "CLAUDE.md" ]

// The closed escape set. Each reason is a claim about the block's
// SHAPE that a reviewer can check by reading it:
//   signature   — an `.fsi`-shaped API listing (`module M = val f: …`).
//                 Not implementation F#; cannot be compiled as such.
//   fragment    — an excerpt: an elided body (`|> ...`), or it reads
//                 locals belonging to a surrounding program the page
//                 does not show.
//   anti-pattern— deliberately wrong, shown as a "don't".
// "It failed to compile" is NOT a reason — that is what the
// baseline is for.
let docSnippetSkipReasons = set [ "signature"; "fragment"; "anti-pattern" ]

// Floor guard: this target legitimately exits 0 with an empty
// corpus (a bad glob, a moved docs folder, an extractor that
// matched no fences), which is indistinguishable from a pass unless
// the count is asserted.
//
// A STATIC lower bound catches the collapse but nothing else, and a
// static number cannot ratchet: as the corpus grows the gap between
// the floor and the truth widens into room for silent hollowing —
// skip-marking a block that used to compile costs the corpus one
// checked block and the gate says nothing, because the floor was
// set years of docs ago. The floor is therefore a HIGH-WATER MARK
// recorded in `docs-snippets/corpus-floor.txt`:
//
//   * `compiled < mark` FAILS. A block that used to compile no
//     longer does — skip-marked, deleted, or moved into an excluded
//     tree. Legitimate cases exist (that is what the exclusion of
//     `docs/design` was), and the remedy is to lower the number BY
//     HAND, which puts the decision in the diff where a reviewer
//     sees it. Deliberately not a flag: an automated lower is the
//     one motion this guard exists to make expensive.
//   * `compiled > mark` REWRITES the mark and says so. Growth is
//     always legitimate, so it must not red a build over a docs
//     addition; but the new number lands in the working tree, so it
//     rides the author's own commit as a reviewed one-line diff
//     rather than being auto-committed by anything.
//
// The collapse case the static floor existed for is subsumed: a
// harness that matches almost nothing scores far below the mark.
let docSnippetFloorSeed = 300

// Doc-declared type parity: the redeclaration arm.
//
// The compile gate above has one structural blind spot, and it is the
// one an api-reference page walks into by design. A block that
// REDECLARES a public SDK type — `type IJobScheduler = abstract …` —
// compiles green forever, because the local declaration SHADOWS the
// real type and the compiler never consults the surface the doc claims
// to describe. `docs/platform/jobs.md` taught a six-method
// `IJobScheduler` for as long as this gate has existed; four of those
// methods have not been on the interface for releases, and every run
// was green. The zero-drift guarantee is hollow wherever a doc
// re-declares.
//
// So a second, compile-independent check: read the type names a block
// declares, resolve them against the `api-baselines/`
// rendering of the real public surface, and hold the declaration to
// what the surface says.
//
// ── The elision rule: SUBSET, and there is no marker ─────────────
//
// Doc listings are routinely simplified — six of eleven methods, the
// fields that matter to the page. Two rules could accommodate that: an
// ELISION MARKER inside the block (unmarked blocks must be exhaustive),
// or MEMBERSHIP-SUBSET TOLERANCE (whatever the block shows must be
// real; what it omits is its own business). Subset, for two reasons,
// and the second is the decisive one.
//
// First, the marker answers the wrong question. A member that CHANGED
// does not present here as a missing member; it presents as a member
// name the real type does not have (a rename) or a signature naming a
// type the real member does not use (a retype). Both are the EXTRA
// direction. A marker's whole semantic domain is the MISSING direction,
// so it can never speak to a changed member — and if it is read, as it
// inevitably would be, as "this listing is partial, relax", the
// relaxation leaks into the extra direction and starts hiding exactly
// the drift this check exists to catch. `IJobScheduler.Unschedule`
// would become an elision.
//
// Second, an unmarked-means-exhaustive rule reddens every listing on
// the page the next time the SDK GAINS a member — a maintenance tax
// unrelated to mis-teaching — so the marker would be applied wholesale
// and read thereafter as "reviewed", having been typed to go green.
// This file already carries that argument twice: the skip set is closed
// and widening it is a visible diff here, and the corpus floor's shrink
// is deliberately a hand edit rather than a flag.
//
// Subset tolerance needs no escape and offers none. It asserts the one
// claim a reader actually relies on — EVERY MEMBER THIS BLOCK TEACHES
// IS A MEMBER THE SDK HAS — and makes the missing direction a
// non-question by construction.
//
// ── What is compared, and why not more ───────────────────────────
//
// Per shown member: its NAME must exist on the real type, and the
// public SDK type names its declared signature mentions must be a
// SUBSET of those the real member's rendering mentions. That second
// half is what catches a retype — `Schedule: JobDefinition -> …`
// against a real `Schedule(JobRegistration)` — without pretending to
// normalise F# source syntax into reflection metadata.
//
// Full signature equality was implemented first and measured before
// being discarded: it produced 416 findings across the corpus, of which
// essentially none were drift. Metadata erases F# type abbreviations
// (`type JobId = Guid` renders as `System.Guid`, so a doc writing
// `JobId` looks wrong), primitives render as `System.String` where a
// doc writes `string`, and curried arrows render as tupled parameter
// lists. Restricting the comparison to names the surface itself OWNS,
// in the subset direction, dropped that to 5 — every one a genuine
// mis-teach. A gate whose findings are mostly rendering artefacts is
// one people learn to step over.
//
// Two residuals, stated rather than hidden, and both visible in the
// census line rather than papered over:
//   * a same-name member retyped only in BCL terms (`string` -> `int`)
//     is not caught — no surface-owned name changed;
//   * a SINGLE-LINE union (`type ChunkOrigin = Document | Note | …`)
//     is read as `not comparable`, because the member reader keys off
//     `|` at the start of a line. Six declarations sit in that bucket
//     today. Worth closing; not closed here, because widening the
//     reader is a corpus-wide change and this phase landed its own
//     corpus at zero.
//
// ── Three soundness filters, each measured ───────────────────────
//
//   * AMBIGUOUS NAMES ARE NOT COMPARED. A doc's own Elmish `Model` /
//     `Msg` example collides with the dozens of nested `…UI+Msg` types
//     the surface renders; matching a bare `Msg` to one of them was
//     never sound. A simple name resolving to more than one public type
//     is counted and skipped.
//   * BCL-COLLIDING SIMPLE NAMES ARE NOT SURFACE NAMES. A ToolUp module
//     named `String` would otherwise make every `System.String` field
//     in the baselines read as a ToolUp type and every doc `string`
//     read as a mismatch.
//   * F# CORE ALIASES ARE NOT SURFACE NAMES (`docParityAliasNames`) —
//     `Set<SubjectKind>` renders as `FSharpSet\`1[…]`, so a doc's `Set`
//     has no counterpart to be a subset of.
//
// The surface is read from the COMMITTED `api-baselines/` text, not
// from built DLLs. That is what makes this check independent of build
// state — it cannot be quietened by a stale `bin/`, and it runs before
// the snippet project is compiled at all. The baselines are themselves
// gated in both directions by the approval test, so a
// surface change that has not been folded into them fails there rather
// than silently moving the target here.
//
// SKIPPED blocks are IN SCOPE here, deliberately. `skip=signature` is
// precisely the `.fsi`-shaped api-reference listing, i.e. the highest-
// risk shape for this defect, and the check needs no compiler — so the
// one pool the compile gate cannot see is the one pool this check can.
let docParityAliasNames =
    set [
        "String"
        "Set"
        "Map"
        "List"
        "Array"
        "Option"
        "Async"
        "Result"
        "Seq"
        "Choice"
        "Ref"
        "Lazy"
        "Nullable"
        "Task"
        "Tuple"
        "Char"
        "Byte"
        "Guid"
        "DateTime"
        "TimeSpan"
    ]

// The fragment symbol-existence lint: the fragment arm.
//
// `skip=fragment` buys silence, and the census above says how much:
// 352 blocks at the time this was written, six times the
// estimate. A fragment is exempt from compilation, so a
// `PlatformMode`-class rename rots it and every run stays green —
// the exact drift class this target exists to catch, occurring in
// the one pool the target cannot see.
//
// Existence, unlike correctness, is checkable WITHOUT compiling. A
// fragment's dotted, capitalized identifiers (`ServerApp.withStorage`,
// `ChunkOrigin.Note`) and its record-construction field labels either
// resolve against the name universe the `api-baselines/`
// render, or name something the block itself introduces, or they are
// a lie. That is a weak check and it is deliberately weak: a RENAMED
// api is caught, a RETYPED one is not. Retyping is compilation's job,
// and redeclaration is the parity arm's.
//
// ── The false-positive budget is the whole design ────────────────
//
// A fragment is, by its own marker, an excerpt full of names that are
// not SDK names: locals of a surrounding program, deliberate
// placeholders (`MyModule.analyse`), vendor identifiers, BCL calls.
// A lint that fires on those is a lint someone turns off, and the
// obvious remedy — an allow-list file — is a second baseline, which
// this file has twice argued against. So the escape is a SHAPE rule
// instead, and it has one governing idea: THE LINT SPEAKS ONLY ABOUT
// NAMES THE SURFACE OWNS.
//
//   * ROOT-ANCHORED. A chain is checked only where a segment names a
//     container the surface owns. `MyModule.analyse`, `Console.Write`,
//     `Fable.Core.JsInterop` anchor nowhere and are not checked — not
//     because they were listed, but because nothing in the universe
//     answers to them. This one rule carries most of the budget.
//   * A VALUE'S PROPERTY IS NOT A CONTAINER. Every segment before the
//     anchor must be capitalized, so `ctx.Progress.Report` cannot
//     anchor at `Progress` even when a `Progress` type exists.
//   * WHAT THE BLOCK INTRODUCES WINS. A `type` / `module` / capitalized
//     `let` in the block, in ANY block of the same page (a reader reads
//     a page top to bottom — the same accumulation the compile arm
//     gives `open`), or in the page's ambient preamble, takes the name
//     out of the check.
//   * AMBIGUOUS NAMES ARE NOT COMPARED, on the parity arm's measured reasoning: a
//     simple name resolving to more than one container was never
//     soundly matchable to one of them.
//   * DU CASES DO NOT CONTRIBUTE CONTAINER NAMES. `ContentBody+Html`
//     would otherwise put Feliz's `Html.div` inside the universe and
//     fail it. The case is still reachable as a MEMBER of its parent,
//     which is how a doc legitimately writes `ContentBody.Html`.
//   * DECLARATION LINES ARE NOT MEMBER ACCESSES. `open` / `namespace` /
//     `module` carry a namespace path; asking whether a namespace
//     segment "has a member" is not a question.
//   * STRING LITERALS AND COMMENTS ARE STRIPPED, and the BCL-colliding
//     and F#-core-alias filters above apply unchanged.
//
// ── Two arms, because a rename does not always present as a dot ───
//
// The dotted arm alone misses the shape a record-building fragment
// takes: `docs/platform/jobs.md` builds a job with `HandlerName`,
// `Retry` and `IdempotencyKey`, none of which the surface has had for
// releases, and not one of them is dotted. So a second arm reads
// RECORD-CONSTRUCTION FIELD LABELS.
//
// It has no type annotation to anchor on, so it INFERS one: the
// surface record whose members cover most of the region's labels. An
// inference needs a floor or it invents anchors, and these three were
// measured rather than picked — the first cut, without them, produced
// ten findings that were all wrong anchor:
//   * a region needs >= `docFragmentMinRecordLabels` labels;
//   * the best candidate must match >= `docFragmentMinRecordMatches`
//     of them AND at least half of them;
//   * that best must be UNIQUE — a tie means the doc is describing
//     something the surface does not uniquely name, and silence is
//     the honest answer.
// One BRACE REGION is one construction. Tracking depth alone folds a
// nested literal into its parent's label set, which then anchors on
// neither type and reports the nesting itself as missing fields; that
// was most of the first cut's remaining noise. `{| … |}` opens no
// region at all — an anonymous record has no surface type to be held to.
//
// ── Retirement ───────────────────────────────────────────
//
// THIS LINT IS A BRIDGE AND IS MEANT TO BE DELETED. The docs project is
// converting `skip=fragment` blocks to compiled ones at docs-project
// pace, and every block it converts leaves this lint's universe by
// construction — the scan reads `skip=fragment` and nothing else, so
// no edit here is ever needed to hand a block over. What the lint
// gives is coverage of the blocks that project has not reached yet, and its
// value falls as that pool does.
//
// The deletion condition is therefore a NUMBER, not a judgement: when
// the `skip=fragment` count on the summary reaches the floor the docs project
// lands on — the residue of genuinely elided, prose-shaped excerpts
// that no ambient preamble can rescue — this whole section goes, with
// its census line, and the remaining fragments are accepted as
// unchecked. Deleting it earlier would drop live coverage; keeping it
// afterwards is a second gate over a handful of blocks, paid for on
// every run. The `fragments walked` figure on the census line is what
// that decision reads.
let docFragmentMinRecordLabels = 3
let docFragmentMinRecordMatches = 3

// ---- The api-baseline surface universe, shared by every doc gate ----
//
// Each arm reads the committed `api-baselines/` text to answer a different
// question about one universe: the members of a redeclared type, the
// container a fragment's name hangs off, the member a source comment
// cites. The text is read through `SurfaceDiff.parse`, the one grammar
// of a baseline that the approval gate, the release bump and the changelog
// read too: a second parser would be a second idea of what the surface is,
// and the two would diverge exactly where a rename made the answer matter.

/// The groups of `pattern`'s first match in the input, whole match first.
let (|Rx|_|) (pattern: string) (input: string) =
    let m = Regex.Match(input, pattern)

    if m.Success then
        Some [ for g in m.Groups -> g.Value ]
    else
        None

let simpleNameOf (s: string) =
    let bare = s.Split('`')[0]
    let i = max (bare.LastIndexOf '.') (bare.LastIndexOf '+')
    if i >= 0 then bare.Substring(i + 1) else bare

/// The public surface the committed baselines render, keyed for the
/// questions the doc gates ask of it.
type Universe = {
    /// Every readable type, with each member's name and rendered line.
    /// Nested types keep their `+`, so the DU-case reader finds `T+Case`.
    RealTypes: (string * (string * string) list) list
    RealFullNames: Set<string>
    /// Simple name to the types that render it, a module and the type of
    /// its own name collapsed into one.
    RealByName: Map<string, (string * (string * string) list) list>
    /// Simple names the surface uses for BCL types.
    BclSimpleNames: Set<string>
    OwnedSimpleNames: Set<string>
    UnambiguousTypeNames: Set<string>
    /// Doc-facing container name to its distinct containers and the member
    /// names a doc may write on it.
    SurfaceContainers: Map<string, string list * Set<string>>
    OwnedContainerNames: Set<string>
}

[<RequireQualifiedAccess>]
module Universe =

    /// The universe the given baselines render.
    let ofSurfaces (surfaces: SurfaceDiff.Surface list) : Universe =
        // A container the RENDERER could not read is NOT
        // COMPARABLE, and must never be rendered as "this type has no
        // members".
        //
        // `PublicApiApproval` writes `<full>  # <members unavailable:
        // FileNotFoundException>` when reflection cannot load an
        // assembly's dependencies to enumerate its members. An unguarded
        // reader sees a container with an EMPTY member list, and every doc
        // that names one of its members becomes a finding whose remedy is
        // to delete a perfectly correct sentence. "I cannot read this" is
        // reported as unknown, never as wrong: dropping the container puts
        // its members in the lint's OUTSIDE bucket, where an unresolvable
        // name already lives.
        let unreadableTypes = surfaces |> List.map _.Unreadable |> Set.unionMany

        let realTypes =
            surfaces
            |> List.collect (fun s ->
                s.Types
                |> List.filter (fun t -> not (unreadableTypes.Contains t.Full))
                |> List.map (fun t -> t.Full, t.Members |> List.map (fun m -> m.Name, m.Rendered)))

        let realFullNames = realTypes |> List.map fst |> Set.ofList

        // An F# `module X` holding a `type X` renders as BOTH `A.X` and
        // `A.X+X`. That is not two competing types — it is a container and
        // the thing inside it, and a doc writing `X` after `open A` means
        // the latter. Left uncollapsed it reads as ambiguous, and the
        // whole family of interfaces whose module shares their name
        // (`IVectorStore`, `IRetrievalPipeline`, `IRetrievalTracer`, …)
        // drops silently out of the comparison.
        let realByName =
            realTypes
            |> List.groupBy (fst >> simpleNameOf)
            |> List.map (fun (name, candidates) ->
                let fulls = candidates |> List.map fst |> Set.ofList

                let collapsed =
                    candidates
                    |> List.filter (fun (full, _) -> not (fulls.Contains(full + "+" + name)))

                name, (if collapsed.IsEmpty then candidates else collapsed))
            |> Map.ofList

        // A simple name the SURFACE uses for a BCL type is not evidence of
        // a ToolUp type of that name.
        let bclSimpleNames =
            realTypes
            |> Seq.collect (fun (_, ms) -> ms |> Seq.map snd)
            |> Seq.collect (fun rendered -> [
                for m in Regex.Matches(rendered, @"[A-Za-z_][A-Za-z0-9_.+`]*") -> m.Value
            ])
            |> Seq.filter (fun v -> v.StartsWith "System." || v.StartsWith "Microsoft.")
            |> Seq.map simpleNameOf
            |> Set.ofSeq

        let ownedSimpleNames =
            Set.difference (realByName |> Map.keys |> Set.ofSeq) (Set.union bclSimpleNames docParityAliasNames)

        let unambiguousTypeNames =
            realByName |> Map.filter (fun _ v -> v.Length = 1) |> Map.keys |> Set.ofSeq

        // ---- The name universe a fragment is held to ----
        //
        // The same reading re-keyed for the question this arm asks. The parity arm
        // resolves a DECLARED type name to one rendered type; this resolves
        // a USED container name to the set of members a doc may name on it,
        // which is a union rather than a choice — over-accepting only ever
        // passes a doc, and the subset direction stays sound.
        let membersByFull =
            realTypes |> List.map (fun (full, ms) -> full, ms |> List.map fst) |> Map.ofList

        let nestedByParent =
            realFullNames
            |> Seq.choose (fun f ->
                let i = f.LastIndexOf '+'

                if i > 0 then
                    Some(f.Substring(0, i), f.Substring(i + 1))
                else
                    None)
            |> Seq.groupBy fst
            |> Seq.map (fun (parent, xs) -> parent, xs |> Seq.map snd |> List.ofSeq)
            |> Map.ofSeq

        let realMemberNames =
            realTypes
            |> List.map (fun (f, ms) -> f, (ms |> List.map fst |> Set.ofList))
            |> Map.ofList

        // F# appends `Module` to a module that shares its name with a type
        // in the same scope, so `RagOptionsModule` IS what a doc calls
        // `RagOptions`. The alias is ADDITIVE, never a replacement:
        // `ServerModule` is a TYPE whose own name ends in `Module`, and
        // replacing would file it under `Server` and lose it.
        let docFacingNamesOf (full: string) =
            let s = simpleNameOf full

            if s.EndsWith "Module" && s.Length > 6 then
                [ s; s.Substring(0, s.Length - 6) ]
            else
                [ s ]

        let isDuCase (full: string) =
            let i = full.LastIndexOf '+'

            if i < 0 then
                false
            else
                match realMemberNames.TryFind(full.Substring(0, i)) with
                | Some ms -> ms.Contains("Is" + full.Substring(i + 1))
                | None -> false

        let surfaceContainers =
            realTypes
            |> List.collect (fun (full, members) -> docFacingNamesOf full |> List.map (fun key -> key, (full, members)))
            |> List.groupBy fst
            |> List.map (fun (key, entries) ->
                let usable =
                    entries |> List.map snd |> List.filter (fun (full, _) -> not (isDuCase full))

                // Two renderings can be ONE doc-facing container: a module
                // and the type it shadows (`X` / `XModule`), and a module
                // and the type nested inside it (`A.X` / `A.X+X`). Generic
                // arity is a third such detail. Collapse only WITHIN this
                // key's candidates — the global form of the first rule
                // fuses `ToolUp.Platform.Server` with the unrelated
                // `ToolUp.Platform.ServerModule`.
                let dropArity (f: string) = Regex.Replace(f, @"`\d+", "")

                let here = usable |> List.map (fst >> dropArity) |> Set.ofList

                let canonical (raw: string) =
                    let full = dropArity raw

                    let f =
                        if
                            full.EndsWith "Module"
                            && full.Length > 6
                            && here.Contains(full.Substring(0, full.Length - 6))
                        then
                            full.Substring(0, full.Length - 6)
                        else
                            full

                    let nested = f + "+" + simpleNameOf f
                    if here.Contains nested then nested else f

                // A GENERIC member renders with its arity —
                // ``ModuleQueryBus.ask`2``, ``Cmd.none`1`` — and a doc
                // writes the bare name. Without the strip every generic
                // function in the surface reads as absent.
                //
                // And the CASES of a DU nested in this
                // container, because F# reaches them THROUGH the container:
                // `StopWords.German` is how the language resolves a case of
                // the `Language` DU declared in `module StopWords`, but the
                // case renders one level down. Narrowed to DU cases by the
                // `IsXxx` companion property the compiler emits for each:
                // folding a nested record's FIELDS into its parent would
                // widen the accepted set for no rule the language has.
                let members =
                    usable
                    |> List.collect (fun (full, ms) ->
                        let children = nestedByParent.TryFind full |> Option.defaultValue []

                        let nestedDuCases =
                            children
                            |> List.collect (fun c ->
                                match membersByFull.TryFind(full + "+" + c) with
                                | None -> []
                                | Some names -> names |> List.filter (fun n -> names |> List.contains ("Is" + n)))

                        (ms |> List.map fst) @ children @ nestedDuCases)
                    |> List.map (fun m -> m.Split('`')[0])
                    |> Set.ofList

                key, (usable |> List.map (fst >> canonical) |> List.distinct, members))
            |> List.filter (fun (_, (fulls, _)) -> not fulls.IsEmpty)
            |> Map.ofList

        let ownedContainerNames =
            Set.difference (surfaceContainers |> Map.keys |> Set.ofSeq) (Set.union bclSimpleNames docParityAliasNames)

        {
            RealTypes = realTypes
            RealFullNames = realFullNames
            RealByName = realByName
            BclSimpleNames = bclSimpleNames
            OwnedSimpleNames = ownedSimpleNames
            UnambiguousTypeNames = unambiguousTypeNames
            SurfaceContainers = surfaceContainers
            OwnedContainerNames = ownedContainerNames
        }

    /// The universe `<root>/api-baselines/*.approved.txt` renders; empty
    /// where the directory does not exist.
    let read (root: string) : Universe =
        let dir = Path.Combine(root, "api-baselines")

        if Directory.Exists dir then
            Directory.EnumerateFiles(dir, "*.approved.txt")
            |> Seq.map (File.ReadAllText >> SurfaceDiff.parse)
            |> List.ofSeq
            |> ofSurfaces
        else
            ofSurfaces []

// Arm 1 — dotted, capitalized identifiers. The anchor is the first
// segment the surface owns, and every segment before it must be
// capitalized (so a lowercase value's property can never anchor).
// Anchor at the RIGHTMOST container in the chain, not
// the leftmost.
//
// A fully-qualified reference is namespace segments followed by a
// container followed by a member: in
// `ToolUp.Voice.Client.VoiceInput.registerPromptMic` the container
// is `VoiceInput` and everything before it is qualification.
// Scanning left-to-right stopped at `Client` — a real container in
// a different package — and then reported the NAMESPACE segment
// `VoiceInput` as a member `Client` does not have, on three pages
// whose prose was correct as written. Rightmost-first reads the
// chain the way F# does.
//
// The leading guard is unchanged and still load-bearing: a chain
// whose first segment is lower-case is a local value being
// dereferenced, not a container path, and must never anchor.
let anchorIn (ownedContainerNames: Set<string>) (chain: string) =
    let segs = chain.Split('.')

    if segs.Length < 2 || not (System.Char.IsUpper(segs[0].[0])) then
        None
    else
        let rec go i =
            if i < 0 then
                None
            elif ownedContainerNames.Contains segs[i] then
                Some(segs[i], segs[i + 1])
            else
                go (i - 1)

        go (segs.Length - 2)
// A finding's value is the fix it suggests. `ServerConfig` renders
// ~150 members, so the whole set is unreadable and an alphabetical
// truncation of it reliably omits the answer — every `with*` helper
// sorts after every field. Rank by shared trigrams, tie-broken by
// shared prefix. Prefix alone was tried and measured against the
// demonstrated-red probe: for a `withStorage` -> `withBlobStorage`
// rename it offered `withScheduledJob` first, because an INFIX
// insertion is exactly the case a prefix measure cannot see, and
// insertion is a common rename shape.
let nearestTo (wanted: string) (members: Set<string>) =
    let trigrams (s: string) =
        let t = s.ToLowerInvariant()

        if t.Length < 3 then
            Set.singleton t
        else
            set [ for i in 0 .. t.Length - 3 -> t.Substring(i, 3) ]

    let wantedGrams = trigrams wanted

    let sharedPrefix (candidate: string) =
        let n = min wanted.Length candidate.Length

        let rec go i =
            if
                i < n
                && System.Char.ToLowerInvariant wanted[i] = System.Char.ToLowerInvariant candidate[i]
            then
                go (i + 1)
            else
                i

        go 0

    members
    |> Set.toList
    |> List.filter (fun m -> m <> ".ctor" && not (m.StartsWith "Is" && members.Contains(m.Substring 2)))
    |> List.sortBy (fun m -> -(Set.intersect wantedGrams (trigrams m)).Count, -(sharedPrefix m), m)
    |> List.truncate 10
    |> String.concat ", "

// ---- What a gate returns ----

/// Where a gate's lines go: the driver passes its trace, a test collects them.
type Sink = {
    /// A progress or summary line.
    Trace: string -> unit
    /// A finding line.
    Error: string -> unit
}

/// Why a gate refused: the finding lines it reported, and its one-line
/// verdict naming the rule and the remedy.
type Refusal = {
    Findings: string list
    Message: string
}

exception Refused of Refusal

/// Report `findings` on the sink's error channel and refuse with `message`.
let private refuse (sink: Sink) (findings: string list) (message: string) : 'a =
    for f in findings do
        sink.Error f

    raise (
        Refused {
            Findings = findings
            Message = message
        }
    )

/// A gate's body as its result: the census it returned, or the refusal it
/// raised on its way there.
let private settle (body: unit -> 'census) : Result<'census, Refusal> =
    try
        Ok(body ())
    with Refused r ->
        Error r

// ---- The documentation-snippet gate: what a page holds ----

/// What a type declaration in a documentation `fsharp` block is, which
/// decides which of its members the parity arm reads and which comparison
/// applies. A union rather than four strings, so each match over it is
/// exhaustive and a fifth kind is a compile error at every arm.
[<RequireQualifiedAccess>]
type DocDeclKind =
    | Interface
    | Union
    | Record
    /// Not comparable: neither abstract members, union cases nor record fields.
    | Other

/// One fenced block of a documentation page.
type Block = {
    /// The page, repository-relative with `/`.
    Rel: string
    /// Its position among the page's fenced blocks, from 1.
    Ordinal: int
    /// The page line of its first content line.
    Start: int
    /// The page line of its closing fence.
    Close: int
    /// The fence's info string: `fsharp`, `fsharp skip=fragment`, `json`.
    Info: string
    /// The content, with the block's common leading indent stripped.
    Body: string list
}

[<RequireQualifiedAccess>]
module Block =

    let isFsharp (b: Block) = b.Info.Split(' ')[0] = "fsharp"

    let isSkipped (b: Block) = b.Info.Contains "skip="

    let skipReason (b: Block) =
        b.Info.Split(' ')
        |> Array.tryPick (fun a -> if a.StartsWith "skip=" then Some(a.Substring 5) else None)

    let isFragment (b: Block) =
        b.Info.Split(' ') |> Array.exists (fun a -> a = "skip=fragment")

    /// The first eight hex digits of the SHA-256 of the body, each line
    /// trimmed at its end: the content part of a baseline key.
    let hash (b: Block) =
        use sha = System.Security.Cryptography.SHA256.Create()

        let bytes =
            System.Text.Encoding.UTF8.GetBytes(String.Join("\n", b.Body |> List.map _.TrimEnd()))

        (sha.ComputeHash bytes)[..3] |> Array.map (sprintf "%02x") |> String.concat ""

/// A fence opens with >= 3 backticks plus an info string, and closes on the
/// first >= as many backticks with no info string.
let private fenceOf (line: string) =
    let t = line.TrimStart(' ')
    let n = t.Length - t.TrimStart('`').Length
    if n >= 3 then Some(n, t.Substring(n).Trim()) else None

/// Every fenced block of a page, in order.
let blocksIn (rel: string) (lines: string array) : Block list =
    let rec go i ordinal acc =
        if i >= lines.Length then
            List.rev acc
        else
            match fenceOf lines[i] with
            | Some(n, info) when info <> "" ->
                let closes k =
                    match fenceOf lines[k] with
                    | Some(m, "") when m >= n -> true
                    | _ -> false

                let j =
                    seq { i + 1 .. lines.Length - 1 }
                    |> Seq.tryFind closes
                    |> Option.defaultValue lines.Length

                let raw = [ for k in i + 1 .. j - 1 -> lines[k] ]

                // Strip the block's COMMON leading indent. A fence nested
                // inside a Markdown list is indented to sit under its list
                // item; that indentation is a Markdown artifact with no F#
                // meaning, but it reaches the compiler as offside structure
                // and every such block fails to parse. Common, not per-line:
                // the block's own internal structure must survive, and blank
                // lines are excluded from the measurement. A compiler error's
                // COLUMN is then the dedented one; its file and LINE, which
                // the `#line` directive carries, stay exact.
                let indent =
                    match raw |> List.filter (fun l -> l.Trim() <> "") with
                    | [] -> 0
                    | content -> content |> List.map (fun l -> l.Length - l.TrimStart(' ').Length) |> List.min

                let body =
                    if indent = 0 then
                        raw
                    else
                        raw
                        |> List.map (fun l ->
                            if l.Length >= indent then
                                l.Substring indent
                            else
                                l.TrimStart(' '))

                let block = {
                    Rel = rel
                    Ordinal = ordinal + 1
                    Start = i + 2
                    Close = j
                    Info = info
                    Body = body
                }

                go (j + 1) (ordinal + 1) (block :: acc)
            | _ -> go (i + 1) ordinal acc

    go 0 0 []

/// The fence attributes that are not in the closed set: an unknown skip
/// reason, a bare `skip`, any other attribute. Validated before anything is
/// decided, so a typo'd marker fails loudly rather than silently leaving a
/// block in or out of scope.
let fenceErrors (blocks: Block list) : string list =
    blocks
    |> List.collect (fun b ->
        b.Info.Split(' ')
        |> Array.toList
        |> List.tail
        |> List.filter (fun a -> a <> "")
        |> List.choose (fun attr ->
            if attr.StartsWith "skip=" then
                let reason = attr.Substring 5

                if docSnippetSkipReasons.Contains reason then
                    None
                else
                    Some(
                        sprintf
                            "%s:%d (block %d): unknown skip reason '%s'. Allowed: %s"
                            b.Rel
                            (b.Start - 1)
                            b.Ordinal
                            reason
                            (String.Join(", ", docSnippetSkipReasons))
                    )
            else
                Some(
                    sprintf
                        "%s:%d (block %d): unrecognised fence attribute '%s'. The only attribute is skip=<reason>."
                        b.Rel
                        (b.Start - 1)
                        b.Ordinal
                        attr
                )))

// ---- The high-water mark ----

/// What the compiled count does to the recorded mark.
[<RequireQualifiedAccess>]
type MarkMove =
    /// Fewer blocks compile than the mark: blocks that were checked no
    /// longer are. Refused; a deliberate loss lowers the mark by hand.
    | Below
    | Hold
    /// More compile: the mark rises to the count, in place.
    | Raise

let markMove (compiled: int) (mark: int) =
    if compiled < mark then MarkMove.Below
    elif compiled > mark then MarkMove.Raise
    else MarkMove.Hold

/// The mark a floor file's lines hold: the first non-comment line that
/// parses, else the seed.
let markOf (lines: string list) =
    lines
    |> List.map _.Trim()
    |> List.filter (fun l -> l <> "" && not (l.StartsWith "#"))
    |> List.tryPick (fun l ->
        match Int32.TryParse l with
        | true, n -> Some n
        | _ -> None)
    |> Option.defaultValue docSnippetFloorSeed

/// The floor file with its number rewritten to `n` and every comment line
/// kept exactly. A deliberate shrink is a hand edit whose whole value is the
/// argument written beside it; regenerating the file wholesale on the next
/// growth would erase that argument. LF line ends, as `.gitattributes` pins.
let rewriteMark (existing: string list) (n: int) : string =
    let isNumber (l: string) =
        l.Trim() <> "" && not (l.TrimStart().StartsWith "#")

    let lines =
        if existing |> List.exists isNumber then
            existing |> List.map (fun l -> if isNumber l then string n else l)
        else
            [
                "# The compiled-doc-snippet HIGH-WATER MARK, asserted by"
                "# `dotnet run --project Build.fsproj -- VerifyDocSnippets`."
                "#"
                "# The gate FAILS when fewer blocks compile than this. Growth"
                "# rewrites the number in place (review it in your diff like any"
                "# other generated line); a genuine, argued shrink — a tree"
                "# exclusion, a page deleted — is a HAND edit, so the decision"
                "# is visible to a reviewer rather than absorbed by a flag."
                ""
                string n
            ]

    String.Join("\n", lines) + "\n"

// ---- Attribution: which block a compiler error belongs to ----

/// One block as the compile arm saw it.
type CompiledBlock = {
    Block: Block
    Hash: string
    /// Compiled. A skipped block is present too, out of scope: a page's
    /// `open` lines are carried to its later blocks, so an unresolvable
    /// `open` declared in a skipped block would otherwise surface as an
    /// unattributable error in every later block on the page.
    InScope: bool
}

/// The block whose line range covers a compiler error line, if any. `+ 1`
/// because an unterminated construct is reported at the line past the
/// block's last.
let attribute (blocks: CompiledBlock list) (line: string) : CompiledBlock option =
    let i = line.LastIndexOf ".md("

    if i < 0 then
        None
    else
        let rest = line.Substring(i + 4)

        match Int32.TryParse(rest.Substring(0, max 0 (rest.IndexOf ','))) with
        | false, _ -> None
        | true, lineNo ->
            let rel = line.Substring(0, i + 3).Replace('\\', '/')

            blocks
            |> List.tryFind (fun c ->
                rel.EndsWith c.Block.Rel
                && lineNo >= c.Block.Start
                && lineNo <= c.Block.Close + 1)

/// The baseline key: the full triple `path#ordinal hash`. Path alone cannot
/// key a page with several failing blocks; hash alone is not unique across
/// files (identical illustrative blocks share one, and a hash-only prune once
/// deleted the wrong file's line); ordinal tells two identical blocks on the
/// same page apart. It is also the text of a baseline line's first two
/// fields, so the key and the line to delete are the same thing.
let keyOf (c: CompiledBlock) =
    sprintf "%s#%d %s" c.Block.Rel c.Block.Ordinal c.Hash

/// The baseline file's entries, by key: `<hash> <path>#<ordinal> (line N) —
/// <error>`, whose fields 2 and 1 are the key.
let readBaseline (lines: string list) : Map<string, string> =
    lines
    |> List.filter (fun l -> l.Trim() <> "" && not (l.StartsWith "#"))
    |> List.choose (fun l ->
        let parts = l.Split(' ')

        if parts.Length >= 2 && parts[1].Contains "#" then
            Some(parts[1] + " " + parts[0], l)
        else
            None)
    |> Map.ofList

// ---- The redeclaration arm ----

/// One type a documentation block declares.
type DocDecl = {
    Rel: string
    Ordinal: int
    /// The page line of the `type` head.
    Line: int
    Name: string
    Kind: DocDeclKind
    /// Member name and declared signature; a union case's signature is empty.
    Members: (string * string) list
}

/// The types a block declares, each with the members its kind reads.
let declsIn (b: Block) : DocDecl list =
    let arr = List.toArray b.Body

    let heads =
        arr
        |> Array.mapi (fun i l ->
            match l with
            | _ when l.StartsWith " " || l.StartsWith "\t" -> None
            | Rx @"^(?:type|and)\s+(?:\[<[^\]]*>\]\s*)?([A-Za-z_][A-Za-z0-9_]*)" [ _; name ] -> Some(i, name)
            | _ -> None)
        |> Array.choose id

    heads
    |> Array.mapi (fun k (i, name) ->
        let stop =
            if k + 1 < heads.Length then
                fst heads[k + 1]
            else
                arr.Length

        let seg = arr[i .. stop - 1]

        let matches pattern =
            seg |> Array.exists (fun l -> Regex.IsMatch(l, pattern))

        let kind =
            if matches @"^\s+abstract\s" then
                DocDeclKind.Interface
            elif matches @"^\s*\|\s*[A-Z]" then
                DocDeclKind.Union
            elif seg |> Array.exists (fun l -> l.Contains "{") then
                DocDeclKind.Record
            else
                DocDeclKind.Other

        let members =
            match kind with
            | DocDeclKind.Interface ->
                seg
                |> Array.choose (function
                    | Rx @"^\s+abstract\s+(?:member\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*:\s*(.+)$" [ _; n; s ] -> Some(n, s)
                    | _ -> None)
            | DocDeclKind.Record ->
                seg
                |> Array.choose (function
                    | Rx @"^\s+(?:mutable\s+)?([A-Z][A-Za-z0-9_]*)\s*:\s*(.+)$" [ _; n; s ] -> Some(n, s)
                    | _ -> None)
            | DocDeclKind.Union ->
                // `| Some x -> …` in a sample body is not a case
                // declaration; an arrow disqualifies the line.
                seg
                |> Array.filter (fun l -> not (l.Contains "->"))
                |> Array.choose (function
                    | Rx @"^\s*\|\s*([A-Z][A-Za-z0-9_]*)\s*(?:of\b.*)?$" [ _; n ] -> Some(n, "")
                    | _ -> None)
            | DocDeclKind.Other -> [||]

        {
            Rel = b.Rel
            Ordinal = b.Ordinal
            Line = b.Start + i
            Name = name
            Kind = kind
            Members = List.ofArray members
        })
    |> List.ofArray

/// How many redeclarations the parity arm saw, and of what kind.
type RedeclarationCensus = {
    /// Declarations of a type name the surface renders exactly once.
    Types: int
    /// The distinct blocks holding them.
    Blocks: int
    /// Of `Types`, those with no members to compare.
    NotComparable: int
    /// Declarations of a name the surface renders more than once.
    Ambiguous: int
}

/// The redeclaration arm. A block that redeclares a public SDK type is
/// invisible to the compile arm (the local declaration shadows the real
/// type), so each declared member is compared with the surface: a union
/// case must be a case; an interface member or record field must exist and
/// its signature may name only surface types the real member carries.
/// Showing FEWER members than the SDK has is fine.
let parityArm (u: Universe) (decls: DocDecl list) : RedeclarationCensus * string list =
    // The public SDK type names a DOC signature mentions. A trailing `//`
    // comment is cut first: prose is not a declaration.
    let docTypeTokens (raw: string) =
        let cut = raw.IndexOf "//"
        let text = if cut >= 0 then raw.Substring(0, cut) else raw

        [ for m in Regex.Matches(text, @"[A-Za-z_][A-Za-z0-9_]*") -> m.Value ]
        |> List.filter u.OwnedSimpleNames.Contains
        |> Set.ofList

    // …and the ones a RENDERED member mentions. A token counts only when its
    // full dotted name is a type the surface renders, which keeps
    // `System.String` out. A nested `Outer+Inner` contributes both halves.
    let realTypeTokens (rendered: string) =
        [ for m in Regex.Matches(rendered, @"[A-Za-z_][A-Za-z0-9_.+`]*") -> m.Value ]
        |> List.map (fun v -> v.Split('`')[0])
        |> List.filter u.RealFullNames.Contains
        |> List.collect (fun bare -> [ for seg in bare.Split '+' -> simpleNameOf seg ])
        |> Set.ofList

    // Case names of a real DU: the nested `T+Case` types, plus the `IsCase`
    // recognisers. Over-collecting only ever ACCEPTS a doc case.
    let realUnionCases (full: string) =
        let nested = full + "+"

        let fromNested =
            u.RealFullNames
            |> Seq.filter (fun f -> f.StartsWith nested)
            |> Seq.map (fun f -> f.Substring nested.Length)
            |> Seq.filter (fun n -> n <> "Tags" && not (n.Contains "+"))
            |> List.ofSeq

        let fromRecognisers =
            u.RealTypes
            |> List.tryFind (fun (f, _) -> f = full)
            |> Option.map (fun (_, ms) ->
                ms
                |> List.map fst
                |> List.filter (fun n -> n.StartsWith "Is" && n.Length > 2)
                |> List.map (fun n -> n.Substring 2))
            |> Option.defaultValue []

        Set.ofList (fromNested @ fromRecognisers)

    let redeclarations =
        decls |> List.filter (fun d -> u.UnambiguousTypeNames.Contains d.Name)

    let findings =
        redeclarations
        |> List.collect (fun d ->
            let full, realMembers = (u.RealByName |> Map.find d.Name).Head

            let fail (memberName: string) (why: string) (docShape: string) (realShape: string) = [
                sprintf
                    "%s (block %d, line %d): %s.%s — %s\n      doc  : %s\n      real : %s"
                    d.Rel
                    d.Ordinal
                    d.Line
                    d.Name
                    memberName
                    why
                    docShape
                    realShape
            ]

            match d.Kind with
            | DocDeclKind.Union ->
                let cases = realUnionCases full

                d.Members
                |> List.collect (fun (caseName, _) ->
                    if cases.Contains caseName then
                        []
                    else
                        fail
                            caseName
                            (sprintf "%s has no such case." full)
                            (sprintf "| %s" caseName)
                            (cases |> Set.toList |> List.sort |> String.concat " | "))
            | DocDeclKind.Interface
            | DocDeclKind.Record ->
                d.Members
                |> List.collect (fun (memberName, memberSig) ->
                    let candidates = realMembers |> List.filter (fun (n, _) -> n = memberName)

                    match candidates with
                    | [] ->
                        fail
                            memberName
                            (sprintf "%s has no such member." full)
                            (sprintf "%s: %s" memberName memberSig)
                            (realMembers |> List.map fst |> List.distinct |> List.sort |> String.concat ", ")
                    | _ ->
                        let declared = docTypeTokens memberSig

                        let ok =
                            candidates
                            |> List.exists (fun (_, rendered) -> Set.isSubset declared (realTypeTokens rendered))

                        if ok then
                            []
                        else
                            // Across OVERLOADS the unmatched set can be empty
                            // while no single overload is a superset, so say
                            // that instead of printing nothing.
                            let unmatched =
                                Set.difference
                                    declared
                                    (candidates |> List.map (snd >> realTypeTokens) |> Set.unionMany)

                            let why =
                                if unmatched.IsEmpty then
                                    sprintf
                                        "no single overload of %s.%s carries all of %s."
                                        full
                                        memberName
                                        (declared |> Set.toList |> List.sort |> String.concat ", ")
                                else
                                    sprintf
                                        "declared signature names %s, which %s.%s does not."
                                        (unmatched |> Set.toList |> List.sort |> String.concat ", ")
                                        full
                                        memberName

                            fail
                                memberName
                                why
                                (sprintf "%s: %s" memberName memberSig)
                                (candidates |> List.map snd |> String.concat "  |  "))
            | DocDeclKind.Other -> [])

    let census = {
        Types = redeclarations.Length
        Blocks = redeclarations |> List.distinctBy (fun d -> d.Rel, d.Ordinal) |> List.length
        NotComparable =
            redeclarations
            |> List.filter (fun d -> d.Kind = DocDeclKind.Other)
            |> List.length
        Ambiguous =
            decls
            |> List.filter (fun d -> u.RealByName.ContainsKey d.Name && not (u.UnambiguousTypeNames.Contains d.Name))
            |> List.length
    }

    census, findings

// ---- The fragment arm ----

/// A line with its string literals emptied and any trailing `//` comment cut.
let stripDocLiterals (line: string) =
    let cut = line.IndexOf "//"
    let text = if cut >= 0 then line.Substring(0, cut) else line

    let noTriple = Regex.Replace(text, @"""""""[\s\S]*?""""""", @"""""")

    Regex.Replace(noTriple, @"""(\\.|[^""\\])*""", @"""""")

/// The type, module and capitalised `let` names a body declares.
let declaredNamesIn (body: string list) =
    body
    |> List.collect (fun line ->
        let t = stripDocLiterals line

        [
            match t with
            | Rx @"^\s*(?:type|and)\s+(?:\[<[^\]]*>\]\s*)?([A-Za-z_][A-Za-z0-9_]*)" [ _; n ] -> n
            | _ -> ()
            match t with
            | Rx @"^\s*(?:\[<[^\]]*>\]\s*)?module\s+(?:rec\s+)?([A-Za-z_][A-Za-z0-9_.]*)" [ _; n ] -> simpleNameOf n
            | _ -> ()
            match t with
            | Rx @"^\s*let\s+(?:mutable\s+|rec\s+|inline\s+)*([A-Z][A-Za-z0-9_]*)" [ _; n ] -> n
            | _ -> ()
        ])
    |> Set.ofList

/// One brace region is one record construction: the page line it opens on
/// and the labels written inside it. An anonymous record (`{|`) is walked
/// and discarded; an unclosed region (the elided `|> ...` tail of a
/// fragment) is still a construction and still checkable.
let recordRegionsIn (body: string list) (startLine: int) : (int * Set<string>) list =
    let labelRx = Regex(@"(?:^|\{|;)\s*([A-Z][A-Za-z0-9_]*)\s*=(?![=>])")
    let out = ResizeArray<int * Set<string>>()
    let stack = Collections.Generic.Stack<int * ResizeArray<string>>()

    let close () =
        let line, labels = stack.Pop()

        if line >= 0 && labels.Count > 0 then
            out.Add(line, Set.ofSeq labels)

    body
    |> List.iteri (fun i raw ->
        let t = stripDocLiterals raw

        // by the position of the NAME, so a label lands in the region open
        // at that point in the line
        let atPos =
            labelRx.Matches t
            |> Seq.map (fun m -> m.Groups[1].Index, m.Groups[1].Value)
            |> Map.ofSeq

        let mutable c = 0

        while c < t.Length do
            match atPos.TryFind c with
            | Some name when stack.Count > 0 -> (snd (stack.Peek())).Add name
            | _ -> ()

            match t[c] with
            | '{' ->
                if c + 1 < t.Length && t[c + 1] = '|' then
                    stack.Push(-1, ResizeArray())
                    c <- c + 1
                else
                    stack.Push(startLine + i, ResizeArray())
            | '}' when stack.Count > 0 -> close ()
            | _ -> ()

            c <- c + 1)

    while stack.Count > 0 do
        close ()

    List.ofSeq out

/// What the fragment arm read: the classes are disjoint and sum to
/// `Checked`, so a collapse in `Resolved` against a steady `Checked` is the
/// tell that the universe or the extractor has broken.
type FragmentCensus = {
    Walked: int
    /// Dotted identifiers read.
    Checked: int
    Resolved: int
    /// Anchored on a name the page itself declares.
    Local: int
    /// Placeholder, vendor or BCL traffic the arm says nothing about.
    Outside: int
    Ambiguous: int
    RecordRegions: int
}

/// How one dotted identifier in a fragment resolved.
[<RequireQualifiedAccess>]
type private Ident =
    | Outside
    | Local
    | Ambiguous
    | Resolved
    | Missing of finding: string

/// The fragment arm. A `skip=fragment` block is exempt from COMPILATION,
/// not from being true: each dotted identifier anchored on a surface
/// container must name a member of it (arm 1), and each record construction
/// that matches one surface record well enough must not write a field it
/// does not have (arm 2). `pageDeclared` is the names a page declares across
/// its blocks and its ambient preamble.
let fragmentArm
    (u: Universe)
    (pageDeclared: Map<string, Set<string>>)
    (blocks: Block list)
    : FragmentCensus * string list =
    let fragments = blocks |> List.filter Block.isFragment

    // A declaration line carries a NAMESPACE path, not a member access.
    let isDeclarationLine (t: string) =
        Regex.IsMatch(t, @"^\s*(open|namespace|#r|#load)\b")
        || Regex.IsMatch(t, @"^\s*(?:\[<[^\]]*>\]\s*)?module\b")

    let anchorOf = anchorIn u.OwnedContainerNames

    let recordAnchorCandidates =
        u.SurfaceContainers
        |> Map.toList
        |> List.filter (fun (_, (fulls, ms)) -> fulls.Length = 1 && ms.Count >= docFragmentMinRecordMatches)
        |> List.map (fun (name, (_, ms)) -> name, ms)

    let perBlock =
        fragments
        |> List.map (fun b ->
            let declared =
                Set.union (declaredNamesIn b.Body) (pageDeclared.TryFind b.Rel |> Option.defaultValue Set.empty)

            let idents =
                b.Body
                |> List.mapi (fun i line -> b.Start + i, stripDocLiterals line)
                |> List.filter (fun (_, t) -> not (isDeclarationLine t))
                |> List.collect (fun (ln, t) ->
                    // An F# identifier admits `'` after its first character
                    // (`CompanionCapability.pure'`), and the baseline renders
                    // the member with it.
                    Regex.Matches(t, @"[A-Za-z_][A-Za-z0-9_']*(?:\.[A-Za-z_][A-Za-z0-9_']*)+")
                    |> Seq.map (fun m -> ln, m.Value)
                    |> List.ofSeq)
                |> List.map (fun (ln, chain) ->
                    match anchorOf chain with
                    | None -> Ident.Outside
                    | Some(root, _) when declared.Contains root -> Ident.Local
                    | Some(root, memberName) ->
                        let fulls, members = u.SurfaceContainers |> Map.find root

                        if fulls.Length > 1 then
                            Ident.Ambiguous
                        elif members.Contains memberName then
                            Ident.Resolved
                        else
                            Ident.Missing(
                                sprintf
                                    "%s:%d (block %d): %s — %s has no member `%s`.\n      nearest: %s"
                                    b.Rel
                                    ln
                                    b.Ordinal
                                    chain
                                    root
                                    memberName
                                    (nearestTo memberName members)
                            ))

            let regions = recordRegionsIn b.Body b.Start

            let recordFindings =
                regions
                |> List.collect (fun (ln, labels) ->
                    if labels.Count < docFragmentMinRecordLabels then
                        []
                    elif u.SurfaceContainers |> Map.exists (fun _ (_, ms) -> Set.isSubset labels ms) then
                        // Every label is a member of SOME surface type, so the
                        // literal is a full construction of a real record. The
                        // anchor candidates exclude a name declared by more
                        // than one container, and without this check a unique
                        // near-miss would be scored best and the real fields
                        // called missing.
                        []
                    else
                        let scored =
                            recordAnchorCandidates
                            |> List.choose (fun (name, ms) ->
                                let hit = Set.intersect labels ms |> Set.count

                                if hit >= docFragmentMinRecordMatches && not (declared.Contains name) then
                                    Some(name, hit, ms)
                                else
                                    None)

                        match scored with
                        | [] -> []
                        | _ ->
                            let best = scored |> List.map (fun (_, hit, _) -> hit) |> List.max

                            match scored |> List.filter (fun (_, hit, _) -> hit = best) with
                            | [ (name, hit, ms) ] when float hit >= 0.5 * float labels.Count ->
                                Set.difference labels ms
                                |> Set.toList
                                |> List.sort
                                |> List.map (fun field ->
                                    sprintf
                                        "%s:%d (block %d): %s has no field `%s` (matched %d of %d labels).\n      nearest: %s"
                                        b.Rel
                                        ln
                                        b.Ordinal
                                        name
                                        field
                                        hit
                                        labels.Count
                                        (nearestTo field ms))
                            | _ -> [])

            idents, regions.Length, recordFindings)

    let idents = perBlock |> List.collect (fun (i, _, _) -> i)

    let count (case: Ident -> bool) =
        idents |> List.filter case |> List.length

    let census = {
        Walked = fragments.Length
        Checked = idents.Length
        Resolved = count (fun i -> i = Ident.Resolved)
        Local = count (fun i -> i = Ident.Local)
        Outside = count (fun i -> i = Ident.Outside)
        Ambiguous = count (fun i -> i = Ident.Ambiguous)
        RecordRegions = perBlock |> List.sumBy (fun (_, n, _) -> n)
    }

    let findings =
        perBlock
        |> List.collect (fun (idents, _, records) ->
            (idents
             |> List.choose (function
                 | Ident.Missing f -> Some f
                 | _ -> None))
            @ records)

    census, findings

// ---- The context a block compiles in ----

/// The ambient opens any ToolUp source file has.
let docSnippetPreamble = [
    "open System"
    "open System.Threading.Tasks"
    "open ToolUp.Platform"
    // The server tier's parts each own a namespace. A page is an excerpt of
    // a deployment that reaches the whole tier, so the preamble opens every
    // part; a consumer's own source derives its opens with the codemod.
    "open ToolUp.Remoting.Server.Http"
    "open ToolUp.Platform.Infra"
    "open ToolUp.Platform.Infra.Metrics"
    "open ToolUp.Platform.Infra.Tracing"
    "open ToolUp.Platform.Infra.Transport"
    "open ToolUp.Platform.Infra.Usage"
    "open ToolUp.Platform.Config"
    "open ToolUp.Platform.Scope"
    "open ToolUp.Platform.Jobs"
    "open ToolUp.Platform.Audit"
    "open ToolUp.Platform.Compose"
    "open ToolUp.Platform.Files"
    "open ToolUp.Platform.Transport"
    "open ToolUp.Platform.Auth"
    "open ToolUp.Platform.VectorKnowledgeTypes"
    "open DataManagementTypes"
]

// The per-package opens, named once. A companion's own README is read in the
// context of its package for the reason `docs/ai/` is: the page is ABOUT that
// package, so its blocks assume it without a ceremonial `open` a reader would
// then copy. Sharing the lists keeps the two entry points from drifting into
// two ideas of what "the AI context" is.
let private aiOpens = [
    "open ToolUp.AI"
    "open ToolUp.AI.AICompose"
    "open ToolUp.Platform.AI"
    "open ToolUp.AI.Wire"
]

let private ragOpens = [ "open ToolUp.RAG"; "open ToolUp.RAG.RAGCompose" ]
let private kbOpens = [ "open ToolUp.KnowledgeBase"; "open SharedTypes" ]

let private formsOpens = [
    "open ToolUp.Forms"
    "open ToolUp.Forms.FormSchema"
    "open ToolUp.Forms.FormSubmission"
    "open ToolUp.Forms.AggregationTypes"
]

let private schedulingOpens = [
    "open ToolUp.Scheduling"
    "open ToolUp.Scheduling.SchedulingTypes"
    "open ToolUp.Scheduling.SchedulingCompose"
]

/// Per doc tree: a page under `docs/rag/` is read in the context of the RAG
/// package, and so is the package's own packed README.
let docSnippetTreePreamble = [
    "docs/ai/", aiOpens
    "docs/rag/", ragOpens
    "docs/knowledge-base/", kbOpens
    "docs/forms/", formsOpens
    "docs/scheduling/", schedulingOpens
    "src/ToolUp.AI/", aiOpens
    "src/AI.Samples/", aiOpens
    "src/AIProviders/", aiOpens
    "src/AICookbooks/", aiOpens
    "src/ToolUp.RAG/", ragOpens
    "src/ToolUp.RAG.StaticCorpus.Server/", ragOpens
    "src/ToolUp.KnowledgeBase/", kbOpens
    "src/ToolUp.Forms/", formsOpens
    "src/ToolUp.Scheduling/", schedulingOpens
]

let preambleFor (rel: string) =
    let tree =
        docSnippetTreePreamble
        |> List.tryPick (fun (prefix, opens) -> if rel.StartsWith prefix then Some opens else None)
        |> Option.defaultValue []

    docSnippetPreamble @ tree

/// The generated module name of a page: its path with every non-alphanumeric
/// turned into `_`, prefixed when it would start with a digit.
let moduleNameOf (rel: string) =
    let stripped = rel.Replace(".md", "")

    let cleaned =
        String([| for c in stripped -> if Char.IsLetterOrDigit c then c else '_' |])

    if Char.IsDigit cleaned[0] then "d" + cleaned else cleaned

// ---- The census and its summary ----

type DocSnippetsCensus = {
    Files: int
    Compiled: int
    Skipped: int
    HighWater: int
    Passing: int
    KnownDrift: int
    NewFailures: int
    FixedButListed: int
    /// Skipped blocks per reason, by reason.
    SkipsByReason: (string * int) list
    AmbientPages: int
    Redeclarations: RedeclarationCensus
    Fragments: FragmentCensus
    /// Skipped blocks whose carried `open` did not resolve.
    UnresolvedOpens: (string * int) list
}

/// The summary block, rendered from the census.
let summaryLines (c: DocSnippetsCensus) : string list = [
    ""
    "VerifyDocSnippets summary:"
    sprintf "  blocks compiled : %d (high-water mark %d)" c.Compiled c.HighWater
    sprintf "  passing         : %d" c.Passing
    sprintf "  known drift     : %d (docs-snippets/known-drift.txt)" c.KnownDrift
    sprintf "  new failures    : %d" c.NewFailures
    sprintf "  fixed-but-listed: %d" c.FixedButListed
    // The unchecked pool, printed every run: a blind spot nobody measures is
    // one that grows.
    for reason, n in c.SkipsByReason do
        sprintf "  skip=%-12s %d" reason n
    sprintf "  ambient pages   : %d (docs-snippets/ambient/)" c.AmbientPages
    // A block that redeclares a public type is invisible to the compile arm,
    // so the size of that pool belongs here for the same reason.
    sprintf
        "  redeclared types: %d in %d block(s) — %d compared, %d not comparable, %d ambiguous"
        c.Redeclarations.Types
        c.Redeclarations.Blocks
        (c.Redeclarations.Types - c.Redeclarations.NotComparable)
        c.Redeclarations.NotComparable
        c.Redeclarations.Ambiguous
    // It MEASURES the largest unchecked pool: how much of `skip=fragment` a
    // compile-free existence check can still see.
    sprintf
        "  fragment symbols: %d fragment(s) walked — %d identifier(s) checked, %d resolved, %d local, %d outside, %d ambiguous; %d record region(s)"
        c.Fragments.Walked
        c.Fragments.Checked
        c.Fragments.Resolved
        c.Fragments.Local
        c.Fragments.Outside
        c.Fragments.Ambiguous
        c.Fragments.RecordRegions
    // A WATCHLIST, not a defect count: an `open` of a deliberately fictional
    // vendor namespace is legitimate in an illustrative fragment; one of a
    // real namespace that has since moved is rot the gate cannot act on.
    if not c.UnresolvedOpens.IsEmpty then
        sprintf "  unresolved opens: %d skipped block(s) — illustrative, or moved?" c.UnresolvedOpens.Length

        for rel, ord in c.UnresolvedOpens do
            sprintf "                    %s#%d" rel ord
]

// ---- The gate ----

/// `VerifyDocSnippets`. Extracts every in-scope `fsharp` block, generates
/// one module per block under `docs-snippets/generated/`, compiles the
/// snippet project with `run` (the driver's process runner), and holds the
/// result to the high-water mark, the empty baseline and the two
/// compile-independent arms. `--update-baseline` rewrites
/// `docs-snippets/known-drift.txt` from the run instead of checking it.
let docSnippets
    (sink: Sink)
    (root: string)
    (flags: Flags)
    (run: string -> string list -> string -> ProcResult)
    : Result<DocSnippetsCensus, Refusal> =
    settle (fun () ->
        let updateBaseline = flags.Switches.Contains "--update-baseline"
        let projDir = Path.Combine(root, "docs-snippets")
        let outDir = Path.Combine(projDir, "generated")
        let baselinePath = Path.Combine(projDir, "known-drift.txt")
        let floorPath = Path.Combine(projDir, "corpus-floor.txt")
        let ambientDir = Path.Combine(projDir, "ambient")

        // The name-scoped walk over `src/**`, pruning `bin` / `obj` /
        // `node_modules` rather than enumerating and discarding them: a
        // packed README is copied into `bin/` on its way to the nupkg, so the
        // same file would otherwise count twice under two paths.
        let rec walkNamed (dir: string) =
            let here =
                Directory.EnumerateFiles dir
                |> Seq.filter (fun f -> docSnippetSrcFileNames.Contains(Path.GetFileName f))
                |> List.ofSeq

            let below =
                Directory.EnumerateDirectories dir
                |> Seq.filter (fun d -> not (docSnippetPrunedDirs.Contains(Path.GetFileName d)))
                |> Seq.collect walkNamed
                |> List.ofSeq

            here @ below

        let docFiles =
            [
                yield!
                    docSnippetRoots
                    |> List.collect (fun r ->
                        let full = Path.Combine(root, r)

                        if Directory.Exists full then
                            Directory.EnumerateFiles(full, "*.md", SearchOption.AllDirectories)
                            |> List.ofSeq
                        else
                            [])

                let srcFull = Path.Combine(root, docSnippetSrcRoot)

                if Directory.Exists srcFull then
                    yield! walkNamed srcFull

                yield!
                    docSnippetLooseFiles
                    |> List.map (fun f -> Path.Combine(root, f))
                    |> List.filter File.Exists
            ]
            |> List.map (fun f -> f, Path.GetRelativePath(root, f).Replace('\\', '/'))
            |> List.filter (fun (_, rel) ->
                docSnippetExcludedTrees |> List.forall (fun t -> not (rel.StartsWith(t + "/"))))
            // A file reachable from two roots is one page, not two.
            |> List.distinctBy snd
            |> List.sortBy snd

        let fsharpBlocks =
            docFiles
            |> List.collect (fun (abs, rel) -> blocksIn rel (File.ReadAllLines abs))
            |> List.filter Block.isFsharp

        match fenceErrors fsharpBlocks with
        | [] -> ()
        | errors ->
            refuse sink errors (sprintf "VerifyDocSnippets: %d invalid fence marker(s) — see above." errors.Length)

        let skippedByReason =
            fsharpBlocks
            |> List.choose Block.skipReason
            |> List.countBy id
            |> List.sortBy fst

        let universe = Universe.read root

        let ambientPathOf (rel: string) =
            Path.Combine(ambientDir, rel.Substring(0, rel.Length - 3) + ".fs")

        // The per-page ambient preamble: its path (for the `#line` directive
        // that makes an error inside it point AT it) and its lines.
        let ambientFor (rel: string) =
            let path = ambientPathOf rel

            if File.Exists path then
                Some(path, File.ReadAllLines path |> List.ofArray)
            else
                None

        let redeclarations, parityFindings =
            parityArm universe (fsharpBlocks |> List.collect declsIn)

        // Page accumulation, exactly as the compile arm gives `open`: a page
        // introduces a type in its first block and reads it from its fifth.
        // The ambient preamble counts for the same reason.
        let pageDeclared =
            fsharpBlocks
            |> List.groupBy _.Rel
            |> List.map (fun (rel, blocks) ->
                rel,
                blocks
                |> List.collect (fun b -> declaredNamesIn b.Body |> Set.toList)
                |> Set.ofList
                |> Set.union (
                    match ambientFor rel with
                    | Some(_, lines) -> declaredNamesIn lines
                    | None -> Set.empty
                ))
            |> Map.ofList

        let fragments, fragmentFindings = fragmentArm universe pageDeclared fsharpBlocks

        let ambientPages =
            docFiles |> List.filter (fun (_, rel) -> (ambientFor rel).IsSome) |> List.length

        if Directory.Exists outDir then
            Directory.Delete(outDir, true)

        Directory.CreateDirectory outDir |> ignore

        let skipped =
            fsharpBlocks
            |> List.filter Block.isSkipped
            |> List.map (fun b -> {
                Block = b
                Hash = Block.hash b
                InScope = false
            })

        let compiled =
            fsharpBlocks
            |> List.groupBy _.Rel
            |> List.collect (fun (rel, blocks) ->
                let m = moduleNameOf rel
                let absPath = docFiles |> List.find (fun (_, r) -> r = rel) |> fst

                let lineDirective line =
                    sprintf "# %d \"%s\"" line (absPath.Replace("\\", "\\\\"))

                // Each carried `open`, stamped with the line it was written on,
                // so an unresolvable one is reported against the block that
                // DECLARED it rather than every block that inherits it. A
                // trailing comment is stripped, not disqualifying; a line that
                // is itself commented out starts with `//`, not `open `.
                let opensOf (b: Block) =
                    b.Body
                    |> List.mapi (fun i l -> i, l.Trim())
                    |> List.filter (fun (_, t) -> t.StartsWith "open ")
                    |> List.choose (fun (i, t) ->
                        let cut = t.IndexOf "//"
                        let text = (if cut >= 0 then t.Substring(0, cut) else t).TrimEnd()
                        if text.Length > 5 then Some(text, b.Start + i) else None)

                let ordered = blocks |> List.sortBy _.Ordinal

                ordered
                |> List.mapi (fun k b -> k, b)
                |> List.filter (fun (_, b) -> not (Block.isSkipped b))
                |> List.map (fun (k, b) ->
                    let carried = ordered |> List.take k |> List.collect opensOf |> List.distinctBy fst

                    // Compared on the same stripped form the carry uses, so a
                    // block that writes `open X // why` does not get a duplicate.
                    let declaresOpen (o: string) =
                        b.Body
                        |> List.exists (fun l ->
                            let t = l.Trim()
                            let cut = t.IndexOf "//"
                            (if cut >= 0 then t.Substring(0, cut) else t).TrimEnd() = o)

                    let text = [
                        yield sprintf "module ToolUp.DocSnippets.Generated.%s_B%02d" m b.Ordinal
                        yield! preambleFor rel
                        // The page's ambient preamble, under its OWN line
                        // directive: an error in it names the ambient file and
                        // is reported as the harness fault it is, rather than
                        // absorbed as drift in a block that inherited it.
                        match ambientFor rel with
                        | Some(ambientPath, ambientLines) ->
                            yield sprintf "# 1 \"%s\"" (ambientPath.Replace("\\", "\\\\"))
                            yield! ambientLines
                        | None -> ()
                        for o, srcLine in carried do
                            if not (declaresOpen o) then
                                yield lineDirective srcLine
                                yield o
                        // Everything after this directive is attributed to the
                        // markdown file: the compiler names the doc.
                        yield lineDirective b.Start
                        yield! b.Body
                    ]

                    File.WriteAllText(
                        Path.Combine(outDir, sprintf "%s_B%02d.fs" m b.Ordinal),
                        text |> List.map (fun l -> l + Environment.NewLine) |> String.concat ""
                    )

                    {
                        Block = b
                        Hash = Block.hash b
                        InScope = true
                    }))

        let units = skipped @ compiled
        let checkedCount = compiled.Length

        sink.Trace(
            sprintf
                "▶ VerifyDocSnippets (1/2): extracted %d compiled block(s) (+%d skipped) from %d file(s)"
                checkedCount
                skipped.Length
                docFiles.Length
        )

        // ---- the self-ratcheting corpus floor (high-water mark) ----
        let floorLines =
            if File.Exists floorPath then
                File.ReadAllLines floorPath |> Array.toList
            else
                []

        let corpusMark = markOf floorLines

        match markMove checkedCount corpusMark with
        | MarkMove.Below ->
            refuse
                sink
                []
                (sprintf
                    "VerifyDocSnippets: only %d block(s) compile, below the recorded high-water mark of %d (docs-snippets/corpus-floor.txt). Blocks that used to be checked no longer are — skip-marked, deleted, or moved into an excluded tree. Restore them, or, if the loss is deliberate and argued, lower the mark BY HAND in the same commit so the decision is in the diff."
                    checkedCount
                    corpusMark)
        | MarkMove.Raise ->
            File.WriteAllText(floorPath, rewriteMark floorLines checkedCount)

            sink.Trace(
                sprintf
                    "VerifyDocSnippets: corpus grew %d -> %d; advanced the high-water mark in docs-snippets/corpus-floor.txt. Include that one-line change in your commit."
                    corpusMark
                    checkedCount
            )
        | MarkMove.Hold -> ()

        sink.Trace "▶ VerifyDocSnippets (2/2): compiling against the real SDK"

        let result =
            run
                "dotnet"
                [
                    "build"
                    "docs-snippets/ToolUp.DocSnippets.fsproj"
                    "--nologo"
                    "-v"
                    "quiet"
                ]
                root

        let output = result.StdOut + result.StdErr

        let errorLines =
            output.Split('\n')
            |> Array.map _.Trim()
            |> Array.filter (fun l -> l.Contains ": error ")
            |> Array.distinct
            |> List.ofArray

        let attributed =
            errorLines
            |> List.choose (fun l -> attribute units l |> Option.map (fun c -> c, l))

        // An error the harness cannot pin to a block is a harness fault (a
        // broken preamble, a missing reference) and is never absorbed as doc
        // drift.
        let unattributed = errorLines |> List.filter (fun l -> (attribute units l).IsNone)

        if not unattributed.IsEmpty then
            refuse
                sink
                (unattributed |> List.truncate 20)
                (sprintf
                    "VerifyDocSnippets: %d compiler error(s) could not be attributed to a documentation block. That is a fault in the harness (preamble, project references, or the generated project), not drift in the docs."
                    unattributed.Length)

        // Errors in a SKIPPED block are dropped — it was declared uncheckable,
        // and its lines reach the compiler only as an `open` carried onto a
        // later block — but they are COUNTED: an unresolvable `open` in a
        // skipped block is rot the gate cannot act on, the most direct
        // evidence of drift inside the blind spot.
        let inSkipped =
            attributed
            |> List.filter (fun (c, _) -> not c.InScope)
            |> List.map (fun (c, _) -> c.Block.Rel, c.Block.Ordinal)
            |> List.distinct

        let failing =
            attributed
            |> List.filter (fun (c, _) -> c.InScope)
            |> List.groupBy (fun (c, _) -> c)
            |> List.map (fun (c, es) -> c, es |> List.map snd)
            |> List.sortBy (fun (c, _) -> c.Block.Rel, c.Block.Ordinal)

        let failingKeys = failing |> List.map (fst >> keyOf) |> Set.ofList

        let baseline =
            if File.Exists baselinePath then
                readBaseline (File.ReadAllLines baselinePath |> List.ofArray)
            else
                Map.empty

        let baselineKeys = baseline |> Map.keys |> Set.ofSeq

        let newFailures =
            failing |> List.filter (fun (c, _) -> not (baselineKeys.Contains(keyOf c)))

        let fixedButListed = baselineKeys - failingKeys

        let census = {
            Files = docFiles.Length
            Compiled = checkedCount
            Skipped = skipped.Length
            HighWater = corpusMark
            Passing = checkedCount - failing.Length
            KnownDrift = baselineKeys.Count
            NewFailures = newFailures.Length
            FixedButListed = fixedButListed.Count
            SkipsByReason = skippedByReason
            AmbientPages = ambientPages
            Redeclarations = redeclarations
            Fragments = fragments
            UnresolvedOpens = List.sort inSkipped
        }

        if updateBaseline then
            let lines = [
                "# Documentation snippets that name an API the SDK does not have."
                "#"
                "# EMPTY IS THE ENFORCED STATE. Any entry below fails"
                "# `dotnet run --project Build.fsproj -- VerifyDocSnippets`."
                "#"
                "# This list was a migration device. When the gate landed it held 231"
                "# entries — blocks that predated it and had already rotted — and it was a"
                "# ratchet that could only shrink. It reached zero, so the ratchet has"
                "# nothing left to hold: a block that does not compile is now simply a"
                "# failure, and the fix is the snippet, against the current SDK surface."
                "#"
                "# `--update-baseline` rewrites this file from a run and remains the"
                "# documented escape for a wholesale re-measurement. It is not a way of"
                "# going green: whatever it writes here still fails the gate."
                "#"
                "# Each line, when one exists: <content-hash> <doc path>#<block> — <first error>"
                "# The KEY is the full triple `<doc path>#<block> <content-hash>` — never the"
                "# hash alone, which identical blocks in different files share."
                ""
                for c, errs in failing do
                    let first = errs |> List.tryHead |> Option.defaultValue ""

                    // Keep the message, drop MSBuild's trailing ` [<path>.fsproj]`
                    // (a machine-local path in a tracked file is churn), and
                    // flatten control characters: FSC embeds raw 0x1D
                    // separators in its hints, and a tracked file carrying them
                    // reads as BINARY to git.
                    let msg =
                        let i = first.IndexOf ": error "
                        let m = if i >= 0 then first.Substring(i + 2) else first
                        let j = m.LastIndexOf " ["
                        let trimmed = if j >= 0 && m.EndsWith "]" then m.Substring(0, j) else m

                        let flattened =
                            String([| for ch in trimmed -> if Char.IsControl ch then ' ' else ch |])

                        Regex.Replace(flattened, @"\s+", " ").Trim()

                    sprintf "%s %s#%d (line %d) — %s" c.Hash c.Block.Rel c.Block.Ordinal c.Block.Start msg
            ]

            // LF explicitly: the file is tracked and `.gitattributes` pins LF.
            File.WriteAllText(baselinePath, String.Join("\n", lines) + "\n")
            sink.Trace(sprintf "VerifyDocSnippets: wrote %d baseline entries to %s" failing.Length baselinePath)
        else
            summaryLines census |> List.iter sink.Trace

            if not newFailures.IsEmpty then
                sink.Trace ""

                refuse
                    sink
                    [
                        for c, errs in newFailures do
                            sprintf "%s — block %d (opens at line %d):" c.Block.Rel c.Block.Ordinal (c.Block.Start - 1)

                            for e in errs |> List.truncate 8 do
                                "    " + e
                    ]
                    (sprintf
                        "VerifyDocSnippets: %d documentation block(s) name an API the SDK does not have. Fix the snippet against the current surface; do NOT add it to known-drift.txt — that list may only shrink. If the block genuinely cannot be compiled (an .fsi-shaped listing, an elided excerpt, a deliberate anti-pattern), mark its fence with a skip reason instead."
                        newFailures.Length)

            if not fixedButListed.IsEmpty then
                sink.Trace ""
                sink.Trace "Delete these lines from docs-snippets/known-drift.txt, verbatim:"

                refuse
                    sink
                    [ for k in fixedButListed -> baseline |> Map.find k ]
                    (sprintf
                        "VerifyDocSnippets: %d baseline entr(ies) in docs-snippets/known-drift.txt now compile. Delete the FULL lines printed above — match on path, ordinal AND hash, never on the hash alone: identical illustrative blocks in different files share a hash, and a hash-only prune deletes the wrong file's entry silently. The ratchet only holds if a fixed snippet is removed from the list."
                        fixedButListed.Count)

            // After the compile arms (a block that does not compile is the
            // more urgent diagnosis) and before the baseline-zero check (a
            // stale redeclaration is a concrete fix; that one is a policy).
            // Zero is the enforced state and there is no baseline.
            if not parityFindings.IsEmpty then
                sink.Trace ""

                refuse
                    sink
                    (parityFindings |> List.map (fun f -> "    " + f))
                    (sprintf
                        "VerifyDocSnippets: %d documentation block(s) REDECLARE a public SDK type with a member the surface does not have. A redeclaration shadows the real type, so the compile arm above cannot see this — that is why the check exists. Fix the snippet against api-baselines/<assembly>.approved.txt (the rendered public surface). Showing FEWER members than the SDK has is fine and needs no marker; showing a member it does not have is not."
                        parityFindings.Length)

            // Last of the compile-independent arms: the weakest claim, that a
            // name exists. Zero is the enforced state and there is no baseline.
            if not fragmentFindings.IsEmpty then
                sink.Trace ""

                refuse
                    sink
                    (fragmentFindings |> List.map (fun f -> "    " + f))
                    (sprintf
                        "VerifyDocSnippets: %d finding(s) — a `skip=fragment` block names an SDK symbol the public surface does not have. A fragment is exempt from COMPILATION, not from being true — the marker says the block cannot be compiled, never that its API names stopped mattering. Fix the snippet against api-baselines/<assembly>.approved.txt. If the name is a placeholder or a local of the surrounding program the page does not show, it must not be spelled like an SDK container: rename it, or give the page a docs-snippets/ambient/ preamble and drop the skip marker entirely, which is the better fix — a block under the compile arm needs no lint at all. See docs-snippets/README.md."
                        fragmentFindings.Length)

            // The baseline reached zero, so EMPTY is the enforced state: an
            // entry is a defect rather than a state, because the risk the
            // ratchet was shaped around is the list growing again one
            // plausible line at a time while the gate reports "0 new
            // failures". Runs after the two above, so a run that both fails
            // and lists gets the specific diagnosis first.
            if not baselineKeys.IsEmpty then
                sink.Trace ""

                refuse
                    sink
                    [ for k in baselineKeys -> baseline |> Map.find k ]
                    (sprintf
                        "VerifyDocSnippets: docs-snippets/known-drift.txt holds %d entr(ies), and empty is the enforced state. The baseline was a migration device for the drift that predated this gate; it reached zero, so a block that does not compile is a failure to fix, not a line to record. Fix the snippet against the current SDK surface, or — if the block genuinely cannot be compiled — mark its fence with a skip reason. `--update-baseline` remains the escape for a wholesale re-measurement, but what it writes still fails here."
                        baselineKeys.Count)

            // An unexplained non-zero exit — no failing block AND no error
            // absorbed by a skipped one — is a harness fault: a restore
            // failure, an MSBuild-level error. It must NOT fire on the
            // explained case, or the gate could never go green.
            if result.ExitCode <> 0 && failing.IsEmpty && inSkipped.IsEmpty then
                sink.Trace output

                refuse
                    sink
                    []
                    (sprintf
                        "VerifyDocSnippets: the snippet project failed to build for a reason not attributable to any block (exit %d)."
                        result.ExitCode)

            sink.Trace ""

            sink.Trace(
                sprintf
                    "VerifyDocSnippets: OK — %d block(s) compile, %d known-drift held at the ratchet."
                    census.Passing
                    census.KnownDrift
            )

        census)

// ---- VerifySourceCitations: comment-cited API resolution ----
//
// Everything above gates DOCS. A comment in `src/**/*.fs` is read with
// more trust than a doc page and checked by nothing at all: it ships
// inside the source it describes, so a reader — human or agent — takes
// it as the local authority and follows it without a second thought.
// A documentation burn-down surfaced the resulting class:
// `ServerApp.withEntityStore` named in two comments and a migration doc
// when the helper is `withEntity<'T>`; `ServerApp.withEntities` cited
// where nothing of that name has ever existed;
// `RedisNotificationChannelValidator.create` in three comments for a
// module called `RedisValidator`; and one pointer comment reading
// `UserSession.fs:342` copied verbatim into fourteen files while the
// binding it points at sat four hundred lines further down.
//
// The check is deliberately NARROWER than the sweep that preceded it,
// and the boundary is mechanical checkability rather than importance:
//
//   * ARM 1 — a `file.fs:NNN` pointer whose file RESOLVES. The claim is
//     that the file still has that many lines, and — where the same
//     comment names a binding as `File.member` — that the member's name
//     still occurs near the cited line. A citation naming a path this
//     repo does not hold is UNKNOWN, not wrong: a comment may legitimately
//     point into a consumer's tree. The one exception is a path that
//     begins `src/`, which can only mean this repo, so an unresolvable
//     one is a finding.
//
//   * ARM 2 — a BACKTICK-QUALIFIED `Container.member` citation, resolved
//     against the same api-baseline universe the fragment arm holds a fragment
//     to, through the same `anchorIn`. Backticks are the whole
//     false-positive budget: comment prose is full of dotted things
//     (`ctx.Request.Path`, a sentence's `e.g.`, a URL), and a bare-token
//     scan of it would need an allow-list, which this repo has twice
//     argued against. A name a writer chose to mark as code is a name the
//     writer is claiming exists.
//
// What stays OUT, on purpose: bare (un-backticked) API prose, a
// citation of a private helper the public surface does not render (the
// anchor rule makes those silent by construction — the lint speaks only
// about names the surface OWNS), whether a comment is TRUE, and whether
// a public member has a comment at all. That last one is the doc-coverage
// ratchet's, a different question: it owns PRESENCE, this owns ACCURACY.
// A noisy lint is a disabled lint, which is worth more than the extra
// findings a fuzzier rule would buy.
//
// ARM 1's CORPUS IS ZERO AT LANDING, and that is the intended end
// state rather than a reason to delete it. The sweep that shipped
// with this check converted all twenty surviving `file.fs:NNN`
// pointers to name or file citations, because a line number is the
// one citation shape that cannot be made durable — it rots on any
// edit above it and rots SILENTLY, since the file still exists and
// the line still has content. So the arm is prospective: it holds
// the first pointer someone writes to the two claims it can check,
// and the census line says how many exist. A zero there is the rule
// in CONTRIBUTING.md being kept, not a check with nothing to do.
//
// Usage: `dotnet run --project Build.fsproj -- VerifySourceCitations`
let sourceCitationWindow = 15

/// A line's COMMENT TEXT, or nothing, by its 1-based line number. String
/// literals are erased FIRST, deliberately: a `"http://…"` or a
/// `"(* not a comment *)"` in code must never be read as prose, and cutting
/// at `//` before erasing strings gets exactly that wrong. `(*)` is the
/// multiplication operator, not a block opener.
///
/// A hand lexer rather than the compiler's tokens: it reads three shapes
/// (`//`, `(* … *)`, and the literals that hide them), and `DocGatesTests`
/// holds it to the corpus CONTRIBUTING.md "Citing APIs in comments"
/// describes. Reading FCS tokens instead would put the F# compiler service
/// on the driver's and the Build pack's dependency graph — a package the
/// Analyzers pack carries and neither of these does — for a lexer this
/// small.
let commentLinesOf (lines: string array) : (int * string) list =
    let out = ResizeArray<int * string>()
    let mutable inBlock = false

    lines
    |> Array.iteri (fun i raw ->
        let noStr =
            Regex.Replace(Regex.Replace(raw, @"""""""[\s\S]*?""""""", @""""""), @"""(\\.|[^""\\])*""", @"""""")

        let sb = Text.StringBuilder()
        let mutable c = 0
        let mutable lineDone = false

        while not lineDone && c < noStr.Length do
            if inBlock then
                if c + 1 < noStr.Length && noStr[c] = '*' && noStr[c + 1] = ')' then
                    inBlock <- false
                    c <- c + 2
                else
                    sb.Append noStr[c] |> ignore
                    c <- c + 1
            elif c + 1 < noStr.Length && noStr[c] = '/' && noStr[c + 1] = '/' then
                sb.Append(noStr.Substring(c + 2)) |> ignore
                lineDone <- true
            elif
                c + 1 < noStr.Length
                && noStr[c] = '('
                && noStr[c + 1] = '*'
                && not (c + 2 < noStr.Length && noStr[c + 2] = ')')
            then
                inBlock <- true
                c <- c + 2
            else
                c <- c + 1

        if sb.Length > 0 then
            out.Add(i + 1, sb.ToString()))

    List.ofSeq out

let private pointerRx =
    Regex(@"(?<path>[A-Za-z0-9_./\\+-]*[A-Za-z0-9_])\.fs:(?<from>\d{1,6})(?:\s*[-–—]\s*(?<to>\d{1,6}))?")

let private namedBindingRx =
    Regex(@"`([A-Z][A-Za-z0-9_]*)\.([A-Za-z_][A-Za-z0-9_]*)`")

let private citationRx =
    Regex(@"`([A-Z][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+)`")

let private containerHeadRx =
    Regex(
        @"^(\s*)(?:\[<[^\]]*>\]\s*)*(module|type)\s+(?:rec\s+|private\s+|internal\s+|public\s+)*(?:\[<[^\]]*>\]\s*)*([A-Za-z_][A-Za-z0-9_.']*)"
    )

let private namespaceRx =
    Regex(@"^\s*(?:namespace|open)\s+(?:rec\s+|global\.)?([A-Za-z_][A-Za-z0-9_.]*)")

// The modifier run is a REPEATED alternation rather than a fixed order,
// because F# permits several and the orders differ: `member inline
// this.addCellRange`, `member private this.IsLastOwner`, `static member val
// internal Instance`, `let mutable private state`. An ordered pattern
// captured the FIRST modifier as the name.
let private declRx =
    Regex(
        @"^(\s*)(?:\[<[^\]]*>\]\s*)*(?:let|and|member|abstract|static|override|default|val)\b(?:\s+(?:inline|mutable|rec|private|internal|public|val|member|static))*\s+(?:[A-Za-z_][A-Za-z0-9_]*\.)?([A-Za-z_][A-Za-z0-9_']*)"
    )

let private caseOrFieldRx =
    Regex(@"^(\s*)(?:\|\s*)?([A-Z][A-Za-z0-9_']*)\s*(?::|of\b|$)")

// A dotted token in prose is not always a member access. A FILE PATH
// (`Directory.Build.props`, `Client.js`) has a segment that is a source
// extension this repo holds; an EXTERNAL namespace (`System.Diagnostics`,
// `Google.Api.Gax`) has a root the tree opens or declares but the surface
// never renders a type under; a QUALIFICATION step's "member" is itself a
// container the surface owns. Each is derived — there is no list of
// blessed names.
let private citationFileExtensions =
    set [
        "config"
        "css"
        "csproj"
        "dll"
        "fs"
        "fsi"
        "fsproj"
        "fsx"
        "html"
        "js"
        "json"
        "md"
        "nupkg"
        "props"
        "ps1"
        "sln"
        "targets"
        "ts"
        "txt"
        "xml"
        "yaml"
        "yml"
    ]

/// What the repository declares over and above what it publishes, for the
/// objection that the surface is PUBLIC while a comment's writer can see the
/// whole module. `Decls` attributes every declared name to EVERY enclosing
/// container by indentation (over-accepting only ever passes a comment);
/// `NamespacePairs` holds parent/child segment pairs from the baseline full
/// names and from every `namespace` and `open` in the tree, so a package
/// path is not read as a member access; `SameUnit` answers whether a name is
/// at least declared in a file that declares the container it hangs off —
/// "the helper next to that type" is how prose refers to one. All rebuilt
/// from source on every run: there is no second baseline to drift.
type private SourceIndex = {
    Decls: Map<string, Set<string>>
    NamespacePairs: Set<string * string>
    SameUnit: string -> string -> bool
}

let private indexSources (u: Universe) (sources: (string * string array) list) : SourceIndex =
    let addPath (path: string) =
        let segs = path.Split('.')
        [ for i in 0 .. segs.Length - 2 -> segs[i], segs[i + 1] ]

    let surfacePairs =
        u.RealTypes |> List.collect (fun (full, _) -> addPath (full.Split('+')[0]))

    // Per file: the namespace pairs it opens, the containers it heads, the
    // names it declares, and each name's enclosing containers.
    let perFile =
        sources
        |> List.map (fun (_, lines) ->
            let step (stack: (int * string) list, acc) (raw: string) =
                let cut = raw.IndexOf "//"
                let line = if cut >= 0 then raw.Substring(0, cut) else raw
                let ns = namespaceRx.Match line

                let pairs = if ns.Success then addPath ns.Groups[1].Value else []

                let head = containerHeadRx.Match line

                if head.Success then
                    let indent = head.Groups[1].Value.Length

                    // A head with no `=` is a FILE-SCOPE declaration whose
                    // contents sit at its own indent, so it is recorded at -1
                    // or everything under it dedents straight out of scope.
                    let scopeIndent =
                        if line.Contains "=" || head.Groups[2].Value = "type" then
                            indent
                        else
                            -1

                    let kept =
                        if scopeIndent >= 0 then
                            stack |> List.skipWhile (fun (i, _) -> i >= scopeIndent)
                        else
                            stack

                    let name = simpleNameOf (head.Groups[3].Value.TrimEnd '.')
                    (scopeIndent, name) :: kept, (pairs, Some name, Some name, []) :: acc
                else
                    let m = declRx.Match line
                    let m2 = if m.Success then m else caseOrFieldRx.Match line

                    if m2.Success then
                        let indent = m2.Groups[1].Value.Length
                        let name = m2.Groups[m2.Groups.Count - 1].Value
                        let kept = stack |> List.skipWhile (fun (i, _) -> i >= indent)
                        kept, (pairs, None, Some name, kept |> List.map (fun (_, c) -> c, name)) :: acc
                    else
                        stack, (pairs, None, None, []) :: acc

            let _, steps = lines |> Array.fold step ([], [])
            let steps = List.rev steps

            {|
                Pairs = steps |> List.collect (fun (p, _, _, _) -> p)
                Containers = steps |> List.choose (fun (_, c, _, _) -> c) |> Set.ofList
                Declared = steps |> List.choose (fun (_, _, d, _) -> d) |> Set.ofList
                Attributed = steps |> List.collect (fun (_, _, _, a) -> a)
            |})

    let containerFiles =
        perFile
        |> List.mapi (fun ix f -> f.Containers |> Set.toList |> List.map (fun c -> c, ix))
        |> List.concat
        |> List.groupBy fst
        |> List.map (fun (c, ixs) -> c, ixs |> List.map snd)
        |> Map.ofList

    let declaredByFile = perFile |> List.map _.Declared |> Array.ofList

    {
        Decls =
            perFile
            |> List.collect _.Attributed
            |> List.groupBy fst
            |> List.map (fun (c, names) -> c, names |> List.map snd |> Set.ofList)
            |> Map.ofList
        NamespacePairs = surfacePairs @ (perFile |> List.collect _.Pairs) |> Set.ofList
        SameUnit =
            fun container name ->
                match containerFiles.TryFind container with
                | Some ixs -> ixs |> List.exists (fun i -> declaredByFile[i].Contains name)
                | None -> false
    }

/// What the source-citation gate read: each class disjoint, so the classes
/// of a family sum to what was seen.
type SourceCitationsCensus = {
    Files: int
    PointersSeen: int
    PointersChecked: int
    /// `File.member` bindings named beside a checked pointer.
    PointersNamed: int
    PointersOutside: int
    PointersAmbiguous: int
    CitationsSeen: int
    /// On the public surface.
    CitationsResolved: int
    /// Declared in-tree but not public.
    CitationsLocal: int
    /// A namespace path rather than a member access.
    CitationsNamespace: int
    CitationsOutside: int
    CitationsAmbiguous: int
}

/// How one `file.fs:NNN` pointer resolved.
[<RequireQualifiedAccess>]
type private Pointer =
    | Outside
    | Ambiguous
    | Missing of finding: string
    /// Resolved to a file; the findings of its length and named-binding
    /// checks, and how many bindings it named.
    | Checked of named: int * findings: string list

/// How one backticked `Container.member` citation resolved.
[<RequireQualifiedAccess>]
type private Citation =
    | Outside
    | Ambiguous
    | Resolved
    | Namespace
    | Local
    | Missing of finding: string

/// The source-citation arms over `src/**/*.fs`, as (repository-relative
/// path, lines). Arm 1: a `file.fs:NNN` pointer whose file resolves must fit
/// that file, and a `File.member` named in the same comment must occur near
/// the cited line; an unresolvable path is unknown, except one beginning
/// `src/`, which can only mean this repository. Arm 2: a backticked
/// `Container.member` anchored on a surface container must name a member of
/// it, a namespace step, or a name the tree declares.
let citationArm (u: Universe) (sources: (string * string array) list) : SourceCitationsCensus * string list =
    let byRelPath = sources |> Map.ofList

    let byBaseName =
        sources |> List.groupBy (fun (rel, _) -> Path.GetFileName rel) |> Map.ofList

    let anchorOf = anchorIn u.OwnedContainerNames
    let index = indexSources u sources

    let externalRoots =
        let surfaceRoots =
            u.RealTypes
            |> List.map (fun (full: string, _) -> (full.Split('+')[0]).Split('.')[0])
            |> Set.ofList

        let openedRoots =
            sources
            |> Seq.collect snd
            |> Seq.choose (fun l ->
                let m = namespaceRx.Match l

                if m.Success then
                    Some(m.Groups[1].Value.Split('.')[0])
                else
                    None)
            |> Set.ofSeq

        Set.difference openedRoots surfaceRoots

    let read =
        sources
        |> List.collect (fun (rel, lines) ->
            commentLinesOf lines
            |> List.map (fun (ln, text) ->
                let where = sprintf "%s:%d" rel ln

                let pointers =
                    pointerRx.Matches text
                    |> Seq.map (fun m ->
                        let token = m.Groups["path"].Value
                        let citedFrom = int m.Groups["from"].Value

                        let citedTo =
                            if m.Groups["to"].Success then
                                int m.Groups["to"].Value
                            else
                                citedFrom

                        let target =
                            if token.Contains "/" || token.Contains "\\" then
                                let relPath = token.Replace('\\', '/') + ".fs"

                                match byRelPath.TryFind relPath with
                                | Some ls -> Ok(relPath, ls)
                                | None when relPath.StartsWith "src/" ->
                                    Error(
                                        Pointer.Missing(
                                            sprintf
                                                "%s: cites `%s:%d`, and this repo has no such file."
                                                where
                                                relPath
                                                citedFrom
                                        )
                                    )
                                | None -> Error Pointer.Outside
                            else
                                match byBaseName.TryFind(token + ".fs") with
                                | Some [ (r, ls) ] -> Ok(r, ls)
                                // Two files share this basename, so the
                                // citation resolves to neither: unknown, never
                                // wrong.
                                | Some(_ :: _ :: _) -> Error Pointer.Ambiguous
                                | _ -> Error Pointer.Outside

                        match target with
                        | Error p -> p
                        | Ok(targetRel, (targetLines: string array)) ->
                            if targetLines.Length < citedTo then
                                Pointer.Checked(
                                    0,
                                    [
                                        sprintf
                                            "%s: cites `%s:%d`, but that file has %d line(s)."
                                            where
                                            targetRel
                                            citedTo
                                            targetLines.Length
                                    ]
                                )
                            else
                                let container = Path.GetFileNameWithoutExtension targetRel

                                let named =
                                    namedBindingRx.Matches text
                                    |> Seq.filter (fun b -> b.Groups[1].Value = container)
                                    |> Seq.map (fun b -> b.Groups[2].Value)
                                    |> Seq.distinct
                                    |> List.ofSeq

                                let lo = max 1 (citedFrom - sourceCitationWindow)
                                let hi = min targetLines.Length (citedTo + sourceCitationWindow)

                                Pointer.Checked(
                                    named.Length,
                                    named
                                    |> List.filter (fun member' ->
                                        not (
                                            seq { lo..hi }
                                            |> Seq.exists (fun i ->
                                                Regex.IsMatch(
                                                    targetLines[i - 1],
                                                    @"\b" + Regex.Escape member' + @"\b"
                                                ))
                                        ))
                                    |> List.map (fun member' ->
                                        sprintf
                                            "%s: cites `%s.%s` at `%s:%d`, but `%s` does not occur within %d line(s) of there."
                                            where
                                            container
                                            member'
                                            targetRel
                                            citedFrom
                                            member'
                                            sourceCitationWindow)
                                ))
                    |> List.ofSeq

                let citations =
                    citationRx.Matches text
                    |> Seq.map (fun m ->
                        let chain = m.Groups[1].Value
                        let segs = chain.Split('.')

                        if
                            segs |> Array.exists citationFileExtensions.Contains
                            || externalRoots.Contains segs[0]
                        then
                            Citation.Outside
                        else
                            match anchorOf chain with
                            | None -> Citation.Outside
                            | Some(root, memberName) ->
                                let fulls, members = u.SurfaceContainers |> Map.find root

                                let declaredLocally =
                                    (index.Decls.TryFind root
                                     |> Option.map (Set.contains memberName)
                                     |> Option.defaultValue false)
                                    || index.SameUnit root memberName

                                if fulls.Length > 1 then
                                    Citation.Ambiguous
                                elif members.Contains memberName then
                                    Citation.Resolved
                                elif
                                    index.NamespacePairs.Contains(root, memberName)
                                    || u.OwnedContainerNames.Contains memberName
                                then
                                    Citation.Namespace
                                elif declaredLocally then
                                    Citation.Local
                                else
                                    Citation.Missing(
                                        sprintf
                                            "%s: `%s` — %s has no member `%s`.\n      nearest: %s"
                                            where
                                            chain
                                            root
                                            memberName
                                            (nearestTo memberName members)
                                    ))
                    |> List.ofSeq

                pointers, citations))

    let pointers = read |> List.collect fst
    let citations = read |> List.collect snd

    let countP case =
        pointers |> List.filter case |> List.length

    let countC case =
        citations |> List.filter case |> List.length

    let census = {
        Files = sources.Length
        PointersSeen = pointers.Length
        PointersChecked =
            countP (function
                | Pointer.Checked _ -> true
                | _ -> false)
        PointersNamed =
            pointers
            |> List.sumBy (function
                | Pointer.Checked(n, _) -> n
                | _ -> 0)
        PointersOutside = countP (fun p -> p = Pointer.Outside)
        PointersAmbiguous = countP (fun p -> p = Pointer.Ambiguous)
        CitationsSeen = citations.Length
        CitationsResolved = countC (fun c -> c = Citation.Resolved)
        CitationsLocal = countC (fun c -> c = Citation.Local)
        CitationsNamespace = countC (fun c -> c = Citation.Namespace)
        CitationsOutside = countC (fun c -> c = Citation.Outside)
        CitationsAmbiguous = countC (fun c -> c = Citation.Ambiguous)
    }

    // Findings in reading order: per comment line, its pointers then its
    // citations.
    let findings =
        read
        |> List.collect (fun (ps, cs) ->
            (ps
             |> List.collect (function
                 | Pointer.Missing f -> [ f ]
                 | Pointer.Checked(_, fs) -> fs
                 | _ -> []))
            @ (cs
               |> List.choose (function
                   | Citation.Missing f -> Some f
                   | _ -> None)))

    census, findings

/// The source-citation summary block, rendered from the census.
let citationSummaryLines (c: SourceCitationsCensus) : string list = [
    ""
    "VerifySourceCitations summary:"
    sprintf "  source files    : %d (src/**/*.fs)" c.Files
    sprintf
        "  line pointers   : %d seen — %d checked, %d named a binding, %d outside, %d ambiguous"
        c.PointersSeen
        c.PointersChecked
        c.PointersNamed
        c.PointersOutside
        c.PointersAmbiguous
    sprintf
        "  API citations   : %d backticked — %d on the surface, %d declared in-tree, %d namespace path(s), %d outside, %d ambiguous"
        c.CitationsSeen
        c.CitationsResolved
        c.CitationsLocal
        c.CitationsNamespace
        c.CitationsOutside
        c.CitationsAmbiguous
]

/// Every `.fs` under `dir`, pruning build output and `node_modules`, and
/// test projects when `skipTests`.
let private sourceFiles (skipTests: bool) (dir: string) =
    let rec walk (d: string) = seq {
        for sub in Directory.EnumerateDirectories d do
            let name = Path.GetFileName sub

            if
                name <> "bin"
                && name <> "obj"
                && name <> "output"
                && name <> "node_modules"
                && not (skipTests && name.EndsWith ".Tests")
            then
                yield! walk sub

        yield! Directory.EnumerateFiles(d, "*.fs")
    }

    walk dir |> List.ofSeq

/// `VerifySourceCitations`, over `<root>/src/**/*.fs`.
let sourceCitations (sink: Sink) (root: string) : Result<SourceCitationsCensus, Refusal> =
    settle (fun () ->
        let sources =
            sourceFiles false (Path.Combine(root, "src"))
            |> List.map (fun f -> Path.GetRelativePath(root, f).Replace('\\', '/'), File.ReadAllLines f)

        let census, findings = citationArm (Universe.read root) sources
        citationSummaryLines census |> List.iter sink.Trace

        if not findings.IsEmpty then
            sink.Trace ""

            refuse
                sink
                (findings |> List.map (fun f -> "    " + f))
                (sprintf
                    "VerifySourceCitations: %d comment(s) in src/**/*.fs cite something that is not there. Fix the citation against the current surface (api-baselines/<assembly>.approved.txt renders it). Prefer citing an API BY NAME over a bare `file.fs:NNN` pointer — a name is checkable and a line number rots the moment anything above it moves; where the pointer adds nothing a name cannot, delete it. See CONTRIBUTING.md, \"Citing APIs in comments\"."
                    findings.Length)

        sink.Trace ""

        sink.Trace(
            sprintf
                "VerifySourceCitations: OK — %d pointer(s) and %d API citation(s) resolve."
                census.PointersChecked
                census.CitationsResolved
        )

        census)

// ---- VerifyUploadResults: a discarded write result is a build failure ----
//
// `IBlobStorage.Upload` returns `Async<Result<string, string>>` and every
// shipped blob store reports a failed write as `Error`, never as an
// exception. So `let! _ = storage.Upload(...)` is a write that cannot
// fail as far as its caller knows: before this phase the default event
// store did it, which made a lost audit row count as a success under
// every audit failure policy, and the blob idempotency store did it,
// which let a retry re-execute a call it claimed to have memoised.
//
// The check is textual and deliberately narrow — the shape that has
// actually shipped, in the two layouts Fantomas produces for it:
//
//   let! _ = storage.Upload(container, name, bytes)
//   let! _ =
//       storage.Upload(container, name, bytes)
//
// `UploadWithETag` (the conditional write) is the same call for this
// purpose and is matched too: its `Result` says whether the write — or
// the precondition — held, and discarding it discards both.
//
// Where best-effort IS the design the site matches the `Error` and logs
// it at Warn, which is not this shape. A discard that must stay a
// discard carries the marker `// best-effort-write: <why>` on the
// discarding line (either line of the two-line form) — one line that
// says so, where a reader meets it.
// The rule, and how a store author chooses between propagating and
// declaring best-effort: docs/platform/storage-write-results.md.
//
// The fan-out shape. A list of writes run through `Async.Parallel` and
// then discarded drops every result at once, and no line of it binds `_`
// to a write:
//
//   do!
//       names
//       |> List.map (fun name -> storage.Upload(container, name, bytes))
//       |> Async.Parallel
//       |> Async.Ignore
//
// Two erasure paths shipped exactly this, so a refused redaction read as
// a completed erasure. The walk therefore also reads the EXPRESSION a
// discard applies to: the pipeline an `Async.Ignore` ends (its head and
// every stage, lambda bodies included), and the whole expression a
// `let! _ =` binds when it spans several lines. A write inside whose
// result is the element's value is the same discard, reported at the
// discarding line; the marker is read there or on the line that makes the
// call. A call whose result is bound to a name or matched on inside the
// element is handled where it is made, and is not a site. A helper that
// wraps the call (a function returning the write's `Result` under another
// name) is beyond a textual check and is held to the rule by review.
//
// `Delete`, in every shape. `IBlobStorage.Delete` is idempotent on a
// missing blob (`IBlobStorageContract` pins it on every bound backend), so
// an `Error` from it is a refusal: a fan-out of deletes, a `let! _ =` over
// one, and a single call discarded where it is made (`let! _ =
// x.Delete(…)`, or `do! x.Delete(…) |> Async.Ignore`) are each a site. A
// delete that is the operation's own effect (an undo of its own write, a
// claim release, the removal of an asset's bytes) propagates; a delete
// that is cleanup carries the marker naming the pass that re-sweeps it
// (storage-write-results.md).
//
// Scope: production code under `src/`. Test projects (a directory named
// `*.Tests`) are excluded — a fixture that seeds a blob is not a store,
// and its failure fails the test that reads it. Comment lines are
// skipped, so prose quoting the pattern is not a finding.
//
// Usage: `dotnet run --project Build.fsproj -- VerifyUploadResults`
//        `… -- VerifyUploadResults --root <dir>` scans `<dir>/src` instead
//        of this repository's (how the check is shown red on a planted
//        discard without touching the tree).
// The result-bearing writes. An upload was the first shape to ship
// discarded; the same discard of a store `Remove`, a secret `SetSecret`, a
// team-store `SetActiveTeam` / `DeleteTeam` or a scheduler `TriggerOnce` hid
// a refused write beside a comment that relied on it. Each returns a
// `Result` its caller must see, so each is held to the upload rule: one
// line, two lines, or a fan-out. `Delete` keeps its own branches.
let private resultWrite =
    @"\.(Upload|UploadWithETag|Remove|SetSecret|SetActiveTeam|DeleteTeam|TriggerOnce)\s*\("

/// The nine shapes the upload-result walk reads, plus the marker.
module UploadShapes =
    /// `let! _ = x.Upload(…)` on one line.
    let oneLine = Regex(@"^\s*let!\s+_\s*=\s*\S.*" + resultWrite)

    /// `let! _ =` with the bound expression below. A trailing comment is
    /// allowed after the `=`: that is where the marker sits on the multi-line
    /// form, and an unrelated comment there must not let a discard slip past.
    let bindOnly = Regex(@"^\s*let!\s+_\s*=\s*(//.*)?$")

    /// A result-bearing write anywhere on the line.
    let writeCall = Regex(resultWrite)

    /// `// best-effort-write: <why>` — the discard is the design.
    let marker = Regex(@"//\s*best-effort-write:\s*\S")

    let asyncIgnore = Regex(@"\bAsync\.Ignore\b")

    /// A write whose result is bound to a NAME or matched on, consumed where
    /// it is made: discarding the fan-out around it drops nothing.
    let consumedCall =
        Regex(
            @"\b(let|use)!\s+(?!_\s*=)[^=]*=\s*\S.*"
            + resultWrite
            + @"|\bmatch!\s.*"
            + resultWrite
        )

    let namedBindOnly = Regex(@"^\s*(let|use)!\s+(?!_\s*=)[^=]*=\s*$")
    let deleteCall = Regex(@"\.Delete\s*\(")
    let asyncParallel = Regex(@"\bAsync\.Parallel\b")
    let discardedHere = Regex(@"\blet!\s+_\s*=|\|>\s*Async\.Ignore\b")

    let consumedDelete =
        Regex(@"\b(let|use)!\s+(?!_\s*=)[^=]*=\s*\S.*\.Delete\s*\(|\bmatch!\s.*\.Delete\s*\(")

    let letDiscardsValue = Regex(@"^\s*let!\s+_\s*=\s*\S")

/// One discarded write: where, the discarding text, and whether it carries
/// the best-effort marker.
type UploadSite = {
    Rel: string
    /// 1-based.
    Line: int
    Text: string
    Marked: bool
}

/// The discarded result-bearing writes in each (repository-relative path,
/// lines) file.
let uploadSites (files: (string * string array) list) : UploadSite list =
    let isComment (line: string) =
        let t = line.TrimStart()
        t.StartsWith "//" || t.StartsWith "(*"

    let indentOf (line: string) = line.Length - line.TrimStart().Length
    let isBlank (line: string) = line.Trim() = ""

    // The lines of the expression a `let! _ =` on line `i` binds when the
    // call is not on that line: every following non-blank line indented
    // deeper than the binding.
    let boundBelow (lines: string array) (i: int) =
        let bindIndent = indentOf lines[i]

        seq { i + 1 .. lines.Length - 1 }
        |> Seq.filter (fun j -> not (isBlank lines[j]))
        |> Seq.takeWhile (fun j -> indentOf lines[j] > bindIndent)
        |> Seq.filter (fun j -> not (isComment lines[j]))
        |> List.ofSeq

    // The lines of the pipeline whose last stage is the `|> …` on line `i`:
    // up through its `|>` stages and the deeper-indented lines between them
    // (lambda bodies) to the pipeline's head, head first.
    let pipelineAbove (lines: string array) (i: int) =
        let stageIndent = indentOf lines[i]

        let rec up j acc =
            if j < 0 then
                acc
            elif isBlank lines[j] || isComment lines[j] then
                up (j - 1) acc
            else
                let indent = indentOf lines[j]

                if
                    indent > stageIndent
                    || (indent = stageIndent && lines[j].TrimStart().StartsWith "|>")
                then
                    up (j - 1) (j :: acc)
                else
                    j :: acc

        up (i - 1) []

    let previousNonBlank (lines: string array) (i: int) =
        seq { i - 1 .. -1 .. 0 } |> Seq.tryFind (fun j -> not (isBlank lines[j]))

    let boundByName (lines: string array) (j: int) =
        match previousNonBlank lines j with
        | Some k -> UploadShapes.namedBindOnly.IsMatch lines[k]
        | None -> false

    // Only a call that IS the element's value is a discard.
    let isDiscardedCall (lines: string array) (j: int) =
        UploadShapes.writeCall.IsMatch lines[j]
        && not (UploadShapes.consumedCall.IsMatch lines[j])
        && not (boundByName lines j)

    // A `Delete` counts only when the call is the element's value: a call
    // already discarded where it is made is a single-call discard, reported
    // at the call itself.
    let isDeleteElementValue (lines: string array) (j: int) =
        UploadShapes.deleteCall.IsMatch lines[j]
        && not (UploadShapes.consumedDelete.IsMatch lines[j])
        && not (UploadShapes.discardedHere.IsMatch lines[j])
        && not (boundByName lines j)

    // The producer lines of a discarded expression. A single-call `Delete`
    // discarded where it is made is always counted, so a `Delete` produces
    // in every shape.
    let isProducer (lines: string array) (j: int) =
        isDiscardedCall lines j || isDeleteElementValue lines j

    let singleDelete (text: string) =
        UploadShapes.deleteCall.IsMatch text
        && not (UploadShapes.consumedDelete.IsMatch text)

    files
    |> List.collect (fun (rel, lines) -> [
        for i in 0 .. lines.Length - 1 do
            let line = lines[i]

            let site text marked = {
                Rel = rel
                Line = i + 1
                Text = text
                Marked = marked
            }

            if not (isComment line) then
                if UploadShapes.oneLine.IsMatch line then
                    site (line.Trim()) (UploadShapes.marker.IsMatch line)
                elif UploadShapes.bindOnly.IsMatch line then
                    // The bound expression is on the following lines: the
                    // call itself, or a fan-out with the call inside it.
                    let bound = boundBelow lines i

                    match bound |> List.tryFind (isProducer lines) with
                    | Some j ->
                        let elided = if bound.Head = j then " " else " … "

                        site
                            (line.Trim() + elided + lines[j].Trim())
                            (UploadShapes.marker.IsMatch line || UploadShapes.marker.IsMatch lines[j])
                    | None -> ()
                elif UploadShapes.letDiscardsValue.IsMatch line && singleDelete line then
                    // A one-line `let! _ =` over a `Delete`, fan-out or not.
                    site (line.Trim()) (UploadShapes.marker.IsMatch line)
                elif UploadShapes.asyncIgnore.IsMatch line then
                    // `… |> Async.Ignore` over a write, on one line or as the
                    // last stage of a pipeline (the fan-out shape).
                    let before = line.Substring(0, UploadShapes.asyncIgnore.Match(line).Index)

                    let pipeline =
                        if line.TrimStart().StartsWith "|>" then
                            pipelineAbove lines i
                        else
                            []

                    // A pipeline a `let! _ =` binds is reported at the
                    // binding, by the branch above.
                    let boundByDiscard =
                        match pipeline with
                        | head :: _ ->
                            match previousNonBlank lines head with
                            | Some j -> UploadShapes.bindOnly.IsMatch lines[j]
                            | None -> false
                        | [] -> false

                    let callLine = pipeline |> List.tryFind (isProducer lines)

                    let sameLine =
                        (UploadShapes.writeCall.IsMatch before
                         && not (UploadShapes.consumedCall.IsMatch before))
                        || (singleDelete before && not (UploadShapes.discardedHere.IsMatch before))

                    if not boundByDiscard && (sameLine || callLine.IsSome) then
                        let text =
                            match pipeline with
                            | head :: _ -> lines[head].Trim() + " … " + line.Trim()
                            | [] -> line.Trim()

                        let markedOnCall =
                            callLine |> Option.exists (fun j -> UploadShapes.marker.IsMatch lines[j])

                        site text (UploadShapes.marker.IsMatch line || markedOnCall)
    ])

type UploadResultsCensus = {
    Files: int
    Sites: int
    Marked: int
    Unmarked: int
}

/// `VerifyUploadResults`, over `<root>/src/**/*.fs` with test projects
/// excluded; `--root <dir>` scans `<dir>/src` instead (how the check is shown
/// red on a planted discard without touching the tree).
let uploadResults (sink: Sink) (root: string) (flags: Flags) : Result<UploadResultsCensus, Refusal> =
    settle (fun () ->
        let root =
            match flags.Values.TryFind "--root" with
            | Some r -> Path.GetFullPath r
            | None -> root

        let srcDir = Path.Combine(root, "src")

        if not (Directory.Exists srcDir) then
            refuse
                sink
                []
                (sprintf "VerifyUploadResults: no `src` directory under %s — nothing to scan is not a pass." root)

        let files =
            sourceFiles true srcDir
            |> List.map (fun f -> Path.GetRelativePath(root, f).Replace('\\', '/'), File.ReadAllLines f)

        let sites = uploadSites files
        let unmarked = sites |> List.filter (fun s -> not s.Marked)

        let census = {
            Files = files.Length
            Sites = sites.Length
            Marked = sites.Length - unmarked.Length
            Unmarked = unmarked.Length
        }

        sink.Trace ""
        sink.Trace "VerifyUploadResults summary:"
        sink.Trace(sprintf "  source files    : %d (src/**/*.fs, test projects excluded)" census.Files)

        sink.Trace(
            sprintf
                "  discarded writes : %d — %d marked best-effort, %d unmarked"
                census.Sites
                census.Marked
                census.Unmarked
        )

        if not unmarked.IsEmpty then
            sink.Trace ""

            refuse
                sink
                [ for s in unmarked -> sprintf "    %s:%d: %s" s.Rel s.Line s.Text ]
                (sprintf
                    "VerifyUploadResults: %d site(s) discard the Result of a result-bearing write — `Upload` / `UploadWithETag` / `Remove` / `SetSecret` / `SetActiveTeam` / `DeleteTeam` / `TriggerOnce`, or a `Delete` (single call or fan-out). A failed write or delete must reach its caller: propagate it (match on the Result; raise where the enclosing signature carries no failure), or — where best-effort is the DESIGN — match the Error and log it at Warn. A discard that must stay a discard says why with `// best-effort-write: <why>` on the line. See docs/platform/storage-write-results.md."
                    unmarked.Length)

        sink.Trace ""

        sink.Trace(
            sprintf
                "VerifyUploadResults: OK — no unmarked discarded write or delete result (%d marked best-effort)."
                census.Marked
        )

        census)