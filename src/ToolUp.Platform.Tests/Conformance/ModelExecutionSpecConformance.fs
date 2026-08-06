// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Conformance.ModelExecutionSpecConformance

// ─── Model-execution wire-spec conformance ───────────────────────────
//
// Certifies the platform's out-of-process model-execution face
// (`ModelExecutionWire.fs` + `ModelExecutionApi`) against an
// **external, language-neutral model-execution conformance corpus** —
// a specification and fixture set that is canonical over any single
// implementation, this one included. The corpus is not authored here
// and is not vendored here; it is resolved at test time, pinned by
// digest, and every one of its vectors is executed.
//
// ── The role this face plays, stated because §2 requires it ──────────
//
// The corpus stratifies conformance by **profile**. This face is the
// surface out-of-process *submitters* call, which puts the platform on
// the **executor** side of it: it consumes submissions, queries and
// score requests, and emits receipts, outcomes, resolved vintages and
// refusals. The executor profile requires every family and — the
// obligation unique to it — requires that every `reject` vector be
// refused with the class the manifest names.
//
// The one exception is the minting rule. The specification forbids an
// executor from re-deriving a `specHash` it was handed, so the
// corresponding reject vector is a **submitter's pre-emit obligation**:
// a document a conformant submitter never emits, rather than one a
// receiver refuses. Both halves are certified here and they pull in
// opposite directions, deliberately —
// `SubmitterPreEmit.check` refuses it, `Decode.envelope` (the receiving
// path) does not, and the platform stores the hash it was handed
// byte-for-byte either way.
//
// ── The carry gaps are the point, not an embarrassment ───────────────
//
// The wire records here are not isomorphic to the specification's
// shapes, and pretending otherwise would make this harness decorative.
// Each family therefore round-trips through the real wire type plus an
// explicit **residue** — a named record of exactly the members the wire
// type does not model. The round-trip is byte-exact overall, and the
// residue is the inventory of the gap. `carryGapTests` pins the wire
// records' own field sets by reflection, so a record widened elsewhere
// fails here and its author decides deliberately whether the new field
// closes a gap.
//
// ── Corpus resolution ────────────────────────────────────────────────
//
// `TOOLUP_MODEL_EXECUTION_CORPUS` names the corpus explicitly (CI sets
// it to the checkout path). With it unset, `Corpus.locate` searches the
// enclosing directories for a corpus that **identifies itself** by its
// manifest's `specification` field — not for a path literal, so a
// corpus that is renamed or relocated needs no edit here.
//
// **An absent corpus is a loud failure, never a skip.** A conformance
// suite that quietly does nothing when its corpus is missing is
// indistinguishable from one that passes, and is worse than no suite at
// all because it is believed.
//
// ── Version pin + drift policy ───────────────────────────────────────
//
// The corpus is pinned two ways, because the two answer different
// questions:
//
//   * `Pin.commit` — the corpus commit this harness was verified
//     against. CI checks the corpus out at exactly this revision, so a
//     corpus commit never breaks this build by surprise. Moving it is a
//     deliberate, reviewed commit here.
//   * `Pin.manifestDigest` — SHA-256 over the corpus manifest's bytes.
//     The manifest is the corpus's authoritative enumeration (families,
//     profiles, every vector with its own digest), so this single value
//     moves whenever anything in the corpus moves. A local checkout that
//     has drifted from the pin fails immediately and by name, rather
//     than producing a subtly different certification.
//
// The corpus carries no version number that could serve instead: its
// envelope version is the *wire* version, and the wire version
// deliberately does not move for an additive change — a new fixture
// family, a new registry entry — which is precisely the class of drift
// a pin has to catch.
//
// Test-tier only: zero shipped code, and a consumer deployment is
// byte-for-byte unchanged (GP 13).

open System
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Reflection
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Expecto
open FSharp.Reflection
open ToolUp.Platform

// ─── Pins (602.B) ────────────────────────────────────────────────────

[<RequireQualifiedAccess>]
module Pin =

    /// The corpus revision this harness is certified against. CI checks
    /// the corpus out at exactly this revision. Bumping it is a
    /// deliberate commit with a diff review — never an incidental edit.
    let commit = "b55833f18334d4fd846c2cd91514946e95ca1941"

    /// SHA-256 over the corpus manifest's bytes. See the header: this is
    /// the value that moves whenever anything in the corpus moves.
    let manifestDigest =
        "094b709a2139bb1b039dc739ad9070f96f997b2ab4475578f4c4a637eaa3ce66"

    /// The specification this harness certifies against, as the corpus
    /// names itself. Used to identify a corpus by content rather than by
    /// path.
    let specification = "model-execution-wire"

    /// The wire version every fixture rides at.
    let envelopeVersion = 1

    /// The profile claimed. A conformance claim without a profile is
    /// unfalsifiable, so it is asserted rather than merely written down.
    let profile = "executor"

// ─── Corpus location ─────────────────────────────────────────────────

[<RequireQualifiedAccess>]
module Corpus =

    let private envVar = "TOOLUP_MODEL_EXECUTION_CORPUS"

    /// Directories that never contain a corpus and are expensive to walk.
    let private pruned =
        HashSet<string>(
            [
                ".git"
                ".vs"
                ".idea"
                "bin"
                "obj"
                "node_modules"
                "artifacts"
                "output"
                "dist"
                "packages"
            ],
            StringComparer.OrdinalIgnoreCase
        )

    /// Repo root derived from the running test assembly:
    /// bin/&lt;Config&gt;/net10.0/ToolUp.Platform.Tests.dll → up 5.
    let repoRoot () =
        let assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
        Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

    /// Does this directory hold the corpus manifest, and does that
    /// manifest declare itself to be the specification we certify
    /// against? Identity by content, so no path literal is load-bearing.
    let private isCorpusDir (dir: string) =
        let manifest = Path.Combine(dir, "manifest.json")

        if not (File.Exists manifest) then
            false
        else
            try
                use doc = JsonDocument.Parse(File.ReadAllBytes manifest)

                match doc.RootElement.TryGetProperty "specification" with
                | true, v -> v.ValueKind = JsonValueKind.String && v.GetString() = Pin.specification
                | _ -> false
            with _ ->
                false

    /// A directory the corpus's fixtures live under, given either the
    /// fixture directory itself or the repository containing it.
    let private asFixtureDir (dir: string) =
        if isCorpusDir dir then
            Some dir
        else
            let nested = Path.Combine(dir, "wire-fixtures")
            if isCorpusDir nested then Some nested else None

    let private searchRoots () = [
        repoRoot ()
        Path.GetFullPath(Path.Combine(repoRoot (), ".."))
        Path.GetFullPath(Path.Combine(repoRoot (), "..", ".."))
        Path.GetFullPath(Path.Combine(repoRoot (), "..", "..", ".."))
    ]

    /// Bounded breadth-first walk: a corpus checked out anywhere within
    /// three levels of an enclosing directory is found, and the walk
    /// never descends into build output.
    let private search (root: string) (maxDepth: int) =
        let rec go (dir: string) (depth: int) =
            match asFixtureDir dir with
            | Some found -> Some found
            | None when depth >= maxDepth -> None
            | None ->
                let children =
                    try
                        Directory.EnumerateDirectories dir
                        |> Seq.filter (fun d -> not (pruned.Contains(Path.GetFileName d)))
                        |> Seq.toList
                    with _ -> []

                children |> List.tryPick (fun child -> go child (depth + 1))

        if Directory.Exists root then go root 0 else None

    /// The resolved fixture directory, or a failure naming what was
    /// tried. Never returns "absent" as a success.
    let locate () : string =
        let fromEnv =
            match Environment.GetEnvironmentVariable envVar with
            | null
            | "" -> None
            | raw ->
                let trimmed = raw.Trim()

                match asFixtureDir trimmed with
                | Some found -> Some found
                | None ->
                    failwithf
                        "%s is set to '%s', which is not a model-execution conformance corpus (no manifest.json declaring specification '%s' there or under wire-fixtures/). Point it at the corpus checkout."
                        envVar
                        trimmed
                        Pin.specification

        match fromEnv with
        | Some found -> found
        | None ->
            match searchRoots () |> List.tryPick (fun root -> search root 3) with
            | Some found -> found
            | None ->
                failwithf
                    "The model-execution conformance corpus was not found. Set %s to the corpus checkout, or place it within three directory levels of one of: %s. This is a hard failure by design — a conformance run without its corpus certifies nothing, so it must not be mistaken for a pass. The corpus revision this build is pinned to is %s."
                    envVar
                    (searchRoots () |> String.concat "; ")
                    Pin.commit

    let dir = lazy (locate ())

    let path (relative: string) =
        Path.Combine(dir.Value, relative.Replace('/', Path.DirectorySeparatorChar))

    let bytes (relative: string) = File.ReadAllBytes(path relative)

// ─── Digests ─────────────────────────────────────────────────────────

[<RequireQualifiedAccess>]
module Digest =

    let hex (data: byte[]) =
        SHA256.HashData data |> Convert.ToHexString |> _.ToLowerInvariant()

    /// The content-address form the specification uses: the algorithm is
    /// named inside the value so a future change is a visible
    /// discontinuity rather than a silent one.
    let contentAddress (data: byte[]) = "sha256:" + hex data

// ─── Canonical JSON (§3) ─────────────────────────────────────────────

/// A canonical-JSON value. `JRec` preserves declaration order (§3.1
/// rule 3); `JMap` sorts ordinally by key (rule 4). They are separate
/// cases because that difference is the divergence from JCS most likely
/// to bite, and a single "object" case would erase it.
type J =
    | JStr of string
    /// A numeric literal already rendered by the rules of §3.1.
    | JNum of string
    | JBool of bool
    | JNull
    | JRec of (string * J) list
    | JMap of (string * J) list
    | JArr of J list

[<RequireQualifiedAccess>]
module Canonical =

    let ordinal (a: string) (b: string) = String.CompareOrdinal(a, b)

    /// §3.1 rule 6 — escape the quote, the backslash and everything
    /// below U+0020, using a short escape where one exists. Everything
    /// at or above U+0020, non-ASCII included, is emitted literally.
    let escape (s: string) =
        let sb = StringBuilder(s.Length + 8)

        for ch in s do
            match ch with
            | '"' -> sb.Append "\\\"" |> ignore
            | '\\' -> sb.Append "\\\\" |> ignore
            | '\b' -> sb.Append "\\b" |> ignore
            | '\f' -> sb.Append "\\f" |> ignore
            | '\n' -> sb.Append "\\n" |> ignore
            | '\r' -> sb.Append "\\r" |> ignore
            | '\t' -> sb.Append "\\t" |> ignore
            | c when c < ' ' -> sb.AppendFormat(CultureInfo.InvariantCulture, "\\u{0:x4}", int c) |> ignore
            | c -> sb.Append c |> ignore

        sb.ToString()

    /// §3.1 rule 9 — the ECMAScript `Number::toString` algorithm: the
    /// shortest decimal that round-trips to the same IEEE-754 double,
    /// rendered with ECMAScript's exponent thresholds rather than
    /// .NET's. Implemented rather than delegated because .NET renders
    /// `1e-7` as `1E-07`, which is a different document.
    let real (x: float) =
        if Double.IsNaN x || Double.IsInfinity x then
            failwithf "a non-finite value must never reach the wire (§3.1 rule 9): %f" x
        elif x = 0.0 then
            // Covers negative zero, which serialises as `0`.
            "0"
        else
            let negative = x < 0.0
            let magnitude = abs x
            let shortest = magnitude.ToString("R", CultureInfo.InvariantCulture)

            let mantissa, exponent =
                match shortest.IndexOfAny [| 'E'; 'e' |] with
                | -1 -> shortest, 0
                | i -> shortest.Substring(0, i), Int32.Parse(shortest.Substring(i + 1), CultureInfo.InvariantCulture)

            let integral, fractional =
                match mantissa.IndexOf '.' with
                | -1 -> mantissa, ""
                | i -> mantissa.Substring(0, i), mantissa.Substring(i + 1)

            // Reduce to `0.<digits> × 10^n`, the form the ECMAScript
            // rule is stated over.
            let raw = integral + fractional
            let stripped = raw.TrimStart '0'
            let n = integral.Length + exponent - (raw.Length - stripped.Length)
            let digits = stripped.TrimEnd '0'
            let k = digits.Length

            let core =
                if k <= n && n <= 21 then
                    digits + String('0', n - k)
                elif 0 < n && n <= 21 then
                    digits.Substring(0, n) + "." + digits.Substring n
                elif -6 < n && n <= 0 then
                    "0." + String('0', -n) + digits
                else
                    let e = n - 1
                    let sign = if e >= 0 then "+" else "-"

                    let head =
                        if k = 1 then
                            digits
                        else
                            digits.Substring(0, 1) + "." + digits.Substring 1

                    head + "e" + sign + string (abs e)

            if negative then "-" + core else core

    /// §3.1 rule 8 — 64-bit integers are decimal strings with an
    /// explicit sign, zero included.
    let int64Literal (v: int64) =
        if v >= 0L then "+" + string v else string v

    let rec internal write (sb: StringBuilder) (value: J) =
        match value with
        | JStr s -> sb.Append('"').Append(escape s).Append('"') |> ignore
        | JNum n -> sb.Append n |> ignore
        | JBool b -> sb.Append(if b then "true" else "false") |> ignore
        | JNull -> sb.Append "null" |> ignore
        | JRec fields -> writeObject sb fields
        | JMap entries -> writeObject sb (entries |> List.sortWith (fun (a, _) (b, _) -> ordinal a b))
        | JArr items ->
            sb.Append '[' |> ignore

            items
            |> List.iteri (fun i item ->
                if i > 0 then
                    sb.Append ',' |> ignore

                write sb item)

            sb.Append ']' |> ignore

    and internal writeObject (sb: StringBuilder) (members: (string * J) list) =
        sb.Append '{' |> ignore

        members
        |> List.iteri (fun i (name, value) ->
            if i > 0 then
                sb.Append ',' |> ignore

            sb.Append('"').Append(escape name).Append("\":") |> ignore
            write sb value)

        sb.Append '}' |> ignore

    /// The canonical bytes of a value: UTF-8, no byte-order mark, no
    /// insignificant whitespace anywhere.
    let toBytes (value: J) =
        let sb = StringBuilder()
        write sb value
        UTF8Encoding(false).GetBytes(sb.ToString())

    let toText (value: J) = Encoding.UTF8.GetString(toBytes value)

