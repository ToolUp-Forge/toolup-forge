// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Remoting.Json

open System
open System.Globalization

// ─── Phase 799 — the closed JSON value model ─────────────────────────
//
// The JSON twin of `ToolUp.Remoting.MsgPack.Value` (Phase 785), and the
// carrier the decoder algebra extends to the JSON wire over. The same
// three properties are load-bearing, for the same reasons:
//
//   * **Closed.** Six cases, no extension point. A decoder's match is
//     exhaustive by construction.
//   * **No null.** The absent value is `Null`, a case. A decoder never
//     needs a null guard.
//   * **Well-founded.** `JsonValue.size` is strictly smaller for every
//     subterm than for its container, so structural recursion terminates
//     by the model's own measure.
//
// **The numeric case carries the token's LEXICAL FORM, and that is the
// whole reason this type exists** rather than the decision going the
// other way. Phase 785.F assessed `ToolUp.AI.Wire.JsonValue` as the
// carrier — right shape, FSharp.Core-only, both hosts, already referenced
// — and disqualified it on one field: `JNumber of float`. A single IEEE
// double cannot hold what the algebra's width discipline exists to
// preserve: `int64` past 2^53 rounds silently, `decimal` becomes
// approximate, and the token's source is gone, so `asInt32` could not
// tell "was an integer" from "happens to be integral". Phase 784 pinned
// the same class of loss live on this wire (the STJ path loses a
// `TimeSpan` tick). So `Number` carries the digits as written — a JSON
// number token is a finite string of ASCII, and keeping it costs a
// string allocation the parse would have made anyway — and every numeric
// combinator reads the width it needs FROM THE TEXT, exactly.
//
// A sibling model rather than a widened `ToolUp.AI.Wire.JsonValue`: that
// type is matched exhaustively in forty-five files across the estate's
// provider mappings, and a seventh case is a break in every one of them
// (the record-field / DU-case widening class the surface guard names).
// The two coexist in different namespaces with different jobs — that one
// is a provider wire-mapping's substrate, this one is a decoder's.
//
// **Object members preserve insertion order**, as the AI.Wire model's
// do: an ordered `(string * JsonValue)` sequence (an array since Phase
// 905), never a `Map`, so a value round-trips to the text it came from
// and a duplicate key is VISIBLE (the decoder decides; the model does
// not silently keep one).

/// Phase 799 — one JSON value, as the wire can carry it.
///
/// `[<RequireQualifiedAccess>]` deliberately: `String`, `Array` and
/// `Bool` would otherwise shadow FSharp.Core names in every file that
/// opens this namespace.
[<RequireQualifiedAccess>]
type JsonValue =
    /// The JSON `null` literal. A CASE, never `null`.
    | Null
    /// A JSON boolean.
    | Bool of bool
    /// A JSON number, as WRITTEN: the token text, validated against the
    /// JSON number grammar by whoever produced this value and never
    /// parsed to a `float` on the way in. `JsonValue.tryInt64` and its
    /// siblings read the width they need from it.
    | Number of lexical: string
    /// A JSON string, unescaped.
    | String of string
    /// A JSON array's elements, in wire order.
    ///
    /// **An array since Phase 905, for positional access** — the change
    /// Phase 856 made to `MsgPack.Value.Arr`, for the same reason. A tuple,
    /// a union's field list and a map entry are read element by element
    /// through `JsonDecode.index`, and over the F# list this case used to
    /// carry, element `i` walked `i` cells: an n-element read was quadratic
    /// in n. Treated as immutable, like `MsgPack.Value.Bin`'s `byte[]`:
    /// nothing in `JsonDecode` writes to one, and a value is never mutated
    /// after it is built.
    | Array of JsonValue[]
    /// An object's members in wire order. Never a `Map`: order and
    /// duplicates are facts about the wire a decoder may need. An array
    /// since Phase 905 (see `Array`), so the member a name resolves to is
    /// read by index; `JsonValue.tryMember` is where that happens.
    | Object of members: (string * JsonValue)[]

