// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 784 — the remoting wire differential corpus: one declaration of
/// API-shaped values, consumed by the MsgPack suite, the STJ suite, the
/// pinned-fixture gate and the Fable parity leg.
///
/// ─── Why this file exists ────────────────────────────────────────────
///
/// Nothing in this repository exercised the MsgPack reader or writer
/// directly. Measured at the commit this phase branched from: `git grep -i
/// msgpack` returns fourteen files and not one of them is a test project,
/// against 3,947 lines of reader/writer/type-shape code and a 1,530-line
/// System.Text.Json converter set reached only indirectly, through
/// federation and AI-wire tests that happen to serialise. Both wires carry
/// every remoting call the platform makes, in both directions, so the
/// blast radius of a silent decode defect is the whole RPC surface.
///
/// ─── What it claims, and at what level ───────────────────────────────
///
/// This is DIFFERENTIAL AGREEMENT OVER SAMPLED INPUTS, and no more than
/// that: `write >> read = id` over a declared population, plus a pinned
/// fixture set two independent hosts decode to the same values. It is not
/// a proof of totality and must not be described as one — a corpus can
/// only ever report disagreement on the inputs it drew. Its value to the
/// later phases is that it gives them something to disagree WITH.
///
/// ─── The one design rule: compare at the STATIC type ─────────────────
///
/// Every case is constructed through `case<'T>`, which closes over the
/// declared value at its static type and compares a decoded `obj` against
/// it there — never by `obj` equality, and never after a `%A` rendering.
/// This is the whole point of the numeric-width and date classes. The
/// reader's `interpretIntegerAs` performs an UNCHECKED conversion
/// (`int32 n`) once it knows the target type, so a decode that lands on
/// the wrong width produces a plausible number rather than an error; boxed
/// equality would then compare an `Int32` with an `Int64`, get `false` for
/// the RIGHT reason by accident, and — worse, in the other direction —
/// a `%A`-string comparison would compare `"3"` with `"3"` and pass. On
/// .NET the check is exact: the decoded value's runtime type must BE the
/// declared type before equality is even considered. The falsifier for
/// this rule is committed beside the suite it protects — see
/// `MsgPackRoundTripTests.narrowingFalsifier`, which writes an `int64`
/// payload, reads it at `int32`, and asserts this comparison REPORTS the
/// narrowing. A corpus that only ever agrees with itself measures nothing.
///
/// ─── Where the corpus lives, and why not at the anchor ───────────────
///
/// The fixtures are in-repo, at `tests/remoting-corpus/`, and are resolved
/// from the RUNNING checkout (`corpusDirectory`, below). Phase 735's
/// `Support/CorpusAnchor.fs` deliberately anchors at the repository's MAIN
/// working tree, which is right for its two consumers — both resolve
/// corpora that live OUTSIDE this repository — and wrong here: a linked
/// worktree's gate run would read the main tree's fixtures and certify a
/// change against files it does not contain. What `CorpusAnchor`
/// contributes to an in-repo corpus is its other half, in the opposite
/// direction: `excluded` says whether a resolved directory sits inside
/// some OTHER working tree of this repository, and the corpus asserts it
/// does not.
///
/// ─── Fable ───────────────────────────────────────────────────────────
///
/// This file is compiled into BOTH hosts — `ToolUp.Platform.Tests` on
/// .NET and `ToolUp.AI.Client.Tests` under Fable — so the expected value
/// a fixture decodes to is ONE F# declaration rather than two
/// transcriptions of it. The encoders, the generator and the corpus's
/// filesystem half are server-tier (`makeSerializer` wants a `Stream`,
/// the STJ converter set lives in `Platform.Server`, `CorpusAnchor` in
/// this pack), so they sit behind a conditional; the declarations, the
/// classes and the comparison do not.
///
/// **That conditional is `TOOLUP_WIRE_CORPUS_DOTNET`, declared by
/// `ToolUp.Platform.Tests.fsproj` alone — NOT `#if !FABLE_COMPILER`,
/// which is the obvious choice and is wrong.** `ToolUp.AI.Client.Tests`
/// is a Fable project that is ALSO an ordinary `net10.0` project in
/// `ToolUp.Forge.sln`, so `dotnet build` compiles this file there with
/// `FABLE_COMPILER` undefined and with neither `Platform.Server` nor
/// this pack's own `Support/` in scope. That is three `FS0039`s that a
/// `dotnet fable` run cannot see and a Platform-pack build cannot see —
/// only the full-solution gate does, which is where it was caught. A
/// define the CONSUMING PROJECT declares says what is actually meant —
/// "this project has the server tier" — rather than "this is not Fable",
/// which is a different claim that happens to coincide in one project.
module ToolUp.Platform.Tests.Remoting.WireCorpus

open System

#if TOOLUP_WIRE_CORPUS_DOTNET
open System.IO
open System.Text
open System.Text.Json
// Phase 783's named refusal — the right answer the refuse-path arm holds
// both decoders to (784.D).
open ToolUp.Remoting
open ToolUp.Remoting.MsgPack
#endif

// ─── The closed shape vocabulary (784.A) ─────────────────────────────

/// The type classes the corpus claims to cover. CLOSED on purpose: the
/// adequacy guard (784.F) enumerates this union by reflection and fails
/// when a class drew zero cases, so adding a class without adding a case
/// is a red run rather than a silent hole. Nothing outside this file
/// should pattern-match on it exhaustively.
[<RequireQualifiedAccess>]
type WireClass =
    /// bool / string / char / int — the shapes every API uses.
    | Primitive
    /// The integer widths the reader narrows between: int16, int64,
    /// uint16, uint32, uint64, byte, sbyte. The class silent narrowing
    /// hides in.
    | NumericWidth
    /// float and float32, including the exactly-representable boundary
    /// values a double-to-single round trip would lose.
    | Floating
    /// decimal — four 32-bit words on the wire, not an IEEE double.
    | Decimal
    /// Strings that exercise an escape or a length-header boundary:
    /// quote, backslash, control, unicode, astral (surrogate pair),
    /// empty, and past the fixstr and str8 headers.
    | StringEscape
    /// DateTime, DateTimeOffset, DateOnly, TimeOnly, TimeSpan.
    | DateFamily
    /// Guid — sixteen bytes through the binary path.
    | Guid
    /// byte[] — the binary path itself, at and past its header boundary.
    | Binary
    /// option — Some and None, the second of which is `null` at runtime
    /// and therefore the case a naive comparison gets wrong.
    | OptionFamily
    /// list and array, empty, small, and past the fixarr header boundary.
    | Collection
    /// Map (string-keyed and non-string-keyed) and Set.
    | MapSet
    /// Tuples, which the wire writes as arrays.
    | Tuple
    /// Unions with no fields, one field, and several.
    | Union
    /// A flat record.
    | Record
    /// A record of records, holding a collection, a union, an option and
    /// a date — the shape an actual API method returns.
    | NestedRecord

#if TOOLUP_WIRE_CORPUS_DOTNET
/// Every class, in declaration order. Derived by reflection so it cannot
/// fall behind the union — adding a case without adding a case to the
/// corpus is then a red adequacy run rather than a silent hole.
///
/// .NET-only, and deliberately: its only consumers are the adequacy
/// guard and the generator, both .NET-side, and running reflection over a
/// union at module-init time is a needless thing to ask of the Fable
/// host, which compiles this file only for the declarations.
let allClasses: WireClass list =
    FSharp.Reflection.FSharpType.GetUnionCases typeof<WireClass>
    |> Array.map (fun c -> FSharp.Reflection.FSharpValue.MakeUnion(c, [||]) :?> WireClass)
    |> Array.toList