// ─── Minting canonicalisation (§4.5, `canonical-json-sha256-v1`) ──────

[<RequireQualifiedAccess>]
module Minting =

    /// The registered identifier of the minting rule implemented here.
    let algorithm = "canonical-json-sha256-v1"

    /// §4.5 step 2 — the same encoding as §3, with two differences that
    /// exist because a specification's interior is not a versioned
    /// record: **every** object's members sort (not only a map's), and
    /// there is no 64-bit-integer string form inside a payload.
    let rec private ofElement (e: JsonElement) : J =
        match e.ValueKind with
        | JsonValueKind.Object ->
            e.EnumerateObject()
            |> Seq.map (fun p -> p.Name, ofElement p.Value)
            |> List.ofSeq
            |> JMap
        | JsonValueKind.Array -> e.EnumerateArray() |> Seq.map ofElement |> List.ofSeq |> JArr
        | JsonValueKind.String -> JStr(e.GetString())
        | JsonValueKind.Number -> JNum(Canonical.real (e.GetDouble()))
        | JsonValueKind.True -> JBool true
        | JsonValueKind.False -> JBool false
        | JsonValueKind.Null -> JNull
        | other -> failwithf "a rendering carried an unexpected JSON value kind: %A" other

    /// The intermediate bytes of §4.5 step 2. Derived, never
    /// authoritative — the rule is. Written out so an implementation
    /// that mints the wrong digest can see *where* it diverged.
    let canonicalBytes (rendering: string) : byte[] =
        use doc = JsonDocument.Parse rendering
        Canonical.toBytes (ofElement doc.RootElement)

    /// §4.5 step 3 — the content address that is `specHash`.
    let mint (rendering: string) : string =
        Digest.contentAddress (canonicalBytes rendering)

// ─── The specification's shapes (§5) ─────────────────────────────────

type SpecContentRef = {
    Format: string
    Hash: string
    RowCount: int64 option
}

type SpecVintageRef = {
    DatasetId: string
    Version: int
    ContentRef: SpecContentRef option
}

type SpecResolvedVintage = {
    Ref: SpecVintageRef
    CreatedAt: DateTimeOffset
    IsLatest: bool
}

type SpecGate = {
    Name: string
    Threshold: float
    Direction: string
}

type SpecGateVerdict = {
    Name: string
    Threshold: float
    Direction: string
    Observed: float
    Passed: bool
}

/// The closed refusal vocabulary of §5.7.1, plus the case a reader
/// synthesises for a class registered after this version (§5.7.2
/// rule 2) — modelled explicitly so "read it as unspecified" is a value
/// a test can assert rather than a behaviour a reader claims.
type SpecRefusal =
    | EnvelopeVersionMismatch of received: int * accepted: int list
    | UnknownDocumentKind of kind: string * known: string list
    | InvalidSubmission of reason: string
    | InvalidQuery of reason: string
    | UnknownProvider of kind: string * known: string list
    | BudgetDenied of quota: float * spent: float * unit: string
    | GateFailed of verdicts: SpecGateVerdict list
    | PolicyRefused of rule: string
    | ScopeUnavailable
    | Forbidden of reason: string
    | NotFound of what: string * id: string
    | SubstrateUnavailable of surface: string
    | ScoreRefused of reason: string * detail: string
    | StorageFailure of reason: string
    | Unspecified of message: string
    | UnrecognisedClass of className: string * message: string

type SpecFitSubmission = {
    Vintage: SpecVintageRef
    SpecPayload: string
    SpecHash: string
    SpecHashAlgorithm: string
    ProviderKind: string
    Seed: int64
    Gates: SpecGate list
    SubmitterClass: string
}

type SpecFitSubmissionBatch = {
    BatchId: string
    Submissions: SpecFitSubmission list
}

type SpecAcceptedItem = { Index: int; JobId: string }

type SpecRejectedItem = { Index: int; Reason: SpecRefusal }

type SpecSubmissionReceipt = {
    BatchId: string
    ItemCount: int
    Accepted: SpecAcceptedItem list
    Rejected: SpecRejectedItem list
}

type SpecArtifactRef = {
    ArtifactId: string
    ContentHash: string
    Format: string option
}

type SpecCompositeKey = {
    SpecHash: string
    Vintage: SpecVintageRef
    Seed: int64
    ProviderId: string
    ProviderVersion: string
}

type SpecTiming = {
    SubmittedAt: DateTimeOffset
    StartedAt: DateTimeOffset option
    CompletedAt: DateTimeOffset option
    DurationMs: int64 option
}

type SpecCost = { Unit: string; Amount: float }

type SpecFitOutcome = {
    CompositeKeyHash: string
    CompositeKey: SpecCompositeKey
    ArtifactRef: SpecArtifactRef option
    Diagnostics: (string * float) list
    GateVerdicts: SpecGateVerdict list
    Status: string
    Timing: SpecTiming
    Cost: SpecCost option
    Annotations: (string * string) list
    RegisteredAt: DateTimeOffset
}

type SpecPage = { Cursor: string option; Limit: int }

type SpecRegistryQuery = {
    SpecHashes: string list
    Vintages: SpecVintageRef list
    Statuses: string list
    BatchId: string option
    Page: SpecPage
}

type SpecOutcomePage = {
    Outcomes: SpecFitOutcome list
    NextCursor: string option
}

type SpecScoreRequest = {
    ArtifactKeyHash: string
    Input: SpecVintageRef
    OutputDatasetId: string
}

type SpecBody =
    | BVintageRef of SpecVintageRef
    | BResolvedVintage of SpecResolvedVintage
    | BFitSubmission of SpecFitSubmission
    | BFitSubmissionBatch of SpecFitSubmissionBatch
    | BSubmissionReceipt of SpecSubmissionReceipt
    | BCompositeKey of SpecCompositeKey
    | BFitOutcome of SpecFitOutcome
    | BRegistryQuery of SpecRegistryQuery
    | BOutcomePage of SpecOutcomePage
    | BScoreRequest of SpecScoreRequest
    | BRefusal of SpecRefusal

type SpecEnvelope = {
    EnvelopeVersion: int
    Kind: string
    Body: SpecBody
}

// ─── Registries (§7) ─────────────────────────────────────────────────

[<RequireQualifiedAccess>]
module Registry =

    /// §7.1 — the values `envelope.kind` may take.
    let kinds = [
        "vintageRef"
        "resolvedVintage"
        "fitSubmission"
        "fitSubmissionBatch"
        "submissionReceipt"
        "compositeKey"
        "fitOutcome"
        "registryQuery"
        "outcomePage"
        "scoreRequest"
        "refusal"
    ]

    /// §7.5 — closed. An unrecognised direction is refused, never
    /// defaulted: a gate whose direction is guessed is a gate that
    /// silently passes.
    let gateDirections = [ "atLeast"; "atMost" ]

    /// §7.6 — closed at version 1.
    let submitterClasses = [ "human"; "scheduled"; "agent" ]

    /// §7.2 / §7.3 — the common lexical rule for a registered
    /// identifier: lowercase, digits, interior hyphens.
    let isLexical (s: string) =
        not (String.IsNullOrEmpty s)
        && s
           |> Seq.forall (fun c -> (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c = '-')
        && s[0] <> '-'
        && s[s.Length - 1] <> '-'

    /// §4.1 — `"{algorithm}:{lowercase hex}"`. A bare hex string with no
    /// algorithm prefix is not a content address.
    let isContentAddress (s: string) =
        match s with
        | null
        | "" -> false
        | _ ->
            match s.IndexOf ':' with
            | i when i <= 0 || i = s.Length - 1 -> false
            | i ->
                let algorithm = s.Substring(0, i)
                let hex = s.Substring(i + 1)

                isLexical algorithm
                && hex |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))

// ─── Encoding back to canonical bytes (§3 + §5 declaration order) ────