/// The measure, the description and the lexical-number readers over
/// `JsonValue`.
[<RequireQualifiedAccess>]
module JsonValue =

    /// The structural size: 1 for a leaf, 1 + the sizes of its children
    /// for a container. The well-founded measure — see the header, and
    /// `MsgPack.Value.size` for why it is shipped rather than implied.
    let rec size (value: JsonValue) : int =
        match value with
        | JsonValue.Null
        | JsonValue.Bool _
        | JsonValue.Number _
        | JsonValue.String _ -> 1
        | JsonValue.Array items -> items |> Array.fold (fun total item -> total + size item) 1
        | JsonValue.Object members -> members |> Array.fold (fun total (_, member') -> total + size member') 1

    /// What a refusal's `Found` field says about this value: the shape,
    /// and for a number its text — never a deep rendering of a container.
    let describe (value: JsonValue) : string =
        match value with
        | JsonValue.Null -> "null"
        | JsonValue.Bool true -> "bool true"
        | JsonValue.Bool false -> "bool false"
        | JsonValue.Number text -> sprintf "number %s" text
        | JsonValue.String text -> sprintf "string of %d character(s)" text.Length
        | JsonValue.Array items -> sprintf "array of %d element(s)" items.Length
        | JsonValue.Object members -> sprintf "object of %d member(s)" members.Length

    // ─── Phase 905 — a member by name, in constant time ──────────────
    //
    // A record goes onto this wire as an object keyed by field name, and a
    // generated record decoder reads it with one `JsonDecode.field` per
    // field — each a lookup BY NAME in the same object. A scan per lookup
    // makes an n-field record decode quadratic in n (Phase 905 measured the
    // per-field cost rising 0.06 -> 0.21 us from 8 to 256 fields). Indexing
    // the carrier alone does not remove that: the scan is still a scan.
    //
    // So a WIDE object's name -> position index is built once, the first
    // time a member of it is looked up, and memoised against the members
    // array's identity in a `ConditionalWeakTable` — it lives exactly as
    // long as the value does, and nothing in the model changes shape for
    // it. Three properties make that safe to put under a combinator the
    // algebra calls pure:
    //
    //   * **It answers what the scan answers.** The index keeps each name's
    //     FIRST position (built forward, first write wins), so a duplicate
    //     key resolves exactly as `Array.tryPick` resolves it. And a hit is
    //     re-checked against the member it names before it is trusted.
    //   * **It depends on nothing but the value.** The table is keyed by the
    //     array itself, never by a decoder or a call site, so no lookup's
    //     answer can depend on what an earlier lookup saw.
    //   * **Narrow objects never pay for it.** At or below
    //     `IndexedLookupThreshold` members the forward scan is no dearer
    //     than the table lookup, and is what runs.
    //
    // The browser host keeps the scan: `ConditionalWeakTable` has no Fable
    // mapping, and the cost this removes is the SERVER's per-request cost
    // (the client decodes a response it asked for, once).

    /// Phase 905 — the member count above which `tryMember` reads a name
    /// through a memoised index rather than a forward scan. Measured, not
    /// guessed (2026-09-29, Release, a loaded machine, the benchmark's
    /// `--wide` JSON table with the threshold forced each way): an index
    /// lookup costs a flat ~0.04-0.05 us per field; a scan ~0.03 at 16
    /// members, ~0.05 at 32 and ~0.07 at 64. They cross near 32.
    [<Literal>]
    let private IndexedLookupThreshold = 32

    let private scanMember (name: string) (members: (string * JsonValue)[]) : JsonValue option =
        let mutable found = None
        let mutable i = 0

        while found.IsNone && i < members.Length do
            let k, v = members[i]

            if k = name then
                found <- Some v

            i <- i + 1

        found

#if !FABLE_COMPILER
    let private memberIndexes =
        System.Runtime.CompilerServices.ConditionalWeakTable<
            (string * JsonValue)[],
            System.Collections.Generic.Dictionary<string, int>
         >()

    let private buildMemberIndex (members: (string * JsonValue)[]) =
        let index =
            System.Collections.Generic.Dictionary<string, int>(members.Length, StringComparer.Ordinal)

        for i in 0 .. members.Length - 1 do
            // First write wins: a duplicate name keeps its FIRST position.
            index.TryAdd(fst members[i], i) |> ignore

        index
#endif

    /// The FIRST member named `name`, or `None`. First rather than last
    /// because that is what a reader sees first; a decoder that must
    /// refuse a duplicate reads `members` itself. Constant time on a wide
    /// object after its first lookup (Phase 905; see the note above).
    let tryMember (name: string) (value: JsonValue) : JsonValue option =
        match value with
        | JsonValue.Object members ->
#if FABLE_COMPILER
            scanMember name members
#else
            if members.Length <= IndexedLookupThreshold then
                scanMember name members
            else
                let index =
                    memberIndexes.GetValue(
                        members,
                        System.Runtime.CompilerServices.ConditionalWeakTable.CreateValueCallback buildMemberIndex
                    )

                match index.TryGetValue name with
                | true, position when position < members.Length && fst members[position] = name ->
                    Some(snd members[position])
                | true, _ -> scanMember name members
                | _ -> None
#endif
        | _ -> None

    // ─── The number grammar, and the widths read from it ─────────────
    //
    // RFC 8259 §6: `-? int frac? exp?` with `int = 0 | [1-9][0-9]*`,
    // `frac = . [0-9]+`, `exp = [eE] [+-]? [0-9]+`. Checked by hand
    // rather than by a regex so both hosts run the same code and the
    // check allocates nothing.

    let private isDigit (c: char) = c >= '0' && c <= '9'

    /// Whether `text` is a JSON number token. The producer of a `Number`
    /// is expected to have checked this; the combinators re-check
    /// cheaply rather than trust it, because a `Number` built by hand
    /// in a test is a legitimate value.
    let isNumberToken (text: string) : bool =
        let n = text.Length

        // The index after a run of digits starting at `i` (possibly `i`).
        let digits (i: int) =
            let mutable j = i

            while j < n && isDigit text.[j] do
                j <- j + 1

            j

        let i = if n > 0 && text.[0] = '-' then 1 else 0

        let afterInt =
            if i >= n then -1
            elif text.[i] = '0' then i + 1
            elif isDigit text.[i] then digits i
            else -1

        if afterInt < 0 then
            false
        else
            let afterFrac =
                if afterInt < n && text.[afterInt] = '.' then
                    let j = digits (afterInt + 1)
                    if j > afterInt + 1 then j else -1
                else
                    afterInt

            if afterFrac < 0 then
                false
            else
                let afterExp =
                    if afterFrac < n && (text.[afterFrac] = 'e' || text.[afterFrac] = 'E') then
                        let signed =
                            afterFrac + 1 < n && (text.[afterFrac + 1] = '+' || text.[afterFrac + 1] = '-')

                        let start = if signed then afterFrac + 2 else afterFrac + 1
                        let j = digits start
                        if j > start then j else -1
                    else
                        afterFrac

                afterExp = n

    /// Whether the token is written as an INTEGER: no fraction, no
    /// exponent. `1.0` and `1e0` are integral in value and not in form,
    /// and the integer combinators refuse them: the writer never emits
    /// an integer that way, so a token that arrives so did not come from
    /// an integer.
    let isIntegralToken (text: string) : bool =
        isNumberToken text
        && not (text.Contains "." || text.Contains "e" || text.Contains "E")

    /// The token as an `int64`, or `None` when it is not an integral
    /// token or does not fit. Exact: no `float` on the way.
    let tryInt64 (text: string) : int64 option =
        if isIntegralToken text then
#if FABLE_COMPILER
            match Int64.TryParse text with
            | true, n -> Some n
            | _ -> None
#else
            match Int64.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) with
            | true, n -> Some n
            | _ -> None