#endif

// ─── API-shaped types ────────────────────────────────────────────────
//
// Deliberately shaped like a remoting API's own types rather than like a
// serialisation test's: a union that carries a correlation id and a
// timestamp, a customer record with an address inside it, an envelope
// that holds all of them at once. A corpus of bare primitives would miss
// every interaction between them, and the interactions are where the
// reader's caches live.

/// A no-field union — the enum-shaped case, written as a tag alone.
type Priority =
    | Low
    | Normal
    | High

/// A union carrying zero, one and several fields across its cases. The
/// several-field case is the one whose wire shape differs (an inner
/// array header the single-field case does not write).
type Outcome =
    | Accepted of id: System.Guid * at: DateTimeOffset
    | Rejected of reason: string
    | Pending

type Address = {
    Line1: string
    Postcode: string
    Country: string
}

type Customer = {
    Id: System.Guid
    Name: string
    Address: Address
    Since: DateOnly
    Balance: decimal
    Tags: string list
}

/// A second nested record, deliberately free of `DateOnly` / `TimeOnly`.
///
/// It exists because the two records above are not cross-host: the Fable
/// MsgPack reader cannot decode either of those types at all (see the
/// recorded divergences), and `Customer.Since` is a `DateOnly`, so
/// without this the Fable parity leg would have no nested-record coverage
/// whatsoever — the exact "the class is empty so the green means nothing"
/// hole the adequacy guard exists to refuse on the .NET side.
type Consignment = {
    Reference: string
    Origin: Address
    Destination: Address
    Priority: Priority
    Outcome: Outcome
    Weights: float list
    Labels: Set<string>
}

type ApiEnvelope = {
    Customer: Customer
    Priority: Priority
    Outcome: Outcome
    Attempts: int
    Window: TimeSpan
    Notes: string option
    Scores: Map<string, float>
    Flags: Set<string>
    Payload: byte[]
}

// ─── A case ──────────────────────────────────────────────────────────

/// Whether a case is in the CROSS-HOST set — the fixtures the Fable leg
/// decodes and compares. A case outside it names the measured reason, and
/// that reason is the finding: an exclusion is a recorded divergence
/// between the two hosts' readers, never a convenience.
type HostCoverage =
    /// Both hosts decode this fixture to the declared value.
    | CrossHost
    /// .NET only. The string is the measured reason, quoted in the
    /// corpus's own report so it is read rather than buried here.
    | DotNetOnly of reason: string

type WireCase = {
    /// Stable and filesystem-safe: it is the fixture file stem, the
    /// Expecto case label and the failure line. Never derived from a
    /// value, so re-pinning never renames a file.
    Name: string
    Class: WireClass
    Coverage: HostCoverage
    /// The type the value was DECLARED at. Both readers take the target
    /// type as an argument, so this is what they are handed — and on
    /// .NET it is what the decoded value's runtime type must equal.
    ClrType: Type
    /// The declared value, boxed. Read only through `Compare`.
    Value: obj
    /// Compare a decoded `obj` against the declared value at the static
    /// type. `Ok ()` or a sentence naming what differed.
    Compare: obj -> Result<unit, string>
#if TOOLUP_WIRE_CORPUS_DOTNET
    /// The MsgPack bytes the SHIPPED writer emits for this value.
    WriteMsgPack: unit -> byte[]
    /// The JSON text the SHIPPED converter set emits for this value.
    WriteJson: unit -> string
#endif
}

#if TOOLUP_WIRE_CORPUS_DOTNET
/// The remoting STJ converter set, exactly as the server composes it. A
/// fresh instance rather than `FableConverters.shared`: the corpus must
/// not be able to perturb the process-wide options, and STJ freezes an
/// options object on first use anyway.
let jsonOptions: JsonSerializerOptions =
    ToolUp.Remoting.Json.SystemTextJson.FableConverters.create ()
#endif

/// Did the decoded value land on a type other than the declared one?
/// `None` when it did not, or when the host cannot tell.
///
/// On .NET this is an EXACT runtime-type comparison and it runs before
/// equality, because the failure this corpus exists to catch — a width
/// narrowed on the way back — produces a value that is equal-looking at
/// every representation except its type. Under Fable there are no CLR
/// runtime types to compare (`int64` is a JS object, `int32` a JS
/// number), so the check is not made rather than faked: the Fable leg's
/// claim is that the fixture decodes to the declared VALUE, which is
/// exactly what it asserts.
/// Public rather than private only because `case` below is `inline`
/// (Fable erases generics unless the constructor inlines, so `typeof<'T>`
/// has to resolve at the call site) and an inline function may not reach a
/// private one. Nothing outside this module should call it.
let typeMismatch (declared: Type) (decoded: obj) : string option =
#if !TOOLUP_WIRE_CORPUS_DOTNET
    ignore declared
    ignore decoded
    None
#else
    let actual = decoded.GetType()

    // `IsInstanceOfType` rather than type EQUALITY, and the difference is
    // measured rather than defensive: a multi-case F# union compiles each
    // case to a nested SUBCLASS, so a `Rejected` value's runtime type is
    // `Outcome+Rejected` and never `Outcome`. (A union whose cases all
    // carry no fields — `Priority` here — compiles to the union type
    // itself with a tag, which is why that one would have passed an
    // equality check and the three `Outcome` cases would not.) Nothing is
    // given up: the narrowing this check exists to catch is a decode onto
    // a different type, not onto a derived one — `Int32` is not an
    // instance of `Int64` — and the falsifier beside the suite pins that.
    if declared.IsInstanceOfType decoded then
        None
    else
        Some(
            sprintf
                "decoded a `%s`, but the case is declared at `%s`. A decode that lands on a different CLR type is a narrowing or a widening, never an equal value — this is the check the boxed comparison cannot make."
                actual.FullName
                declared.FullName
        )
#endif

