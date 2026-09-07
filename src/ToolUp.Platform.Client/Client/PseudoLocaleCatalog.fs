// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── The pseudo-locale MessageCatalog (Phase 758) ─────────────────────
//
// Phase 751 finished the string sweep; nothing kept it finished. The
// next module view written with a bare literal regresses silently,
// because English-rendered-as-English is indistinguishable from
// English-that-never-reached-the-catalog. This module removes that
// ambiguity by making the two look different: under the pseudo-locale
// every string the CATALOG serves is accented, padded and bracketed, so
// a literal that never went through the catalog stands out as the one
// plain-looking string on the screen.
//
// ─── What this reuses, and what Phase 179 left behind ─────────────────
//
// Phase 179 built the same two artefacts for the Phase 12a `Translations`
// substrate that Phase 444 superseded. Of that machinery:
//
//   * `ToolUp.Platform.PseudoLocale` (Core/Shared) — the per-string
//     `transform`, the `qps-ploc` tag, `isActive` — SURVIVES UNCHANGED
//     and is what this module calls. It is a pure `string -> string`
//     over BCL strings, so it was never coupled to the map substrate,
//     and re-deriving a second accent/pad/bracket transform here would
//     be two spellings of one contract.
//   * `I18nCoverage.audit` / `.validator` (Core) — the key-versus-locale
//     coverage audit — is RETAINED WHERE IT IS, not ported. It answers a
//     question the `MessageCatalog` era cannot ask: which KEYS of a
//     `Map<string, Map<LocaleCode, string>>` are missing a locale. A
//     record has no missing-key state — a translation that omits a field
//     keeps the built-in English string, by construction — so there is
//     nothing here for it to audit. It still governs the live
//     `Translations` surface (the hosted-tree `IHostI18nResolver` of
//     Phase 275, and `I18nDefaults`' `sdk.*` / `ApiError` seed), which is
//     a different substrate rather than a second copy of this one.
//
// So there is exactly ONE pseudo-localiser in the tree (Core's
// `transform`) and exactly one coverage gate per substrate, each over
// the surface it can actually see.
//
// ─── Why the derivation is reflective ─────────────────────────────────
//
// `MessageCatalog` is ~1,200 fields across ~60 nested section records.
// A hand-written pseudo-catalog would be a second full copy of the
// English one, and the field added tomorrow would silently keep its
// English value — which is precisely the regression the gate exists to
// catch, reintroduced inside the gate itself. Walking the record instead
// means a new field is covered the moment it is declared, with no edit
// here, and a field shape the walk does NOT understand fails loudly
// rather than passing through untransformed (see `unsupported`).
//
// The walk understands exactly three shapes, which is every shape the
// record uses:
//
//   `string`                     → the transformed string;
//   a nested record              → recursively transformed;
//   `a -> b -> … -> string`      → a function of the same type that
//                                  calls the original and transforms the
//                                  string it returns. Parameterised
//                                  messages are FUNCTION fields in this
//                                  substrate — the substitution point is
//                                  part of the type rather than a `{0}`
//                                  in the text — so without this arm the
//                                  ~170 parameterised messages would be
//                                  the untransformed holes in the sea.
//
// One consequence of that last arm is worth stating rather than
// discovering: the argument a caller passes is interpolated by the
// ORIGINAL function before the transform ever sees the result, so
// `ResultsAvailableIn "Insights"` pseudo-localises the module name along
// with the sentence around it. There is no template to transform
// separately — the interpolation is compiled into the field — and for a
// developer-facing locale that is the right trade: the sentence is still
// bracketed and still visibly localised, which is the whole signal.
// (Core's `transform` does preserve literal `{name}` spans, which is
// what the `Translations` substrate needs; it simply has nothing to bite
// on here.)

