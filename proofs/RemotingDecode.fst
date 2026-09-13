(*
   Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

   Licensed under the Apache License, Version 2.0 (the "License");
   you may not use this file except in compliance with the License.
   You may obtain a copy of the License at

       http://www.apache.org/licenses/LICENSE-2.0

   Unless required by applicable law or agreed to in writing, software
   distributed under the License is distributed on an "AS IS" BASIS,
   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
   See the License for the specific language governing permissions and
   limitations under the License.
*)

/// Phase 787 — the remoting decoder algebra, modelled in F* and proved
/// total.
///
/// # What this module is
///
/// A hand-written model of `ToolUp.Remoting.Decode` (the combinators)
/// over `ToolUp.Remoting.MsgPack.Value` (the closed value model), clause
/// for clause. Every definition below names its F# counterpart in the
/// comment above it, and every refusal reproduces the F# message
/// VERBATIM — the differential host compares the messages, not merely
/// the accept/refuse class, so a message that drifted here would be
/// caught there rather than quietly diverging.
///
/// The method is not new: an open-source F# wire decoder whose
/// combinators were proved total in F* established it, and the findings
/// this module inherits from that work rather than rediscovering are
/// recorded in `README.md` and in `check.ps1`'s header. The theorem,
/// though, is about THIS algebra.
///
/// # The theorem
///
/// Given a parsed `value`, no combinator and no decoder built from them
/// can diverge, throw, or reach a state that is neither an accept nor a
/// named refusal — and WHICH of the two is characterised structurally,
/// so the failure classification is exhaustive.
///
/// In F* the first half is not a lemma anyone could fail to prove: every
/// definition here is `Tot` (F*'s default effect for a `let`), the
/// checker rejects a partial match, a non-terminating recursion or a
/// division by zero, and `outcome` has exactly two cases. So the content
/// of "total" is the CHECKER'S acceptance of this file, and
/// `decode_total` below merely names it. The content that a reader
/// should weigh is in the CHARACTERISATION lemmas — they say which
/// outcome, for which input — and in the three width-rule lemmas, which
/// pin Phase 786's information rule as a statement about width classes
/// rather than as three `if` branches nobody has read since.
///
/// # What is modelled, and what is deliberately not
///
/// Three payload kinds are modelled as data the HOST measured rather
/// than as something this model recomputes, and the reason is the same
/// in all three cases: recomputing them would introduce a SECOND
/// implementation that can disagree with the host's, which is precisely
/// the class of divergence the model exists to rule out.
///
///   * A string's LENGTH (`VStr`'s second field). `FStar.String.length`
///     counts F* string characters; `System.String.Length` counts UTF-16
///     code units. They disagree on any astral character, so a model
///     that computed the length would refuse a different set of payloads
///     from the code it models — and would be WRONG about it, because
///     the shipped refusal quotes the host's number.
///   * A `bin` payload's LENGTH (`VBin`'s second field), for the same
///     reason and because F* has no byte-array type that extracts to
///     `System.Byte[]`.
///   * A float's RENDERING (`VFloat`'s third field). `Value.describe`
///     quotes `string n` for a float; reproducing .NET's shortest
///     round-trippable float formatting in F* is a research project of
///     its own and would be a second implementation of exactly the kind
///     above. The float payload is therefore an opaque type parameter —
///     no combinator inspects a float, `asFloat` only moves one and
///     `asFloat32` decides on the WIDTH — and its text is carried.
///
/// Integers are the opposite case and are modelled CONCRETELY, as F*'s
/// unbounded `int`: the width rule is a statement about them, the
/// combinators only ever COMPARE one against a constant, and
/// `string_of_int` and `System.Int64.ToString()` agree on every value
/// either can hold.
///
/// Out of scope, stated here and on the README's last rung: the
/// bytes-to-`value` pass (Phase 785.A — bounded, not proved), the
/// System.Text.Json path (785.F deferred its carrier), and the
/// reflection fallback (`Read.Reader.Read`, outside by construction —
/// it walks an open type graph, so "total on every input" is not a
/// well-formed statement about it).

module RemotingDecode