[<RequireQualifiedAccess>]
module Encode =

    let private jInt (v: int) = JNum(string v)
    let private jI64 (v: int64) = JStr(Canonical.int64Literal v)
    let private jReal (x: float) = JNum(Canonical.real x)

    let private jOpt (f: 'a -> J) =
        function
        | None -> JNull
        | Some v -> f v

    let private jInstant (v: DateTimeOffset) =
        JStr(v.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture))

    let contentRef (c: SpecContentRef) =
        JRec [
            "format", JStr c.Format
            "hash", JStr c.Hash
            "rowCount", jOpt jI64 c.RowCount
        ]

    let vintageRef (v: SpecVintageRef) =
        JRec [
            "datasetId", JStr v.DatasetId
            "version", jInt v.Version
            "contentRef", jOpt contentRef v.ContentRef
        ]

    let resolvedVintage (r: SpecResolvedVintage) =
        JRec [
            "ref", vintageRef r.Ref
            "createdAt", jInstant r.CreatedAt
            "isLatest", JBool r.IsLatest
        ]

    let gate (g: SpecGate) =
        JRec [
            "name", JStr g.Name
            "threshold", jReal g.Threshold
            "direction", JStr g.Direction
        ]

    let gateVerdict (v: SpecGateVerdict) =
        JRec [
            "name", JStr v.Name
            "threshold", jReal v.Threshold
            "direction", JStr v.Direction
            "observed", jReal v.Observed
            "passed", JBool v.Passed
        ]

    /// §3.4 — a tagged shape is an ordinary record whose first member is
    /// the discriminator, followed by that case's declared members in
    /// declaration order. There is no nested single-member-object form.
    let refusal (r: SpecRefusal) =
        let tagged cls members = JRec(("class", JStr cls) :: members)

        match r with
        | EnvelopeVersionMismatch(received, accepted) ->
            tagged "envelopeVersionMismatch" [ "received", jInt received; "accepted", JArr(accepted |> List.map jInt) ]
        | UnknownDocumentKind(kind, known) ->
            tagged "unknownDocumentKind" [ "kind", JStr kind; "known", JArr(known |> List.map JStr) ]
        | InvalidSubmission reason -> tagged "invalidSubmission" [ "reason", JStr reason ]
        | InvalidQuery reason -> tagged "invalidQuery" [ "reason", JStr reason ]
        | UnknownProvider(kind, known) ->
            tagged "unknownProvider" [ "kind", JStr kind; "known", JArr(known |> List.map JStr) ]
        | BudgetDenied(quota, spent, unit) ->
            tagged "budgetDenied" [ "quota", jReal quota; "spent", jReal spent; "unit", JStr unit ]
        | GateFailed verdicts -> tagged "gateFailed" [ "verdicts", JArr(verdicts |> List.map gateVerdict) ]
        | PolicyRefused rule -> tagged "policyRefused" [ "rule", JStr rule ]
        | ScopeUnavailable -> tagged "scopeUnavailable" []
        | Forbidden reason -> tagged "forbidden" [ "reason", JStr reason ]
        | NotFound(what, id) -> tagged "notFound" [ "what", JStr what; "id", JStr id ]
        | SubstrateUnavailable surface -> tagged "substrateUnavailable" [ "surface", JStr surface ]
        | ScoreRefused(reason, detail) -> tagged "scoreRefused" [ "reason", JStr reason; "detail", JStr detail ]
        | StorageFailure reason -> tagged "storageFailure" [ "reason", JStr reason ]
        | Unspecified message -> tagged "unspecified" [ "message", JStr message ]
        | UnrecognisedClass(cls, message) ->
            // Never emitted by a conformant party — §5.7.2 rule 4. It
            // exists so a decoded unknown class is a value, and encoding
            // it back would be inventing a class in a private namespace.
            failwithf "an unrecognised refusal class must not be re-emitted (§5.7.2 rule 4): '%s' / '%s'" cls message

    let fitSubmission (s: SpecFitSubmission) =
        JRec [
            "vintage", vintageRef s.Vintage
            "specPayload", JStr s.SpecPayload
            "specHash", JStr s.SpecHash
            "specHashAlgorithm", JStr s.SpecHashAlgorithm
            "providerKind", JStr s.ProviderKind
            "seed", jI64 s.Seed
            "gates", JArr(s.Gates |> List.map gate)
            "submitterClass", JStr s.SubmitterClass
        ]

    let compositeKey (k: SpecCompositeKey) =
        JRec [
            "specHash", JStr k.SpecHash
            "vintage", vintageRef k.Vintage
            "seed", jI64 k.Seed
            "providerId", JStr k.ProviderId
            "providerVersion", JStr k.ProviderVersion
        ]

    let artifactRef (a: SpecArtifactRef) =
        JRec [
            "artifactId", JStr a.ArtifactId
            "contentHash", JStr a.ContentHash
            "format", jOpt JStr a.Format
        ]

    let timing (t: SpecTiming) =
        JRec [
            "submittedAt", jInstant t.SubmittedAt
            "startedAt", jOpt jInstant t.StartedAt
            "completedAt", jOpt jInstant t.CompletedAt
            "durationMs", jOpt jI64 t.DurationMs
        ]

    let cost (c: SpecCost) =
        JRec [ "unit", JStr c.Unit; "amount", jReal c.Amount ]

    let fitOutcome (o: SpecFitOutcome) =
        JRec [
            "compositeKeyHash", JStr o.CompositeKeyHash
            "compositeKey", compositeKey o.CompositeKey
            "artifactRef", jOpt artifactRef o.ArtifactRef
            "diagnostics", JMap(o.Diagnostics |> List.map (fun (k, v) -> k, jReal v))
            "gateVerdicts", JArr(o.GateVerdicts |> List.map gateVerdict)
            "status", JStr o.Status
            "timing", timing o.Timing
            "cost", jOpt cost o.Cost
            "annotations", JMap(o.Annotations |> List.map (fun (k, v) -> k, JStr v))
            "registeredAt", jInstant o.RegisteredAt
        ]

    let registryQuery (q: SpecRegistryQuery) =
        JRec [
            "specHashes", JArr(q.SpecHashes |> List.map JStr)
            "vintages", JArr(q.Vintages |> List.map vintageRef)
            "statuses", JArr(q.Statuses |> List.map JStr)
            "batchId", jOpt JStr q.BatchId
            "page", JRec [ "cursor", jOpt JStr q.Page.Cursor; "limit", jInt q.Page.Limit ]
        ]

    let scoreRequest (s: SpecScoreRequest) =
        JRec [
            "artifactKeyHash", JStr s.ArtifactKeyHash
            "input", vintageRef s.Input
            "outputDatasetId", JStr s.OutputDatasetId
        ]

    let body (value: SpecBody) =
        match value with
        | BVintageRef v -> vintageRef v
        | BResolvedVintage r -> resolvedVintage r
        | BFitSubmission s -> fitSubmission s
        | BFitSubmissionBatch batch ->
            JRec [
                "batchId", JStr batch.BatchId
                "submissions", JArr(batch.Submissions |> List.map fitSubmission)
            ]
        | BSubmissionReceipt r ->
            JRec [
                "batchId", JStr r.BatchId
                "itemCount", jInt r.ItemCount
                "accepted",
                JArr(
                    r.Accepted
                    |> List.map (fun a -> JRec [ "index", jInt a.Index; "jobId", JStr a.JobId ])
                )
                "rejected",
                JArr(
                    r.Rejected
                    |> List.map (fun a -> JRec [ "index", jInt a.Index; "reason", refusal a.Reason ])
                )
            ]
        | BCompositeKey k -> compositeKey k
        | BFitOutcome o -> fitOutcome o
        | BRegistryQuery q -> registryQuery q
        | BOutcomePage p ->
            JRec [
                "outcomes", JArr(p.Outcomes |> List.map fitOutcome)
                "nextCursor", jOpt JStr p.NextCursor
            ]
        | BScoreRequest s -> scoreRequest s
        | BRefusal r -> refusal r

    let envelope (e: SpecEnvelope) =
        JRec [
            "envelopeVersion", jInt e.EnvelopeVersion
            "kind", JStr e.Kind
            "body", body e.Body
        ]

    let bytes (e: SpecEnvelope) = Canonical.toBytes (envelope e)

// ─── Decoding, with the executor's refusal obligations (§9.1) ────────

/// A refusal raised during decoding, carrying the corpus's stable
/// reject-class identifier alongside the refusal a conformant party
/// emits. The class is normative; the wording never is.
exception Refused of rejectClass: string * refusal: SpecRefusal