#endif
        else
            None

    /// The token as a `uint64`, or `None`. A leading `-` is refused
    /// outright rather than parsed and range-checked.
    let tryUInt64 (text: string) : uint64 option =
        if isIntegralToken text && not (text.StartsWith "-") then
#if FABLE_COMPILER
            match UInt64.TryParse text with
            | true, n -> Some n
            | _ -> None
#else
            match UInt64.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture) with
            | true, n -> Some n
            | _ -> None
#endif
        else
            None

    /// The token as a `decimal`, exactly — the 28 significant digits
    /// `decimal` carries, read from the digits as written. `None` when
    /// the token is not a number or exceeds `decimal`'s range or scale.
    let tryDecimal (text: string) : decimal option =
        if isNumberToken text then
#if FABLE_COMPILER
            match Decimal.TryParse text with
            | true, d -> Some d
            | _ -> None
#else
            match Decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture) with
            | true, d -> Some d
            | _ -> None
#endif
        else
            None

    /// The token as a `float`. The one convenience that IS a `float`,
    /// for a target that is one; correctly rounded, which is what
    /// "declared float" means on a text wire.
    let tryFloat (text: string) : float option =
        if isNumberToken text then
#if FABLE_COMPILER
            match Double.TryParse text with
            | true, f -> Some f
            | _ -> None