/// Declare a case at its static type.
///
/// The `null` arm is load-bearing rather than defensive: `None` and
/// `unit` box to `null` in F#, so a comparison that reached `unbox` first
/// would throw on the ONE case class whose whole purpose is the absent
/// value. It is admitted only when the DECLARED value is itself null, so
/// a genuine `nil` decoded where a value was expected is still a failure.
let inline case<'T when 'T: equality> (cls: WireClass) (coverage: HostCoverage) (name: string) (value: 'T) : WireCase =
    let compare (decoded: obj) : Result<unit, string> =
        if isNull decoded then
            if isNull (box value) then
                Ok()
            else
                Error(sprintf "decoded `nil`, but the case declares %A" value)
        else
            match typeMismatch typeof<'T> decoded with
            | Some problem -> Error problem
            | None ->
                try
                    let typed = unbox<'T> decoded

                    if typed = value then
                        Ok()
                    else
                        Error(sprintf "decoded %A, but the case declares %A" typed value)
                with ex ->
                    Error(sprintf "decoded value could not be read at the declared type: %s" ex.Message)

    {
        Name = name
        Class = cls
        Coverage = coverage
        ClrType = typeof<'T>
        Value = box value
        Compare = compare
#if TOOLUP_WIRE_CORPUS_DOTNET
        WriteMsgPack =
            fun () ->
                let serializer = Write.makeSerializer<'T> ()
                use buffer = new MemoryStream()
                serializer.Invoke(value, buffer)
                buffer.ToArray()
        WriteJson = fun () -> JsonSerializer.Serialize<'T>(value, jsonOptions)
#endif
    }

/// A cross-host case — the common form.
let inline both cls name value = case cls CrossHost name value

// ─── The pinned cases (784.C) ────────────────────────────────────────
//
// These are the cases whose encodings are COMMITTED under
// `tests/remoting-corpus/`. They are hand-declared rather than generated
// precisely so their bytes are stable: a generated value re-drawn on a
// different machine would re-pin every fixture on every run, which is a
// snapshot gate that agrees with whatever it is shown.

let private sampleGuid = System.Guid.Parse "3f2504e0-4f89-11d3-9a0c-0305e82c3301"

let private sampleAddress = {
    Line1 = "17 Lower Marsh"
    Postcode = "SE1 7RJ"
    Country = "GB"
}

let private sampleCustomer = {
    Id = sampleGuid
    Name = "Ada Lovelace"
    Address = sampleAddress
    Since = DateOnly(1843, 10, 1)
    Balance = 1234.56m
    Tags = [ "priority"; "legacy"; "eu" ]
}

let private sampleConsignment = {
    Reference = "CN-4417"
    Origin = sampleAddress
    Destination = {
        sampleAddress with
            Country = "IE"
            Postcode = "D02 XY45"
    }
    Priority = Normal
    Outcome = Rejected "over weight"
    Weights = [ 12.5; 0.25; 400.0 ]
    Labels = set [ "fragile"; "priority" ]
}

let private sampleEnvelope = {
    Customer = sampleCustomer
    Priority = High
    Outcome = Accepted(sampleGuid, DateTimeOffset(2026, 9, 13, 8, 30, 0, TimeSpan.Zero))
    Attempts = 3
    Window = TimeSpan.FromMinutes 90.0
    Notes = Some "re-queued after a transient 503"
    Scores = Map [ "latency", 0.25; "accuracy", 0.875 ]
    Flags = set [ "audited"; "billable" ]
    Payload = [| 0uy; 1uy; 127uy; 128uy; 255uy |]
}

/// A string that crosses the `str8` length header (> 31 characters).
let private mediumString = String.replicate 8 "0123456789"

/// A string that crosses the `str16` length header (> 255 characters).
let private longString = String.replicate 30 "0123456789"

let pinnedCases: WireCase list = [
    // ── Primitive ──
    both WireClass.Primitive "primitive-bool-true" true
    both WireClass.Primitive "primitive-bool-false" false
    both WireClass.Primitive "primitive-int" 42
    both WireClass.Primitive "primitive-int-negative" -1
    both WireClass.Primitive "primitive-string" "hello"
    case
        WireClass.Primitive
        (DotNetOnly
            "the wire carries a char as a one-character string and JavaScript has no char type, so the Fable reader returns that string — a genuine host divergence, pinned here rather than hidden")
        "primitive-char"
        'q'

    // ── NumericWidth ──
    // Every one of these is written by a width-choosing encoder and read
    // back by an UNCHECKED conversion at the target type. The boundary
    // values are the point: each sits where the next-narrower width
    // would wrap rather than error.
    both WireClass.NumericWidth "width-byte-max" Byte.MaxValue
    both WireClass.NumericWidth "width-sbyte-min" SByte.MinValue
    both WireClass.NumericWidth "width-int16-min" Int16.MinValue
    both WireClass.NumericWidth "width-uint16-max" UInt16.MaxValue
    both WireClass.NumericWidth "width-int32-min" Int32.MinValue
    both WireClass.NumericWidth "width-uint32-max" UInt32.MaxValue
    both WireClass.NumericWidth "width-int64-max" Int64.MaxValue
    both WireClass.NumericWidth "width-int64-beyond-int32" 2147483648L
    both WireClass.NumericWidth "width-uint64-max" UInt64.MaxValue

    // ── Floating ──
    // Exactly representable in both widths, so a float32 that came back
    // as a double still compares equal and the case is about the TYPE,
    // which the .NET arm checks and the Fable arm does not claim.
    both WireClass.Floating "float-double" 1.5
    both WireClass.Floating "float-double-negative" -0.125
    both WireClass.Floating "float-single" 1.5f

    // ── Decimal ──
    both WireClass.Decimal "decimal-simple" 1234.56m
    both WireClass.Decimal "decimal-negative-scale" -0.0001m
    both WireClass.Decimal "decimal-max" Decimal.MaxValue

    // ── StringEscape ──
    both WireClass.StringEscape "string-empty" ""
    both WireClass.StringEscape "string-quote" "she said \"no\""
    both WireClass.StringEscape "string-backslash" "C:\\temp\\x"
    both WireClass.StringEscape "string-control" "line\nbreak\ttab\r\n"
    both WireClass.StringEscape "string-unicode" "naïve Ωμέγα «quoted»"
    both WireClass.StringEscape "string-astral" "emoji: \U0001F600\U0001F1EC\U0001F1E7"
    both WireClass.StringEscape "string-past-fixstr" mediumString
    both WireClass.StringEscape "string-past-str8" longString

    // ── DateFamily ──
    both WireClass.DateFamily "date-datetime-utc" (DateTime(2026, 9, 13, 8, 30, 0, DateTimeKind.Utc))
    both WireClass.DateFamily "date-datetime-unspecified" (DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Unspecified))
    both WireClass.DateFamily "date-datetimeoffset" (DateTimeOffset(2026, 9, 13, 8, 30, 0, TimeSpan.FromHours 5.5))
    both WireClass.DateFamily "date-timespan" (TimeSpan.FromMinutes 90.0)
    both WireClass.DateFamily "date-timespan-negative" (TimeSpan.FromTicks -1L)
    case
        WireClass.DateFamily
        (DotNetOnly
            "the Fable MsgPack reader THROWS `Cannot interpret integer 673049 as DateOnly.` — `Read.interpretIntegerAs`'s Fable arm carries an EMPTY `#if NET6_0_OR_GREATER` block where its .NET arm handles DateOnly and TimeOnly, so neither type survives to a browser client at all. Measured 2026-09-13 through the Fable parity leg")
        "date-dateonly"
        (DateOnly(1843, 10, 1))
    case
        WireClass.DateFamily
        (DotNetOnly
            "the Fable MsgPack reader THROWS `Cannot interpret integer 673049 as DateOnly.` — `Read.interpretIntegerAs`'s Fable arm carries an EMPTY `#if NET6_0_OR_GREATER` block where its .NET arm handles DateOnly and TimeOnly, so neither type survives to a browser client at all. Measured 2026-09-13 through the Fable parity leg")
        "date-timeonly"
        (TimeOnly(23, 59, 58))

    // ── Guid ──
    both WireClass.Guid "guid" sampleGuid
    both WireClass.Guid "guid-empty" System.Guid.Empty

    // ── Binary ──
    both WireClass.Binary "binary-empty" (Array.empty<byte>)
    both WireClass.Binary "binary-small" [| 0uy; 1uy; 127uy; 128uy; 255uy |]
    case
        WireClass.Binary
        (DotNetOnly
            "the Fable MsgPack reader fails on a `bin16`-framed byte array with `Cannot interpret integer 47 as Byte[]`. The committed fixture is a correct `c5 01 2c` header over 300 bytes and the `bin8`-framed `binary-small` case decodes fine on the same host, so the fault is in the Fable arm's bin16 length read or its ReadBin type dispatch rather than in the fixture. Measured 2026-09-13; the diagnosis belongs to the decoder phases this corpus exists to feed")
        "binary-past-bin8"
        (Array.init 300 (fun i -> byte (i % 251)))

    // ── OptionFamily ──
    both WireClass.OptionFamily "option-some-int" (Some 7)
    both WireClass.OptionFamily "option-none-int" (None: int option)
    both WireClass.OptionFamily "option-some-string" (Some "value")
    both WireClass.OptionFamily "option-none-string" (None: string option)
    both WireClass.OptionFamily "option-some-record" (Some sampleAddress)

    // ── Collection ──
    both WireClass.Collection "list-empty" (List.empty<int>)
    both WireClass.Collection "list-small" [ 1; 2; 3 ]
    both WireClass.Collection "list-past-fixarr" [ 1..40 ]
    both WireClass.Collection "list-of-records" [ sampleAddress; { sampleAddress with Country = "IE" } ]
    both WireClass.Collection "array-string" [| "a"; "b"; "c" |]
    both WireClass.Collection "array-empty" (Array.empty<string>)

    // ── MapSet ──
    both WireClass.MapSet "map-string-key" (Map [ "a", 1; "b", 2 ])
    both WireClass.MapSet "map-empty" (Map.empty<string, int>)
    both WireClass.MapSet "map-int-key" (Map [ 1, "one"; 2, "two" ])
    both WireClass.MapSet "set-string" (set [ "alpha"; "beta" ])
    both WireClass.MapSet "set-int" (set [ 3; 1; 2 ])

    // ── Tuple ──
    both WireClass.Tuple "tuple-pair" (1, "one")
    both WireClass.Tuple "tuple-triple" (1, "one", true)

    // ── Union ──
    both WireClass.Union "union-nofield-low" Low
    both WireClass.Union "union-nofield-high" High
    both WireClass.Union "union-onefield" (Rejected "quota exceeded")
    both WireClass.Union "union-multifield" (Accepted(sampleGuid, DateTimeOffset(2026, 9, 13, 8, 30, 0, TimeSpan.Zero)))
    both WireClass.Union "union-emptycase" Pending

    // ── Record / NestedRecord ──
    both WireClass.Record "record-flat" sampleAddress
    both WireClass.NestedRecord "record-consignment" sampleConsignment
    case
        WireClass.NestedRecord
        (DotNetOnly
            "holds a `DateOnly` (`Customer.Since`), which the Fable reader refuses outright — see the `date-dateonly` divergence. `record-consignment` carries the cross-host nested-record coverage instead")
        "record-nested"
        sampleCustomer
    case
        WireClass.NestedRecord
        (DotNetOnly
            "holds a `Customer`, and so a `DateOnly` the Fable reader refuses — see the `date-dateonly` divergence. `record-consignment` carries the cross-host nested-record coverage instead")
        "record-envelope"
        sampleEnvelope
]

