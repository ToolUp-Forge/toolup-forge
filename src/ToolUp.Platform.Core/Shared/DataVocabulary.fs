// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Phase 594 — pinned data-vocabulary packs ─────────────────────────
//
// Nothing governs what a wire `TypeName` *means*: two modules can collide
// on a name, and two federated instances can agree on a contract while
// meaning different things by `"SalesData"`. A **data-vocabulary pack** is
// the decentralised answer — a versioned, immutable document declaring a
// closed set of data-type semantics within a namespace (`TypeName` →
// schema: fields, value-types, units, semantic description). A deployment
// *pins* zero or more packs; the composition validator then checks every
// registered data type whose name the pack governs against the declared
// schema. Cross-instance agreement is by *pinned copy*, not by a central
// registry — a federation pins a pack exactly as it pins a contract.
//
// **Immutable per version — a change is a new version.** A pack's identity
// is `(Id, Version)`; the canonical JSON + SHA-256 hash make an in-place
// mutation detectable (the same version hashing differently is the
// discipline the pinning rests on). This mirrors the local-feed pin
// discipline: the pinned bytes are the contract, and re-issuing them under
// the same version is the defect the hash catches.
//
// **Generic substrate (GP 1); zero cost when unused (GP 11 / GP 13).** The
// pack shape carries no vendor / domain vocabulary — only strings, a closed
// value-type DU, and lists. A deployment that pins no pack composes exactly
// as before; the validator rules degrade to no-ops against an empty pin set.
//
// **Fable-safe floor.** The pack *types* + the pure governance / drift /
// canonical-JSON helpers are Fable-safe (records, DUs, string building), so
// `ServerConfig` can carry a pinned-pack field that ships under `fable/`.
// Only the SHA-256 `hash` and the `load` parser are BCL-bound and guarded
// under `#if !FABLE_COMPILER` (the JwtCrypto ship-with-guard pattern) — the
// module declaration stays outside the guard so a Fable consumer transpiles
// it to a valid module.

/// A field's declared value-type in a vocabulary schema — a closed,
/// wire-neutral, generic set. Deliberately *not* F#'s type system nor a
/// vendor's: it is the coarse semantic shape two instances must agree on
/// for a shared name to mean the same thing.
type VocabularyFieldType =
    | VocabString
    | VocabInteger
    | VocabNumber
    | VocabBoolean
    | VocabTimestamp
    | VocabId

/// One field within a data-type schema: its name, value-type, optional
/// unit (`"USD"`, `"kg"`, … — `None` for dimensionless / non-quantity
/// fields), and a human-readable semantic description.
type VocabularyField = {
    Name: string
    Type: VocabularyFieldType
    Unit: string option
    Description: string
}

/// The semantic schema of one `TypeName` within a pack: the closed set of
/// fields the name carries plus a prose description of what it means.
type VocabularyEntry = {
    TypeName: string
    Fields: VocabularyField list
    Description: string
}

/// A pack version — monotone `(Major, Minor)`. Any change to any entry is a
/// new version (the feed discipline); a pack is immutable at a given version.
type VocabularyPackVersion = { Major: int; Minor: int }

/// A versioned, immutable data-vocabulary pack: a closed set of `TypeName`
/// → schema entries governing a declared `Namespace`. A `TypeName` is
/// *governed* by the pack when it sits under the namespace (`"<ns>.<name>"`);
/// a governed name with no matching entry squats, a governed name whose
/// declared schema drifts contradicts the pinned meaning.
type DataVocabularyPack = {
    Id: string
    /// The name-domain the pack governs. A registered data type whose name
    /// begins with `"<Namespace>."` falls under this pack's authority; a
    /// data type outside the namespace is unaffected (GP 13).
    Namespace: string
    Version: VocabularyPackVersion
    Entries: VocabularyEntry list
}

/// A pin: the identity `(Id, Version)` a deployment / federation pins,
/// plus the canonical hash so a counterparty detects an in-place mutation
/// of the same version. What surfaces on the cross-instance face.
type VocabularyPackPin = {
    PackId: string
    Version: VocabularyPackVersion
    /// Lowercase-hex SHA-256 over the pack's canonical JSON, or `""` on a
    /// Fable-compiled surface where the BCL hash is unavailable.
    Hash: string
}