(* ───────────────────────────────────────────────────────────────────
   Small closed types the model owns.

   `opt`, `outcome`, `pair` and `wire_entry` are declared here rather
   than taken from `FStar.Pervasives.Native` so that the EXTRACTION
   references `Prims` and nothing else — which is what lets the oracle
   host compile against a sixteen-line shim instead of an F* runtime.
   Each is isomorphic to the F# type it stands for, named in its
   comment.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: `'a option`.
type opt (a: Type0) =
  | ONone : opt a
  | OSome : item: a -> opt a

/// F#: `'a * 'b`.
type pair (a: Type0) (b: Type0) =
  | Pair : first: a -> second: b -> pair a b

/// F#: `DecodeError` (`Shared/Remoting/DecodeError.fs`). `path` is
/// OUTERMOST-FIRST, exactly as the F# record documents.
type refusal = {
  path: list string;
  expected: string;
  found: string;
}

/// F#: `Result<'T, DecodeError>`.
type outcome (a: Type0) =
  | Accepted : value: a -> outcome a
  | Refused : error: refusal -> outcome a

(* ───────────────────────────────────────────────────────────────────
   The value model — `Shared/Remoting/MsgPack/Value.fs`.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: `IntegerWidth` — the width class the FORMAT BYTE declared. This
/// is the wire's width, never the target's.
type integer_width =
  | Fixnum : integer_width
  | Bits8 : integer_width
  | Bits16 : integer_width
  | Bits32 : integer_width
  | Bits64 : integer_width

/// F#: `IntegerWidth.bits`. `Fixnum` answers 8 — its payload is seven
/// bits of magnitude (or five plus a sign), so it is contained in a
/// byte.
let width_bits (w: integer_width) : nat =
  match w with
  | Fixnum -> 8
  | Bits8 -> 8
  | Bits16 -> 16
  | Bits32 -> 32
  | Bits64 -> 64

/// F#: `IntegerWidth.label` — the stable lowercase label a refusal's
/// `Found` text carries.
let width_label (w: integer_width) : string =
  match w with
  | Fixnum -> "fixnum"
  | Bits8 -> "8-bit"
  | Bits16 -> "16-bit"
  | Bits32 -> "32-bit"
  | Bits64 -> "64-bit"

/// F#: `FloatWidth`.
type float_width =
  | Single : float_width
  | Double : float_width

/// F#: `Value` — one MessagePack value as this transport's wire can
/// carry it. NINE cases and no extension point, so a decoder's match
/// over them is exhaustive by construction. There is no `Ext` case,
/// and its absence is a finding rather than an omission: the
/// transport's reader has no ext family, so a case the one-pass reader
/// can never produce would be surface every consumer has to match on
/// and no payload could reach.
///
/// `raw` is the `bin` payload and `flt` the float payload — opaque
/// type parameters, because no combinator inspects either. See the
/// module header for why the lengths and the float's rendering are
/// carried as data.
noeq
type value (raw: Type0) (flt: Type0) =
  /// F#: `Value.Nil` — the absent value. A CASE, never a null.
  | VNil : value raw flt
  /// F#: `Value.Bool`.
  | VBool : payload: bool -> value raw flt
  /// F#: `Value.Int of value: int64 * width: IntegerWidth`.
  | VInt : payload: int -> width: integer_width -> value raw flt
  /// F#: `Value.UInt of value: uint64 * width: IntegerWidth`. Separate
  /// from `VInt` for the reason the F# is: a `uint64` above
  /// `Int64.MaxValue` has no `int64` representation at all.
  | VUInt : payload: nat -> width: integer_width -> value raw flt
  /// F#: `Value.Float of value: float * width: FloatWidth`, plus the
  /// host's own rendering of the payload.
  | VFloat : payload: flt -> width: float_width -> rendered: string -> value raw flt
  /// F#: `Value.Str`, plus the host's own `String.Length`.
  | VStr : payload: string -> length: nat -> value raw flt
  /// F#: `Value.Bin`, plus the host's own `Array.Length`.
  | VBin : payload: raw -> length: nat -> value raw flt
  /// F#: `Value.Arr`.
  | VArr : items: list (value raw flt) -> value raw flt
  /// F#: `Value.Map of entries: (Value * Value) list` — entries in WIRE
  /// ORDER, not sorted, so a value round-trips to the bytes it came
  /// from.
  ///
  /// Spelled with `pair` rather than as a mutually-recursive
  /// `wire_entry` type because the F# backend emits a mutual type
  /// group's `and` INDENTED, which F# rejects outright (`FS0010:
  /// Unexpected keyword 'and' in member definition`). One inductive
  /// under a parameterised type is the same shape the F# uses and
  /// extracts cleanly.
  | VMap : entries: list (pair (value raw flt) (value raw flt)) -> value raw flt

/// The length of a list. Needed at RUNTIME (every `describe` of a
/// container quotes it), so unlike `size` below it is extracted.
let rec count (#a: Type0) (xs: list a) : Tot nat (decreases xs) =
  match xs with
  | [] -> 0
  | _ :: tail -> 1 + count tail

(* ───────────────────────────────────────────────────────────────────
   The well-founded measure — F#: `Value.size`.

   `noextract_to "FSharp"` because it exists only for the proof: it
   appears in `element_at`'s refinement, refinements are erased at
   extraction, and shipping a second copy of it into the oracle would
   be dead code the F# compiler would warn about.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: `Value.size` — 1 for a leaf, 1 + the sizes of its children for
/// a container. Every subterm of a container is strictly smaller than
/// the container, which is the totality argument for every
/// structurally-recursive combinator below.
[@@ noextract_to "FSharp"]
let rec size (#raw #flt: Type0) (v: value raw flt) : Tot nat =
  match v with
  | VArr items -> 1 + size_items items
  | VMap entries -> 1 + size_entries entries
  | _ -> 1

and size_items (#raw #flt: Type0) (xs: list (value raw flt)) : Tot nat =
  match xs with
  | [] -> 0
  | head :: tail -> size head + size_items tail

and size_entries (#raw #flt: Type0) (es: list (pair (value raw flt) (value raw flt))) : Tot nat =
  match es with
  | [] -> 0
  | Pair k v :: tail -> size k + size v + size_entries tail

(* ───────────────────────────────────────────────────────────────────
   Describing a value, and the two fits predicates.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: `Value.describe` — what a refusal's `Found` field says. Shape
/// and, for the numeric cases, the value and its wire width; never a
/// deep rendering of a container.
let describe (#raw #flt: Type0) (v: value raw flt) : string =
  match v with
  | VNil -> "nil"
  | VBool true -> "bool true"
  | VBool false -> "bool false"
  | VInt n w -> width_label w ^ " signed integer " ^ string_of_int n
  | VUInt n w -> width_label w ^ " unsigned integer " ^ string_of_int n
  | VFloat _ Single rendered -> "float32 " ^ rendered
  | VFloat _ Double rendered -> "float64 " ^ rendered
  | VStr _ length -> "string of " ^ string_of_int length ^ " character(s)"
  | VBin _ length -> "bin of " ^ string_of_int length ^ " byte(s)"
  | VArr items -> "array of " ^ string_of_int (count items) ^ " element(s)"
  | VMap entries -> "map of " ^ string_of_int (count entries) ^ " entry(ies)"

/// F#: `Value.signedFits` — Phase 786's information rule over a signed
/// wire integer.
///
///   * A NEGATIVE value never decodes into an unsigned target, and
///     always fits a signed target whose minimum it clears.
///   * A source NO WIDER than the target always survives, sign
///     reinterpretation included — that is the format, not a loophole.
///   * A source WIDER than the target must fit the target's range.
///
/// The F# writes the last clause as `uint64 n <= hi` because its `hi`
/// is a `uint64`; the cast is reached only on the non-negative branch,
/// where it is the identity on value, so the model's `n <= hi` is the
/// same predicate.
///
/// `target_bits` of 0 names a target whose legal values are a DOMAIN
/// rather than a width: it disables the reinterpretation clause so the
/// range check applies to every source.
let signed_fits (source_bits: nat) (target_bits: nat) (lo: int) (hi: nat) (n: int) : bool =
  if n < 0 then lo < 0 && n >= lo
  else if source_bits <= target_bits then true
  else n <= hi

/// F#: `Value.unsignedFits`. A wire value that arrived unsigned is
/// never negative, so only the last two clauses apply.
let unsigned_fits (source_bits: nat) (target_bits: nat) (hi: nat) (n: nat) : bool =
  if source_bits <= target_bits then true else n <= hi

(* ───────────────────────────────────────────────────────────────────
   The refusal — `Shared/Remoting/DecodeError.fs`.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: `DecodeError.create` — a refusal at the root, no path.
let refusal_at (expected: string) (found: string) : refusal = {
  path = [];
  expected = expected;
  found = found;
}

/// F#: `DecodeError.under` — push one segment onto the FRONT of the
/// path, the annotation a decoder applies as a refusal unwinds through
/// it.
let under (segment: string) (e: refusal) : refusal = { e with path = segment :: e.path }

/// F#: `Decode.refuse` — a refusal at the value in hand.
let refuse (#raw #flt #a: Type0) (expected: string) (v: value raw flt) : outcome a =
  Refused (refusal_at expected (describe v))

/// F#: `Decode.refuseWith` — a refusal whose `Found` text the caller
/// supplies.
let refuse_with (#a: Type0) (expected: string) (found: string) : outcome a =
  Refused (refusal_at expected found)

(* ───────────────────────────────────────────────────────────────────
   The algebra — `Shared/Remoting/Decode.fs`.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: `type Decoder<'T> = Value -> Result<'T, DecodeError>`.
type decoder (raw: Type0) (flt: Type0) (a: Type0) = value raw flt -> outcome a

// ─── The monad / applicative ─────────────────────────────────────────

/// F#: `Decode.succeed` — ignores its input and succeeds. The head of
/// every applicative pipeline.
let succeed (#raw #flt #a: Type0) (x: a) : decoder raw flt a = fun _ -> Accepted x

/// F#: `Decode.fail` — ignores its input and refuses, naming what was
/// expected.
let fail (#raw #flt #a: Type0) (expected: string) : decoder raw flt a =
  fun v -> refuse expected v

/// F#: `Decode.map`.
let map (#raw #flt #a #b: Type0) (f: a -> b) (d: decoder raw flt a) : decoder raw flt b =
  fun v ->
    match d v with
    | Accepted x -> Accepted (f x)
    | Refused e -> Refused e

/// F#: `Decode.bind`.
let bind (#raw #flt #a #b: Type0) (f: a -> decoder raw flt b) (d: decoder raw flt a)
  : decoder raw flt b =
  fun v ->
    match d v with
    | Accepted x -> f x v
    | Refused e -> Refused e

/// F#: `Decode.apply` — one field decoder in an applicative pipeline.
/// Short-circuits on the FIRST refusal.
let apply (#raw #flt #a #b: Type0) (argument: decoder raw flt a) (fn: decoder raw flt (a -> b))
  : decoder raw flt b =
  fun v ->
    match fn v with
    | Refused e -> Refused e
    | Accepted f ->
      (match argument v with
       | Refused e -> Refused e
       | Accepted x -> Accepted (f x))

/// The applicative pipeline step, spelled so the model reads in the
/// same order as the F# (`succeed ctor |> Decode.apply (field …)`).
///
/// A plain `let` rather than `unfold`, and that is a CONSTRAINT rather
/// than a preference: an `unfold` inlines the whole body at every step,
/// and the F# backend then emits a four-deep pipeline as four nested
/// `match` expressions whose cases start at column 0 — which F#'s
/// offside rule rejects however the indentation flags are set. As an
/// ordinary function it extracts as four flat calls.
let ( |>> ) (#raw #flt #a #b: Type0) (fn: decoder raw flt (a -> b)) (arg: decoder raw flt a)
  : decoder raw flt b =
  apply arg fn

// ─── Scalars ─────────────────────────────────────────────────────────

/// F#: `Decode.asBool`.
let as_bool (#raw #flt: Type0) : decoder raw flt bool =
  fun v ->
    match v with
    | VBool b -> Accepted b
    | _ -> refuse "bool" v

/// F#: `Decode.asUnit` — the `nil` value and nothing else. `unit` rides
/// this arm: the writer emits `unit` as `nil`.
let as_unit (#raw #flt: Type0) : decoder raw flt unit =
  fun v ->
    match v with
    | VNil -> Accepted ()
    | _ -> refuse "nil" v

/// F#: `Decode.asString`.
let as_string (#raw #flt: Type0) : decoder raw flt string =
  fun v ->
    match v with
    | VStr text _ -> Accepted text
    | _ -> refuse "string" v

/// F#: `Decode.asChar` — a one-character string, with the length
/// requirement a REFUSAL rather than a silent `str.[0]`.
///
/// `pick` is the host's own `text.[0]`: the model decides the length
/// discipline and the refusal, and hands the one-character payload to
/// the host to index, because an F* model has no UTF-16 code unit.
let as_char_with (#raw #flt #a: Type0) (pick: string -> a) : decoder raw flt a =
  fun v ->
    match v with
    | VStr text length ->
      if length = 1 then Accepted (pick text)
      else
        refuse_with
          "a one-character string"
          ("string of " ^ string_of_int length ^ " character(s)")
    | _ -> refuse "char" v

/// F#: `Decode.integer` — the private worker every integer arm is
/// built from. Each applies Phase 786's information rule against the
/// SOURCE width the format byte declared, so a same-width
/// reinterpretation survives and a genuine narrowing refuses.
let integer
  (#raw #flt #a: Type0)
  (type_name: string)
  (target_bits: nat)
  (lo: int)
  (hi: nat)
  (of_signed: int -> a)
  (of_unsigned: nat -> a)
  : decoder raw flt a =
  fun v ->
    match v with
    | VInt n width ->
      if signed_fits (width_bits width) target_bits lo hi n then Accepted (of_signed n)
      else refuse_with type_name ("out-of-range integer " ^ string_of_int n)
    | VUInt n width ->
      if unsigned_fits (width_bits width) target_bits hi n then Accepted (of_unsigned n)
      else refuse_with type_name ("out-of-range integer " ^ string_of_int n)
    | _ -> refuse type_name v

/// The identity carriers. The F# arms cast (`int32 n`); the model
/// yields the mathematical value and the host casts, which is faithful
/// precisely BECAUSE of `lemma_accept_is_fits` below — the cast is
/// reached only where the value fits the target, so it is lossless.
let keep_signed (n: int) : int = n
let keep_unsigned (n: nat) : int = n

/// F#: `Decode.asInt32`.
let as_int32 (#raw #flt: Type0) : decoder raw flt int =
  integer "Int32" 32 (-2147483648) 2147483647 keep_signed keep_unsigned

/// F#: `Decode.asInt64`.
let as_int64 (#raw #flt: Type0) : decoder raw flt int =
  integer "Int64" 64 (-9223372036854775808) 9223372036854775807 keep_signed keep_unsigned

/// F#: `Decode.asInt16`.
let as_int16 (#raw #flt: Type0) : decoder raw flt int =
  integer "Int16" 16 (-32768) 32767 keep_signed keep_unsigned

/// F#: `Decode.asSByte`.
let as_sbyte (#raw #flt: Type0) : decoder raw flt int =
  integer "SByte" 8 (-128) 127 keep_signed keep_unsigned

/// F#: `Decode.asByte`.
let as_byte (#raw #flt: Type0) : decoder raw flt int =
  integer "Byte" 8 0 255 keep_signed keep_unsigned

/// F#: `Decode.asUInt16`.
let as_uint16 (#raw #flt: Type0) : decoder raw flt int =
  integer "UInt16" 16 0 65535 keep_signed keep_unsigned

/// F#: `Decode.asUInt32`.
let as_uint32 (#raw #flt: Type0) : decoder raw flt int =
  integer "UInt32" 32 0 4294967295 keep_signed keep_unsigned

/// F#: `Decode.asUInt64`.
let as_uint64 (#raw #flt: Type0) : decoder raw flt int =
  integer "UInt64" 64 0 18446744073709551615 keep_signed keep_unsigned

/// F#: `Decode.asTimeSpan` — a tick count on the wire, so it decodes
/// through the `Int64` arm and inherits its width rule exactly. `make`
/// is the host's `TimeSpan` constructor.
let as_time_span_with (#raw #flt #a: Type0) (make: int -> a) : decoder raw flt a =
  integer "TimeSpan" 64 (-9223372036854775808) 9223372036854775807 make (fun n -> make n)

/// F#: `Decode.asFloat` — accepts EITHER IEEE width, because a
/// `float32` widened to a `float` is exact and refusing it would refuse
/// well-formed traffic.
let as_float (#raw #flt: Type0) : decoder raw flt flt =
  fun v ->
    match v with
    | VFloat n _ _ -> Accepted n
    | _ -> refuse "Double" v

/// F#: `Decode.asFloat32` — accepts ONLY a `float32` source. A `float`
/// narrowed to a `float32` is the lossy direction and is refused, for
/// the reason `asInt32` refuses a wide integer.
let as_float32 (#raw #flt: Type0) : decoder raw flt flt =
  fun v ->
    match v with
    | VFloat n Single _ -> Accepted n
    | VFloat _ Double rendered ->
      refuse_with "Single" ("float64 " ^ rendered ^ ", which a Single cannot carry without loss")
    | _ -> refuse "Single" v

/// F#: `Decode.asBytes` — the `bin` family, returned as handed over.
let as_bytes (#raw #flt: Type0) : decoder raw flt raw =
  fun v ->
    match v with
    | VBin payload _ -> Accepted payload
    | _ -> refuse "Byte[]" v

/// F#: `Decode.asGuid` — sixteen `bin` bytes, with the length a
/// refusal rather than a constructor throw. `make` is the host's `Guid`
/// constructor.
let as_guid_with (#raw #flt #a: Type0) (make: raw -> a) : decoder raw flt a =
  fun v ->
    match v with
    | VBin payload length ->
      if length = 16 then Accepted (make payload)
      else refuse_with "Guid" ("bin of " ^ string_of_int length ^ " byte(s), not 16")
    | _ -> refuse "Guid" v

// ─── Containers ──────────────────────────────────────────────────────

/// F#: `Decode.items` — the elements of an array value.
let items (#raw #flt: Type0) : decoder raw flt (list (value raw flt)) =
  fun v ->
    match v with
    | VArr elements -> Accepted elements
    | _ -> refuse "array" v

/// F#: `Decode.exactly` — an array of exactly `arity` elements. The
/// arity check turns "the writer emitted five fields and this decoder
/// reads six" into a named refusal rather than a missing element read
/// as absent.
let exactly (#raw #flt: Type0) (arity: nat) : decoder raw flt (list (value raw flt)) =
  fun v ->
    let expected = "an array of " ^ string_of_int arity ^ " element(s)" in
    match v with
    | VArr elements ->
      if count elements = arity then Accepted elements
      else
        refuse_with
          expected
          ("an array of " ^ string_of_int (count elements) ^ " element(s)")
    | _ -> refuse expected v

/// F#: `List.tryItem`.
let rec try_item (#raw #flt: Type0) (position: nat) (xs: list (value raw flt))
  : Tot (opt (value raw flt)) (decreases xs) =
  match xs with
  | [] -> ONone
  | head :: tail -> if position = 0 then OSome head else try_item (position - 1) tail

[@@ noextract_to "FSharp"]
let rec lemma_try_item_smaller (#raw #flt: Type0) (position: nat) (xs: list (value raw flt))
  : Lemma
      (ensures
        (match try_item position xs with
         | OSome x -> size x <= size_items xs
         | ONone -> True))
      (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: tail -> if position = 0 then () else lemma_try_item_smaller (position - 1) tail

/// **The termination measure IS the theorem.** The accessor that
/// `index` and `field` are both built from carries, in its RETURN
/// TYPE, the fact that whatever it hands back is strictly smaller than
/// what it was given. So a decoder that descends through it cannot
/// recur forever — not by a depth counter, but by the value model's own
/// well-founded measure.
///
/// The Phase 787 shard states this refinement on `field`. It cannot
/// live there and type-check: `field` returns the SUB-DECODER'S
/// outcome, whose payload is a decoded `'T` and not a `value` at all,
/// so there is nothing left to measure by the time `field` returns.
/// This is where the fact belongs and where every descent consumes it.
let element_at (#raw #flt: Type0) (position: nat) (v: value raw flt)
  : r: opt (value raw flt) { OSome? r ==> size (OSome?.item r) < size v } =
  match v with
  | VArr elements ->
    lemma_try_item_smaller position elements;
    try_item position elements
  | _ -> ONone

/// F#: `Decode.index` — decode element `position` of an array,
/// annotating any refusal beneath it with `[position]`.
let index (#raw #flt #a: Type0) (position: nat) (d: decoder raw flt a) : decoder raw flt a =
  fun v ->
    let expected = "an array with an element at index " ^ string_of_int position in
    match v with
    | VArr elements ->
      (match try_item position elements with
       | OSome element ->
         (match d element with
          | Accepted x -> Accepted x
          | Refused e -> Refused (under ("[" ^ string_of_int position ^ "]") e))
       | ONone ->
         refuse_with
           expected
           ("an array of " ^ string_of_int (count elements) ^ " element(s)"))
    | _ -> refuse expected v

/// F#: `Decode.field` — decode element `position` of an array,
/// annotating any refusal beneath it with the field's NAME. The
/// combinator that gives a POSITIONAL wire a named path: the writer
/// emits a record as an array of its fields in declaration order, so
/// there are no keys to look a field up by, and the path is a property
/// of the DECODER rather than of the wire.
let field (#raw #flt #a: Type0) (name: string) (position: nat) (d: decoder raw flt a)
  : decoder raw flt a =
  fun v ->
    let expected =
      "a record with a `" ^ name ^ "` field at index " ^ string_of_int position
    in
    match v with
    | VArr elements ->
      (match try_item position elements with
       | OSome element ->
         (match d element with
          | Accepted x -> Accepted x
          | Refused e -> Refused (under name e))
       | ONone ->
         refuse_with
           expected
           ("an array of " ^ string_of_int (count elements) ^ " element(s)"))
    | _ -> refuse expected v

/// F#: `List.rev` — the accumulator flip both walks end with.
let rec rev_onto (#a: Type0) (xs: list a) (acc: list a) : Tot (list a) (decreases xs) =
  match xs with
  | [] -> acc
  | head :: tail -> rev_onto tail (head :: acc)

/// The walk `Decode.list` performs. Structural and short-circuiting:
/// it descends only into elements — each strictly smaller than the
/// array by `size` — and stops at the first refusal, annotating it
/// with the element's index.
let rec walk_items
  (#raw #flt #a: Type0)
  (element: decoder raw flt a)
  (position: nat)
  (remaining: list (value raw flt))
  (accumulated: list a)
  : Tot (outcome (list a)) (decreases remaining) =
  match remaining with
  | [] -> Accepted (rev_onto accumulated [])
  | head :: tail ->
    (match element head with
     | Accepted x -> walk_items element (position + 1) tail (x :: accumulated)
     | Refused e -> Refused (under ("[" ^ string_of_int position ^ "]") e))

/// F#: `Decode.list`.
let list_of (#raw #flt #a: Type0) (element: decoder raw flt a) : decoder raw flt (list a) =
  fun v ->
    match v with
    | VArr elements -> walk_items element 0 elements []
    | _ -> refuse "array" v

/// The walk `Decode.entries` performs.
let rec walk_entries
  (#raw #flt #k #w: Type0)
  (key: decoder raw flt k)
  (entry: decoder raw flt w)
  (position: nat)
  (remaining: list (pair (value raw flt) (value raw flt)))
  (accumulated: list (pair k w))
  : Tot (outcome (list (pair k w))) (decreases remaining) =
  match remaining with
  | [] -> Accepted (rev_onto accumulated [])
  | Pair k v :: tail ->
    (match key k with
     | Refused e -> Refused (under ("[" ^ string_of_int position ^ "].key") e)
     | Accepted decoded_key ->
       (match entry v with
        | Refused e -> Refused (under ("[" ^ string_of_int position ^ "].value") e)
        | Accepted decoded_value ->
          walk_entries key entry (position + 1) tail (Pair decoded_key decoded_value :: accumulated)))

/// F#: `Decode.entries` — every entry of a map value, through one key
/// decoder and one value decoder, in WIRE ORDER.
let entries_of (#raw #flt #k #w: Type0) (key: decoder raw flt k) (entry: decoder raw flt w)
  : decoder raw flt (list (pair k w)) =
  fun v ->
    match v with
    | VMap pairs -> walk_entries key entry 0 pairs []
    | _ -> refuse "map" v

// ─── Unions ──────────────────────────────────────────────────────────

/// F#: `Decode.UnionCase<'T> = Value option -> Result<'T, DecodeError>`.
///
/// The payload is an OPTION rather than a value, and the distinction is
/// load-bearing: a case with no fields is written as `[tag]` and has no
/// payload slot at all, while a case with one field writes the field
/// DIRECTLY into the second slot and a case with several writes them as
/// an inner array there. A single-field case whose field is itself an
/// array is therefore indistinguishable from a multi-field case by
/// inspection — only the case's own arity tells them apart.
type union_case (raw: Type0) (flt: Type0) (a: Type0) = opt (value raw flt) -> outcome a

/// F#: `Decode.case0` — a union case carrying no fields. Refuses a
/// payload: a `[tag; field]` term decoded as a zero-field case would
/// silently drop the field.
let case0 (#raw #flt #a: Type0) (x: a) : union_case raw flt a =
  fun carried ->
    match carried with
    | ONone -> Accepted x
    | OSome payload -> refuse_with "a union case with no fields" (describe payload)

/// F#: `Decode.payload` — a union case carrying a payload. Covers both
/// the single-field shape and the several-field shape.
let payload (#raw #flt #a: Type0) (d: decoder raw flt a) : union_case raw flt a =
  fun carried ->
    match carried with
    | OSome v -> d v
    | ONone -> refuse_with "a union case carrying a payload" "a union case with no payload"

/// F#: `Decode.union` — dispatch on the tag the writer emits.
///
/// `cases` answers for a tag it recognises and `ONone` for one it does
/// not: an unrecognised tag is a refusal naming the type and the tag,
/// never a fallback case. That is the arm the reflection reader cannot
/// make total — it resolves a tag with `Array.find`, which throws.
let union (#raw #flt #a: Type0) (type_name: string) (cases: int -> opt (union_case raw flt a))
  : decoder raw flt a =
  fun v ->
    // The case decoder's refusal is carried out UNANNOTATED: a union is
    // one wire term, and pushing `Option[1]` onto the path of every
    // `Some` field would bury the field name the reader came for.
    let dispatch (tag: int) (carried: opt (value raw flt)) : outcome a =
      match cases tag with
      | OSome decode_case -> decode_case carried
      | ONone ->
        refuse_with type_name ("union tag " ^ string_of_int tag ^ ", which names no case")
    in
    match v with
    | VArr [ tag ] ->
      (match as_int32 tag with
       | Refused e -> Refused e
       | Accepted t -> dispatch t ONone)
    | VArr [ tag; carried ] ->
      (match as_int32 tag with
       | Refused e -> Refused e
       | Accepted t -> dispatch t (OSome carried))
    | _ -> refuse (type_name ^ " (a union term of [tag] or [tag; payload])") v

/// F#: `Decode.stringEnum` — a union attributed `[<StringEnum>]` is
/// written as its CASE NAME rather than as a tag. It is the ATTRIBUTE
/// that selects this shape, not the absence of fields, so a field-less
/// union still takes `union` with `case0` arms.
let rec try_pick_case (#a: Type0) (cases: list (pair string a)) (name: string)
  : Tot (opt a) (decreases cases) =
  match cases with
  | [] -> ONone
  | Pair label case :: tail -> if label = name then OSome case else try_pick_case tail name

let string_enum (#raw #flt #a: Type0) (type_name: string) (cases: list (pair string a))
  : decoder raw flt a =
  fun v ->
    match v with
    | VStr name _ ->
      (match try_pick_case cases name with
       | OSome case -> Accepted case
       | ONone ->
         refuse_with type_name ("case name `" ^ name ^ "`, which names no case"))
    | _ -> refuse type_name v

/// F#: `Decode.option` — an ordinary two-case union on this wire:
/// `None` is tag 0 with no fields, `Some` is tag 1 carrying the value.
let as_option (#raw #flt #a: Type0) (inner: decoder raw flt a) : decoder raw flt (opt a) =
  union
    "Option"
    (fun tag ->
      if tag = 0 then OSome (case0 ONone)
      else if tag = 1 then OSome (payload (map (fun x -> OSome x) inner))
      else ONone)

// ─── Composite scalars written as arrays ─────────────────────────────

/// F#: `Decode.asDateTime` — `[ticks; kind]`. `make` is the host's
/// construction, which also applies the total kind rule (1 is UTC, 2 is
/// Local, anything else Unspecified) so the two paths agree on a
/// payload carrying a kind byte neither enum value names.
let as_date_time_with (#raw #flt #a: Type0) (make: int -> int -> a) : decoder raw flt a =
  fun v ->
    match exactly 2 v with
    | Refused e -> Refused (under "DateTime" e)
    | Accepted _ -> (succeed make |>> field "Ticks" 0 as_int64 |>> field "Kind" 1 as_int64) v

/// F#: `Decode.asDateTimeOffset` — `[ticks; offsetMinutes]`.
let as_date_time_offset_with (#raw #flt #a: Type0) (make: int -> int -> a) : decoder raw flt a =
  fun v ->
    match exactly 2 v with
    | Refused e -> Refused (under "DateTimeOffset" e)
    | Accepted _ ->
      (succeed make |>> field "Ticks" 0 as_int64 |>> field "OffsetMinutes" 1 as_int64) v

/// F#: `Decode.asDecimal` — the four 32-bit words, each written
/// through `write32bitNumber`, so a NEGATIVE word arrives as a `uint32`
/// and the `Int32` target recovers the sign. That is the same-width
/// reinterpretation Phase 786's second clause admits, and refusing it
/// would refuse every negative decimal the corpus carries.
let as_decimal_with (#raw #flt #a: Type0) (make: int -> int -> int -> int -> a)
  : decoder raw flt a =
  fun v ->
    match exactly 4 v with
    | Refused e -> Refused (under "Decimal" e)
    | Accepted _ ->
      (succeed make
       |>> index 0 as_int32
       |>> index 1 as_int32
       |>> index 2 as_int32
       |>> index 3 as_int32)
        v

/// F#: `Decode.run`.
let run (#raw #flt #a: Type0) (d: decoder raw flt a) (v: value raw flt) : outcome a = d v

(* ───────────────────────────────────────────────────────────────────
   THE THEOREMS
   ─────────────────────────────────────────────────────────────────── *)

// ─── Totality ────────────────────────────────────────────────────────

/// **`decode_total`, for every decoder at once.** Not "for every
/// combinator" one lemma at a time: the statement quantifies over ALL
/// decoders, including any a consumer or Phase 69k's generator builds
/// from the combinators above, because it rests on two facts neither a
/// combinator nor a generated pipeline can escape — `decoder` is a
/// `Tot` arrow, so the checker has already rejected anything partial or
/// non-terminating, and `outcome` has exactly two cases, so there is no
/// third state to reach.
///
/// This is why the phase models the algebra rather than auditing it:
/// the property is compositional, and a proof about the combinators is
/// therefore a proof about everything built from them.
let decode_total (#raw #flt #a: Type0) (d: decoder raw flt a) (v: value raw flt)
  : Lemma (Accepted? (d v) \/ Refused? (d v)) = ()

// ─── Structural characterisation ─────────────────────────────────────
//
// WHICH outcome, for which input. This is the half that could have been
// false, and the half a reviewer should read: each lemma pins the exact
// refusal — expected text, found text and path — that the F# produces,
// so the classification is exhaustive rather than merely non-empty.

let lemma_as_bool_characterised (#raw #flt: Type0) (v: value raw flt)
  : Lemma
      (match v with
       | VBool b -> as_bool v == Accepted b
       | _ -> as_bool v == Refused (refusal_at "bool" (describe v))) = ()

let lemma_as_string_characterised (#raw #flt: Type0) (v: value raw flt)
  : Lemma
      (match v with
       | VStr text _ -> as_string v == Accepted text
       | _ -> as_string v == Refused (refusal_at "string" (describe v))) = ()

let lemma_as_unit_characterised (#raw #flt: Type0) (v: value raw flt)
  : Lemma
      (match v with
       | VNil -> as_unit v == Accepted ()
       | _ -> as_unit v == Refused (refusal_at "nil" (describe v))) = ()

/// The integer arm, characterised: an accept is EXACTLY the fits
/// predicate holding, and a refusal is exactly its failure. So "the
/// width rule decides" is a theorem rather than a reading of the code.
let lemma_integer_characterised
  (#raw #flt #a: Type0)
  (type_name: string)
  (target_bits: nat)
  (lo: int)
  (hi: nat)
  (of_signed: int -> a)
  (of_unsigned: nat -> a)
  (v: value raw flt)
  : Lemma
      (let d = integer type_name target_bits lo hi of_signed of_unsigned in
       match v with
       | VInt n w ->
         (Accepted? (d v) <==> signed_fits (width_bits w) target_bits lo hi n)
       | VUInt n w ->
         (Accepted? (d v) <==> unsigned_fits (width_bits w) target_bits hi n)
       | _ -> Refused? (d v)) = ()

/// `asFloat32` refuses on the WIDTH and nothing else — it never
/// inspects the payload, which is why the payload can be opaque.
let lemma_as_float32_decides_on_width (#raw #flt: Type0) (v: value raw flt)
  : Lemma
      (match v with
       | VFloat n Single _ -> as_float32 v == Accepted n
       | _ -> Refused? (as_float32 v)) = ()

/// `field` on a non-array refuses by NAME, and on an array short of
/// the position refuses by name — never reads off the end. This is the
/// `missing-field-record` mutation Phase 784 measured escaping the
/// reflection reader as an `IndexOutOfRangeException`.
let lemma_field_refuses_short_array
  (#raw #flt #a: Type0)
  (name: string)
  (position: nat)
  (d: decoder raw flt a)
  (v: value raw flt)
  : Lemma
      (requires (match v with
                 | VArr elements -> ONone? (try_item position elements)
                 | _ -> true))
      (ensures Refused? (field name position d v)) = ()

/// A union term of any shape but `[tag]` or `[tag; payload]` refuses,
/// and an unrecognised tag refuses. There is no fallback case and no
/// `Array.find`.
let lemma_union_refuses_unknown_tag
  (#raw #flt #a: Type0)
  (type_name: string)
  (cases: int -> opt (union_case raw flt a))
  (v: value raw flt)
  : Lemma
      (requires
        (match v with
         | VArr [ VInt n _ ] -> ONone? (cases n)
         | _ -> false))
      (ensures Refused? (union type_name cases v)) = ()

// ─── Phase 786's information rule, as three lemmas ───────────────────
//
// The rule is three sentences in `Value.fs`'s comment and three `if`
// branches in `signedFits`. These are those three sentences, stated so
// that a change to either one goes red here.

/// **Clause 1 — a NEGATIVE value never decodes into an unsigned
/// target.** An unsigned target is one whose `lo` is not negative.
let lemma_negative_never_unsigned (source_bits target_bits: nat) (lo: int) (hi: nat) (n: int)
  : Lemma (requires n < 0 /\ lo >= 0)
          (ensures not (signed_fits source_bits target_bits lo hi n)) = ()

/// **Clause 1' — and a negative value fits a signed target exactly
/// when it clears that target's minimum**, whatever the widths are.
let lemma_negative_fits_iff_clears_minimum
  (source_bits target_bits: nat) (lo: int) (hi: nat) (n: int)
  : Lemma (requires n < 0 /\ lo < 0)
          (ensures signed_fits source_bits target_bits lo hi n <==> n >= lo) = ()

/// **Clause 2 — a source NO WIDER than its target always survives**,
/// sign reinterpretation included. This is the clause that makes
/// `writeSByte`'s `-128y` (on the wire as `uint8 128`) and
/// `writeDecimal`'s negative words decodable, and the one a
/// signed-range rule would get wrong.
let lemma_no_wider_always_survives
  (source_bits target_bits: nat) (lo: int) (hi: nat) (n: int)
  : Lemma (requires n >= 0 /\ source_bits <= target_bits)
          (ensures signed_fits source_bits target_bits lo hi n) = ()

let lemma_no_wider_always_survives_unsigned
  (source_bits target_bits: nat) (hi: nat) (n: nat)
  : Lemma (requires source_bits <= target_bits)
          (ensures unsigned_fits source_bits target_bits hi n) = ()

/// **Clause 3 — a source WIDER than its target must fit the target's
/// range**, and nothing else is consulted.
let lemma_wider_must_fit
  (source_bits target_bits: nat) (lo: int) (hi: nat) (n: int)
  : Lemma (requires n >= 0 /\ source_bits > target_bits)
          (ensures signed_fits source_bits target_bits lo hi n <==> n <= hi) = ()

let lemma_wider_must_fit_unsigned (source_bits target_bits: nat) (hi: nat) (n: nat)
  : Lemma (requires source_bits > target_bits)
          (ensures unsigned_fits source_bits target_bits hi n <==> n <= hi) = ()

// ─── What an accept actually licenses ────────────────────────────────

/// The widest SIGNED payload a width class can carry. `Fixnum`'s real
/// range is narrower still (a positive fixint is 0–127 and a negative
/// fixint −32 to −1), so treating it as eight bits is a sound
/// over-approximation: every value the reader can put in a `Fixnum`
/// satisfies this, and the lemmas below are therefore true of more
/// values than the reader can produce, never fewer.
[@@ noextract_to "FSharp"]
let signed_floor (w: integer_width) : int =
  match w with
  | Fixnum -> -128
  | Bits8 -> -128
  | Bits16 -> -32768
  | Bits32 -> -2147483648
  | Bits64 -> -9223372036854775808

[@@ noextract_to "FSharp"]
let signed_ceiling (w: integer_width) : nat =
  match w with
  | Fixnum -> 127
  | Bits8 -> 127
  | Bits16 -> 32767
  | Bits32 -> 2147483647
  | Bits64 -> 9223372036854775807

[@@ noextract_to "FSharp"]
let unsigned_ceiling (w: integer_width) : nat =
  match w with
  | Fixnum -> 255
  | Bits8 -> 255
  | Bits16 -> 65535
  | Bits32 -> 4294967295
  | Bits64 -> 18446744073709551615

/// **A value the READER can actually produce.** The one-pass
/// bytes-to-`value` pass reads a payload out of a field of the width
/// its format byte declared, so the payload is within that width by
/// construction — but the value model does not ENFORCE it (a `value`
/// built by hand can carry `VInt 99999999999 Bits8`), and that gap is
/// named here rather than assumed away. Every lemma that needs it says
/// so in its `requires`.
[@@ noextract_to "FSharp"]
let value_wf (#raw #flt: Type0) (v: value raw flt) : bool =
  match v with
  | VInt n w -> signed_floor w <= n && n <= signed_ceiling w
  | VUInt n w -> n <= unsigned_ceiling w
  | _ -> true

/// **What an `asInt32` accept licenses — and, sharply, what it does
/// not.** On a reader-producible value, an accept implies the payload
/// carries no more than THIRTY-TWO BITS of information. It does NOT
/// imply the payload sits in `Int32`'s signed range, and that is the
/// whole of Phase 786's finding: `writeDecimal`'s negative words arrive
/// as `uint32 0xFFFFFFFF`, the host's `int32` cast REINTERPRETS them,
/// and a decoder that demanded the signed range would refuse every
/// negative decimal the Phase 784 corpus carries.
///
/// So the bound proved here is the union of the signed and unsigned
/// 32-bit ranges — which is exactly the set on which the host's cast is
/// a bijection, and therefore exactly the licence the cast needs.
let lemma_int32_accept_is_32_bit_information (#raw #flt: Type0) (v: value raw flt)
  : Lemma
      (requires value_wf v)
      (ensures
        (match as_int32 v with
         | Accepted n -> -2147483648 <= n /\ n <= 4294967295
         | Refused _ -> True)) =
  match v with
  | VInt _ w -> (match w with | Fixnum -> () | Bits8 -> () | Bits16 -> () | Bits32 -> () | Bits64 -> ())
  | VUInt _ w -> (match w with | Fixnum -> () | Bits8 -> () | Bits16 -> () | Bits32 -> () | Bits64 -> ())
  | _ -> ()

/// **The rule is TOTAL over the width classes**: for every pair of a
/// source class and a target width, the predicate decides. There is no
/// pair it is silent about — which is what "the failure classification
/// is exhaustive" means for the integer arms.
let lemma_width_rule_decides (w: integer_width) (target_bits: nat) (lo: int) (hi: nat) (n: int)
  : Lemma (signed_fits (width_bits w) target_bits lo hi n
           \/ not (signed_fits (width_bits w) target_bits lo hi n)) = ()

(* ───────────────────────────────────────────────────────────────────
   A reference API-record vocabulary, its encoder, and the round trip.

   The records are the shape a remoting API record actually has — a
   nested record, a string, a bounded integer, a flag — and the encoder
   is `Write.writeRecord`'s shape: an array of the fields in
   DECLARATION ORDER, positionally.
   ─────────────────────────────────────────────────────────────────── *)

/// An `int` in `Int32`'s range. The bound is what makes the round trip
/// TRUE rather than nearly true: a weight outside it could not be
/// encoded as a `Bits32` wire integer and decoded back unchanged.
type i32 = n: int { -2147483648 <= n /\ n <= 2147483647 }

type ref_address = {
  line1: string;
  postcode: string;
  country: string;
}

type ref_consignment = {
  reference: string;
  origin: ref_address;
  weight: i32;
  urgent: bool;
}

/// The encoder. `str_len` is the host's own string-length function,
/// supplied rather than modelled for the reason the module header
/// gives. The round-trip theorem holds for ANY `str_len` — which is
/// exactly the right strength, because a decoder that consulted the
/// length when reading a string would be one this theorem could not
/// prove.
let encode_address (#raw #flt: Type0) (str_len: string -> nat) (a: ref_address)
  : value raw flt =
  VArr [
    VStr a.line1 (str_len a.line1);
    VStr a.postcode (str_len a.postcode);
    VStr a.country (str_len a.country);
  ]

let encode_consignment (#raw #flt: Type0) (str_len: string -> nat) (c: ref_consignment)
  : value raw flt =
  VArr [
    VStr c.reference (str_len c.reference);
    encode_address str_len c.origin;
    VInt c.weight Bits32;
    VBool c.urgent;
  ]

/// The decoder, in exactly the applicative shape Phase 69k's generator
/// emits from a record's own field list: `succeed ctor` then one
/// `field` per field, in declaration order.
let decode_address (#raw #flt: Type0) : decoder raw flt ref_address =
  succeed (fun l p c -> { line1 = l; postcode = p; country = c })
  |>> field "Line1" 0 as_string
  |>> field "Postcode" 1 as_string
  |>> field "Country" 2 as_string

/// The host's `int32` cast, as the model expresses it. `as_int32`
/// admits any payload carrying no more than 32 bits of information
/// (`lemma_int32_accept_is_32_bit_information`), and the host's cast
/// REINTERPRETS rather than truncates — so the model reinterprets by
/// the same arithmetic, and the round trip below is a statement about
/// what the host actually does.
let reinterpret_i32 (n: int) : i32 =
  if n < -2147483648 then 0
  else if n <= 2147483647 then n
  else if n <= 4294967295 then n - 4294967296
  else 0

let decode_consignment (#raw #flt: Type0) : decoder raw flt ref_consignment =
  succeed (fun r o w u -> {
    reference = r;
    origin = o;
    weight = reinterpret_i32 w;
    urgent = u;
  })
  |>> field "Reference" 0 as_string
  |>> field "Origin" 1 decode_address
  |>> field "Weight" 2 as_int32
  |>> field "Urgent" 3 as_bool

/// **`decode_record_total`** — the record decoder is total, which is
/// `decode_total` at this instance and is recorded separately because
/// the shard names it.
let decode_record_total (#raw #flt: Type0) (v: value raw flt)
  : Lemma (Accepted? (decode_consignment v) \/ Refused? (decode_consignment v)) = ()

/// **`decode_record_wf`** — a WELL-FORMED encoding always decodes.
/// Not "decodes to something": the encoding of any consignment is
/// accepted, so the decoder can never refuse traffic its own encoder
/// produced.
let decode_record_wf (#raw #flt: Type0) (str_len: string -> nat) (c: ref_consignment)
  : Lemma (Accepted? (decode_consignment #raw #flt (encode_consignment str_len c))) = ()

/// **`decode_encode_roundtrip`** — and it decodes back to the value it
/// started from. `decode (encode v) == Ok v`, over the reference
/// vocabulary, for every string-length function the host might supply.
let decode_encode_roundtrip (#raw #flt: Type0) (str_len: string -> nat) (c: ref_consignment)
  : Lemma (decode_consignment #raw #flt (encode_consignment str_len c) == Accepted c) = ()

/// The same for the nested record on its own, so a failure localises.
let decode_encode_roundtrip_address (#raw #flt: Type0) (str_len: string -> nat) (a: ref_address)
  : Lemma (decode_address #raw #flt (encode_address str_len a) == Accepted a) = ()