/// The cross-host subset — what the Fable leg reads.
let crossHostCases = pinnedCases |> List.filter (fun c -> c.Coverage = CrossHost)

/// The recorded divergences: cases the Fable reader does not decode to
/// the declared value, each with the measured reason. Enumerated so the
/// list is greppable and a suite can assert it is the list it expects.
let recordedDivergences =
    pinnedCases
    |> List.choose (fun c ->
        match c.Coverage with
        | DotNetOnly reason -> Some(c.Name, reason)
        | CrossHost -> None)

// ─── The fixture directory ───────────────────────────────────────────

/// The corpus directory's name, stated once. The DIRECTORY is the
/// interface both hosts resolve, so neither host's path arithmetic is
/// allowed to spell it independently.
[<Literal>]
let CorpusDirName = "remoting-corpus"

/// The corpus's path from a repository root.
[<Literal>]
let CorpusRelativePath = "tests/remoting-corpus"

/// The environment variable that re-pins the fixtures. Named here so the
/// suites that quote it in a failure message cannot misspell it.
[<Literal>]
let RefreshVariable = "TOOLUP_REMOTING_CORPUS_REFRESH"

#if TOOLUP_WIRE_CORPUS_DOTNET

/// The repository root of the RUNNING checkout, derived from the test
/// assembly's own location: `bin/<Config>/net10.0/…dll` → up five.
///
/// The running checkout, NOT Phase 735's anchor, and that is the whole
/// difference between this corpus and the two that use the anchor: they
/// resolve corpora that live outside the repository, where the main
/// working tree is the only sane root; this one is tracked IN the
/// repository, so a linked worktree resolving to the main tree would
/// gate a change against fixtures the change does not contain.
let repoRoot () =
    let assemblyDir =
        Path.GetDirectoryName(Reflection.Assembly.GetExecutingAssembly().Location)

    Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

/// Where the fixtures are, on this machine, for this run.
let corpusDirectory () =
    Path.Combine(repoRoot (), "tests", CorpusDirName)

/// Is the resolved corpus inside some OTHER working tree of this
/// repository? Phase 735's guard, applied in the direction an in-repo
/// corpus needs it: the answer must be `false`, and a suite asserts so.
/// A `true` here would mean the path arithmetic above had walked out of
/// the running checkout, which is the failure mode that made a sibling
/// worktree's transient contents resolve as a corpus.
let resolvesInsideAForeignWorktree () =
    let checkout = repoRoot ()
    let anchoring = ToolUp.Platform.Tests.Support.CorpusAnchor.resolve checkout

    ToolUp.Platform.Tests.Support.CorpusAnchor.excluded anchoring (corpusDirectory ())

let msgPackFixturePath (c: WireCase) =
    Path.Combine(corpusDirectory (), c.Name + ".msgpack")

let jsonFixturePath (c: WireCase) =
    Path.Combine(corpusDirectory (), c.Name + ".json")

/// Is a re-pin requested? Absent and empty are the same answer, and `0`
/// is an explicit no — the shape Phase 613 had to learn the hard way
/// when an unset variable read as "yes" and rewrote every baseline.
let refreshRequested () =
    match Environment.GetEnvironmentVariable RefreshVariable with
    | null
    | ""
    | "0" -> false
    | _ -> true

/// Decode MsgPack bytes at a case's declared type.
let readMsgPack (c: WireCase) (bytes: byte[]) : obj = Read.Reader(bytes).Read c.ClrType

/// Decode JSON text at a case's declared type.
let readJson (c: WireCase) (text: string) : obj =
    JsonSerializer.Deserialize(text, c.ClrType, jsonOptions)

// ─── The writer's regime, measured before anything is asserted ───────
//
// Found by this corpus on its first run, 2026-09-13, and it is the
// reason the MsgPack suite measures before it asserts.
//
// `Write.fs` builds its scratch buffers through
//
//     let inline stackalloc<'a when 'a: unmanaged> length =
//         Span<'a>(NativePtr.stackalloc<'a> length |> NativePtr.toVoidPtr, length)
//
// — a `Span` over memory `NativePtr.stackalloc` obtained. **With the F#
// optimiser OFF the `inline` is not honoured**, so the allocation happens
// in the HELPER's frame, which is popped before the caller reads it back.
// `writeString` writes its length header (a call) between filling the
// buffer and reading it, and `writeDecimal` calls `write32bitNumber` once
// per word; those intervening frames land on the dead region, and the
// writer stops being a function of its input — two runs of
// `writeString "hello"` produced `d0 a4 9b e4 b4` and `f0 d6 ae 03 00`,
// both under a correct `a5` length header. `writeGuid` uses the same
// helper and is CORRECT, because it reads the buffer with no intervening
// call; a 600-character string is correct too, because at that length
// `writeString` takes the `ArrayPool` branch instead of the stack one.
// Those two are the controls that locate the fault at the stack buffer
// rather than at strings or at the helper as such.
//
// Measured, in all three directions: `-c Debug` corrupt; `-c Release`
// correct; `-c Debug -p:Optimize=true` CORRECT. So the discriminator is
// the optimiser, not the configuration and not `SkipLocalsInit`.
//
// It has been invisible because production ships Release while
// `verify.ps1` builds the solution with no configuration flag — Debug.
// Nothing in this repository exercised the writer, so nothing looked.
//
// This corpus does not fix it: `Write.fs` is remoting source, outside
// the cross-section this phase declared. What it does is refuse to
// pretend. The suite asks which regime it is in and asserts accordingly,
// so a fix turns the full arms back on with no edit here, and a change
// in the defect's SHAPE is a red run rather than a quiet one.