/// Pure governance / drift helpers over `DataVocabularyPack`, plus the
/// canonical-JSON projection and (server-only) hash + loader.
module DataVocabulary =

    /// The current pack canonical-JSON format version — bumped only if the
    /// *serialisation vocabulary* changes, distinct from any pack's own
    /// `Version`.
    [<Literal>]
    let formatVersion = 1

    /// Render a version as `"Major.Minor"`.
    let versionString (v: VocabularyPackVersion) : string = sprintf "%d.%d" v.Major v.Minor

    /// The wire token for a field value-type — the stable string the
    /// canonical JSON emits and the loader parses.
    let fieldTypeToWire (t: VocabularyFieldType) : string =
        match t with
        | VocabString -> "string"
        | VocabInteger -> "integer"
        | VocabNumber -> "number"
        | VocabBoolean -> "boolean"
        | VocabTimestamp -> "timestamp"
        | VocabId -> "id"

    /// Parse a wire token back to a field value-type.
    let fieldTypeOfWire (s: string) : VocabularyFieldType option =
        match s with
        | "string" -> Some VocabString
        | "integer" -> Some VocabInteger
        | "number" -> Some VocabNumber
        | "boolean" -> Some VocabBoolean
        | "timestamp" -> Some VocabTimestamp
        | "id" -> Some VocabId
        | _ -> None

    /// True when `typeName` falls under the pack's governed namespace
    /// (`"<Namespace>.<name>"`). A name outside the namespace is never
    /// governed — the pack has no authority over it (GP 13).
    let governs (pack: DataVocabularyPack) (typeName: string) : bool =
        not (System.String.IsNullOrEmpty pack.Namespace)
        && typeName.StartsWith(pack.Namespace + ".", System.StringComparison.Ordinal)

    /// The pack's entry for `typeName`, if it declares one.
    let tryEntry (pack: DataVocabularyPack) (typeName: string) : VocabularyEntry option =
        pack.Entries |> List.tryFind (fun e -> e.TypeName = typeName)

    // ── Schema drift (pure) ─────────────────────────────────────────────

    /// Enumerate how a *declared* data-type schema drifts from the pinned
    /// pack entry that governs its name. Structural only — field presence,
    /// value-type, and unit — never prose descriptions. An empty list means
    /// the declared schema conforms. Deterministic (sorted by field name).
    let schemaDrift (declared: VocabularyEntry) (packEntry: VocabularyEntry) : string list =
        let declaredByName = declared.Fields |> List.map (fun f -> f.Name, f) |> Map.ofList
        let packByName = packEntry.Fields |> List.map (fun f -> f.Name, f) |> Map.ofList

        let allNames =
            (declared.Fields @ packEntry.Fields)
            |> List.map _.Name
            |> List.distinct
            |> List.sort

        allNames
        |> List.choose (fun name ->
            match Map.tryFind name declaredByName, Map.tryFind name packByName with
            | None, Some packField ->
                Some(
                    sprintf "missing required field '%s' (%s) the pack declares" name (fieldTypeToWire packField.Type)
                )
            | Some _, None -> Some(sprintf "field '%s' is not declared by the pack entry" name)
            | Some declaredField, Some packField ->
                if declaredField.Type <> packField.Type then
                    Some(
                        sprintf
                            "field '%s' is declared %s but the pack specifies %s"
                            name
                            (fieldTypeToWire declaredField.Type)
                            (fieldTypeToWire packField.Type)
                    )
                elif declaredField.Unit <> packField.Unit then
                    let render u = u |> Option.defaultValue "(none)"

                    Some(
                        sprintf
                            "field '%s' declares unit %s but the pack specifies %s"
                            name
                            (render declaredField.Unit)
                            (render packField.Unit)
                    )
                else
                    None
            | None, None -> None)

    // ── Canonical JSON (pure, Fable-safe) ───────────────────────────────

    /// JSON-escape a string body (no surrounding quotes).
    let private escape (s: string) : string =
        let sb = System.Text.StringBuilder(s.Length + 2)

        for ch in s do
            match ch with
            | '"' -> sb.Append "\\\"" |> ignore
            | '\\' -> sb.Append "\\\\" |> ignore
            | '\n' -> sb.Append "\\n" |> ignore
            | '\r' -> sb.Append "\\r" |> ignore
            | '\t' -> sb.Append "\\t" |> ignore
            | c when c < ' ' -> sb.Append(sprintf "\\u%04x" (int c)) |> ignore
            | c -> sb.Append c |> ignore

        sb.ToString()

    let private quote (s: string) : string = "\"" + escape s + "\""

    let private jsonUnit (u: string option) : string =
        match u with
        | Some v -> quote v
        | None -> "null"

    let private fieldJson (f: VocabularyField) : string =
        sprintf
            "{\"name\":%s,\"type\":%s,\"unit\":%s,\"description\":%s}"
            (quote f.Name)
            (quote (fieldTypeToWire f.Type))
            (jsonUnit f.Unit)
            (quote f.Description)

    let private entryJson (e: VocabularyEntry) : string =
        let fields =
            e.Fields |> List.sortBy _.Name |> List.map fieldJson |> String.concat ","

        sprintf "{\"typeName\":%s,\"description\":%s,\"fields\":[%s]}" (quote e.TypeName) (quote e.Description) fields

    /// The pack's canonical JSON: deterministic by construction — entries
    /// sorted by `TypeName`, fields sorted by name, fixed key order — so the
    /// same pack always yields the same bytes (and the same hash) regardless
    /// of authoring order. The pinned bytes two instances compare.
    let canonicalJson (pack: DataVocabularyPack) : string =
        let entries =
            pack.Entries
            |> List.sortBy _.TypeName
            |> List.map entryJson
            |> String.concat ","

        sprintf
            "{\"formatVersion\":%d,\"id\":%s,\"namespace\":%s,\"version\":{\"major\":%d,\"minor\":%d},\"entries\":[%s]}"
            formatVersion
            (quote pack.Id)
            (quote pack.Namespace)
            pack.Version.Major
            pack.Version.Minor
            entries