#else
            match Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture) with
            | true, f -> Some f
            | _ -> None
#endif
        else
            None

// ─── Phase 843 — text to `JsonValue`, on both hosts ──────────────────
//
// The browser twin of `JsonRead` (the server's `JsonElement` pass), and
// the parse the client's JSON response decode stands on. It reads the
// response TEXT directly into the value model, so a number token is
// carried as written from the first character the parse sees of it.
//
// **Why the text and not `JSON.parse` — 843.A's recorded answer.** The
// shard asked whether the token can be recovered from `SimpleJson`'s
// tree (`parseNative` is `JSON.parse`), and its 2026-09-26 amendment
// proposed the `JSON.parse` source-text-access reviver: the reviver's
// third argument carries each primitive's lexical source, so numbers
// could be built as text inside the one native parse. Settled by
// measurement on Node 25 (V8), over a 760 KB response-shaped array of
// 5,000 records, each figure the mean of 100 parses after 20 warm-up
// parses, both run orders:
//
//   * `JSON.parse`, no reviver ...................... 5-16 ms
//   * `JSON.parse`, an IDENTITY reviver ............. 79-89 ms
//   * `JSON.parse`, a reviver building THIS model ... 134-137 ms
//   * `JsonText.tryParse` below, as Fable emits it .. 18-21 ms
//
// So the reviver DOES carry the token, and still loses on three counts,
// any one of which would decide it:
//
//   1. **It is the slow path of `JSON.parse`.** A reviver that does
//      nothing costs seven to nine times a plain parse, because the
//      spec's internalize walk re-reads and re-defines every property
//      through it; building this model in one costs six to seven times
//      what this module's transpiled scan costs to build the same model.
//   2. **It cannot carry two facts the model promises.** A reviver sees
//      the object `JSON.parse` has already built, so an array-index key
//      (`"10"`, `"2"`) has already been moved ahead of the others and a
//      duplicate key has already collapsed to its LAST value. The model's
//      members are wire order with duplicates visible, which is what
//      `JsonRead` (System.Text.Json) delivers — so through a reviver
//      `{"Ok":1,"Ok":2}` would decode as `Ok 2` in the browser while the
//      server refuses it as a two-member object.
//   3. **It is not on every engine the platform runs on.** V8 has it
//      (Chrome and Edge 114+, Node 21+), but the Fable test tier's
//      declared floor is Node 20, which does not, and a fallback path
//      would then decide what the client reads on some engines.
//
// The tree walk the amendment names as the fallback is no answer at all:
// once `JSON.parse` has read `0.10000000000000000555` as the double
// `0.1`, no walk can tell that token from `0.1`, so it can neither
// recover the digits nor refuse the ones it lost.
//
// One implementation, then: FSharp.Core-only and compiled on BOTH hosts.
// The browser decodes through it, and the .NET pack holds it to
// `JsonRead` over the same texts, which is what makes "the two hosts
// read one text identically" a red run rather than a hope. The grammar
// is RFC 8259 exactly as System.Text.Json's defaults read it — no
// comments, no trailing commas, no leading `+`, no byte-order mark, the
// four whitespace characters only — and an unpaired surrogate ESCAPE is
// refused, as `JsonElement.GetString` refuses it. (An unpaired surrogate
// written RAW cannot reach the browser: the response text is decoded
// from UTF-8, which has no encoding for one. It passes through here as
// written.)
//
// The bounds are `JsonRead`'s, restated here because a bound is this
// pass's own contract: containers nest at most `DefaultMaxDepth` deep
// and carry at most `DefaultMaxMembers` entries, each checked BEFORE the
// value that would breach it is built. A bound refusal carries the path
// to the container that breached it, as `JsonRead`'s does; a syntax
// refusal carries the offset instead, which is where a reader looks.

