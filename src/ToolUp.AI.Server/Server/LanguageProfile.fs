// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.AI

open System
open System.Text
open Fuaran.Core
open ToolUp.AI.Wire

// ─── The assistant's language profile ──────────────────────────────
//
// A deployment registers the Fuaran-UI subset its assistant may compose: a
// set of kinds and, per kind, the fields of it the model may write. The
// profile sits between a render tool's definition (a few hundred tokens) and
// the whole language, so the trade between prompt size and the range of what
// the model can compose is the deployment's to make.
//
// **Registration validates against the published schema.** The kinds and
// fields are checked against the vocabulary the pinned `Fuaran.UI` package
// publishes (`Generated.kindFieldNames`, `Generated.envelopeFieldNames`), the
// same IDL its encoder and decoder are generated from. An unknown kind or
// field refuses the registration by name, so a profile cannot name something
// the language does not have.
//
// **The fragment is rendered once.** `register` renders the profile into a
// prompt fragment and keeps it; `SystemPromptBuilder.languageProfile` serves
// that one string on every turn. A deployment with no profile composes
// nothing, and its prompt is byte-identical to what it was before.
//
// **The profile is the validator's grammar.** `LanguageProfile.validateBlock`
// runs a model-composed block, as wire JSON, through four checks, each a
// typed refusal naming the node:
//
//   1. every node's kind, kind field and node property is one the profile
//      admits (read off the JSON, because the decoder drops a member it does
//      not know rather than refusing it);
//   2. a data-bearing node draws from a request: a `Chart`'s, `DataGrid`'s
//      or `Sparkline`'s `source`, and a `Metric`'s `value`, is a `Query`
//      binding naming a request the caller holds, and a `DataGrid` carries no
//      `staticRows`. Rows are never written inline, so a model cannot put a
//      figure on the page;
//   3. the language tier's decoder, under a decode policy admitting exactly
//      the profile's kinds;
//   4. the pre-emit validator (`PreEmitValidate.validateForTransport`, the
//      block travels as wire JSON) finds no Error.

/// A deployment's language profile, before registration: the kinds the
/// assistant may compose and the fields of each it may write. Built with
/// `LanguageProfile.named`, `LanguageProfile.withKind` and
/// `LanguageProfile.withNodeProperties`, then checked by
/// `LanguageProfile.register`.
type LanguageProfile = {
    /// The profile's name. Reported in the fragment, in the boot log and in
    /// a decode refusal, so a log line says which profile refused.
    Name: string
    /// Each admitted kind, by its wire discriminator (`kind.$type`), and the
    /// kind fields of it the model may write.
    Kinds: Map<string, Set<string>>
    /// The node properties beside `id` and `kind` the model may write
    /// (`style`, `tooltip`, …). Empty by default.
    NodeProperties: Set<string>
}

/// Why a profile was refused at registration. Nothing was registered.
[<RequireQualifiedAccess>]
type LanguageProfileRefusal =
    /// The profile has no name.
    | NoName
    /// The profile admits no kind.
    | NoKinds
    /// The published schema has no kind of this name.
    | UnknownKind of kind: string
    /// The published schema gives the kind no field of this name.
    | UnknownField of kind: string * field: string
    /// The published schema has no node property of this name.
    | UnknownNodeProperty of property: string
    /// The field carries rows inline, which no profile may admit.
    | InlineRowsField of kind: string * field: string

/// A profile that passed registration, with its prompt fragment rendered
/// once. Only `LanguageProfile.register` makes one.
[<Sealed>]
type RegisteredLanguageProfile internal (profile: LanguageProfile, fragment: string) =
    /// The profile as registered.
    member _.Profile = profile
    /// The prompt fragment, rendered once at registration.
    member _.Fragment = fragment
    /// The fragment's size in UTF-8 bytes, as the boot log reports it.
    member _.FragmentBytes = Encoding.UTF8.GetByteCount fragment

/// Why a model-composed block was refused under a profile. Each names the
/// node it is about; nothing reached the user.
[<RequireQualifiedAccess>]
type ComposedBlockRefusal =
    /// The block is not JSON, or not a node.
    | Malformed of reason: string
    /// The node's kind is not in the profile.
    | KindNotInProfile of nodeId: string * kind: string
    /// The node writes a kind field the profile does not admit.
    | FieldNotInProfile of nodeId: string * kind: string * field: string
    /// The node writes a node property the profile does not admit.
    | NodePropertyNotInProfile of nodeId: string * property: string
    /// A data-bearing node carries its rows (or value) inline rather than
    /// naming a request.
    | InlineRows of nodeId: string * kind: string * field: string
    /// A data-bearing node names a request the caller does not hold.
    | UnknownRequest of nodeId: string * kind: string * key: string
    /// The language tier's decoder refused the block.
    | Decode of reason: string
    /// The pre-emit validator found errors, as `code: message`.
    | Grammar of findings: string list