#if !FABLE_COMPILER
    /// Lowercase-hex SHA-256 over the pack's canonical JSON. Server-only
    /// (BCL crypto); the canonical determinism above is what makes it a
    /// stable pin fingerprint.
    let hash (pack: DataVocabularyPack) : string =
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonicalJson pack))
        |> System.Convert.ToHexString
        |> _.ToLowerInvariant()

    /// Load a pack from its canonical JSON. Server-only (`System.Text.Json`
    /// is not Fable-compatible). Returns `Error` with a descriptive reason
    /// on malformed input or an unknown field value-type — a pinned pack that
    /// does not parse is a compose-time defect, not a silent skip.
    let load (json: string) : Result<DataVocabularyPack, string> =
        try
            use doc = System.Text.Json.JsonDocument.Parse json
            let root = doc.RootElement

            let getString (el: System.Text.Json.JsonElement) (name: string) : Result<string, string> =
                match el.TryGetProperty name with
                | true, v when v.ValueKind = System.Text.Json.JsonValueKind.String -> Ok(v.GetString())
                | _ -> Error(sprintf "missing or non-string property '%s'" name)

            let getInt (el: System.Text.Json.JsonElement) (name: string) : Result<int, string> =
                match el.TryGetProperty name with
                | true, v when v.ValueKind = System.Text.Json.JsonValueKind.Number -> Ok(v.GetInt32())
                | _ -> Error(sprintf "missing or non-numeric property '%s'" name)

            let optString (el: System.Text.Json.JsonElement) (name: string) : string =
                match el.TryGetProperty name with
                | true, v when v.ValueKind = System.Text.Json.JsonValueKind.String -> v.GetString()
                | _ -> ""

            let resultList (items: Result<'a, string> list) : Result<'a list, string> =
                (Ok [], items)
                ||> List.fold (fun acc item ->
                    match acc, item with
                    | Ok xs, Ok x -> Ok(xs @ [ x ])
                    | Error e, _ -> Error e
                    | _, Error e -> Error e)

            let parseField (el: System.Text.Json.JsonElement) : Result<VocabularyField, string> =
                getString el "name"
                |> Result.bind (fun name ->
                    getString el "type"
                    |> Result.bind (fun typeToken ->
                        match fieldTypeOfWire typeToken with
                        | None -> Error(sprintf "field '%s' has unknown value-type '%s'" name typeToken)
                        | Some fieldType ->
                            let unit =
                                match el.TryGetProperty "unit" with
                                | true, v when v.ValueKind = System.Text.Json.JsonValueKind.String ->
                                    Some(v.GetString())
                                | _ -> None

                            Ok {
                                Name = name
                                Type = fieldType
                                Unit = unit
                                Description = optString el "description"
                            }))

            let parseEntry (el: System.Text.Json.JsonElement) : Result<VocabularyEntry, string> =
                getString el "typeName"
                |> Result.bind (fun typeName ->
                    let fields =
                        match el.TryGetProperty "fields" with
                        | true, arr when arr.ValueKind = System.Text.Json.JsonValueKind.Array ->
                            arr.EnumerateArray() |> Seq.map parseField |> List.ofSeq |> resultList
                        | _ -> Ok []

                    fields
                    |> Result.map (fun fs -> {
                        TypeName = typeName
                        Fields = fs
                        Description = optString el "description"
                    }))

            getString root "id"
            |> Result.bind (fun id ->
                getString root "namespace"
                |> Result.bind (fun ns ->
                    match root.TryGetProperty "version" with
                    | true, ver ->
                        getInt ver "major"
                        |> Result.bind (fun major ->
                            getInt ver "minor"
                            |> Result.bind (fun minor ->
                                let entries =
                                    match root.TryGetProperty "entries" with
                                    | true, arr when arr.ValueKind = System.Text.Json.JsonValueKind.Array ->
                                        arr.EnumerateArray() |> Seq.map parseEntry |> List.ofSeq |> resultList
                                    | _ -> Ok []

                                entries
                                |> Result.map (fun es -> {
                                    Id = id
                                    Namespace = ns
                                    Version = { Major = major; Minor = minor }
                                    Entries = es
                                })))
                    | _ -> Error "missing 'version' object"))
        with ex ->
            Error(sprintf "malformed vocabulary pack JSON: %s" ex.Message)
#endif

    /// The pinnable `(Id, Version, Hash)` of a pack. `Hash` is `""` on a
    /// Fable surface (no BCL SHA-256); server-side it carries the canonical
    /// hash so a counterparty can detect an in-place mutation. Placed after
    /// the guarded `hash` so the server compile resolves it in-order.
    let pin (pack: DataVocabularyPack) : VocabularyPackPin = {
        PackId = pack.Id
        Version = pack.Version
        Hash =
#if FABLE_COMPILER
            ""
#else
            hash pack
#endif
    }