/// Phase 843 — parse JSON text into the value model, numbers as their
/// lexical tokens, under explicit bounds. Total: every input is a value
/// or a named refusal, never an exception.
[<RequireQualifiedAccess>]
module JsonText =

    /// How deeply containers may nest — `JsonRead.DefaultMaxDepth`'s
    /// value, and System.Text.Json's own default.
    [<Literal>]
    let DefaultMaxDepth = 64

    /// How many elements or members one container may carry —
    /// `JsonRead.DefaultMaxMembers`'s value.
    [<Literal>]
    let DefaultMaxMembers = 1_000_000

    let private hexDigit (c: char) : int =
        if c >= '0' && c <= '9' then int c - int '0'
        elif c >= 'a' && c <= 'f' then int c - int 'a' + 10
        elif c >= 'A' && c <= 'F' then int c - int 'A' + 10
        else -1

    let private hex4 (code: int) : string =
        let digits = "0123456789ABCDEF"

        String [|
            digits.[(code >>> 12) &&& 0xF]
            digits.[(code >>> 8) &&& 0xF]
            digits.[(code >>> 4) &&& 0xF]
            digits.[code &&& 0xF]
        |]

    /// One scan over one text. Mutable by design: the position and the
    /// first refusal are the scan's whole state, and threading either
    /// through a `Result` per value would allocate on every value of
    /// every response. A refusal is recorded ONCE, every frame unwinds on
    /// seeing it, and a bound refusal gains each frame's segment on the
    /// way out.
    type private Scanner(text: string, maxDepth: int, maxMembers: int) =
        let n = text.Length
        let mutable i = 0
        let mutable failure: ToolUp.Remoting.DecodeError option = None
        let mutable boundFailure = false

        let describeAt (position: int) : string =
            if position >= n then
                sprintf "the end of the text at offset %d" position
            else
                let c = text.[position]

                if c < ' ' then
                    sprintf "control character U+%s at offset %d" (hex4 (int c)) position
                else
                    sprintf "`%c` at offset %d" c position

        let syntax (expected: string) : unit =
            if failure.IsNone then
                failure <-
                    Some(
                        ToolUp.Remoting.DecodeError.create
                            "a JSON document"
                            (sprintf "%s, where %s was expected" (describeAt i) expected)
                    )

        let bound (expected: string) (found: string) : unit =
            if failure.IsNone then
                boundFailure <- true
                failure <- Some(ToolUp.Remoting.DecodeError.create expected found)

        /// Annotate a bound refusal raised beneath this frame; a syntax
        /// refusal keeps its offset and gains no path.
        let under (segment: string) : unit =
            match failure with
            | Some error when boundFailure -> failure <- Some(ToolUp.Remoting.DecodeError.under segment error)
            | _ -> ()

        let skipWhitespace () =
            let mutable scanning = true

            while scanning && i < n do
                let c = text.[i]

                if c = ' ' || c = '\t' || c = '\n' || c = '\r' then
                    i <- i + 1
                else
                    scanning <- false

        let literal (word: string) (value: JsonValue) : JsonValue =
            let mutable k = 0

            while k < word.Length && i + k < n && text.[i + k] = word.[k] do
                k <- k + 1

            i <- i + k

            if k = word.Length then
                value
            else
                syntax (sprintf "the rest of the literal `%s`" word)
                JsonValue.Null

        let number () : JsonValue =
            let start = i
            let mutable scanning = true

            while scanning && i < n do
                let c = text.[i]

                if (c >= '0' && c <= '9') || c = '-' || c = '+' || c = '.' || c = 'e' || c = 'E' then
                    i <- i + 1
                else
                    scanning <- false

            let token = text.Substring(start, i - start)

            if JsonValue.isNumberToken token then
                JsonValue.Number token
            else
                i <- start
                syntax "a number token (RFC 8259 section 6)"
                JsonValue.Null

        /// The code unit of the `\uXXXX` escape whose `u` is at `at`, or -1.
        let escapeUnit (at: int) : int =
            if at + 4 >= n then
                -1
            else
                let a = hexDigit text.[at + 1]
                let b = hexDigit text.[at + 2]
                let c = hexDigit text.[at + 3]
                let d = hexDigit text.[at + 4]

                if a < 0 || b < 0 || c < 0 || d < 0 then
                    -1
                else
                    (a <<< 12) ||| (b <<< 8) ||| (c <<< 4) ||| d

        /// The escape at `i` (a backslash), appended to `built`; `i` moves
        /// past it.
        let escape (built: System.Text.StringBuilder) : unit =
            if i + 1 >= n then
                i <- i + 1
                syntax "an escape character"
            else
                let simple =
                    match text.[i + 1] with
                    | '"' -> '"'
                    | '\\' -> '\\'
                    | '/' -> '/'
                    | 'b' -> '\b'
                    | 'f' -> '\f'
                    | 'n' -> '\n'
                    | 'r' -> '\r'
                    | 't' -> '\t'
                    | _ -> '\000'

                if simple <> '\000' then
                    built.Append(simple) |> ignore
                    i <- i + 2
                elif text.[i + 1] = 'u' then
                    let code = escapeUnit (i + 1)

                    if code < 0 then
                        i <- i + 2
                        syntax "four hexadecimal digits after the \\u"
                    elif code >= 0xD800 && code <= 0xDBFF then
                        // A high surrogate is admitted only with the low
                        // one that completes it. Alone it is refused, as
                        // `JsonElement.GetString` refuses it: a string
                        // that is not valid UTF-16 is not a value.
                        let low =
                            if i + 7 < n && text.[i + 6] = '\\' && text.[i + 7] = 'u' then
                                escapeUnit (i + 7)
                            else
                                -1

                        if low >= 0xDC00 && low <= 0xDFFF then
                            built.Append(char code).Append(char low) |> ignore
                            i <- i + 12
                        else
                            syntax "a low-surrogate escape completing the high surrogate here"
                    elif code >= 0xDC00 && code <= 0xDFFF then
                        syntax "an escape that is not an unpaired low surrogate"
                    else
                        built.Append(char code) |> ignore
                        i <- i + 6
                else
                    i <- i + 1
                    syntax "an escape character (one of \" \\ / b f n r t u)"

        /// The string literal whose opening quote is at `i`, unescaped.
        let stringLiteral () : string =
            i <- i + 1
            let mutable start = i
            let mutable built: System.Text.StringBuilder = null
            let mutable result: string = null

            while isNull result && failure.IsNone do
                if i >= n then
                    syntax "a closing quote"
                else
                    let c = text.[i]

                    if c = '"' then
                        let tail = text.Substring(start, i - start)
                        result <- if isNull built then tail else built.Append(tail).ToString()
                        i <- i + 1
                    elif c = '\\' then
                        if isNull built then
                            built <- System.Text.StringBuilder()

                        built.Append(text.Substring(start, i - start)) |> ignore
                        escape built
                        start <- i
                    elif c < ' ' then
                        syntax "an escaped control character"
                    else
                        i <- i + 1

            if isNull result then "" else result

        member this.Value(depth: int) : JsonValue =
            skipWhitespace ()

            if i >= n then
                syntax "a JSON value"
                JsonValue.Null
            else
                let c = text.[i]

                if c = '{' then
                    this.Object depth
                elif c = '[' then
                    this.Array depth
                elif c = '"' then
                    JsonValue.String(stringLiteral ())
                elif c = 't' then
                    literal "true" (JsonValue.Bool true)
                elif c = 'f' then
                    literal "false" (JsonValue.Bool false)
                elif c = 'n' then
                    literal "null" JsonValue.Null
                elif c = '-' || (c >= '0' && c <= '9') then
                    number ()
                else
                    syntax "a JSON value"
                    JsonValue.Null

        member this.Array(depth: int) : JsonValue =
            if depth >= maxDepth then
                bound (sprintf "nesting at most %d container(s) deep" maxDepth) "an array nested deeper"
                JsonValue.Null
            else
                i <- i + 1
                skipWhitespace ()

                if i < n && text.[i] = ']' then
                    i <- i + 1
                    JsonValue.Array [||]
                else
                    let mutable acc = []
                    let mutable count = 0
                    let mutable closed = false

                    while not closed && failure.IsNone do
                        if count >= maxMembers then
                            bound (sprintf "an array of at most %d element(s)" maxMembers) "an array with more"
                        else
                            let item = this.Value(depth + 1)

                            if failure.IsSome then
                                under (sprintf "[%d]" count)
                            else
                                acc <- item :: acc
                                count <- count + 1
                                skipWhitespace ()

                                if i < n && text.[i] = ',' then
                                    i <- i + 1
                                elif i < n && text.[i] = ']' then
                                    i <- i + 1
                                    closed <- true
                                else
                                    syntax "`,` or `]`"

                    if failure.IsSome then
                        JsonValue.Null
                    else
                        JsonValue.Array(Array.ofList (List.rev acc))

        member this.Object(depth: int) : JsonValue =
            if depth >= maxDepth then
                bound (sprintf "nesting at most %d container(s) deep" maxDepth) "an object nested deeper"
                JsonValue.Null
            else
                i <- i + 1
                skipWhitespace ()

                if i < n && text.[i] = '}' then
                    i <- i + 1
                    JsonValue.Object [||]
                else
                    let mutable acc = []
                    let mutable count = 0
                    let mutable closed = false

                    while not closed && failure.IsNone do
                        skipWhitespace ()

                        if count >= maxMembers then
                            bound (sprintf "an object of at most %d member(s)" maxMembers) "an object with more"
                        elif i >= n || text.[i] <> '"' then
                            syntax "a member name"
                        else
                            let name = stringLiteral ()
                            skipWhitespace ()

                            if failure.IsNone then
                                if i < n && text.[i] = ':' then
                                    i <- i + 1
                                    let item = this.Value(depth + 1)

                                    if failure.IsSome then
                                        under name
                                    else
                                        acc <- (name, item) :: acc
                                        count <- count + 1
                                        skipWhitespace ()

                                        if i < n && text.[i] = ',' then
                                            i <- i + 1
                                        elif i < n && text.[i] = '}' then
                                            i <- i + 1
                                            closed <- true
                                        else
                                            syntax "`,` or `}`"
                                else
                                    syntax "`:`"

                    if failure.IsSome then
                        JsonValue.Null
                    else
                        JsonValue.Object(Array.ofList (List.rev acc))

        /// The whole text as ONE value: anything but whitespace after it
        /// is a refusal.
        member this.Document() : Result<JsonValue, ToolUp.Remoting.DecodeError> =
            let value = this.Value 0

            if failure.IsNone then
                skipWhitespace ()

                if i < n then
                    syntax "the end of the document"

            match failure with
            | Some error -> Error error
            | None -> Ok value

    /// Parse `text` under explicit bounds.
    let tryParseWith (maxDepth: int) (maxMembers: int) (text: string) : Result<JsonValue, ToolUp.Remoting.DecodeError> =
        if isNull text then
            Error(ToolUp.Remoting.DecodeError.create "a JSON document" "no text")
        else
            try
                Scanner(text, maxDepth, maxMembers).Document()
            with ex ->
                // Unreachable by construction — the scan reads only below
                // the text's length and recurses at most `maxDepth` deep —
                // and kept so "total" does not rest on that argument alone.
                Error(ToolUp.Remoting.DecodeError.create "a JSON document" ex.Message)

    /// Parse `text` under the default bounds.
    let tryParse (text: string) : Result<JsonValue, ToolUp.Remoting.DecodeError> =
        tryParseWith DefaultMaxDepth DefaultMaxMembers text