/// The registration builder, the fragment and the grammar.
[<RequireQualifiedAccess>]
module LanguageProfile =

    // ── The published schema ──────────────────────────────────────

    /// The data-bearing slot of each kind that draws rows or a value: it must
    /// be a `Query` binding naming a request.
    let private dataSlots: Map<string, string> =
        Map.ofList [
            "Chart", "source"
            "DataGrid", "source"
            "Metric", "value"
            "Sparkline", "source"
        ]

    /// Fields that carry rows inline, whatever their binding.
    let private inlineRowFields: Set<string * string> = set [ "DataGrid", "staticRows" ]

    let private wireTags: Set<string> = Set.ofList Fuaran.UI.Generated.allWireTags

    let private categoryOf (kind: string) : string option =
        Fuaran.UI.Generated.kindCategories
        |> Map.tryPick (fun category kinds -> if kinds.Contains kind then Some category else None)

    // ── The builder ───────────────────────────────────────────────

    /// An empty profile of this name.
    let named (name: string) : LanguageProfile = {
        Name = name
        Kinds = Map.empty
        NodeProperties = Set.empty
    }

    /// Admit `kind` (its wire discriminator) with these kind fields. Naming a
    /// kind twice admits the union of its fields.
    let withKind (kind: string) (fields: string list) (profile: LanguageProfile) : LanguageProfile =
        let existing = profile.Kinds.TryFind kind |> Option.defaultValue Set.empty

        {
            profile with
                Kinds = profile.Kinds.Add(kind, Set.union existing (Set.ofList fields))
        }

    /// Admit these node properties (beside `id` and `kind`) on every node.
    let withNodeProperties (properties: string list) (profile: LanguageProfile) : LanguageProfile = {
        profile with
            NodeProperties = Set.union profile.NodeProperties (Set.ofList properties)
    }

    // ── The fragment ──────────────────────────────────────────────

    let private code (s: string) = "`" + s + "`"

    let private fieldList (fields: Set<string>) : string =
        if fields.IsEmpty then
            "no fields"
        else
            fields |> Seq.map code |> String.concat ", "

    /// The prompt fragment for `profile`: the kinds and fields it admits and
    /// the rule a data-bearing node is held to. Pure; `register` renders it
    /// once and keeps it.
    let fragment (profile: LanguageProfile) : string =
        let sb = StringBuilder()
        let line (s: string) = sb.Append(s).Append('\n') |> ignore

        line (sprintf "## Composing a block (language profile '%s')" profile.Name)
        line ""

        line (
            "You may compose a Fuaran-UI block from the kinds and fields below, and from nothing else. "
            + "A node is `{\"id\":\"<unique id>\",\"kind\":{\"$type\":\"<Kind>\", <fields>}}`; "
            + "nest nodes in a field that takes children. A kind, field or node property not listed here is refused by name, "
            + "before the block reaches the user."
        )

        line ""
        line "Kinds:"

        for KeyValue(kind, fields) in profile.Kinds do
            let category =
                categoryOf kind |> Option.map (sprintf " (%s)") |> Option.defaultValue ""

            let rows =
                match dataSlots.TryFind kind with
                | Some slot when fields.Contains slot -> sprintf "; %s draws from a request" (code slot)
                | _ -> ""

            line (sprintf "- %s%s: %s%s" (code kind) category (fieldList fields) rows)

        line ""

        line (
            if profile.NodeProperties.IsEmpty then
                "Node properties beside `id` and `kind`: none."
            else
                sprintf "Node properties beside `id` and `kind`: %s." (fieldList profile.NodeProperties)
        )

        if profile.Kinds |> Map.exists (fun kind _ -> dataSlots.ContainsKey kind) then
            line ""

            line (
                "Rows and values come only from a request: a data-bearing field is "
                + "`{\"$type\":\"Query\",\"name\":\"<request key>\"}`, naming a request the conversation holds. "
                + "Never write rows, values or a figure inline; a block that does is refused."
            )

        sb.ToString().TrimEnd('\n')

    // ── Registration ──────────────────────────────────────────────

    /// Check `profile` against the published schema and render its fragment
    /// once. Every refusal is reported, each by name.
    let register (profile: LanguageProfile) : Result<RegisteredLanguageProfile, LanguageProfileRefusal list> =
        let refusals = [
            if String.IsNullOrWhiteSpace profile.Name then
                LanguageProfileRefusal.NoName
            if profile.Kinds.IsEmpty then
                LanguageProfileRefusal.NoKinds
            for KeyValue(kind, fields) in profile.Kinds do
                match Fuaran.UI.Generated.kindFieldNames.TryFind kind with
                | None -> LanguageProfileRefusal.UnknownKind kind
                | Some known ->
                    for field in fields do
                        if not (known.Contains field) then
                            LanguageProfileRefusal.UnknownField(kind, field)
                        elif inlineRowFields.Contains(kind, field) then
                            LanguageProfileRefusal.InlineRowsField(kind, field)
            for property in profile.NodeProperties do
                if not (Fuaran.UI.Generated.envelopeFieldNames.Contains property) then
                    LanguageProfileRefusal.UnknownNodeProperty property
        ]

        if List.isEmpty refusals then
            Ok(RegisteredLanguageProfile(profile, fragment profile))
        else
            Error refusals

    // ── The grammar ───────────────────────────────────────────────

    let private stringMember (name: string) (members: (string * JsonValue) list) : string option =
        members
        |> List.tryPick (fun (k, v) ->
            match v with
            | JsonValue.JString s when k = name -> Some s
            | _ -> None)

    /// A node's id, wire kind and kind members, when `members` is a node: an
    /// object with a string `id` and a `kind` object whose `$type` is a kind
    /// of the language. A column's `kind` (`{"$type":"Text"}`) is not.
    let private asNode (members: (string * JsonValue) list) : (string * string * (string * JsonValue) list) option =
        match stringMember "id" members, members |> List.tryFind (fun (k, _) -> k = "kind") with
        | Some id, Some(_, JsonValue.JObject kindMembers) ->
            match stringMember "$type" kindMembers with
            | Some kind when wireTags.Contains kind -> Some(id, kind, kindMembers)
            | _ -> None
        | _ -> None

    /// The data slot's request key, or the inline-rows refusal.
    let private slotCheck
        (requests: Set<string>)
        (id: string)
        (kind: string)
        (kindMembers: (string * JsonValue) list)
        : ComposedBlockRefusal list =
        [
            match dataSlots.TryFind kind with
            | None -> ()
            | Some slot ->
                match kindMembers |> List.tryFind (fun (k, _) -> k = slot) with
                | None -> ()
                | Some(_, JsonValue.JObject binding) when stringMember "$type" binding = Some "Query" ->
                    match stringMember "name" binding with
                    | Some key when requests.Contains key -> ()
                    | Some key -> ComposedBlockRefusal.UnknownRequest(id, kind, key)
                    | None -> ComposedBlockRefusal.InlineRows(id, kind, slot)
                | Some _ -> ComposedBlockRefusal.InlineRows(id, kind, slot)
            for (k, _) in kindMembers do
                if inlineRowFields.Contains(kind, k) then
                    ComposedBlockRefusal.InlineRows(id, kind, k)
        ]

    let rec private walk
        (profile: LanguageProfile)
        (requests: Set<string>)
        (value: JsonValue)
        : ComposedBlockRefusal list =
        match value with
        | JsonValue.JArray items -> items |> List.collect (walk profile requests)
        | JsonValue.JObject members ->
            match asNode members with
            | None -> members |> List.collect (snd >> walk profile requests)
            | Some(id, kind, kindMembers) ->
                let own = [
                    match profile.Kinds.TryFind kind with
                    | None -> ComposedBlockRefusal.KindNotInProfile(id, kind)
                    | Some fields ->
                        for (field, _) in kindMembers do
                            if field <> "$type" && not (fields.Contains field) then
                                ComposedBlockRefusal.FieldNotInProfile(id, kind, field)

                        yield! slotCheck requests id kind kindMembers
                    for (property, _) in members do
                        if
                            property <> "id"
                            && property <> "kind"
                            && not (profile.NodeProperties.Contains property)
                        then
                            ComposedBlockRefusal.NodePropertyNotInProfile(id, property)
                ]

                let nested =
                    (kindMembers |> List.filter (fun (k, _) -> k <> "$type"))
                    @ (members |> List.filter (fun (k, _) -> k <> "id" && k <> "kind"))
                    |> List.collect (snd >> walk profile requests)

                own @ nested
        | _ -> []

    let private grammarErrors (node: Fuaran.UI.Types.Node<unit>) : string list =
        match Fuaran.UI.PreEmitValidate.validateForTransport node with
        | Ok() -> []
        | Error defects ->
            defects
            |> List.choose (fun d ->
                match Fuaran.UI.PreEmitValidate.describe d with
                | code, Severity.Error, message -> Some(sprintf "%s: %s" code message)
                | _ -> None)

    /// Run a model-composed block (its wire JSON) against the profile, with
    /// `requests` the keys of the requests a data-bearing node may name.
    /// `Ok` carries the decoded node; `Error` every refusal, each naming its
    /// node.
    let validateBlock
        (registered: RegisteredLanguageProfile)
        (requests: Set<string>)
        (blockJson: string)
        : Result<Fuaran.UI.Types.Node<unit>, ComposedBlockRefusal list> =
        let profile = registered.Profile

        let parsed =
            if String.IsNullOrWhiteSpace blockJson then
                None
            else
                JsonHost.parse blockJson

        match parsed with
        | None -> Error [ ComposedBlockRefusal.Malformed "the block is not JSON" ]
        | Some(JsonValue.JObject members as root) when (asNode members).IsSome ->
            match walk profile requests root with
            | _ :: _ as refusals -> Error refusals
            | [] ->
                let policy =
                    Fuaran.UI.KindPolicy.DecodePolicy.admitting profile.Name (profile.Kinds |> Map.keys)

                match Fuaran.UI.Ops.JsonDecode.decodeNodeWithPolicy policy blockJson with
                | Error e -> Error [ ComposedBlockRefusal.Decode(Fuaran.UI.Ops.JsonDecode.DecodeError.render e) ]
                | Ok tree ->
                    let node =
                        Fuaran.UI.Ops.Types.WireTree.reify tree
                        |> Fuaran.UI.NodeMap.mapMsg (fun _ -> ())

                    match grammarErrors node with
                    | [] -> Ok node
                    | findings -> Error [ ComposedBlockRefusal.Grammar findings ]
        | Some _ -> Error [ ComposedBlockRefusal.Malformed "the block is not a node" ]