/// What the five probes below found. Each field is "the bytes the writer
/// emitted are the bytes the format says", never a round trip — a round
/// trip through a reader that made the same mistake would agree.
type WriterProbe = {
    ShortStringExact: bool
    LongStringExact: bool
    DecimalExact: bool
    GuidExact: bool
    IntegerExact: bool
}

/// The regimes this corpus knows how to be in.
type WriterRegime =
    /// The writer emits what the format says for every probe.
    | Sound
    /// The measured signature of the unoptimised-`stackalloc` defect:
    /// short strings and decimals corrupt, everything else correct.
    | UnoptimisedStackalloc
    /// Neither — the defect has changed shape, or a new one has arrived.
    /// Carries the probe so the failure names what it saw.
    | UnknownRegime of WriterProbe

let private writeBytes<'T> (value: 'T) =
    let serializer = Write.makeSerializer<'T> ()
    use buffer = new MemoryStream()
    serializer.Invoke(value, buffer)
    buffer.ToArray()

/// A string that takes `writeString`'s STACK branch (max UTF-8 byte
/// count under 1500), and one that takes its `ArrayPool` branch.
let private probeShortString = "hello"

let private probeLongString = String.replicate 60 "0123456789"

/// Probe the writer. Cheap, and deliberately re-run rather than cached
/// across processes: the regime is a property of THIS build.
let probeWriter () : WriterProbe =
    let expectedShort =
        Array.append [| 0xA5uy |] (Encoding.UTF8.GetBytes probeShortString)

    let expectedLongBody = Encoding.UTF8.GetBytes probeLongString

    {
        ShortStringExact = writeBytes probeShortString = expectedShort
        LongStringExact =
            let emitted = writeBytes probeLongString
            // str16 header (0xDA) + two length bytes, then the body.
            emitted.Length = expectedLongBody.Length + 3
            && emitted[0] = 0xDAuy
            && emitted[3..] = expectedLongBody
        // 1234.56m is `GetBits` [123456; 0; 0; 131072] — fixarr 4, then
        // uint32 123456, nil-ish zeros, then the scale word. Compared by
        // DECODING with the reader, which is sound in both regimes, so
        // this probe does not have to re-derive the decimal encoding.
        DecimalExact = (Read.Reader(writeBytes 1234.56m).Read typeof<decimal> :?> decimal) = 1234.56m
        GuidExact =
            let g = System.Guid.Parse "3f2504e0-4f89-11d3-9a0c-0305e82c3301"
            (Read.Reader(writeBytes g).Read typeof<System.Guid> :?> System.Guid) = g
        IntegerExact = writeBytes 42 = [| 42uy |]
    }

let regimeOf (probe: WriterProbe) =
    if
        probe.ShortStringExact
        && probe.LongStringExact
        && probe.DecimalExact
        && probe.GuidExact
        && probe.IntegerExact
    then
        Sound
    elif
        not probe.ShortStringExact
        && not probe.DecimalExact
        && probe.LongStringExact
        && probe.GuidExact
        && probe.IntegerExact
    then
        UnoptimisedStackalloc
    else
        UnknownRegime probe

/// Measured once per process. The probe shells nothing and allocates
/// nothing notable, but every arm of the suite consults it.
let writerRegime = lazy (regimeOf (probeWriter ()))

/// The sentence the suite prints, and the one an operator meets first.
let regimeReport (regime: WriterRegime) =
    match regime with
    | Sound -> "MsgPack writer: SOUND — the byte-pin and round-trip arms are live."
    | UnoptimisedStackalloc ->
        "MsgPack writer: CORRUPT under this build — short strings and decimals are written from a popped stack frame (Write.fs's `inline stackalloc` helper, optimiser OFF). Measured 2026-09-13: `-c Debug` corrupt, `-c Release` correct, `-c Debug -p:Optimize=true` CORRECT, so the discriminator is the optimiser. The writer is not a function of its input here, so the byte-pin and round-trip arms cannot run and say anything true; the committed fixtures are still DECODED and compared, because the reader is unaffected. Fixing Write.fs turns the other arms back on with no edit to the corpus."
    | UnknownRegime probe ->
        sprintf
            "MsgPack writer: an UNKNOWN regime — %A. The corpus knows two: sound, and the unoptimised-stackalloc corruption whose signature is short-string and decimal corrupt with long-string, Guid and integer correct. This is neither, so either the defect has changed shape or a new one has arrived; do not touch the quarantine until this is understood."
            probe

/// Bytes rendered for a failure message: hex, space-separated, truncated
/// with an honest count rather than an ellipsis that hides the length.
let describeBytes (bytes: byte[]) =
    let shown = min bytes.Length 48

    let hex =
        bytes |> Array.take shown |> Array.map (sprintf "%02x") |> String.concat " "

    if bytes.Length > shown then
        sprintf "%s … (%d bytes total)" hex bytes.Length
    else
        sprintf "%s (%d bytes)" hex bytes.Length

// ─── The seeded generator (784.A) ────────────────────────────────────
//
// FsCheck is not a dependency of this repository and adding one to the
// test tier to draw a few hundred values would be the larger change, so
// the generator is here: deterministic, seeded, and size-graded.
//
// The PRNG is written out rather than taken from `System.Random`. A
// seeded `Random` is documented as NOT guaranteed to produce the same
// sequence across .NET versions, and "reproduce the failure with this
// seed" has to survive a runtime upgrade to be worth printing. This is
// SplitMix64 — eight lines, and its sequence is a property of this file.

/// SplitMix64. One `uint64` of state; `next` returns the drawn value and
/// the advanced state, so a generator is a pure function of its seed.
let private splitMix (state: uint64) : uint64 * uint64 =
    let state = state + 0x9E3779B97F4A7C15UL
    let z = state
    let z = (z ^^^ (z >>> 30)) * 0xBF58476D1CE4E5B9UL
    let z = (z ^^^ (z >>> 27)) * 0x94D049BB133111EBUL
    (z ^^^ (z >>> 31)), state