[<RequireQualifiedAccess>]
module Decode =

    let private refuse rejectClass refusal = raise (Refused(rejectClass, refusal))

    let private malformed reason =
        refuse "envelope-malformed" (InvalidSubmission reason)

    let private prop (e: JsonElement) (name: string) =
        match e.TryGetProperty name with
        | true, v -> v
        | _ -> malformed $"member '{name}' is absent"

    let private expect (kind: JsonValueKind) (name: string) (e: JsonElement) =
        if e.ValueKind <> kind then
            malformed $"member '{name}' is a %A{e.ValueKind} where a %A{kind} was declared"

        e

    let private str e name =
        (expect JsonValueKind.String name (prop e name)).GetString()

    let private int32Of e name =
        let v = prop e name

        if v.ValueKind <> JsonValueKind.Number then
            malformed $"member '{name}' is not a number"

        match v.TryGetInt32() with
        | true, i -> i
        | _ -> malformed $"member '{name}' is not a 32-bit integer"

    let private real e name =
        let v = prop e name

        if v.ValueKind <> JsonValueKind.Number then
            malformed $"member '{name}' is not a number"

        v.GetDouble()

    let private boolOf e name =
        let v = prop e name

        match v.ValueKind with
        | JsonValueKind.True -> true
        | JsonValueKind.False -> false
        | _ -> malformed $"member '{name}' is not a boolean"

    /// §3.1 rule 8 — a 64-bit integer is a sign-prefixed decimal string,
    /// and the sign is always present.
    let private int64Of e name =
        let raw = str e name

        if raw.Length < 2 || (raw[0] <> '+' && raw[0] <> '-') then
            malformed $"member '{name}' is a 64-bit integer and must carry an explicit sign (§3.1 rule 8): '{raw}'"

        match Int64.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) with
        | true, v -> v
        | _ -> malformed $"member '{name}' is not a 64-bit integer: '{raw}'"

    let private instant e name =
        let raw = str e name

        match
            DateTimeOffset.TryParseExact(
                raw,
                "yyyy-MM-ddTHH:mm:sszzz",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None
            )
        with
        | true, v -> v
        | _ -> malformed $"member '{name}' is not an ISO-8601 instant with an explicit offset: '{raw}'"

    let private isNull' (e: JsonElement) = e.ValueKind = JsonValueKind.Null

    /// §3.1 rule 5 — an absent optional value is `null`, never omitted,
    /// so an option is read from a member that is always present.
    let private optional (e: JsonElement) (name: string) (read: JsonElement -> 'a) =
        let v = prop e name
        if isNull' v then None else Some(read v)

    let private strList e name =
        (expect JsonValueKind.Array name (prop e name)).EnumerateArray()
        |> Seq.map _.GetString()
        |> List.ofSeq

    let private intList e name =
        (expect JsonValueKind.Array name (prop e name)).EnumerateArray()
        |> Seq.map _.GetInt32()
        |> List.ofSeq

    let private contentRef (e: JsonElement) : SpecContentRef =
        let format = str e "format"

        // §7.2 — an unregistered identifier is not an error, but one
        // that violates the lexical rule can never be registered and is
        // therefore always a defect.
        if not (Registry.isLexical format) then
            refuse
                "vintage-format-invalid"
                (InvalidSubmission $"content-ref format '{format}' violates the lexical rule")

        {
            Format = format
            Hash = str e "hash"
            RowCount =
                optional e "rowCount" (fun v ->
                    let raw = v.GetString()

                    if raw.Length < 2 || (raw[0] <> '+' && raw[0] <> '-') then
                        malformed $"rowCount must carry an explicit sign (§3.1 rule 8): '{raw}'"

                    Int64.Parse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture))
        }

    let private vintageRef (e: JsonElement) : SpecVintageRef = {
        DatasetId = str e "datasetId"
        Version = int32Of e "version"
        ContentRef = optional e "contentRef" contentRef
    }

    let private resolvedVintage (e: JsonElement) : SpecResolvedVintage = {
        Ref = vintageRef (expect JsonValueKind.Object "ref" (prop e "ref"))
        CreatedAt = instant e "createdAt"
        IsLatest = boolOf e "isLatest"
    }

    let private gate (e: JsonElement) : SpecGate =
        let direction = str e "direction"

        if not (List.contains direction Registry.gateDirections) then
            refuse
                "submission-gate-direction-unknown"
                (InvalidSubmission $"gate direction '{direction}' is not one of {Registry.gateDirections}")

        {
            Name = str e "name"
            Threshold = real e "threshold"
            Direction = direction
        }

    let private gateVerdict (e: JsonElement) : SpecGateVerdict = {
        Name = str e "name"
        Threshold = real e "threshold"
        Direction = str e "direction"
        Observed = real e "observed"
        Passed = boolOf e "passed"
    }

    let private array (e: JsonElement) (name: string) (read: JsonElement -> 'a) =
        (expect JsonValueKind.Array name (prop e name)).EnumerateArray()
        |> Seq.map read
        |> List.ofSeq

    /// §3.3 — a map's keys are data. Read in document order; the encoder
    /// sorts.
    let private mapOf (e: JsonElement) (name: string) (read: JsonElement -> 'a) =
        (expect JsonValueKind.Object name (prop e name)).EnumerateObject()
        |> Seq.map (fun p -> p.Name, read p.Value)
        |> List.ofSeq

    let private fitSubmission (e: JsonElement) : SpecFitSubmission =
        let specHash = str e "specHash"

        // §7.9 `submission-spec-hash-absent`. Note what is NOT checked
        // here: whether the hash is the minting of the payload. That is
        // §4.2 rule 2, and it is the whole reason the platform's storage
        // path is safe to key by a value it did not compute.
        if not (Registry.isContentAddress specHash) then
            refuse
                "submission-spec-hash-absent"
                (InvalidSubmission "specHash is empty or is not a content address (§4.1)")

        let submitterClass = str e "submitterClass"

        if not (List.contains submitterClass Registry.submitterClasses) then
            refuse
                "submission-submitter-class-unknown"
                (InvalidSubmission $"submitterClass '{submitterClass}' is not one of {Registry.submitterClasses}")

        let gates = array e "gates" gate

        let duplicates =
            gates
            |> List.countBy _.Name
            |> List.filter (fun (_, n) -> n > 1)
            |> List.map fst

        if not (List.isEmpty duplicates) then
            refuse "submission-gate-duplicate" (InvalidSubmission $"gate declared twice: {duplicates}")

        {
            Vintage = vintageRef (expect JsonValueKind.Object "vintage" (prop e "vintage"))
            SpecPayload = str e "specPayload"
            SpecHash = specHash
            SpecHashAlgorithm = str e "specHashAlgorithm"
            ProviderKind = str e "providerKind"
            Seed = int64Of e "seed"
            Gates = gates
            SubmitterClass = submitterClass
        }

    let private fitSubmissionBatch (e: JsonElement) : SpecFitSubmissionBatch =
        let batchId = str e "batchId"
        let submissions = array e "submissions" fitSubmission

        if String.IsNullOrEmpty batchId || List.isEmpty submissions then
            refuse
                "submission-batch-empty"
                (InvalidSubmission "a batch must carry a batchId and at least one submission")

        {
            BatchId = batchId
            Submissions = submissions
        }

    let private refusal (e: JsonElement) : SpecRefusal =
        match str e "class" with
        | "envelopeVersionMismatch" -> EnvelopeVersionMismatch(int32Of e "received", intList e "accepted")
        | "unknownDocumentKind" -> UnknownDocumentKind(str e "kind", strList e "known")
        | "invalidSubmission" -> InvalidSubmission(str e "reason")
        | "invalidQuery" -> InvalidQuery(str e "reason")
        | "unknownProvider" -> UnknownProvider(str e "kind", strList e "known")
        | "budgetDenied" -> BudgetDenied(real e "quota", real e "spent", str e "unit")
        | "gateFailed" -> GateFailed(array e "verdicts" gateVerdict)
        | "policyRefused" -> PolicyRefused(str e "rule")
        | "scopeUnavailable" -> ScopeUnavailable
        | "forbidden" -> Forbidden(str e "reason")
        | "notFound" -> NotFound(str e "what", str e "id")
        | "substrateUnavailable" -> SubstrateUnavailable(str e "surface")
        | "scoreRefused" -> ScoreRefused(str e "reason", str e "detail")
        | "storageFailure" -> StorageFailure(str e "reason")
        | "unspecified" -> Unspecified(str e "message")
        | unknown ->
            // §5.7.2 rule 2 — a reader MUST NOT fail on an unrecognised
            // class. It reads it as `unspecified`, keeps whatever
            // human-readable text it can find, and reports the class so
            // an operator can see an upgrade is available.
            let text =
                match e.TryGetProperty "message" with
                | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
                | _ -> ""

            UnrecognisedClass(unknown, text)

    let private artifactRef (e: JsonElement) : SpecArtifactRef = {
        ArtifactId = str e "artifactId"
        ContentHash = str e "contentHash"
        Format = optional e "format" _.GetString()
    }

    let private compositeKey (e: JsonElement) : SpecCompositeKey = {
        SpecHash = str e "specHash"
        Vintage = vintageRef (expect JsonValueKind.Object "vintage" (prop e "vintage"))
        Seed = int64Of e "seed"
        ProviderId = str e "providerId"
        ProviderVersion = str e "providerVersion"
    }

    let private timing (e: JsonElement) : SpecTiming = {
        SubmittedAt = instant e "submittedAt"
        StartedAt = optional e "startedAt" (fun _ -> instant e "startedAt")
        CompletedAt = optional e "completedAt" (fun _ -> instant e "completedAt")
        DurationMs = optional e "durationMs" (fun _ -> int64Of e "durationMs")
    }

    let private cost (e: JsonElement) : SpecCost = {
        Unit = str e "unit"
        Amount = real e "amount"
    }

    let private fitOutcome (e: JsonElement) : SpecFitOutcome =
        let key =
            compositeKey (expect JsonValueKind.Object "compositeKey" (prop e "compositeKey"))

        let declared = str e "compositeKeyHash"
        let recomputed = Digest.contentAddress (Canonical.toBytes (Encode.compositeKey key))

        // §4.3 consequence 3 — an outcome whose hash does not equal a
        // recomputation over its own composite key is CORRUPT and must
        // never be stored. It is a different condition from an unknown
        // key and is reported differently: the join that names this fit
        // across two parties is exactly the value that does not hold.
        //
        // Note this is a rule the receiving party is not merely allowed
        // but REQUIRED to check, which is the opposite posture from
        // `specHash` (§4.2 rule 2). The difference is who minted it: a
        // composite-key hash is computed from members that all cross the
        // wire, so a receiver can recompute it without interpreting
        // anything it was not given.
        if declared <> recomputed then
            refuse
                "outcome-composite-key-mismatch"
                (InvalidSubmission
                    $"compositeKeyHash '{declared}' is not a recomputation over the outcome's own compositeKey (which addresses '{recomputed}')")

        {
            CompositeKeyHash = declared
            CompositeKey = key
            ArtifactRef = optional e "artifactRef" artifactRef
            Diagnostics = mapOf e "diagnostics" _.GetDouble()
            GateVerdicts = array e "gateVerdicts" gateVerdict
            Status = str e "status"
            Timing = timing (expect JsonValueKind.Object "timing" (prop e "timing"))
            Cost = optional e "cost" cost
            Annotations = mapOf e "annotations" _.GetString()
            RegisteredAt = instant e "registeredAt"
        }

    let private page (e: JsonElement) : SpecPage =
        let limit = int32Of e "limit"

        if limit < 1 || limit > 1000 then
            refuse "query-limit-out-of-range" (InvalidQuery $"page.limit must be between 1 and 1000, not {limit}")

        {
            Cursor = optional e "cursor" _.GetString()
            Limit = limit
        }

    let private registryQuery (e: JsonElement) : SpecRegistryQuery = {
        SpecHashes = strList e "specHashes"
        Vintages = array e "vintages" vintageRef
        Statuses = strList e "statuses"
        BatchId = optional e "batchId" _.GetString()
        Page = page (expect JsonValueKind.Object "page" (prop e "page"))
    }

    let private outcomePage (e: JsonElement) : SpecOutcomePage = {
        Outcomes = array e "outcomes" fitOutcome
        NextCursor = optional e "nextCursor" _.GetString()
    }

    let private scoreRequest (e: JsonElement) : SpecScoreRequest =
        let input = vintageRef (expect JsonValueKind.Object "input" (prop e "input"))
        let output = str e "outputDatasetId"

        // §5.6 — writing predictions as a new version of the very
        // dataset they came from makes the input irreproducible.
        if output = input.DatasetId then
            refuse
                "score-output-collides-with-input"
                (InvalidSubmission "outputDatasetId must not equal input.datasetId")

        {
            ArtifactKeyHash = str e "artifactKeyHash"
            Input = input
            OutputDatasetId = output
        }

    /// The receiving path. Everything an executor is obliged to refuse
    /// is refused here; nothing an executor is forbidden to check is
    /// checked here.
    let private envelopeOf (root: JsonElement) : SpecEnvelope =
        if root.ValueKind <> JsonValueKind.Object then
            malformed "an envelope must be an object"

        let version = int32Of root "envelopeVersion"

        // §5.1 — refused whole, never read partially: a member a reader
        // has no field for would otherwise satisfy a requirement by
        // omission.
        if version <> Pin.envelopeVersion then
            refuse "envelope-version-unsupported" (EnvelopeVersionMismatch(version, [ Pin.envelopeVersion ]))

        let kind = str root "kind"

        if not (List.contains kind Registry.kinds) then
            refuse "envelope-kind-unknown" (UnknownDocumentKind(kind, Registry.kinds))

        let body = expect JsonValueKind.Object "body" (prop root "body")

        let decoded =
            match kind with
            | "vintageRef" -> BVintageRef(vintageRef body)
            | "resolvedVintage" -> BResolvedVintage(resolvedVintage body)
            | "fitSubmission" -> BFitSubmission(fitSubmission body)
            | "fitSubmissionBatch" -> BFitSubmissionBatch(fitSubmissionBatch body)
            | "submissionReceipt" ->
                BSubmissionReceipt {
                    BatchId = str body "batchId"
                    ItemCount = int32Of body "itemCount"
                    Accepted =
                        array body "accepted" (fun item -> {
                            Index = int32Of item "index"
                            JobId = str item "jobId"
                        })
                    Rejected =
                        array body "rejected" (fun item -> {
                            Index = int32Of item "index"
                            Reason = refusal (expect JsonValueKind.Object "reason" (prop item "reason"))
                        })
                }
            | "compositeKey" -> BCompositeKey(compositeKey body)
            | "fitOutcome" -> BFitOutcome(fitOutcome body)
            | "registryQuery" -> BRegistryQuery(registryQuery body)
            | "outcomePage" -> BOutcomePage(outcomePage body)
            | "scoreRequest" -> BScoreRequest(scoreRequest body)
            | "refusal" -> BRefusal(refusal body)
            | other -> malformed $"unhandled kind '{other}'"

        {
            EnvelopeVersion = version
            Kind = kind
            Body = decoded
        }

    /// `Ok` when the document is accepted; `Error (rejectClass,
    /// refusal)` when a rule of §5 / §7.9 refuses it.
    let envelope (bytes: byte[]) : Result<SpecEnvelope, string * SpecRefusal> =
        try
            use doc = JsonDocument.Parse(ReadOnlyMemory bytes)
            Ok(envelopeOf doc.RootElement)
        with
        | Refused(cls, r) -> Error(cls, r)
        | :? JsonException as ex -> Error("envelope-malformed", InvalidSubmission ex.Message)

// ─── The submitter's pre-emit obligation (§4.2 rule 5) ───────────────

[<RequireQualifiedAccess>]
module SubmitterPreEmit =

    /// §4.2 rule 5 — a submitter MUST NOT emit a submission whose
    /// `specHash` is not the minting of its own `specPayload` under the
    /// algorithm it names.
    ///
    /// This is the only check in this file that a receiving party is
    /// **forbidden** to perform (§4.2 rule 2). It lives here, apart from
    /// `Decode`, so that separation is structural rather than a comment:
    /// nothing on the receiving path can reach it.
    ///
    /// A submission naming an unregistered algorithm carries no
    /// obligation this rule can express — that is an ordinary use of an
    /// open registry, not a defect — so it is accepted.
    let check (submission: SpecFitSubmission) : Result<unit, string * SpecRefusal> =
        if submission.SpecHashAlgorithm <> Minting.algorithm then
            Ok()
        else
            let minted = Minting.mint submission.SpecPayload

            if minted = submission.SpecHash then
                Ok()
            else
                Error(
                    "submission-spec-hash-non-canonical",
                    InvalidSubmission
                        $"specHash '{submission.SpecHash}' is not the minting of specPayload under '{Minting.algorithm}' (which mints '{minted}')"
                )

// ─── The manifest (§9) ───────────────────────────────────────────────

type Vector = {
    Id: string
    Family: string
    Profile: string
    Kind: string
    File: string
    Sha256: string
    Reject: string option
    Digest: string option
    CanonicalPayload: string option
    Interpretation: string option
}

type Manifest = {
    Specification: string
    EnvelopeVersion: int
    Families: string list
    Profiles: (string * string list) list
    Vectors: Vector list
    Bytes: byte[]
}

[<RequireQualifiedAccess>]
module Manifest =

    let private optStr (e: JsonElement) (name: string) =
        match e.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
        | _ -> None

    let load () : Manifest =
        let raw = Corpus.bytes "manifest.json"
        use doc = JsonDocument.Parse(ReadOnlyMemory raw)
        let root = doc.RootElement

        {
            Specification = root.GetProperty("specification").GetString()
            EnvelopeVersion = root.GetProperty("envelopeVersion").GetInt32()
            Families =
                root.GetProperty("families").EnumerateArray()
                |> Seq.map _.GetString()
                |> List.ofSeq
            Profiles =
                root.GetProperty("profiles").EnumerateObject()
                |> Seq.map (fun p -> p.Name, (p.Value.EnumerateArray() |> Seq.map _.GetString() |> List.ofSeq))
                |> List.ofSeq
            Vectors =
                root.GetProperty("vectors").EnumerateArray()
                |> Seq.map (fun v -> {
                    Id = v.GetProperty("id").GetString()
                    Family = v.GetProperty("family").GetString()
                    Profile = v.GetProperty("profile").GetString()
                    Kind = v.GetProperty("kind").GetString()
                    File = v.GetProperty("file").GetString()
                    Sha256 = v.GetProperty("sha256").GetString()
                    Reject = optStr v "reject"
                    Digest = optStr v "digest"
                    CanonicalPayload = optStr v "canonicalPayload"
                    Interpretation = optStr v "interpretation"
                })
                |> List.ofSeq
            Bytes = raw
        }

    let instance = lazy (load ())

// ─── The bridge to the platform's wire face, with its carry gaps ─────
//
// Every family round-trips through the real `ModelExecution*` record
// plus a `Residue` naming exactly the members that record does not
// model. Both halves are needed for the round-trip to be byte-exact,
// which is what makes the residue an honest inventory rather than a
// disclaimer: anything omitted from it fails the test.

/// Residue for a refusal: the members the platform's DU case does not
/// carry.
///
/// Declared ahead of the shape residues because the receipt's rejected
/// items carry refusals, and since Phase 640 they carry them *as
/// refusals* rather than as flattened class names — so the receipt's
/// residue is now expressed in terms of this one.
type RefusalResidue = {
    UnknownProviderKnown: string list
    BudgetUnit: string
    NotFoundId: string
    ScoreReason: string
}

/// Members of a `fitSubmission` the platform's submission record does
/// not carry.
///
/// _(Phase 640 removed `SpecHashAlgorithm`: the submission record now
/// carries the minting rule verbatim. The opacity posture is unchanged —
/// forge still never re-derives the hash — but a rotation is now visible
/// on this face instead of silent.)_
type SubmissionResidue = {
    /// A submitter-resolved content ref on the pinned vintage. The
    /// submission record pins by `(datasetId, version)` alone, which
    /// §5.2 declares fully conformant.
    VintageContentRef: SpecContentRef option
}

/// Members of a `submissionReceipt` the platform's receipt record does
/// not carry.
///
/// _(Phase 640 removed `AcceptedJobIds` — the handle is an opaque string
/// on both sides now, so nothing is lost inbound — and reduced
/// `RejectedRefusals` to `RejectedResidues`: the receipt carries the
/// typed refusal itself, and what remains is only the refusal family's
/// own residue, inventoried once below rather than a second time here.)_
type ReceiptResidue = {
    /// Per rejected index, the residue of that item's refusal.
    RejectedResidues: (int * RefusalResidue) list
}

/// Members of a `fitOutcome` the platform's outcome record does not
/// carry.
///
/// _(Phase 640 removed `ArtifactAbsent`, `ArtifactFormat`, `Timing` and
/// `Cost`: the outcome record carries an optional artifact reference with
/// its format, plus timing and cost. What forge's own registry retains
/// for a given outcome is a separate question from what this face can
/// express — the residue was only ever about the latter.)_
type OutcomeResidue = {
    /// The outcome record keys the vintage as a composed string, which
    /// cannot express a content ref.
    VintageContentRef: SpecContentRef option
}

type QueryResidue = {
    /// The query record carries each vintage as a composed string.
    /// §5.5 requires matching on `(datasetId, version)` only, so nothing
    /// is lost for matching — only for echoing the caller's document
    /// back.
    VintageContentRefs: SpecContentRef option list
}

type ResolvedVintageResidue = {
    /// Advisory at the moment of resolution, and never an identity
    /// input — so its absence from the dataset-version record costs
    /// nothing but is still a difference.
    IsLatest: bool
    /// The dataset-version record's row count is not optional.
    RowCountAbsent: bool
}

[<RequireQualifiedAccess>]
module Bridge =

    // ── Vocabulary mapping ───────────────────────────────────────────
    //
    // The wire vocabulary and the platform's own case-name strings are
    // not the same words. The mapping is here, at the seam, rather than
    // by renaming either side.

    let toWireDirection (d: string) =
        match d with
        | "AtLeast" -> "atLeast"
        | "AtMost" -> "atMost"
        | other -> other

    let ofWireDirection (d: string) =
        match d with
        | "atLeast" -> "AtLeast"
        | "atMost" -> "AtMost"
        | other -> other

    /// The dataset-version key the platform's outcome + query records
    /// use. Composed **without a scope segment**: §6.2 forbids a scope
    /// on the wire, and a receiver resolves its own.
    let datasetKey (v: SpecVintageRef) = $"{v.DatasetId}@v{v.Version}"

    let refusalClassName (r: SpecRefusal) =
        match r with
        | EnvelopeVersionMismatch _ -> "envelopeVersionMismatch"
        | UnknownDocumentKind _ -> "unknownDocumentKind"
        | InvalidSubmission _ -> "invalidSubmission"
        | InvalidQuery _ -> "invalidQuery"
        | UnknownProvider _ -> "unknownProvider"
        | BudgetDenied _ -> "budgetDenied"
        | GateFailed _ -> "gateFailed"
        | PolicyRefused _ -> "policyRefused"
        | ScopeUnavailable -> "scopeUnavailable"
        | Forbidden _ -> "forbidden"
        | NotFound _ -> "notFound"
        | SubstrateUnavailable _ -> "substrateUnavailable"
        | ScoreRefused _ -> "scoreRefused"
        | StorageFailure _ -> "storageFailure"
        | Unspecified _ -> "unspecified"
        | UnrecognisedClass(cls, _) -> cls

    let parseDatasetKey (key: string) =
        match key.LastIndexOf "@v" with
        | -1 -> failwithf "not a dataset-version key: '%s'" key
        | i -> key.Substring(0, i), Int32.Parse(key.Substring(i + 2), CultureInfo.InvariantCulture)

    // ── refusal ──────────────────────────────────────────────────────
    //
    // Kept ahead of the shape families because a receipt's rejected items
    // carry refusals, and since Phase 640 they carry them as refusals.

    /// The refusal classes the platform's closed refusal DU has no case
    /// for. Pinned as a set rather than described in prose: if a case is
    /// added, this pin fails and its author decides deliberately whether
    /// the gap is closed.
    ///
    /// **Empty since Phase 640**, which added the four that were missing
    /// (`envelopeVersionMismatch`, `unknownDocumentKind`, `gateFailed`,
    /// `policyRefused`). Kept rather than deleted, and kept as a list
    /// rather than folded into a boolean: the next class registered
    /// against the specification lands here first, and a named, empty
    /// inventory is the thing that makes its arrival a decision instead of
    /// an omission.
    let unmappedRefusalClasses: string list = []

    let emptyRefusalResidue = {
        UnknownProviderKnown = []
        BudgetUnit = ""
        NotFoundId = ""
        ScoreReason = ""
    }

    let toRefusal (r: SpecRefusal) : (ModelExecutionRefusal * RefusalResidue) option =
        match r with
        | InvalidSubmission reason -> Some(ModelExecutionRefusal.InvalidSubmission reason, emptyRefusalResidue)
        | InvalidQuery reason -> Some(ModelExecutionRefusal.InvalidQuery reason, emptyRefusalResidue)
        | UnknownProvider(kind, known) ->
            Some(
                ModelExecutionRefusal.UnknownProvider kind,
                {
                    emptyRefusalResidue with
                        UnknownProviderKnown = known
                }
            )
        | BudgetDenied(quota, spent, unit) ->
            Some(
                ModelExecutionRefusal.BudgetDenied {
                    ScopeId = ""
                    SubmitterClass = ""
                    Dimension = ""
                    Quota = decimal quota
                    Spent = decimal spent
                    Requested = 0m
                    PeriodKey = ""
                },
                {
                    emptyRefusalResidue with
                        BudgetUnit = unit
                }
            )
        | ScopeUnavailable -> Some(ModelExecutionRefusal.ScopeUnavailable, emptyRefusalResidue)
        | Forbidden reason -> Some(ModelExecutionRefusal.Forbidden reason, emptyRefusalResidue)
        | NotFound(what, id) ->
            Some(
                ModelExecutionRefusal.NotFound what,
                {
                    emptyRefusalResidue with
                        NotFoundId = id
                }
            )
        | SubstrateUnavailable surface -> Some(ModelExecutionRefusal.SubstrateDisabled surface, emptyRefusalResidue)
        | ScoreRefused(reason, detail) ->
            let scoring =
                match reason with
                | "provider-not-found" -> ModelExecutionScoreRefusal.ProviderNotFound detail
                | "not-approved" -> ModelExecutionScoreRefusal.NotApproved detail
                | "input-schema-mismatch" -> ModelExecutionScoreRefusal.InputSchemaMismatch detail
                | "input-unavailable" -> ModelExecutionScoreRefusal.InputUnavailable detail
                | "provider-failed" -> ModelExecutionScoreRefusal.ProviderFailed("", detail)
                | _ -> ModelExecutionScoreRefusal.StorageFailure detail

            Some(
                ModelExecutionRefusal.ScoreRefused scoring,
                {
                    emptyRefusalResidue with
                        ScoreReason = reason
                }
            )
        | StorageFailure reason -> Some(ModelExecutionRefusal.StorageFailure reason, emptyRefusalResidue)
        | Unspecified message -> Some(ModelExecutionRefusal.Unexpected message, emptyRefusalResidue)
        | EnvelopeVersionMismatch(received, accepted) ->
            Some(ModelExecutionRefusal.EnvelopeVersionMismatch(received, accepted), emptyRefusalResidue)
        | UnknownDocumentKind(kind, known) ->
            Some(ModelExecutionRefusal.UnknownDocumentKind(kind, known), emptyRefusalResidue)
        | GateFailed verdicts ->
            Some(
                ModelExecutionRefusal.GateFailed(
                    verdicts
                    |> List.map (fun v -> {
                        Name = v.Name
                        Threshold = v.Threshold
                        Direction = ofWireDirection v.Direction
                        Observed = v.Observed
                        Passed = v.Passed
                    })
                ),
                emptyRefusalResidue
            )
        | PolicyRefused rule -> Some(ModelExecutionRefusal.PolicyRefused rule, emptyRefusalResidue)
        // The one case that stays unmapped, and stays unmapped on purpose:
        // §5.7.2 rule 2 says a reader treats an unregistered class AS
        // `unspecified`, so the platform's DU is right not to have a case
        // for it. It is not a carry gap — it is the extension rule working.
        | UnrecognisedClass _ -> None

    let ofRefusal (f: ModelExecutionRefusal) (r: RefusalResidue) : SpecRefusal =
        match f with
        | ModelExecutionRefusal.InvalidSubmission reason -> InvalidSubmission reason
        | ModelExecutionRefusal.InvalidQuery reason -> InvalidQuery reason
        | ModelExecutionRefusal.UnknownProvider kind -> UnknownProvider(kind, r.UnknownProviderKnown)
        | ModelExecutionRefusal.BudgetDenied d -> BudgetDenied(float d.Quota, float d.Spent, r.BudgetUnit)
        | ModelExecutionRefusal.ScopeUnavailable -> ScopeUnavailable
        | ModelExecutionRefusal.Forbidden reason -> Forbidden reason
        | ModelExecutionRefusal.NotFound what -> NotFound(what, r.NotFoundId)
        | ModelExecutionRefusal.SubstrateDisabled surface -> SubstrateUnavailable surface
        | ModelExecutionRefusal.ScoreRefused scoring ->
            let detail =
                match scoring with
                | ModelExecutionScoreRefusal.ProviderNotFound d
                | ModelExecutionScoreRefusal.NotApproved d
                | ModelExecutionScoreRefusal.InputSchemaMismatch d
                | ModelExecutionScoreRefusal.InputUnavailable d
                | ModelExecutionScoreRefusal.StorageFailure d -> d
                | ModelExecutionScoreRefusal.ProviderFailed(_, d) -> d

            ScoreRefused(r.ScoreReason, detail)
        | ModelExecutionRefusal.EnvelopeVersionMismatch(received, accepted) ->
            EnvelopeVersionMismatch(received, accepted)
        | ModelExecutionRefusal.UnknownDocumentKind(kind, known) -> UnknownDocumentKind(kind, known)
        | ModelExecutionRefusal.GateFailed verdicts ->
            GateFailed(
                verdicts
                |> List.map (fun v -> {
                    Name = v.Name
                    Threshold = v.Threshold
                    Direction = toWireDirection v.Direction
                    Observed = v.Observed
                    Passed = v.Passed
                })
            )
        | ModelExecutionRefusal.PolicyRefused rule -> PolicyRefused rule
        | ModelExecutionRefusal.StorageFailure reason -> StorageFailure reason
        | ModelExecutionRefusal.Unexpected message -> Unspecified message
    // ── fit submission ───────────────────────────────────────────────

    let toSubmission (s: SpecFitSubmission) : ModelExecutionFitSubmission * SubmissionResidue =
        let forge = {
            DatasetId = s.Vintage.DatasetId
            DatasetVersion = s.Vintage.Version
            SpecPayload = s.SpecPayload
            // Stored exactly as handed. Never re-derived — §4.2 rule 2.
            SpecHash = s.SpecHash
            // Carried since Phase 640, and carrying it changes nothing
            // about rule 2: the platform stores the identifier without
            // acting on it, which is what makes a rotation visible without
            // making the hash checkable.
            SpecHashAlgorithm = s.SpecHashAlgorithm
            ProviderKind = s.ProviderKind
            Seed = s.Seed
            Gates =
                s.Gates
                |> List.map (fun g -> {
                    Name = g.Name
                    Threshold = g.Threshold
                    Direction = ofWireDirection g.Direction
                })
            SubmitterClass =
                match SubmitterClass.parse s.SubmitterClass with
                | Some c -> c
                | None -> failwithf "submitter class '%s' is not one the platform models" s.SubmitterClass
        }

        // Annotated because `OutcomeResidue` now has the same single
        // member, and unannotated record inference takes the last one
        // declared. Two residues converging on one field is a sign the
        // inventory shrank, not that either is redundant.
        let residue: SubmissionResidue = {
            VintageContentRef = s.Vintage.ContentRef
        }

        forge, residue

    let ofSubmission (f: ModelExecutionFitSubmission) (r: SubmissionResidue) : SpecFitSubmission = {
        Vintage = {
            DatasetId = f.DatasetId
            Version = f.DatasetVersion
            ContentRef = r.VintageContentRef
        }
        SpecPayload = f.SpecPayload
        SpecHash = f.SpecHash
        SpecHashAlgorithm = f.SpecHashAlgorithm
        ProviderKind = f.ProviderKind
        Seed = f.Seed
        Gates =
            f.Gates
            |> List.map (fun g -> {
                Name = g.Name
                Threshold = g.Threshold
                Direction = toWireDirection g.Direction
            })
        SubmitterClass = SubmitterClass.label f.SubmitterClass
    }

    // ── batch ────────────────────────────────────────────────────────

    let toBatch (b: SpecFitSubmissionBatch) : ModelExecutionBatchSubmission * SubmissionResidue list =
        let pairs = b.Submissions |> List.map toSubmission

        {
            BatchId = b.BatchId
            Items = pairs |> List.map fst
        },
        pairs |> List.map snd

    let ofBatch (f: ModelExecutionBatchSubmission) (residues: SubmissionResidue list) : SpecFitSubmissionBatch = {
        BatchId = f.BatchId
        Submissions = List.map2 ofSubmission f.Items residues
    }

    // ── receipt ──────────────────────────────────────────────────────

    // The `handleGuid` stand-in this section used until Phase 640 is gone
    // with the gap that required it: the receipt's handle is a string on
    // both sides now, so there is nothing to derive and nothing to invert.

    let toReceipt (r: SpecSubmissionReceipt) : ModelExecutionReceipt * ReceiptResidue =
        let rejected =
            r.Rejected
            |> List.map (fun x ->
                match toRefusal x.Reason with
                | Some(forgeRefusal, refusalResidue) -> x.Index, forgeRefusal, refusalResidue
                | None ->
                    // Only `UnrecognisedClass` returns `None`, and a
                    // corpus receipt never carries one — a fixture that
                    // did would be asserting the extension rule, not a
                    // receipt. Loud rather than silently dropped.
                    failwithf "receipt item %d carries a refusal class the platform cannot model" x.Index)

        let forge = {
            BatchId = r.BatchId
            ItemCount = r.ItemCount
            Jobs = r.Accepted |> List.map (fun a -> { Index = a.Index; JobId = a.JobId })
            EnqueueFailures = rejected |> List.map (fun (index, refusal, _) -> index, refusal)
        }

        let residue = {
            RejectedResidues = rejected |> List.map (fun (index, _, res) -> index, res)
        }

        forge, residue

    let ofReceipt (f: ModelExecutionReceipt) (r: ReceiptResidue) : SpecSubmissionReceipt =
        let residues = dict r.RejectedResidues

        {
            BatchId = f.BatchId
            ItemCount = f.ItemCount
            Accepted = f.Jobs |> List.map (fun j -> { Index = j.Index; JobId = j.JobId })
            Rejected =
                f.EnqueueFailures
                |> List.map (fun (index, refusal) -> {
                    Index = index
                    Reason = ofRefusal refusal residues[index]
                })
        }

    // ── outcome ──────────────────────────────────────────────────────

    let toOutcome (o: SpecFitOutcome) : ModelExecutionOutcome * OutcomeResidue =
        let forge = {
            CompositeKeyHash = o.CompositeKeyHash
            SpecHash = o.CompositeKey.SpecHash
            DatasetVersion = datasetKey o.CompositeKey.Vintage
            Seed = o.CompositeKey.Seed
            ProviderId = o.CompositeKey.ProviderId
            ProviderVersion = o.CompositeKey.ProviderVersion
            Artifact =
                o.ArtifactRef
                |> Option.map (fun a -> {
                    ArtifactId = a.ArtifactId
                    ContentHash = a.ContentHash
                    Format = a.Format
                })
            Timing = {
                SubmittedAt = o.Timing.SubmittedAt
                StartedAt = o.Timing.StartedAt
                CompletedAt = o.Timing.CompletedAt
                DurationMs = o.Timing.DurationMs
            }
            Cost = o.Cost |> Option.map (fun c -> { Unit = c.Unit; Amount = c.Amount })
            Diagnostics = Map.ofList o.Diagnostics
            GateVerdicts =
                o.GateVerdicts
                |> List.map (fun v -> {
                    Name = v.Name
                    Threshold = v.Threshold
                    Direction = ofWireDirection v.Direction
                    Observed = v.Observed
                    Passed = v.Passed
                })
            Status = o.Status
            Annotations = Map.ofList o.Annotations
            RegisteredAt = o.RegisteredAt
        }

        let residue: OutcomeResidue = {
            VintageContentRef = o.CompositeKey.Vintage.ContentRef
        }

        forge, residue

    let ofOutcome (f: ModelExecutionOutcome) (r: OutcomeResidue) : SpecFitOutcome =
        let datasetId, version = parseDatasetKey f.DatasetVersion

        {
            CompositeKeyHash = f.CompositeKeyHash
            CompositeKey = {
                SpecHash = f.SpecHash
                Vintage = {
                    DatasetId = datasetId
                    Version = version
                    ContentRef = r.VintageContentRef
                }
                Seed = f.Seed
                ProviderId = f.ProviderId
                ProviderVersion = f.ProviderVersion
            }
            ArtifactRef =
                f.Artifact
                |> Option.map (fun a -> {
                    ArtifactId = a.ArtifactId
                    ContentHash = a.ContentHash
                    Format = a.Format
                })
            // A map's keys are data and the encoder sorts them, so the
            // list order recovered here is not load-bearing.
            Diagnostics = f.Diagnostics |> Map.toList
            GateVerdicts =
                f.GateVerdicts
                |> List.map (fun v -> {
                    Name = v.Name
                    Threshold = v.Threshold
                    Direction = toWireDirection v.Direction
                    Observed = v.Observed
                    Passed = v.Passed
                })
            Status = f.Status
            Timing = {
                SubmittedAt = f.Timing.SubmittedAt
                StartedAt = f.Timing.StartedAt
                CompletedAt = f.Timing.CompletedAt
                DurationMs = f.Timing.DurationMs
            }
            Cost = f.Cost |> Option.map (fun c -> { Unit = c.Unit; Amount = c.Amount })
            Annotations = f.Annotations |> Map.toList
            RegisteredAt = f.RegisteredAt
        }

    // ── registry query + page ────────────────────────────────────────

    let toQuery (q: SpecRegistryQuery) : ModelExecutionOutcomeQuery * (string option * int) * QueryResidue =
        {
            SpecHashes = q.SpecHashes
            DatasetVersions = q.Vintages |> List.map datasetKey
            Statuses = q.Statuses
            BatchId = q.BatchId
        },
        (q.Page.Cursor, q.Page.Limit),
        {
            VintageContentRefs = q.Vintages |> List.map _.ContentRef
        }

    let ofQuery
        (f: ModelExecutionOutcomeQuery)
        (cursor: string option, limit: int)
        (r: QueryResidue)
        : SpecRegistryQuery =
        {
            SpecHashes = f.SpecHashes
            Vintages =
                List.map2
                    (fun key contentRef ->
                        let datasetId, version = parseDatasetKey key

                        {
                            DatasetId = datasetId
                            Version = version
                            ContentRef = contentRef
                        })
                    f.DatasetVersions
                    r.VintageContentRefs
            Statuses = f.Statuses
            BatchId = f.BatchId
            Page = { Cursor = cursor; Limit = limit }
        }

    let toPage (p: SpecOutcomePage) : ModelExecutionOutcomePage * OutcomeResidue list =
        let pairs = p.Outcomes |> List.map toOutcome

        {
            Outcomes = pairs |> List.map fst
            NextCursor = p.NextCursor
        },
        pairs |> List.map snd

    let ofPage (f: ModelExecutionOutcomePage) (residues: OutcomeResidue list) : SpecOutcomePage = {
        Outcomes = List.map2 ofOutcome f.Outcomes residues
        NextCursor = f.NextCursor
    }

    // ── score request ────────────────────────────────────────────────

    let toScoreRequest (s: SpecScoreRequest) : ModelExecutionScoreRequest * SpecContentRef option =
        {
            ArtifactKeyHash = s.ArtifactKeyHash
            InputDatasetId = s.Input.DatasetId
            InputVersion = s.Input.Version
            OutputDatasetId = s.OutputDatasetId
        },
        s.Input.ContentRef

    let ofScoreRequest (f: ModelExecutionScoreRequest) (contentRef: SpecContentRef option) : SpecScoreRequest = {
        ArtifactKeyHash = f.ArtifactKeyHash
        Input = {
            DatasetId = f.InputDatasetId
            Version = f.InputVersion
            ContentRef = contentRef
        }
        OutputDatasetId = f.OutputDatasetId
    }

    // ── resolved vintage ─────────────────────────────────────────────

    let toResolvedVintage (r: SpecResolvedVintage) : ModelExecutionDatasetVersion * ResolvedVintageResidue =
        // §5.2 — a resolution that resolves nothing is not an answer, so
        // the content ref is required on this shape.
        let contentRef =
            match r.Ref.ContentRef with
            | Some c -> c
            | None -> failwith "a resolvedVintage must carry a content ref (§5.2)"

        {
            DatasetId = r.Ref.DatasetId
            Version = r.Ref.Version
            RowCount = defaultArg contentRef.RowCount 0L
            Format = contentRef.Format
            ContentHash = contentRef.Hash
            CreatedAt = r.CreatedAt
        },
        {
            IsLatest = r.IsLatest
            RowCountAbsent = Option.isNone contentRef.RowCount
        }

    let ofResolvedVintage (f: ModelExecutionDatasetVersion) (r: ResolvedVintageResidue) : SpecResolvedVintage = {
        Ref = {
            DatasetId = f.DatasetId
            Version = f.Version
            ContentRef =
                Some {
                    Format = f.Format
                    Hash = f.ContentHash
                    RowCount = if r.RowCountAbsent then None else Some f.RowCount
                }
        }
        CreatedAt = f.CreatedAt
        IsLatest = r.IsLatest
    }


// ─── Tests ───────────────────────────────────────────────────────────

[<AutoOpen>]
module private Helpers =

    /// Loaded once, defensively: a corpus that cannot be resolved must
    /// fail as a named test rather than as an exception during module
    /// initialisation, which would take the whole runner down and bury
    /// the reason under five thousand unrelated cases. The failure is
    /// still unconditional — see `tests` at the foot of this file.
    let loadOutcome: Result<Manifest, string> =
        try
            Ok(Manifest.load ())
        with ex ->
            Error ex.Message

    let loaded =
        match loadOutcome with
        | Ok m -> Some m
        | Error _ -> None

    let manifest =
        lazy
            (match loadOutcome with
             | Ok m -> m
             | Error message -> failwith message)

    let vectorsOf (kind: string) =
        match loaded with
        | Some m -> m.Vectors |> List.filter (fun v -> v.Kind = kind)
        | None -> []

    let decoded (v: Vector) =
        match Decode.envelope (Corpus.bytes v.File) with
        | Ok envelope -> envelope
        | Error(cls, refusal) -> failwithf "%s was refused (%s / %A) where it must be accepted" v.Id cls refusal

    let expectBytes (context: string) (expected: byte[]) (actual: byte[]) =
        if expected <> actual then
            Expect.equal (Encoding.UTF8.GetString actual) (Encoding.UTF8.GetString expected) context

    /// Round-trip through the spec model: decode, re-encode, compare
    /// bytes. `§9.1` — the bytes MUST be identical.
    let roundTrip (v: Vector) =
        let original = Corpus.bytes v.File
        expectBytes $"{v.Id} must re-encode to the bytes it was decoded from" original (Encode.bytes (decoded v))

    let recordFieldNames<'T> () =
        FSharpType.GetRecordFields(typeof<'T>, BindingFlags.Public ||| BindingFlags.NonPublic)
        |> Array.map _.Name
        |> List.ofArray

    /// The counter §9.3 asks for: not that everything passed, but that
    /// the expected number of vectors actually ran.
    let executed = HashSet<string>()

    let markExecuted (v: Vector) =
        lock executed (fun () -> executed.Add v.Id |> ignore)

// ── The pin + the corpus itself ──────────────────────────────────────

let private pinTests =
    testList "pin" [
        test "the corpus is present, identifies itself, and matches the pinned digest" {
            let m = manifest.Value

            Expect.equal
                m.Specification
                Pin.specification
                "the corpus must be the specification this harness certifies against"

            Expect.equal
                m.EnvelopeVersion
                Pin.envelopeVersion
                "the wire version must be the one this harness implements"

            Expect.equal
                (Digest.hex m.Bytes)
                Pin.manifestDigest
                $"the corpus has drifted from the pin. This harness is certified against corpus revision {Pin.commit}; check that revision out, or bump Pin.commit and Pin.manifestDigest together in a reviewed commit after reading the corpus diff (602.B)."
        }

        test "the claimed profile exists and requires every family" {
            let m = manifest.Value

            let required =
                m.Profiles
                |> List.tryFind (fun (name, _) -> name = Pin.profile)
                |> Option.map snd
                |> Option.defaultWith (fun () -> failwithf "the corpus declares no '%s' profile" Pin.profile)

            Expect.equal
                (List.sort required)
                (List.sort m.Families)
                "the executor profile requires every family — certifying a subset is not certifying (§9.2)"
        }

        test "every fixture's bytes match the digest the manifest records" {
            for v in manifest.Value.Vectors do
                Expect.equal (Digest.hex (Corpus.bytes v.File)) v.Sha256 $"{v.Id} has been edited in place"
        }
    ]

// ── round-trip (§9.1) ────────────────────────────────────────────────

let private roundTripTests =
    testList "round-trip" [
        for v in vectorsOf "round-trip" ->
            test v.Id {
                roundTrip v
                markExecuted v
            }
    ]

// ── hash (§9.1, §9.4) ────────────────────────────────────────────────

let private hashTests =
    testList "hash" [
        for v in vectorsOf "hash" ->
            test v.Id {
                roundTrip v

                let digest = Option.get v.Digest

                match v.Family, (decoded v).Body with
                // §9.4 — a fit-outcome hash vector recomputes the
                // composite-key content address from the document's own
                // composite key.
                | "fit-outcome", BCompositeKey key ->
                    Expect.equal
                        (Digest.contentAddress (Canonical.toBytes (Encode.compositeKey key)))
                        digest
                        "the composite key's content address must be reproducible from the key itself (§4.3)"
                | "fit-outcome", BFitOutcome outcome ->
                    let recomputed =
                        Digest.contentAddress (Canonical.toBytes (Encode.compositeKey outcome.CompositeKey))

                    Expect.equal recomputed digest "the manifest's digest must be the recomputed composite-key address"

                    Expect.equal
                        outcome.CompositeKeyHash
                        recomputed
                        "an outcome whose hash does not equal a recomputation over its own composite key is corrupt (§4.3)"
                // §9.4 — a spec-hash vector recomputes the minted
                // specHash from the document's own payload. A different
                // recomputation from the one above, on the same vector
                // kind.
                | "spec-hash", BFitSubmission submission ->
                    Expect.equal
                        (Encoding.UTF8.GetString(Minting.canonicalBytes submission.SpecPayload))
                        (Option.get v.CanonicalPayload)
                        "the minting intermediate must match the corpus's own, so a divergence names where it happened (§9.4)"

                    Expect.equal
                        (Minting.mint submission.SpecPayload)
                        digest
                        "the minted specHash must match the manifest"

                    Expect.equal
                        submission.SpecHash
                        digest
                        "a conformant submission's specHash is the minting of its own payload (§4.2 rule 5)"
                | family, body -> failwithf "unexpected hash vector %s in family %s: %A" v.Id family body

                markExecuted v
            }
    ]

// ── reject (§9.1) ────────────────────────────────────────────────────

let private rejectTests =
    testList "reject" [
        for v in vectorsOf "reject" ->
            test v.Id {
                let expected = Option.get v.Reject
                let bytes = Corpus.bytes v.File

                if v.Profile = "submitter" then
                    // §2 — a reject vector's profile names the party
                    // that must catch it, and reading it as always
                    // meaning "executor" is the mistake this branch
                    // exists to prevent. The receiving path MUST NOT
                    // refuse this document (§4.2 rule 2); the
                    // submitter's pre-emit check MUST.
                    let received =
                        match Decode.envelope bytes with
                        | Ok e -> e
                        | Error(cls, r) ->
                            failwithf
                                "%s is a submitter's pre-emit obligation and the receiving path is forbidden to check it (§4.2 rule 2), yet it refused with %s / %A"
                                v.Id
                                cls
                                r

                    match received.Body with
                    | BFitSubmission submission ->
                        match SubmitterPreEmit.check submission with
                        | Ok() -> failtestf "%s must be refused before it is ever emitted (§4.2 rule 5)" v.Id
                        | Error(cls, _) -> Expect.equal cls expected "the reject class is normative; the wording is not"
                    | other -> failtestf "%s decoded to an unexpected body: %A" v.Id other
                else
                    match Decode.envelope bytes with
                    | Ok _ -> failtestf "%s must be refused by the receiving path" v.Id
                    | Error(cls, _) -> Expect.equal cls expected "the reject class is normative; the wording is not"

                markExecuted v
            }
    ]

// ── accept (§9.1) — the forward-compatibility vectors ────────────────

let private acceptTests =
    testList "accept" [
        for v in vectorsOf "accept" ->
            test v.Id {
                let received =
                    match Decode.envelope (Corpus.bytes v.File) with
                    | Ok e -> e
                    | Error(cls, r) ->
                        failwithf
                            "%s must not be refused — an implementation that refuses it will break on the next additive change (§9.1): %s / %A"
                            v.Id
                            cls
                            r

                match received.Body, Option.get v.Interpretation with
                | BRefusal(UnrecognisedClass(cls, message)), "unspecified" ->
                    Expect.isNotEmpty
                        cls
                        "the unrecognised class name must be reported so an operator sees an upgrade is available"

                    Expect.isNotEmpty
                        message
                        "whatever human-readable text can be found must be preserved (§5.7.2 rule 2)"
                | body, interpretation -> failtestf "%s: %A does not read as '%s'" v.Id body interpretation

                markExecuted v
            }
    ]

// ── The bridge: the corpus driven through the platform's wire face ───

let private bridgeTests =
    testList "wire-face" [
        test "fit submissions round-trip through the submission record" {
            for v in manifest.Value.Vectors do
                match Decode.envelope (Corpus.bytes v.File) with
                | Ok({ Body = BFitSubmission s } as envelope) ->
                    let forge, residue = Bridge.toSubmission s
                    let back = Bridge.ofSubmission forge residue

                    expectBytes
                        $"{v.Id} must survive the submission record byte-for-byte"
                        (Corpus.bytes v.File)
                        (Encode.bytes {
                            envelope with
                                Body = BFitSubmission back
                        })
                | _ -> ()
        }

        test "batches round-trip through the batch record, order preserved" {
            let v = manifest.Value.Vectors |> List.find (fun x -> x.Id = "fit-submission/batch")
            let envelope = decoded v

            match envelope.Body with
            | BFitSubmissionBatch b ->
                let forge, residues = Bridge.toBatch b
                let back = Bridge.ofBatch forge residues

                // §5.3 — the submission list is the one list that is not
                // sorted, because the receipt indexes into it.
                Expect.equal
                    (back.Submissions |> List.map _.Seed)
                    (b.Submissions |> List.map _.Seed)
                    "an emitter that sorts the submission list renumbers the submitter's own work"

                expectBytes
                    "the batch must survive the batch record byte-for-byte"
                    (Corpus.bytes v.File)
                    (Encode.bytes {
                        envelope with
                            Body = BFitSubmissionBatch back
                    })
            | other -> failtestf "unexpected body: %A" other
        }

        test "receipts round-trip through the receipt record" {
            let v =
                manifest.Value.Vectors |> List.find (fun x -> x.Id = "fit-submission/receipt")

            let envelope = decoded v

            match envelope.Body with
            | BSubmissionReceipt r ->
                let forge, residue = Bridge.toReceipt r
                let back = Bridge.ofReceipt forge residue

                // §5.3 — accepted and rejected must partition the
                // submitted indices exactly.
                Expect.equal
                    (List.length back.Accepted + List.length back.Rejected)
                    back.ItemCount
                    "accepted and rejected must partition the submitted indices exactly"

                expectBytes
                    "the receipt must survive the receipt record byte-for-byte"
                    (Corpus.bytes v.File)
                    (Encode.bytes {
                        envelope with
                            Body = BSubmissionReceipt back
                    })
            | other -> failtestf "unexpected body: %A" other
        }

        test "outcomes round-trip through the outcome record" {
            for v in manifest.Value.Vectors |> List.filter (fun x -> x.Kind <> "reject") do
                match Decode.envelope (Corpus.bytes v.File) with
                | Ok({ Body = BFitOutcome o } as envelope) ->
                    let forge, residue = Bridge.toOutcome o
                    let back = Bridge.ofOutcome forge residue

                    expectBytes
                        $"{v.Id} must survive the outcome record byte-for-byte"
                        (Corpus.bytes v.File)
                        (Encode.bytes {
                            envelope with
                                Body = BFitOutcome back
                        })
                | _ -> ()
        }

        test "queries and pages round-trip through the query and page records" {
            for v in manifest.Value.Vectors |> List.filter (fun x -> x.Kind <> "reject") do
                match Decode.envelope (Corpus.bytes v.File) with
                | Ok({ Body = BRegistryQuery q } as envelope) ->
                    let forge, paging, residue = Bridge.toQuery q
                    let back = Bridge.ofQuery forge paging residue

                    expectBytes
                        $"{v.Id} must survive the query record byte-for-byte"
                        (Corpus.bytes v.File)
                        (Encode.bytes {
                            envelope with
                                Body = BRegistryQuery back
                        })
                | Ok({ Body = BOutcomePage p } as envelope) ->
                    let forge, residues = Bridge.toPage p
                    let back = Bridge.ofPage forge residues

                    // §5.5 — pagination is by a value that exists before
                    // the outcome is stored and never changes.
                    Expect.equal
                        (back.Outcomes |> List.map _.CompositeKeyHash)
                        (back.Outcomes |> List.map _.CompositeKeyHash |> List.sortWith Canonical.ordinal)
                        "a page's outcomes are sorted ordinally ascending by composite-key hash"

                    expectBytes
                        $"{v.Id} must survive the page record byte-for-byte"
                        (Corpus.bytes v.File)
                        (Encode.bytes {
                            envelope with
                                Body = BOutcomePage back
                        })
                | _ -> ()
        }

        test "score requests round-trip through the score-request record" {
            let v =
                manifest.Value.Vectors |> List.find (fun x -> x.Id = "score-request/request")

            let envelope = decoded v

            match envelope.Body with
            | BScoreRequest s ->
                let forge, residue = Bridge.toScoreRequest s
                let back = Bridge.ofScoreRequest forge residue

                expectBytes
                    "the score request must survive the score-request record byte-for-byte"
                    (Corpus.bytes v.File)
                    (Encode.bytes {
                        envelope with
                            Body = BScoreRequest back
                    })
            | other -> failtestf "unexpected body: %A" other
        }

        test "resolved vintages round-trip through the dataset-version record" {
            let v = manifest.Value.Vectors |> List.find (fun x -> x.Id = "vintage-ref/resolved")
            let envelope = decoded v

            match envelope.Body with
            | BResolvedVintage r ->
                let forge, residue = Bridge.toResolvedVintage r
                let back = Bridge.ofResolvedVintage forge residue

                expectBytes
                    "the resolved vintage must survive the dataset-version record byte-for-byte"
                    (Corpus.bytes v.File)
                    (Encode.bytes {
                        envelope with
                            Body = BResolvedVintage back
                    })
            | other -> failtestf "unexpected body: %A" other
        }

        test "every refusal class the platform models round-trips through its refusal DU" {
            let mutable mapped = 0

            for v in
                manifest.Value.Vectors
                |> List.filter (fun x -> x.Family = "refusal" && x.Kind = "round-trip") do
                let envelope = decoded v

                match envelope.Body with
                | BRefusal r ->
                    match Bridge.toRefusal r with
                    | Some(forge, residue) ->
                        mapped <- mapped + 1

                        expectBytes
                            $"{v.Id} must survive the refusal DU byte-for-byte"
                            (Corpus.bytes v.File)
                            (Encode.bytes {
                                envelope with
                                    Body = BRefusal(Bridge.ofRefusal forge residue)
                            })
                    | None ->
                        Expect.contains
                            Bridge.unmappedRefusalClasses
                            (Bridge.refusalClassName r)
                            $"{v.Id} has no case in the platform's refusal DU and is not declared as a known gap"
                | other -> failtestf "unexpected body: %A" other

            Expect.equal
                mapped
                (List.length (
                    manifest.Value.Vectors
                    |> List.filter (fun x -> x.Family = "refusal" && x.Kind = "round-trip")
                 )
                 - List.length Bridge.unmappedRefusalClasses)
                "the mapped and unmapped refusal classes must account for every class in the closed vocabulary"
        }

        test "no document carries a scope, tenant or user identifier (§6.2)" {
            // A wire-supplied scope is an impersonation primitive. This
            // is asserted over the corpus rather than assumed, because
            // the platform's own dataset-version key embeds a scope and
            // the bridge has to compose one without it.
            let banned = [ "scopeId"; "tenantId"; "teamId"; "userId"; "principal" ]

            for v in manifest.Value.Vectors do
                let text = Encoding.UTF8.GetString(Corpus.bytes v.File)

                for token in banned do
                    Expect.isFalse
                        (text.Contains($"\"{token}\"", StringComparison.Ordinal))
                        $"{v.Id} carries a '{token}' member, which §6.2 forbids"
        }
    ]

// ── The opaque-hash posture (§4.2), certified end to end ─────────────

let private opaqueHashTests =
    testList "opaque-spec-hash" [
        test "the platform stores exactly the hash it was handed, never a re-derivation" {
            let vectors =
                manifest.Value.Vectors |> List.filter (fun v -> v.Family = "spec-hash")

            Expect.isNonEmpty vectors "the spec-hash family must be present"

            for v in vectors do
                match (decoded v).Body with
                | BFitSubmission submission ->
                    let forge, _ = Bridge.toSubmission submission

                    Expect.equal
                        forge.SpecHash
                        submission.SpecHash
                        $"{v.Id}: the submission record must key by the value it was handed (§4.2 rule 1)"
                | other -> failtestf "%s decoded to an unexpected body: %A" v.Id other
        }

        test "a hash minted over the wrong bytes crosses the receiving path unchanged and unrefused" {
            // The reject vector of the spec-hash family: a well-formed
            // content address over the WRONG bytes. Nothing downstream is
            // permitted to notice (§4.2 rule 2), which is exactly why it
            // is a submitter's pre-emit obligation. Both halves are
            // asserted here on one document, because either alone reads
            // as the opposite posture.
            let v =
                manifest.Value.Vectors
                |> List.find (fun x -> x.Id = "spec-hash/reject-raw-bytes-minted")

            let envelope = decoded v

            match envelope.Body with
            | BFitSubmission submission ->
                let forge, _ = Bridge.toSubmission submission

                Expect.equal
                    forge.SpecHash
                    submission.SpecHash
                    "the wrong hash is stored verbatim: correcting it would re-key someone else's fit"

                Expect.notEqual
                    forge.SpecHash
                    (Minting.mint submission.SpecPayload)
                    "this vector's whole point is that the declared hash is NOT the minting of its payload"

                match SubmitterPreEmit.check submission with
                | Ok() -> failtest "the submitter's pre-emit check must catch what nothing downstream may"
                | Error(cls, _) -> Expect.equal cls "submission-spec-hash-non-canonical" "the reject class is normative"
            | other -> failtestf "unexpected body: %A" other
        }

        test "the pre-emit check accepts a correctly-minted submission and an unregistered algorithm" {
            match (decoded (manifest.Value.Vectors |> List.find (fun x -> x.Id = "spec-hash/canonical-form"))).Body with
            | BFitSubmission submission ->
                Expect.isOk (SubmitterPreEmit.check submission) "a correctly-minted submission must not be refused"

                // §7.4 — a submitter whose rendering is outside the
                // registered rule's domain names its own identifier, and
                // interop is then bounded by agreement between
                // submitters. That is a supported posture, not a defect.
                Expect.isOk
                    (SubmitterPreEmit.check {
                        submission with
                            SpecHashAlgorithm = "unregistered-submitter-rule-v0"
                            SpecHash = "sha256:0000000000000000000000000000000000000000000000000000000000000000"
                    })
                    "an unregistered minting rule carries no obligation this check can express"
            | other -> failtestf "unexpected body: %A" other
        }

        test "two renderings of one specification mint one identity" {
            // §4.2 rule 3 made observable, and the reason the spec-hash
            // family exists at all.
            let payloadOf id =
                match (decoded (manifest.Value.Vectors |> List.find (fun x -> x.Id = id))).Body with
                | BFitSubmission s -> s.SpecPayload
                | other -> failwithf "unexpected body: %A" other

            let canonical = payloadOf "spec-hash/canonical-form"
            let permuted = payloadOf "spec-hash/permuted-form"

            Expect.notEqual canonical permuted "the two renderings must differ on the wire"

            Expect.equal
                (Minting.mint permuted)
                (Minting.mint canonical)
                "a canonicalisation that depends on authoring order breaks the composite key silently"
        }
    ]

// ── The carry-gap inventory (602.B drift policy) ─────────────────────

let private carryGapTests =
    testList "carry-gap-inventory" [
        // These pins do not describe the specification; they describe
        // THIS platform's wire records. A record widened or narrowed
        // elsewhere fails here, and its author decides deliberately
        // whether the change closes a gap the residue above records.
        test "the submission record's fields are the ones the bridge maps" {
            Expect.equal
                (recordFieldNames<ModelExecutionFitSubmission> ())
                [
                    "DatasetId"
                    "DatasetVersion"
                    "SpecPayload"
                    "SpecHash"
                    "SpecHashAlgorithm"
                    "ProviderKind"
                    "Seed"
                    "Gates"
                    "SubmitterClass"
                ]
                "the submission record moved — re-derive SubmissionResidue against the specification's fitSubmission"
        }

        test "the outcome record's fields are the ones the bridge maps" {
            Expect.equal
                (recordFieldNames<ModelExecutionOutcome> ())
                [
                    "CompositeKeyHash"
                    "SpecHash"
                    "DatasetVersion"
                    "Seed"
                    "ProviderId"
                    "ProviderVersion"
                    "Artifact"
                    "Diagnostics"
                    "GateVerdicts"
                    "Status"
                    "Timing"
                    "Cost"
                    "Annotations"
                    "RegisteredAt"
                ]
                "the outcome record moved — re-derive OutcomeResidue against the specification's fitOutcome"

            // Phase 640 added three records to this face; each is pinned
            // for exactly the reason the ones above are. An optional
            // artifact whose reference silently grew a member, or a timing
            // that quietly lost one, would otherwise change what this
            // harness certifies without changing anything it checks.
            Expect.equal
                (recordFieldNames<ModelExecutionArtifactRef> ())
                [ "ArtifactId"; "ContentHash"; "Format" ]
                "the artifact-ref record moved"

            Expect.equal
                (recordFieldNames<ModelExecutionTiming> ())
                [ "SubmittedAt"; "StartedAt"; "CompletedAt"; "DurationMs" ]
                "the timing record moved"

            Expect.equal (recordFieldNames<ModelExecutionCost> ()) [ "Unit"; "Amount" ] "the cost record moved"
        }

        test "the receipt, query, page, score-request and dataset-version records are the ones the bridge maps" {
            Expect.equal
                (recordFieldNames<ModelExecutionReceipt> ())
                [ "BatchId"; "ItemCount"; "Jobs"; "EnqueueFailures" ]
                "the receipt record moved"

            Expect.equal
                (recordFieldNames<ModelExecutionOutcomeQuery> ())
                [ "SpecHashes"; "DatasetVersions"; "Statuses"; "BatchId" ]
                "the query record moved"

            Expect.equal
                (recordFieldNames<ModelExecutionOutcomePage> ())
                [ "Outcomes"; "NextCursor" ]
                "the page record moved"

            Expect.equal
                (recordFieldNames<ModelExecutionScoreRequest> ())
                [ "ArtifactKeyHash"; "InputDatasetId"; "InputVersion"; "OutputDatasetId" ]
                "the score-request record moved"

            Expect.equal
                (recordFieldNames<ModelExecutionDatasetVersion> ())
                [ "DatasetId"; "Version"; "RowCount"; "Format"; "ContentHash"; "CreatedAt" ]
                "the dataset-version record moved"
        }

        test "the refusal DU has a case for every class in the closed vocabulary" {
            let cases =
                FSharpType.GetUnionCases typeof<ModelExecutionRefusal>
                |> Array.map _.Name
                |> Set.ofArray

            Expect.equal
                cases
                (Set.ofList [
                    "SubstrateDisabled"
                    "ScopeUnavailable"
                    "Forbidden"
                    "UnknownProvider"
                    "InvalidSubmission"
                    "NotFound"
                    "InvalidQuery"
                    "ScoreRefused"
                    "BudgetDenied"
                    "EnvelopeVersionMismatch"
                    "UnknownDocumentKind"
                    "GateFailed"
                    "PolicyRefused"
                    "StorageFailure"
                    "Unexpected"
                ])
                "the refusal DU moved — re-derive Bridge.unmappedRefusalClasses against §5.7.1"

            Expect.isEmpty
                Bridge.unmappedRefusalClasses
                "Phase 640 closed the last of them; a re-appearance is a class the DU stopped covering"
        }

        test "every class the specification registers is one the platform's DU can express" {
            // The pin above is over the DU's own case names, which are the
            // platform's words. This one is over the SPECIFICATION's, via
            // the bridge — so a class registered against §5.7.1 that the
            // platform has no case for fails here by name, rather than
            // being noticed only when a corpus vector happens to carry it.
            //
            // `UnrecognisedClass` is excluded because it is not a
            // registered class at all: it is the reader's synthesis for one
            // that is not registered (§5.7.2 rule 2), and having no case
            // for it is the rule being obeyed, not a gap.
            let registered: SpecRefusal list = [
                EnvelopeVersionMismatch(1, [ 1 ])
                UnknownDocumentKind("x", [])
                InvalidSubmission "r"
                InvalidQuery "r"
                UnknownProvider("k", [])
                BudgetDenied(1.0, 0.0, "u")
                GateFailed []
                PolicyRefused "r"
                ScopeUnavailable
                Forbidden "r"
                NotFound("outcome", "id")
                SubstrateUnavailable "s"
                ScoreRefused("not-approved", "d")
                StorageFailure "r"
                Unspecified "m"
            ]

            for r in registered do
                Expect.isSome
                    (Bridge.toRefusal r)
                    $"'{Bridge.refusalClassName r}' is registered by §5.7.1 and the platform's DU cannot express it"
        }
    ]

// ── §9.3 — the two things the corpus asks of a harness ───────────────

let private nonVacuityTests =
    testList "non-vacuity" [
        test "the number of vectors executed equals the number the manifest enumerates" {
            // Not that they all passed — that the expected count ran.
            Expect.equal
                (executed.Count)
                (List.length manifest.Value.Vectors)
                "a green run that exercised nothing looks exactly like a green run that exercised everything (§9.3)"
        }

        test "a mutated document makes this harness go red" {
            // Three mutations, one per vector kind, each proving a
            // different arm can fail. Without this the suite is a
            // decoration.
            let original = Corpus.bytes "envelope/v1.json"

            // 1. Insignificant whitespace: the document decodes to the
            //    same value and re-encodes to different bytes.
            let padded =
                Encoding.UTF8.GetBytes("{ " + Encoding.UTF8.GetString(original).Substring 1)

            let reencoded =
                match Decode.envelope padded with
                | Ok e -> Encode.bytes e
                | Error(cls, _) -> failwithf "the padded document should still decode, not refuse with %s" cls

            Expect.notEqual reencoded padded "a round-trip comparison that tolerates whitespace certifies nothing"

            // 2. A mutated payload must not mint the recorded digest.
            let hashVector =
                manifest.Value.Vectors |> List.find (fun v -> v.Id = "spec-hash/canonical-form")

            match (decoded hashVector).Body with
            | BFitSubmission s ->
                let mutated = s.SpecPayload.Replace("linear", "quadratic")
                Expect.notEqual mutated s.SpecPayload "the mutation must actually change the payload"

                Expect.notEqual
                    (Minting.mint mutated)
                    (Option.get hashVector.Digest)
                    "a minting check that survives a changed payload is not a minting check"
            | other -> failtestf "unexpected body: %A" other

            // 3. A reject vector repaired must stop being refused, so
            //    "refused" is not a reader that refuses everything.
            let repaired =
                Encoding.UTF8
                    .GetString(Corpus.bytes "fit-submission/reject-gate-direction.json")
                    .Replace("greaterThan", "atLeast")
                |> Encoding.UTF8.GetBytes

            Expect.isOk (Decode.envelope repaired) "the reject arm must distinguish the defect from the document"
        }
    ]

let tests =
    match loadOutcome with
    | Error message ->
        // Deliberately one loud, unconditional failure rather than a
        // skip: a conformance run without its corpus certifies nothing.
        testList "model-execution-spec-conformance" [
            test "the conformance corpus must be resolvable" { failtest message }
        ]
    | Ok _ ->

        testList "model-execution-spec-conformance" [
            pinTests
            roundTripTests
            hashTests
            rejectTests
            acceptTests
            bridgeTests
            opaqueHashTests
            carryGapTests
            // Last: it counts what the arms above executed.
            nonVacuityTests
        ]