/// Companion helpers for `LanguageProfileRefusal`.
[<RequireQualifiedAccess>]
module LanguageProfileRefusal =
    /// One sentence naming what to repair.
    let describe (refusal: LanguageProfileRefusal) : string =
        match refusal with
        | LanguageProfileRefusal.NoName -> "the profile has no name"
        | LanguageProfileRefusal.NoKinds -> "the profile admits no kind"
        | LanguageProfileRefusal.UnknownKind kind -> sprintf "the language has no kind '%s'" kind
        | LanguageProfileRefusal.UnknownField(kind, field) -> sprintf "kind '%s' has no field '%s'" kind field
        | LanguageProfileRefusal.UnknownNodeProperty property -> sprintf "a node has no property '%s'" property
        | LanguageProfileRefusal.InlineRowsField(kind, field) ->
            sprintf "field '%s' of kind '%s' carries rows inline, which no profile may admit" field kind

/// Companion helpers for `ComposedBlockRefusal`.
[<RequireQualifiedAccess>]
module ComposedBlockRefusal =
    /// One sentence naming the node and what to repair.
    let describe (refusal: ComposedBlockRefusal) : string =
        match refusal with
        | ComposedBlockRefusal.Malformed reason -> reason
        | ComposedBlockRefusal.KindNotInProfile(id, kind) ->
            sprintf "node '%s' is a '%s', which the profile does not admit" id kind
        | ComposedBlockRefusal.FieldNotInProfile(id, kind, field) ->
            sprintf "node '%s' (%s) writes field '%s', which the profile does not admit" id kind field
        | ComposedBlockRefusal.NodePropertyNotInProfile(id, property) ->
            sprintf "node '%s' writes property '%s', which the profile does not admit" id property
        | ComposedBlockRefusal.InlineRows(id, kind, field) ->
            sprintf "node '%s' (%s) writes '%s' inline; it must name a request" id kind field
        | ComposedBlockRefusal.UnknownRequest(id, kind, key) ->
            sprintf "node '%s' (%s) names request '%s', which the conversation does not hold" id kind key
        | ComposedBlockRefusal.Decode reason -> sprintf "the block does not decode: %s" reason
        | ComposedBlockRefusal.Grammar findings ->
            sprintf "the pre-emit validator refused the block: %s" (String.concat "; " findings)