/// A draw sequence. Mutable inside one generator invocation only — two
/// invocations with the same seed produce the same sequence, which is
/// the property the whole design rests on.
type private Rng(seed: uint64) =
    let mutable state = seed

    member _.NextUInt64() =
        let value, advanced = splitMix state
        state <- advanced
        value

    /// A non-negative int below `bound`.
    member this.Next(bound: int) =
        if bound <= 0 then
            0
        else
            int (this.NextUInt64() % uint64 bound)

    member this.NextInt() =
        int (this.NextUInt64() &&& 0x7FFFFFFFUL)

    member this.NextInt64() = int64 (this.NextUInt64())
    member this.NextBool() = this.Next 2 = 1

    /// A double in [0, 1) with 53 bits of mantissa — reproducible
    /// because it is derived from the integer stream, not from a
    /// floating-point RNG.
    member this.NextDouble() =
        float (this.NextUInt64() >>> 11) * (1.0 / 9007199254740992.0)

    member this.Pick(items: 'a[]) = items[this.Next items.Length]

let private alphabet = [| "a"; "Z"; "0"; " "; "\""; "\\"; "\n"; "é"; "Ω"; "\U0001F600" |]

let private randomString (rng: Rng) (size: int) =
    let builder = StringBuilder()

    for _ in 1..size do
        builder.Append(rng.Pick alphabet) |> ignore

    builder.ToString()

let private randomAddress (rng: Rng) = {
    Line1 = randomString rng 6
    Postcode = randomString rng 4
    Country = rng.Pick [| "GB"; "IE"; "FR" |]
}

let private randomPriority (rng: Rng) = rng.Pick [| Low; Normal; High |]

let private randomOutcome (rng: Rng) =
    match rng.Next 3 with
    | 0 ->
        Accepted(
            System.Guid(Array.init 16 (fun _ -> byte (rng.Next 256))),
            DateTimeOffset(DateTime(2000, 1, 1).AddMinutes(float (rng.Next 1_000_000)), TimeSpan.Zero)
        )
    | 1 -> Rejected(randomString rng 5)
    | _ -> Pending

let private randomCustomer (rng: Rng) (size: int) = {
    Id = System.Guid(Array.init 16 (fun _ -> byte (rng.Next 256)))
    Name = randomString rng (1 + size)
    Address = randomAddress rng
    Since = DateOnly.FromDayNumber(rng.Next 700_000)
    Balance = decimal (rng.Next 1_000_000) / 100m
    Tags = List.init size (fun _ -> randomString rng 3)
}

let private randomEnvelope (rng: Rng) (size: int) = {
    Customer = randomCustomer rng size
    Priority = randomPriority rng
    Outcome = randomOutcome rng
    Attempts = rng.Next 1000
    // Whole MILLISECONDS, not arbitrary ticks — and that is a recorded
    // finding rather than a convenience. The STJ remoting converter
    // writes a TimeSpan as a JSON number of milliseconds, so a tick count
    // whose millisecond value is not exactly representable as a double
    // comes back up to one tick short; the MsgPack wire writes ticks as
    // an int64 and is exact. The divergence is pinned, with its own
    // search and its own bound, in `StjRoundTripTests.timeSpanTickLoss` —
    // widen this draw the day that test goes red.
    Window = TimeSpan.FromTicks((rng.NextInt64() % 864_000_000_000L) / 10_000L * 10_000L)
    Notes = if rng.NextBool() then Some(randomString rng size) else None
    Scores = List.init size (fun i -> sprintf "k%d" i, rng.NextDouble()) |> Map.ofList
    Flags = List.init size (fun i -> sprintf "f%d" i) |> Set.ofList
    Payload = Array.init (size * 3) (fun _ -> byte (rng.Next 256))
}

/// A shape the generator can draw at any size. `Draw` MUST be a pure
/// function of `(seed, size)` — the whole reproduction story is that
/// re-running with the printed seed re-draws the same value.
type ShapeGenerator = {
    ShapeName: string
    Class: WireClass
    Draw: uint64 -> int -> WireCase
}

/// Mix the shape's name into the seed so two shapes at one seed do not
/// draw correlated values (they would otherwise share a prefix of the
/// same stream, which makes a whole generation look independent when it
/// is not).
let private shapeSeed (name: string) (seed: uint64) =
    let mutable h = seed ^^^ 0xCBF29CE484222325UL

    for ch in name do
        h <- (h ^^^ uint64 (uint16 ch)) * 0x100000001B3UL

    h

let private generator name cls (draw: Rng -> int -> WireCase) = {
    ShapeName = name
    Class = cls
    Draw = fun seed size -> draw (Rng(shapeSeed name seed)) size
}

/// The generated shapes. Every class in `allClasses` is represented, so
/// the adequacy guard (784.F) passes on the generated population alone
/// as well as on the pinned one.
let shapeGenerators: ShapeGenerator list = [
    generator "gen-int" WireClass.Primitive (fun rng size ->
        both WireClass.Primitive (sprintf "gen-int-%d" size) (rng.NextInt() - rng.NextInt()))
    generator "gen-bool" WireClass.Primitive (fun rng size ->
        both WireClass.Primitive (sprintf "gen-bool-%d" size) (rng.NextBool()))
    generator "gen-int64" WireClass.NumericWidth (fun rng size ->
        both WireClass.NumericWidth (sprintf "gen-int64-%d" size) (rng.NextInt64()))
    generator "gen-uint64" WireClass.NumericWidth (fun rng size ->
        both WireClass.NumericWidth (sprintf "gen-uint64-%d" size) (rng.NextUInt64()))
    generator "gen-int16" WireClass.NumericWidth (fun rng size ->
        both WireClass.NumericWidth (sprintf "gen-int16-%d" size) (int16 (rng.Next 65536 - 32768)))
    generator "gen-double" WireClass.Floating (fun rng size ->
        both WireClass.Floating (sprintf "gen-double-%d" size) (rng.NextDouble() * 1e6 - 5e5))
    generator "gen-decimal" WireClass.Decimal (fun rng size ->
        both WireClass.Decimal (sprintf "gen-decimal-%d" size) (decimal (rng.NextInt()) / 1000m))
    generator "gen-string" WireClass.StringEscape (fun rng size ->
        both WireClass.StringEscape (sprintf "gen-string-%d" size) (randomString rng size))
    generator "gen-datetime" WireClass.DateFamily (fun rng size ->
        both
            WireClass.DateFamily
            (sprintf "gen-datetime-%d" size)
            (DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(float (rng.Next 100_000_000))))
    generator "gen-timespan" WireClass.DateFamily (fun rng size ->
        // Whole milliseconds — see the note on `randomEnvelope`'s `Window`
        // and the pinned divergence in `StjRoundTripTests.timeSpanTickLoss`.
        both
            WireClass.DateFamily
            (sprintf "gen-timespan-%d" size)
            (TimeSpan.FromTicks((rng.NextInt64() % 864_000_000_000L) / 10_000L * 10_000L)))
    generator "gen-guid" WireClass.Guid (fun rng size ->
        both WireClass.Guid (sprintf "gen-guid-%d" size) (System.Guid(Array.init 16 (fun _ -> byte (rng.Next 256)))))
    generator "gen-binary" WireClass.Binary (fun rng size ->
        both WireClass.Binary (sprintf "gen-binary-%d" size) (Array.init (size * 7) (fun _ -> byte (rng.Next 256))))
    generator "gen-option" WireClass.OptionFamily (fun rng size ->
        both
            WireClass.OptionFamily
            (sprintf "gen-option-%d" size)
            (if rng.NextBool() then Some(randomString rng size) else None))
    generator "gen-list" WireClass.Collection (fun rng size ->
        both WireClass.Collection (sprintf "gen-list-%d" size) (List.init size (fun _ -> rng.NextInt())))
    generator "gen-array" WireClass.Collection (fun rng size ->
        both WireClass.Collection (sprintf "gen-array-%d" size) (Array.init size (fun _ -> randomString rng 2)))
    generator "gen-map" WireClass.MapSet (fun rng size ->
        both
            WireClass.MapSet
            (sprintf "gen-map-%d" size)
            (List.init size (fun i -> sprintf "k%d" i, rng.NextInt()) |> Map.ofList))
    generator "gen-set" WireClass.MapSet (fun rng size ->
        both WireClass.MapSet (sprintf "gen-set-%d" size) (List.init size (fun i -> i * rng.Next 7 + i) |> Set.ofList))
    generator "gen-tuple" WireClass.Tuple (fun rng size ->
        both WireClass.Tuple (sprintf "gen-tuple-%d" size) (rng.NextInt(), randomString rng size, rng.NextBool()))
    generator "gen-union" WireClass.Union (fun rng size ->
        both WireClass.Union (sprintf "gen-union-%d" size) (randomOutcome rng))
    generator "gen-record" WireClass.Record (fun rng size ->
        both WireClass.Record (sprintf "gen-record-%d" size) (randomAddress rng))
    generator "gen-envelope" WireClass.NestedRecord (fun rng size ->
        both WireClass.NestedRecord (sprintf "gen-envelope-%d" size) (randomEnvelope rng size))
    generator "gen-customer" WireClass.NestedRecord (fun rng size ->
        both WireClass.NestedRecord (sprintf "gen-customer-%d" size) (randomCustomer rng size))
]

/// The sizes every shape is drawn at, SMALLEST FIRST.
///
/// This ordering is what the corpus offers in place of a value shrinker,
/// and the claim is deliberately modest: a shape that fails is failing at
/// a known smallest size, because the sizes are enumerated and the run
/// reports which ones failed. `shrinkSize` below turns that into the
/// answer directly. A value-level shrinker over arbitrary F# records
/// would be the larger change by an order of magnitude and would buy the
/// same thing for the shapes this corpus draws, every one of which is
/// parameterised by exactly this size.
let sizes = [ 0; 1; 2; 5; 17; 40 ]

/// The seed this run draws at. Fixed, so the corpus is the same
/// population on every machine and in CI; overridable so a reported
/// failure can be reproduced and so a nightly can widen the search
/// without an edit.
[<Literal>]
let SeedVariable = "TOOLUP_REMOTING_CORPUS_SEED"

[<Literal>]
let DefaultSeed = 0x5EED1EUL

let seed () =
    match Environment.GetEnvironmentVariable SeedVariable with
    | null
    | "" -> DefaultSeed
    | text ->
        match UInt64.TryParse text with
        | true, parsed -> parsed
        | _ ->
            failwithf
                "%s is set to `%s`, which is not a uint64. Unset it or give it a number — a corpus that silently fell back to the default seed here would report a green run over a population nobody asked for."
                SeedVariable
                text

/// Every generated case: each shape at each size, at the run's seed.
let generatedCases () =
    let s = seed ()

    [
        for shape in shapeGenerators do
            for size in sizes do
                shape.Draw s size
    ]

/// The smallest size at which `fails` still reports a failure for
/// `shape`, or `None` when it fails at no size. The shrink: run the same
/// deterministic draw at each declared size, smallest first, and hand
/// back the first that reproduces.
let shrinkSize (shape: ShapeGenerator) (fails: WireCase -> bool) =
    let s = seed ()
    sizes |> List.tryFind (fun size -> fails (shape.Draw s size))

// ─── The adequacy guard (784.F) ──────────────────────────────────────

/// How many cases each class drew. Every class in `allClasses` appears,
/// including with a count of zero — a report that omitted the empty
/// classes would be a report that cannot show the thing it exists to
/// show.
let drawsByClass (cases: WireCase list) =
    allClasses
    |> List.map (fun cls -> cls, cases |> List.filter (fun c -> c.Class = cls) |> List.length)

/// The classes that drew nothing. A green run over an empty class
/// measures nothing, so this is the guard's whole content.
let emptyClasses (cases: WireCase list) =
    drawsByClass cases |> List.filter (snd >> (=) 0) |> List.map fst

/// The per-class report, printed by the suite whether or not it passes —
/// a coverage number nobody reads on a green run is a coverage number
/// nobody checks on a red one.
let adequacyReport (label: string) (cases: WireCase list) =
    let rows =
        drawsByClass cases
        |> List.map (fun (cls, count) -> sprintf "%A=%d" cls count)
        |> String.concat " "

    sprintf "%s: %d case(s) over %d class(es) — %s" label (List.length cases) (List.length allClasses) rows


// ─── Refuse-path mutations (784.D) ───────────────────────────────────
//
// A corpus that only covers ACCEPT measures half the contract. Phase 783
// gave both decoders a named refusal — `Reader.TryRead` and
// `FableConverters.tryDeserialiseElement` return `Result<obj,
// DecodeError>` — so a malformed payload now has a stated right answer,
// and this arm is what holds them to it.
//
// The outcome each mutation produces is DECLARED, and the three
// possibilities are kept distinct on purpose. "Refused" is the contract.
// "Accepted" is a real gap — the decoder read a value out of a payload
// that does not encode one — and calling that a refusal would make the
// corpus agree with whatever it is shown. "ThrewUnnamed" is 783's own
// stated boundary: its decoder interiors are still exception-shaped, and
// an escape that is not a named refusal is precisely what Phase 785's
// closed algebra exists to remove. Recording all three means that the day
// either decoder improves, this list goes red and names the mutation that
// moved — which is the only way a quarantine retires itself.

/// The mutation classes this arm claims to cover.
[<RequireQualifiedAccess>]
type MutationKind =
    /// A leading format byte replaced with a different, individually
    /// valid one — the payload is well-formed for some OTHER shape.
    | WrongTag
    /// The payload cut short mid-value.
    | Truncated
    /// A value written at a width the target type cannot hold.
    | WrongWidth
    /// A record written with fewer fields than its type has.
    | MissingField
    /// The same with more.
    | ExtraField

/// What a decoder did with a mutated payload, as measured.
type RefusalOutcome =
    /// A named `DecodeError` came back as data. The contract.
    | Refused
    /// The decoder produced a VALUE from a payload that does not encode
    /// one. A gap, not a pass — the note says what it produced.
    | Accepted of note: string
    /// Something escaped that is not a named refusal. 783's declared
    /// boundary; the note says what refused and where.
    | ThrewUnnamed of note: string

type WireMutation = {
    Name: string
    Kind: MutationKind
    /// The type the mutated payload is decoded AT.
    Target: Type
    /// The mutated MsgPack payload, or `None` when the mutation is
    /// JSON-only.
    MsgPack: byte[] option
    /// The mutated JSON text, or `None` when the mutation is
    /// MsgPack-only.
    Json: string option
    /// The measured outcome, per wire. A mutation can be refused on one
    /// wire and accepted on the other, and that difference is one of the
    /// things this corpus exists to surface.
    ExpectedMsgPack: RefusalOutcome
    ExpectedJson: RefusalOutcome
}

/// Classify what the MsgPack reader does with a payload. Never throws:
/// the classification IS the measurement.
let classifyMsgPack (target: Type) (bytes: byte[]) : RefusalOutcome * string =
    try
        match Read.Reader(bytes).TryRead target with
        | Ok value -> Accepted(sprintf "%A" value), "Ok"
        | Error e -> Refused, DecodeError.render e
    with ex ->
        ThrewUnnamed(ex.GetType().Name), ex.Message

/// The same for the STJ converter set. A payload that is not even
/// well-formed JSON cannot reach `tryDeserialiseElement` (which takes an
/// already-parsed `JsonElement`), so the parse is part of what is
/// classified — that is the real seam a dispatcher sits behind.
let classifyJson (target: Type) (text: string) : RefusalOutcome * string =
    try
        use document = JsonDocument.Parse text

        match
            ToolUp.Remoting.Json.SystemTextJson.FableConverters.tryDeserialiseElement
                document.RootElement
                target
                jsonOptions
        with
        | Ok value -> Accepted(sprintf "%A" value), "Ok"
        | Error e -> Refused, DecodeError.render e
    with ex ->
        ThrewUnnamed(ex.GetType().Name), ex.Message

/// The mutated population. Derived FROM the pinned cases rather than
/// written out as byte literals, so a writer change re-derives the
/// mutations instead of leaving a wall of stale hex behind.
let mutations () : WireMutation list =
    let flatRecord = pinnedCases |> List.find (fun c -> c.Name = "record-flat")
    let listCase = pinnedCases |> List.find (fun c -> c.Name = "list-small")
    let stringCase = pinnedCases |> List.find (fun c -> c.Name = "primitive-string")

    let int64Case =
        pinnedCases |> List.find (fun c -> c.Name = "width-int64-beyond-int32")

    [
        // ── WrongTag ──
        {
            Name = "wrong-tag-nil-for-record"
            Kind = MutationKind.WrongTag
            Target = flatRecord.ClrType
            // `c0` is nil: individually valid, structurally wrong for a
            // three-field record.
            MsgPack = Some [| 0xC0uy |]
            Json = Some "null"
            ExpectedMsgPack = Accepted "nil decodes to a null obj at any target type"
            ExpectedJson = Accepted "JSON null maps onto a null reference for a record type"
        }
        {
            Name = "wrong-tag-string-for-list"
            Kind = MutationKind.WrongTag
            Target = listCase.ClrType
            MsgPack = Some(Array.append [| 0xA3uy |] (Encoding.UTF8.GetBytes "abc"))
            Json = Some "\"abc\""
            ExpectedMsgPack =
                ThrewUnnamed
                    "KeyNotFoundException from interpretStringAs, which looks the string up as a string-enum case name of the target union"
            ExpectedJson = Refused
        }
        {
            Name = "wrong-tag-bool-for-string"
            Kind = MutationKind.WrongTag
            Target = stringCase.ClrType
            MsgPack = Some [| 0xC3uy |]
            Json = Some "true"
            ExpectedMsgPack =
                Accepted
                    "a bool decodes to a boxed bool whatever the target type, so the cast fails later and elsewhere"
            ExpectedJson = Refused
        }

        // ── Truncated ──
        {
            Name = "truncated-record-body"
            Kind = MutationKind.Truncated
            Target = flatRecord.ClrType
            MsgPack = Some(let b = flatRecord.WriteMsgPack() in b[.. b.Length / 2])
            Json = Some(let t = flatRecord.WriteJson() in t.Substring(0, t.Length / 2))
            ExpectedMsgPack =
                ThrewUnnamed "ArgumentOutOfRangeException from Encoding.UTF8.GetString reading past the buffer"
            ExpectedJson = ThrewUnnamed "JsonDocument.Parse refuses malformed JSON before the converter seam is reached"
        }
        {
            Name = "truncated-string-header"
            Kind = MutationKind.Truncated
            Target = stringCase.ClrType
            // A fixstr header claiming five bytes with two present.
            MsgPack = Some [| 0xA5uy; 0x68uy; 0x65uy |]
            Json = None
            ExpectedMsgPack =
                ThrewUnnamed "ArgumentOutOfRangeException from Encoding.UTF8.GetString — the header's length is trusted"
            ExpectedJson = Refused
        }
        {
            Name = "truncated-empty-payload"
            Kind = MutationKind.Truncated
            Target = stringCase.ClrType
            MsgPack = Some Array.empty
            Json = Some ""
            ExpectedMsgPack = ThrewUnnamed "IndexOutOfRangeException on the very first byte read"
            ExpectedJson = ThrewUnnamed "an empty document is not JSON; the parse refuses before the converter seam"
        }

        // ── WrongWidth ──
        // The narrowing the whole corpus is built around, asked of the
        // REFUSAL path. Reading an int64 payload at int32 is a decode that
        // cannot be right, and it is ACCEPTED: `interpretIntegerAs`
        // narrows with an unchecked conversion once it knows the target
        // type, so nothing in the reader is in a position to notice. This
        // is the most valuable row in the list — a silent wrong ANSWER
        // rather than a missing error, and exactly what a closed decoder
        // algebra has to close.
        {
            Name = "wrong-width-int64-into-int32"
            Kind = MutationKind.WrongWidth
            Target = typeof<int32>
            MsgPack = Some(int64Case.WriteMsgPack())
            Json = Some(int64Case.WriteJson())
            ExpectedMsgPack = Accepted "narrowed by an unchecked conversion in interpretIntegerAs"
            ExpectedJson = Refused
        }
        {
            Name = "wrong-width-string-into-int"
            Kind = MutationKind.WrongWidth
            Target = typeof<int32>
            MsgPack = Some(Array.append [| 0xA1uy |] (Encoding.UTF8.GetBytes "7"))
            Json = Some "\"7\""
            ExpectedMsgPack =
                ThrewUnnamed
                    "ArgumentException — interpretStringAs asks FSharpType.GetUnionCases for int32, which is not a union"
            ExpectedJson =
                Accepted
                    "the remoting converter set enables JsonNumberHandling.AllowReadingFromString, so a quoted number is a legitimate int on this wire"
        }

        // ── MissingField / ExtraField ──
        // A record is an ARRAY on the msgpack wire, so field count is
        // structural there: a three-field record written as a two-element
        // array is a different shape, not a shorter one. On the JSON wire
        // it is a property bag, and both directions are deliberately
        // tolerant — which is what keeps an additive wire change
        // non-breaking, and is also why an additive field reads back as
        // null rather than as a refusal.
        {
            Name = "missing-field-record"
            Kind = MutationKind.MissingField
            Target = flatRecord.ClrType
            MsgPack =
                Some(
                    Array.concat [
                        [| 0x92uy |]
                        [| 0xA1uy |]
                        Encoding.UTF8.GetBytes "a"
                        [| 0xA1uy |]
                        Encoding.UTF8.GetBytes "b"
                    ]
                )
            Json = Some "{\"Line1\":\"a\"}"
            ExpectedMsgPack =
                ThrewUnnamed
                    "IndexOutOfRangeException — the record reader reads field 3 off the end of a 2-element array"
            ExpectedJson =
                Accepted
                    "an absent reference-type field reads back as null rather than refusing — the additive read path this SDK documents"
        }
        {
            Name = "extra-field-record"
            Kind = MutationKind.ExtraField
            Target = flatRecord.ClrType
            MsgPack =
                Some(
                    Array.append
                        (flatRecord.WriteMsgPack() |> Array.mapi (fun i b -> if i = 0 then 0x94uy else b))
                        (Array.append [| 0xA1uy |] (Encoding.UTF8.GetBytes "x"))
                )
            Json = Some "{\"Line1\":\"a\",\"Postcode\":\"b\",\"Country\":\"c\",\"Surplus\":\"x\"}"
            ExpectedMsgPack =
                Accepted
                    "the record reader consumes exactly as many elements as the type has fields and ignores the surplus"
            ExpectedJson =
                Accepted
                    "an unmatched property is ignored by default, which is what keeps an additive wire change non-breaking"
        }
    ]

/// Every mutation kind, for this arm's own adequacy check.
let allMutationKinds: MutationKind list =
    FSharp.Reflection.FSharpType.GetUnionCases typeof<MutationKind>
    |> Array.map (fun c -> FSharp.Reflection.FSharpValue.MakeUnion(c, [||]) :?> MutationKind)
    |> Array.toList

/// Render an outcome for a message without leaking a whole value into it.
let describeOutcome (outcome: RefusalOutcome) =
    match outcome with
    | Refused -> "Refused (a named DecodeError)"
    | Accepted note -> sprintf "Accepted — %s" note
    | ThrewUnnamed note -> sprintf "ThrewUnnamed — %s" note

/// Two outcomes match when they are the same CLASS. The notes are prose
/// for a reader, never part of the assertion: a stale note is a
/// documentation defect, a changed class is a contract change.
let sameOutcomeClass (a: RefusalOutcome) (b: RefusalOutcome) =
    match a, b with
    | Refused, Refused -> true
    | Accepted _, Accepted _ -> true
    | ThrewUnnamed _, ThrewUnnamed _ -> true
    | _ -> false

#endif