/// The pseudo-locale rendering of the built-in `MessageCatalog`, derived
/// mechanically from `MessageCatalog.english`.
///
/// Install it like any other translation — it IS one, and it is the
/// worked full-coverage example the localization guide walks:
///
/// ```fsharp
/// { ClientConfig.defaults with
///     Locale = FixedLocale PseudoLocaleCatalog.Tag
///     MessageCatalogOverride = Some PseudoLocaleCatalog.overrideFor }
/// ```
[<RequireQualifiedAccess>]
module PseudoLocaleCatalog =

    open System
    open FSharp.Reflection

    /// The reserved pseudo-locale tag as a bare BCP 47 string, which is
    /// what `MessageCatalog.Locale` and `ClientConfig.Locale` speak.
    /// Unwrapped from Core's `PseudoLocale.code` rather than restated, so
    /// the two tiers cannot drift to different tags.
    let Tag =
        match PseudoLocale.code with
        | LocaleCode tag -> tag

    /// True when `locale` is the pseudo-locale. Case-insensitive, matching
    /// `PseudoLocale.isActive`'s comparison semantics — a deployment that
    /// writes `QPS-PLOC` into config gets the pseudo-locale.
    let isActive (locale: string) : bool =
        not (String.IsNullOrWhiteSpace locale)
        && String.Equals(locale.Trim(), Tag, StringComparison.OrdinalIgnoreCase)

    /// The failure a field shape the walk does not understand raises.
    ///
    /// Deliberately fatal rather than a pass-through: an untransformed
    /// field is invisible in the rendered output — it looks exactly like
    /// the un-externalised literal the gate hunts for — so silently
    /// skipping it would make the gate report green while covering less
    /// than it claims. Naming the path and the shape is what turns
    /// "someone added a field the pseudo-locale cannot reach" into a
    /// build failure that says which field and what to do.
    let private unsupported (path: string) (t: Type) : 'a =
        invalidOp (
            $"PseudoLocaleCatalog: catalog field `{path}` has shape `{t}`, which the pseudo-locale walk "
            + "does not understand. The walk covers `string`, nested message records, and functions of "
            + "`string` / `int` / `int64` returning `string`. Add an arm to the shape table (and a case "
            + "to LocalizationTests) rather than leaving the field untransformed — an untransformed field "
            + "is indistinguishable from an un-externalised literal, which is what this gate exists to detect."
        )

    // ─── Parameterised messages: the shape table ──────────────────────
    //
    // A parameterised message has to be WRAPPED, not evaluated: the
    // arguments are the caller's (a module name, a row count), and only
    // the string the message BUILDS is ours to transform.
    //
    // The obvious way to write that is reflectively —
    // `FSharpValue.MakeFunction` over a `Type.GetMethod "Invoke"`. It
    // works on .NET and **Fable supports none of the three calls**
    // (`Type.GetMethod`, `MethodBase.Invoke`, `MakeFunction`), which
    // would confine the pseudo-locale to the test harness and cost it
    // its other half: a developer running the app under `qps-ploc` and
    // seeing the one plain-looking string on the screen. The record walk
    // itself is Fable-clean, so only this arm needed rewriting.
    //
    // Hence a table over the argument shapes the catalog actually uses.
    // Each arm is ordinary typed F#, so Fable applies its own currying
    // conventions to the wrapper exactly as it does to the original —
    // which a hand-built curried closure could not promise. The trade is
    // explicit: a new field of an existing shape still needs no edit
    // here, but a new ARGUMENT TYPE does, and the `_` arm names it
    // rather than letting the field through untransformed.
    //
    // Both derived forms come from ONE arm each, on purpose: the wrapper
    // and the coverage probe are the same function seen twice, and
    // separate tables would be two places to get one shape wrong.

    let private sampleString = "sample"
    let private sampleInt = 1
    let private sampleLong = 1L

    type private Parameterised = {
        /// The message, post-composed with the transform. Boxed because
        /// its type is only known reflectively at the call site.
        Wrapped: obj
        /// The string this message builds from the sample arguments.
        /// A thunk — the derivation never needs it.
        Probe: unit -> string
    }

    /// Decompose a curried function type into its argument types and the
    /// type it finally returns.
    let rec private signature (t: Type) : Type list * Type =
        if FSharpType.IsFunction t then
            let domain, range = FSharpType.GetFunctionElements t
            let args, result = signature range
            domain :: args, result
        else
            [], t

    /// A compact key for an argument list: `s` string, `i` int, `l`
    /// int64. `"sii"` is `string -> int -> int -> string`.
    let private shapeKey (args: Type list) : string =
        args
        |> List.map (fun a ->
            if a = typeof<string> then "s"
            elif a = typeof<int> then "i"
            elif a = typeof<int64> then "l"
            else "?")
        |> String.concat ""

    let private parameterised (path: string) (t: Type) (v: obj) : Parameterised =
        let args, result = signature t
        let tr = PseudoLocale.transform
        let s = sampleString
        let i = sampleInt
        let l = sampleLong

        if result <> typeof<string> then
            unsupported path t
        else
            match shapeKey args with
            | "s" ->
                let f = v :?> (string -> string)

                {
                    Wrapped = box (fun (a: string) -> tr (f a))
                    Probe = fun () -> f s
                }
            | "i" ->
                let f = v :?> (int -> string)

                {
                    Wrapped = box (fun (a: int) -> tr (f a))
                    Probe = fun () -> f i
                }
            | "l" ->
                let f = v :?> (int64 -> string)

                {
                    Wrapped = box (fun (a: int64) -> tr (f a))
                    Probe = fun () -> f l
                }
            | "ss" ->
                let f = v :?> (string -> string -> string)

                {
                    Wrapped = box (fun (a: string) (b: string) -> tr (f a b))
                    Probe = fun () -> f s s
                }
            | "si" ->
                let f = v :?> (string -> int -> string)

                {
                    Wrapped = box (fun (a: string) (b: int) -> tr (f a b))
                    Probe = fun () -> f s i
                }
            | "sl" ->
                let f = v :?> (string -> int64 -> string)

                {
                    Wrapped = box (fun (a: string) (b: int64) -> tr (f a b))
                    Probe = fun () -> f s l
                }
            | "is" ->
                let f = v :?> (int -> string -> string)

                {
                    Wrapped = box (fun (a: int) (b: string) -> tr (f a b))
                    Probe = fun () -> f i s
                }
            | "ii" ->
                let f = v :?> (int -> int -> string)

                {
                    Wrapped = box (fun (a: int) (b: int) -> tr (f a b))
                    Probe = fun () -> f i i
                }
            | "il" ->
                let f = v :?> (int -> int64 -> string)

                {
                    Wrapped = box (fun (a: int) (b: int64) -> tr (f a b))
                    Probe = fun () -> f i l
                }
            | "sss" ->
                let f = v :?> (string -> string -> string -> string)

                {
                    Wrapped = box (fun (a: string) (b: string) (c: string) -> tr (f a b c))
                    Probe = fun () -> f s s s
                }
            | "sis" ->
                let f = v :?> (string -> int -> string -> string)

                {
                    Wrapped = box (fun (a: string) (b: int) (c: string) -> tr (f a b c))
                    Probe = fun () -> f s i s
                }
            | "sii" ->
                let f = v :?> (string -> int -> int -> string)

                {
                    Wrapped = box (fun (a: string) (b: int) (c: int) -> tr (f a b c))
                    Probe = fun () -> f s i i
                }
            | "iss" ->
                let f = v :?> (int -> string -> string -> string)

                {
                    Wrapped = box (fun (a: int) (b: string) (c: string) -> tr (f a b c))
                    Probe = fun () -> f i s s
                }
            | "isl" ->
                let f = v :?> (int -> string -> int64 -> string)

                {
                    Wrapped = box (fun (a: int) (b: string) (c: int64) -> tr (f a b c))
                    Probe = fun () -> f i s l
                }
            | "iii" ->
                let f = v :?> (int -> int -> int -> string)

                {
                    Wrapped = box (fun (a: int) (b: int) (c: int) -> tr (f a b c))
                    Probe = fun () -> f i i i
                }
            | "siii" ->
                let f = v :?> (string -> int -> int -> int -> string)

                {
                    Wrapped = box (fun (a: string) (b: int) (c: int) (d: int) -> tr (f a b c d))
                    Probe = fun () -> f s i i i
                }
            | "iiii" ->
                let f = v :?> (int -> int -> int -> int -> string)

                {
                    Wrapped = box (fun (a: int) (b: int) (c: int) (d: int) -> tr (f a b c d))
                    Probe = fun () -> f i i i i
                }
            | "ssis" ->
                let f = v :?> (string -> string -> int -> string -> string)

                {
                    Wrapped = box (fun (a: string) (b: string) (c: int) (d: string) -> tr (f a b c d))
                    Probe = fun () -> f s s i s
                }
            | _ -> unsupported path t

    /// Rebuild `value` of type `t` with every reachable string replaced by
    /// its pseudo-localised form. Total over the three shapes above;
    /// raises on anything else.
    let rec private transformValue (path: string) (t: Type) (value: obj) : obj =
        if t = typeof<string> then
            box (PseudoLocale.transform (value :?> string))
        elif FSharpType.IsFunction t then
            (parameterised path t value).Wrapped
        elif FSharpType.IsRecord(t, true) then
            let fields = FSharpType.GetRecordFields(t, true)

            let transformed =
                fields
                |> Array.map (fun field ->
                    transformValue $"{path}.{field.Name}" field.PropertyType (field.GetValue value))

            FSharpValue.MakeRecord(t, transformed, true)
        else
            unsupported path t

    /// Derive the pseudo-locale rendering of `source`.
    ///
    /// `Locale` is re-stamped rather than transformed: it is the one
    /// field of the record that is machinery rather than prose, and a
    /// `⟦én·⟧` locale tag would reach `Intl` and throw. Every other
    /// field — including the ones a consumer's own override put there —
    /// goes through the walk, so deriving from an already-translated
    /// catalog pseudo-localises THAT translation, which is how a
    /// deployment checks its own coverage rather than only the SDK's.
    let derive (source: MessageCatalog) : MessageCatalog =
        let walked =
            transformValue "" typeof<MessageCatalog> (box source) :?> MessageCatalog

        { walked with Locale = Tag }

    let private derived = lazy (derive MessageCatalog.english)

    /// The SDK's English catalog, pseudo-localised. Memoised: the walk
    /// allocates ~1,200 strings and ~60 records, which is nothing once
    /// and pointless per render.
    ///
    /// A function rather than a value on purpose. As a module-level value
    /// it would run the whole walk during this module's static
    /// initialisation — which `isActive` triggers, and `isActive` is on
    /// the path of every `overrideFor` call including the ones that
    /// resolve to a different language entirely.
    let catalog () : MessageCatalog = derived.Force()

    /// A `ClientConfig.MessageCatalogOverride` that serves the
    /// pseudo-locale and passes every other language through untouched.
    ///
    /// Written in exactly the shape the guide teaches for a real
    /// translation — match on the locale you were asked for, return the
    /// argument unchanged for one you do not cover — so wiring it is the
    /// same act as wiring French, and a deployment can compose the two.
    let overrideFor (requested: MessageCatalog) : MessageCatalog =
        if isActive requested.Locale then
            derive requested
        else
            requested

    // ─── The coverage walk ────────────────────────────────────────────

    /// Every string leaf a catalog can render, as `path * value`, in a
    /// stable order. A parameterised message contributes the string it
    /// BUILDS from the sample arguments rather than being skipped —
    /// which is what lets the coverage gate see a section whose fields
    /// are all functions. The sample values are never asserted on, only
    /// the sentence built around them.
    ///
    /// Two catalogs of the same type always produce the same paths in the
    /// same order, which is what lets the gate compare a derived catalog
    /// to its source leaf-for-leaf and name the ones that did not move.
    let stringLeaves (source: MessageCatalog) : (string * string) list =
        let rec walk (path: string) (t: Type) (value: obj) : (string * string) list =
            if t = typeof<string> then
                [ path, (value :?> string) ]
            elif FSharpType.IsFunction t then
                [ path, (parameterised path t value).Probe() ]
            elif FSharpType.IsRecord(t, true) then
                FSharpType.GetRecordFields(t, true)
                |> Array.toList
                |> List.collect (fun field -> walk $"{path}.{field.Name}" field.PropertyType (field.GetValue value))
            else
                unsupported path t

        walk "" typeof<MessageCatalog> (box source)