// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.ScimTypes

open System
open System.Text
open System.Text.Json
open ToolUp.Platform

// ─── SCIM 2.0 resource model (RFC 7643 / RFC 7644) ───────────────────
//
// The wire shape an enterprise IdP (Entra ID, Okta, OneLogin) pushes at
// a service provider. Protocol-only: no vendor SDK, no vendor-specific
// type reaches `ToolUp.Platform.*` (GP 1). The whole file is BCL +
// `System.Text.Json`.
//
// **Why the codec is hand-rolled rather than record-mapped.** SCIM is
// not "a record in camelCase". Three properties of the wire format are
// not expressible by mapping an F# record through `JsonSerializer`,
// with or without the SDK's `FableConverters` converter set:
//
//   * Every resource carries an explicit `schemas` envelope holding
//     `urn:ietf:params:scim:...` URNs, and a service provider is
//     required to echo the right URN per resource kind — a field whose
//     value is decided by the resource's TYPE, not by the value.
//   * `emails` is a multi-valued complex attribute where exactly one
//     member may carry `primary: true`, and IdPs disagree about whether
//     they send `primary` at all. That is a decode-time selection rule,
//     not a field.
//   * `Operations[].value` in a PATCH is polymorphic — a bare boolean
//     (`active`), a bare string, or an array of member objects — keyed
//     off the sibling `path`. A single F# type cannot receive it
//     faithfully without erasure.
//
// So `ScimJson` below reads with `JsonDocument` and writes with
// `Utf8JsonWriter`, both explicitly. That also means an IdP sending an
// unknown attribute is IGNORED rather than throwing, which is what
// RFC 7644 §3.5.2 requires of a service provider and what a strict
// record mapping would get wrong in the other direction.

// ─── Schema URNs ─────────────────────────────────────────────────────

module ScimSchemas =
    [<Literal>]
    let User = "urn:ietf:params:scim:schemas:core:2.0:User"

    [<Literal>]
    let Group = "urn:ietf:params:scim:schemas:core:2.0:Group"

    [<Literal>]
    let ListResponse = "urn:ietf:params:scim:api:messages:2.0:ListResponse"

    [<Literal>]
    let Error = "urn:ietf:params:scim:api:messages:2.0:Error"

    [<Literal>]
    let PatchOp = "urn:ietf:params:scim:api:messages:2.0:PatchOp"

    [<Literal>]
    let ServiceProviderConfig =
        "urn:ietf:params:scim:schemas:core:2.0:ServiceProviderConfig"

    [<Literal>]
    let ResourceType = "urn:ietf:params:scim:schemas:core:2.0:ResourceType"

// ─── Resources ───────────────────────────────────────────────────────

/// Common `meta` sub-attribute (RFC 7643 §3.1). `Version` is the
/// weak ETag an IdP may echo back on a conditional write; the shipped
/// handler emits it but does not require it.
type ScimMeta = {
    ResourceType: string
    Created: DateTime option
    LastModified: DateTime option
    Location: string option
    Version: string option
}

/// A `name` complex attribute. Every field is optional — Okta sends the
/// pair, Entra frequently sends only `formatted`, and a minimal client
/// sends none of it.
type ScimName = {
    Formatted: string option
    FamilyName: string option
    GivenName: string option
}

module ScimName =
    let empty: ScimName = {
        Formatted = None
        FamilyName = None
        GivenName = None
    }

    let isEmpty (n: ScimName) =
        n.Formatted.IsNone && n.FamilyName.IsNone && n.GivenName.IsNone

/// One entry of the multi-valued `emails` attribute.
type ScimEmail = {
    Value: string
    Type: string option
    Primary: bool
}

/// A `Group.members[]` entry. `Value` is the member resource's `id` —
/// for this companion, a platform user id.
type ScimGroupMember = {
    Value: string
    Display: string option
    /// `"User"` / `"Group"`. Group-in-group nesting is refused by the
    /// handler; the field is carried so the refusal can name what it saw.
    Type: string option
}

/// A SCIM `User` resource.
type ScimUser = {
    Id: string
    ExternalId: string option
    UserName: string
    Name: ScimName
    DisplayName: string option
    Emails: ScimEmail list
    Active: bool
    Meta: ScimMeta option
}

/// A SCIM `Group` resource.
type ScimGroup = {
    Id: string
    ExternalId: string option
    DisplayName: string
    Members: ScimGroupMember list
    Meta: ScimMeta option
}

module ScimUser =
    /// The primary email: the entry flagged `primary`, else the first.
    /// IdPs disagree about whether `primary` is sent at all, so "first"
    /// is the documented fallback rather than an error.
    let primaryEmail (u: ScimUser) : string option =
        u.Emails
        |> List.tryFind _.Primary
        |> Option.orElse (List.tryHead u.Emails)
        |> Option.map _.Value

// ─── Query surface ───────────────────────────────────────────────────

/// `startIndex` / `count` (RFC 7644 §3.4.2.4). SCIM's `startIndex` is
/// **1-based**, which is the single most common off-by-one in a SCIM
/// implementation — `ScimPage.offset` is the only place that
/// conversion happens.
type ScimPage = { StartIndex: int; Count: int }

module ScimPage =
    /// RFC 7644 §3.4.2.4: a `count` below 1 means "no resources", and a
    /// `startIndex` below 1 is clamped to 1.
    [<Literal>]
    let DefaultCount = 100

    [<Literal>]
    let MaxCount = 500

    let defaults: ScimPage = { StartIndex = 1; Count = DefaultCount }

    let create (startIndex: int) (count: int) : ScimPage = {
        StartIndex = max 1 startIndex
        Count = min MaxCount (max 0 count)
    }

    /// 0-based offset into a result list.
    let offset (p: ScimPage) = p.StartIndex - 1

    let apply (p: ScimPage) (items: 'a list) : 'a list =
        items |> List.skip (min (offset p) (List.length items)) |> List.truncate p.Count

/// The filter shapes this service provider answers. RFC 7644 §3.4.2.2
/// defines a whole filter grammar; enterprise IdPs in practice send
/// exactly one shape — an equality test on the resource's unique
/// attribute, to decide create-vs-update. Everything else is answered
/// with the spec's `501` (RFC 7644 §3.4.2.2: "the service provider
/// SHOULD respond with HTTP 501"), naming the expression, rather than
/// being silently mis-parsed into a wrong result set.
type ScimFilter =
    | NoFilter
    | UserNameEquals of string
    | ExternalIdEquals of string
    | DisplayNameEquals of string
    | UnsupportedFilter of expression: string

module ScimFilter =
    /// Parse the one supported shape: `<attr> eq "<value>"`, with the
    /// attribute name case-insensitive (RFC 7643 §2.1) and the value in
    /// double quotes. Anything else is `UnsupportedFilter`, carrying the
    /// original expression so the 501 can quote it back.
    let parse (expression: string) : ScimFilter =
        if String.IsNullOrWhiteSpace expression then
            NoFilter
        else
            let trimmed = expression.Trim()
            let firstSpace = trimmed.IndexOf ' '

            let quoted (rest: string) =
                let r = rest.Trim()

                if r.Length >= 2 && r.StartsWith "\"" && r.EndsWith "\"" then
                    Some(r.Substring(1, r.Length - 2))
                else
                    None

            if firstSpace <= 0 then
                UnsupportedFilter trimmed
            else
                let attr = trimmed.Substring(0, firstSpace)
                let rest = trimmed.Substring(firstSpace + 1).Trim()
                let opSpace = rest.IndexOf ' '

                if opSpace <= 0 then
                    UnsupportedFilter trimmed
                else
                    let op = rest.Substring(0, opSpace)
                    let value = rest.Substring(opSpace + 1)

                    if not (String.Equals(op, "eq", StringComparison.OrdinalIgnoreCase)) then
                        UnsupportedFilter trimmed
                    else
                        match quoted value, attr.ToLowerInvariant() with
                        | Some v, "username" -> UserNameEquals v
                        | Some v, "externalid" -> ExternalIdEquals v
                        | Some v, "displayname" -> DisplayNameEquals v
                        | _ -> UnsupportedFilter trimmed

// ─── PATCH (RFC 7644 §3.5.2) ─────────────────────────────────────────

type ScimPatchVerb =
    | PatchAdd
    | PatchReplace
    | PatchRemove

/// The polymorphic `Operations[].value`. Decoded by shape rather than
/// by a declared type, because the wire gives no type tag — the sibling
/// `path` is the only hint, and Entra omits `path` on a replace whose
/// value is an object of attributes.
type ScimPatchValue =
    /// `"value": true` — the `active` flag's usual shape.
    | PatchBool of bool
    | PatchString of string
    /// `"value": [ { "value": "user-id" } ]` — a members mutation.
    | PatchMembers of ScimGroupMember list
    /// `"value": { "active": false, ... }` — a path-less replace whose
    /// object carries the attributes. Decoded to the pairs it holds so
    /// the handler can read `active` without re-parsing JSON.
    | PatchAttributes of (string * ScimPatchScalar) list
    /// Present but of a shape this provider does not interpret.
    | PatchNoValue

and ScimPatchScalar =
    | ScalarBool of bool
    | ScalarString of string
    | ScalarOther

type ScimPatchOperation = {
    Op: ScimPatchVerb
    /// The attribute path. Absent on Entra's path-less replace.
    /// A value filter (`members[value eq "x"]`) is carried verbatim —
    /// `ScimPatchPath` below is what interprets it.
    Path: string option
    Value: ScimPatchValue
}

type ScimPatchRequest = { Operations: ScimPatchOperation list }

/// The interpreted form of an `Operations[].path`. Only the paths this
/// provider acts on are named; everything else is `OtherPath`, which the
/// handler ignores rather than failing the whole PATCH — RFC 7644
/// §3.5.2 makes a PATCH atomic, but an unknown attribute is not an
/// error, and failing on one would make every IdP that decorates its
/// requests unable to provision at all.
type ScimPatchPath =
    | ActivePath
    | MembersPath
    /// `members[value eq "<id>"]` — a targeted member removal, which is
    /// how both Entra and Okta deprovision one member of a group.
    | MemberValuePath of userId: string
    | UserNamePath
    | DisplayNamePath
    | OtherPath of string

module ScimPatchPath =
    let parse (path: string) : ScimPatchPath =
        let p = path.Trim()
        let lower = p.ToLowerInvariant()

        if lower = "active" then
            ActivePath
        elif lower = "username" then
            UserNamePath
        elif lower = "displayname" then
            DisplayNamePath
        elif lower = "members" then
            MembersPath
        elif lower.StartsWith "members[" && lower.EndsWith "]" then
            // `members[value eq "abc"]` — recover the quoted id.
            let inner = p.Substring("members[".Length, p.Length - "members[".Length - 1)

            match ScimFilter.parse inner with
            | UnsupportedFilter _ ->
                // The inner expression is `value eq "..."`, which
                // `ScimFilter.parse` does not name (it is not a
                // resource attribute). Recover the quoted literal.
                let firstQuote = inner.IndexOf '"'
                let lastQuote = inner.LastIndexOf '"'

                if firstQuote >= 0 && lastQuote > firstQuote then
                    MemberValuePath(inner.Substring(firstQuote + 1, lastQuote - firstQuote - 1))
                else
                    OtherPath p
            | _ -> OtherPath p
        else
            OtherPath p

// ─── Errors ──────────────────────────────────────────────────────────

/// RFC 7644 §3.12 error response. `Status` is the HTTP code as a
/// STRING on the wire, which is a genuine oddity of the spec and a
/// frequent interop break — an IdP parsing `"status": 404` as a number
/// fails. It is a string here for that reason.
type ScimError = {
    Status: int
    /// `scimType` — the spec's typed error keyword (`invalidFilter`,
    /// `uniqueness`, `mutability`, `invalidValue`, `invalidSyntax`,
    /// `noTarget`). `None` for a plain status-only error.
    ScimType: string option
    Detail: string
}

module ScimError =
    let create (status: int) (detail: string) : ScimError = {
        Status = status
        ScimType = None
        Detail = detail
    }

    let typed (status: int) (scimType: string) (detail: string) : ScimError = {
        Status = status
        ScimType = Some scimType
        Detail = detail
    }

    let notFound (detail: string) = create 404 detail
    let unauthorized (detail: string) = create 401 detail

    let uniqueness (detail: string) = typed 409 "uniqueness" detail

    let invalidValue (detail: string) = typed 400 "invalidValue" detail

    let invalidSyntax (detail: string) = typed 400 "invalidSyntax" detail

    /// RFC 7644 §3.4.2.2 — an unsupported filter is answered `501` with
    /// `scimType: invalidFilter`.
    let invalidFilter (expression: string) =
        typed 501 "invalidFilter" (sprintf "Filter expression is not supported by this service provider: %s" expression)

// ─── Attribute mapping, declared as data ─────────────────────────────

/// Which SCIM attribute supplies the platform `userId`.
///
/// A deployment's choice here is not cosmetic: it is the join key
/// between the IdP's directory and the platform's membership rows, and
/// changing it after provisioning has begun orphans every member added
/// under the old rule. Declared as data so it is visible in the
/// composition rather than buried in a handler.
type ScimIdentitySource =
    /// `userName` — the RFC's own unique attribute. The default.
    | FromUserName
    /// The primary `emails[]` entry. Common where the platform's user
    /// ids are already email addresses.
    | FromPrimaryEmail
    /// `externalId` — the IdP's own immutable id. The most stable
    /// choice, and the only one that survives a rename in the IdP.
    | FromExternalId

/// SCIM `Group` → platform `TeamRole`. A group whose `displayName`
/// matches an entry takes that role; anything unmatched takes
/// `Default`. Comparison is ordinal-ignore-case, because IdP group
/// names are administrator-typed.
type ScimRoleMapping = {
    ByGroupName: Map<string, TeamRole>
    Default: TeamRole
}

module ScimRoleMapping =
    /// Every provisioned member lands as `Member`. The conservative
    /// default: an IdP push can never mint an Owner by accident, so a
    /// misconfigured group name degrades to least privilege rather than
    /// most (GP 4).
    let defaults: ScimRoleMapping = {
        ByGroupName = Map.empty
        Default = Member
    }

    let withGroup (groupName: string) (role: TeamRole) (m: ScimRoleMapping) : ScimRoleMapping = {
        m with
            ByGroupName = m.ByGroupName.Add(groupName.ToLowerInvariant(), role)
    }

    let resolve (groupName: string) (m: ScimRoleMapping) : TeamRole =
        match m.ByGroupName.TryFind(groupName.ToLowerInvariant()) with
        | Some r -> r
        | None -> m.Default

/// The whole SCIM ↔ platform attribute mapping, as one value.
type ScimAttributeMapping = {
    Identity: ScimIdentitySource
    Roles: ScimRoleMapping
}

module ScimAttributeMapping =
    let defaults: ScimAttributeMapping = {
        Identity = FromUserName
        Roles = ScimRoleMapping.defaults
    }

    /// Resolve the platform user id this SCIM user maps to. `Error`
    /// when the source attribute is absent — a provisioning request
    /// with no join key is refused rather than given a synthetic id,
    /// because a synthetic id is unmatchable on the deprovision leg.
    let userId (mapping: ScimAttributeMapping) (u: ScimUser) : Result<string, string> =
        let nonEmpty (label: string) (v: string option) =
            match v with
            | Some s when not (String.IsNullOrWhiteSpace s) -> Ok(s.Trim())
            | _ -> Error(sprintf "SCIM user has no usable '%s' attribute to map onto a platform user id" label)

        match mapping.Identity with
        | FromUserName -> nonEmpty "userName" (Some u.UserName)
        | FromPrimaryEmail -> nonEmpty "emails" (ScimUser.primaryEmail u)
        | FromExternalId -> nonEmpty "externalId" u.ExternalId

// ─── JSON codec ──────────────────────────────────────────────────────

/// Explicit SCIM wire codec. See the file header for why this is
/// hand-rolled rather than record-mapped.
module ScimJson =

    let private writerOptions = JsonWriterOptions(Indented = false)

    let private tryProp (el: JsonElement) (name: string) : JsonElement option =
        match el.TryGetProperty name with
        | true, v when v.ValueKind <> JsonValueKind.Null -> Some v
        | _ ->
            // SCIM attribute names are case-insensitive (RFC 7643
            // §2.1) and IdPs are inconsistent (`externalId` vs
            // `externalid`). Fall back to a case-insensitive scan
            // rather than silently dropping the attribute.
            if el.ValueKind <> JsonValueKind.Object then
                None
            else
                el.EnumerateObject()
                |> Seq.tryFind (fun p ->
                    String.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)
                    && p.Value.ValueKind <> JsonValueKind.Null)
                |> Option.map _.Value

    let private tryString (el: JsonElement) (name: string) : string option =
        tryProp el name
        |> Option.bind (fun v ->
            if v.ValueKind = JsonValueKind.String then
                Some(v.GetString())
            else
                None)

    let private tryBool (el: JsonElement) (name: string) : bool option =
        tryProp el name
        |> Option.bind (fun v ->
            match v.ValueKind with
            | JsonValueKind.True -> Some true
            | JsonValueKind.False -> Some false
            // Okta has been observed sending `"active": "true"`.
            | JsonValueKind.String ->
                match Boolean.TryParse(v.GetString()) with
                | true, b -> Some b
                | _ -> None
            | _ -> None)

    let private tryArray (el: JsonElement) (name: string) : JsonElement list =
        match tryProp el name with
        | Some v when v.ValueKind = JsonValueKind.Array -> v.EnumerateArray() |> List.ofSeq
        | _ -> []

    // ─── Decode ──────────────────────────────────────────────────

    let private decodeName (el: JsonElement) : ScimName =
        match tryProp el "name" with
        | Some n when n.ValueKind = JsonValueKind.Object -> {
            Formatted = tryString n "formatted"
            FamilyName = tryString n "familyName"
            GivenName = tryString n "givenName"
          }
        | _ -> ScimName.empty

    let private decodeEmails (el: JsonElement) : ScimEmail list =
        tryArray el "emails"
        |> List.choose (fun e ->
            match e.ValueKind with
            | JsonValueKind.Object ->
                tryString e "value"
                |> Option.map (fun v -> {
                    Value = v
                    Type = tryString e "type"
                    Primary = tryBool e "primary" |> Option.defaultValue false
                })
            | JsonValueKind.String ->
                Some {
                    Value = e.GetString()
                    Type = None
                    Primary = false
                }
            | _ -> None)

    let private decodeMembers (el: JsonElement) : ScimGroupMember list =
        tryArray el "members"
        |> List.choose (fun m ->
            match m.ValueKind with
            | JsonValueKind.Object ->
                tryString m "value"
                |> Option.map (fun v -> {
                    Value = v
                    Display = tryString m "display"
                    Type = tryString m "type"
                })
            | JsonValueKind.String ->
                Some {
                    Value = m.GetString()
                    Display = None
                    Type = None
                }
            | _ -> None)

    /// Decode a `User` resource. `active` defaults to **true** when
    /// absent — RFC 7643 §4.1.1 makes the attribute optional, and a
    /// create that omitted it meaning "provision this person" must not
    /// be read as a deactivation.
    let decodeUser (json: string) : Result<ScimUser, ScimError> =
        try
            use doc = JsonDocument.Parse json
            let root = doc.RootElement

            if root.ValueKind <> JsonValueKind.Object then
                Error(ScimError.invalidSyntax "SCIM request body must be a JSON object")
            else
                match tryString root "userName" with
                | None -> Error(ScimError.invalidValue "SCIM User resource requires a 'userName' attribute")
                | Some userName ->
                    Ok {
                        Id = tryString root "id" |> Option.defaultValue ""
                        ExternalId = tryString root "externalId"
                        UserName = userName
                        Name = decodeName root
                        DisplayName = tryString root "displayName"
                        Emails = decodeEmails root
                        Active = tryBool root "active" |> Option.defaultValue true
                        Meta = None
                    }
        with :? JsonException as ex ->
            Error(ScimError.invalidSyntax (sprintf "Malformed JSON in SCIM request body: %s" ex.Message))

    let decodeGroup (json: string) : Result<ScimGroup, ScimError> =
        try
            use doc = JsonDocument.Parse json
            let root = doc.RootElement

            if root.ValueKind <> JsonValueKind.Object then
                Error(ScimError.invalidSyntax "SCIM request body must be a JSON object")
            else
                match tryString root "displayName" with
                | None -> Error(ScimError.invalidValue "SCIM Group resource requires a 'displayName' attribute")
                | Some displayName ->
                    Ok {
                        Id = tryString root "id" |> Option.defaultValue ""
                        ExternalId = tryString root "externalId"
                        DisplayName = displayName
                        Members = decodeMembers root
                        Meta = None
                    }
        with :? JsonException as ex ->
            Error(ScimError.invalidSyntax (sprintf "Malformed JSON in SCIM request body: %s" ex.Message))

    let private decodeScalar (v: JsonElement) : ScimPatchScalar =
        match v.ValueKind with
        | JsonValueKind.True -> ScalarBool true
        | JsonValueKind.False -> ScalarBool false
        | JsonValueKind.String ->
            match Boolean.TryParse(v.GetString()) with
            | true, b -> ScalarBool b
            | _ -> ScalarString(v.GetString())
        | _ -> ScalarOther

    let private decodePatchValue (op: JsonElement) : ScimPatchValue =
        match tryProp op "value" with
        | None -> PatchNoValue
        | Some v ->
            match v.ValueKind with
            | JsonValueKind.True -> PatchBool true
            | JsonValueKind.False -> PatchBool false
            | JsonValueKind.String ->
                match Boolean.TryParse(v.GetString()) with
                | true, b -> PatchBool b
                | _ -> PatchString(v.GetString())
            | JsonValueKind.Array ->
                v.EnumerateArray()
                |> Seq.choose (fun m ->
                    match m.ValueKind with
                    | JsonValueKind.Object ->
                        tryString m "value"
                        |> Option.map (fun value -> {
                            Value = value
                            Display = tryString m "display"
                            Type = tryString m "type"
                        })
                    | JsonValueKind.String ->
                        Some {
                            Value = m.GetString()
                            Display = None
                            Type = None
                        }
                    | _ -> None)
                |> List.ofSeq
                |> PatchMembers
            | JsonValueKind.Object ->
                v.EnumerateObject()
                |> Seq.map (fun p -> p.Name, decodeScalar p.Value)
                |> List.ofSeq
                |> PatchAttributes
            | _ -> PatchNoValue

    let decodePatch (json: string) : Result<ScimPatchRequest, ScimError> =
        try
            use doc = JsonDocument.Parse json
            let root = doc.RootElement

            if root.ValueKind <> JsonValueKind.Object then
                Error(ScimError.invalidSyntax "SCIM PATCH body must be a JSON object")
            else
                let ops = tryArray root "Operations"

                if ops.IsEmpty then
                    Error(ScimError.invalidValue "SCIM PATCH requires a non-empty 'Operations' array")
                else
                    let decoded =
                        ops
                        |> List.choose (fun op ->
                            match tryString op "op" with
                            | None -> None
                            | Some verb ->
                                let parsed =
                                    match verb.ToLowerInvariant() with
                                    | "add" -> Some PatchAdd
                                    | "replace" -> Some PatchReplace
                                    | "remove" -> Some PatchRemove
                                    | _ -> None

                                parsed
                                |> Option.map (fun v -> {
                                    Op = v
                                    Path = tryString op "path"
                                    Value = decodePatchValue op
                                }))

                    if decoded.IsEmpty then
                        Error(
                            ScimError.invalidValue
                                "SCIM PATCH 'Operations' carried no recognised 'op' (add / replace / remove)"
                        )
                    else
                        Ok { Operations = decoded }
        with :? JsonException as ex ->
            Error(ScimError.invalidSyntax (sprintf "Malformed JSON in SCIM PATCH body: %s" ex.Message))

    // ─── Encode ──────────────────────────────────────────────────

    let private writeSchemas (w: Utf8JsonWriter) (urns: string list) =
        w.WriteStartArray "schemas"

        for urn in urns do
            w.WriteStringValue urn

        w.WriteEndArray()

    let private writeOptString (w: Utf8JsonWriter) (name: string) (v: string option) =
        match v with
        | Some s -> w.WriteString(name, s)
        | None -> ()

    let private writeMeta (w: Utf8JsonWriter) (meta: ScimMeta option) =
        match meta with
        | None -> ()
        | Some m ->
            w.WriteStartObject "meta"
            w.WriteString("resourceType", m.ResourceType)

            match m.Created with
            | Some c -> w.WriteString("created", c.ToUniversalTime().ToString("o"))
            | None -> ()

            match m.LastModified with
            | Some c -> w.WriteString("lastModified", c.ToUniversalTime().ToString("o"))
            | None -> ()

            writeOptString w "location" m.Location
            writeOptString w "version" m.Version
            w.WriteEndObject()

    let private writeUserBody (w: Utf8JsonWriter) (u: ScimUser) =
        writeSchemas w [ ScimSchemas.User ]
        w.WriteString("id", u.Id)
        writeOptString w "externalId" u.ExternalId
        w.WriteString("userName", u.UserName)

        if not (ScimName.isEmpty u.Name) then
            w.WriteStartObject "name"
            writeOptString w "formatted" u.Name.Formatted
            writeOptString w "familyName" u.Name.FamilyName
            writeOptString w "givenName" u.Name.GivenName
            w.WriteEndObject()

        writeOptString w "displayName" u.DisplayName

        if not u.Emails.IsEmpty then
            w.WriteStartArray "emails"

            for e in u.Emails do
                w.WriteStartObject()
                w.WriteString("value", e.Value)
                writeOptString w "type" e.Type

                if e.Primary then
                    w.WriteBoolean("primary", true)

                w.WriteEndObject()

            w.WriteEndArray()

        w.WriteBoolean("active", u.Active)
        writeMeta w u.Meta

    let private writeGroupBody (w: Utf8JsonWriter) (g: ScimGroup) =
        writeSchemas w [ ScimSchemas.Group ]
        w.WriteString("id", g.Id)
        writeOptString w "externalId" g.ExternalId
        w.WriteString("displayName", g.DisplayName)
        w.WriteStartArray "members"

        for m in g.Members do
            w.WriteStartObject()
            w.WriteString("value", m.Value)
            writeOptString w "display" m.Display
            writeOptString w "type" m.Type
            w.WriteEndObject()

        w.WriteEndArray()
        writeMeta w g.Meta

    let private toJson (write: Utf8JsonWriter -> unit) : string =
        use stream = new IO.MemoryStream()
        use w = new Utf8JsonWriter(stream, writerOptions)
        w.WriteStartObject()
        write w
        w.WriteEndObject()
        w.Flush()
        Encoding.UTF8.GetString(stream.ToArray())

    let encodeUser (u: ScimUser) : string = toJson (fun w -> writeUserBody w u)

    let encodeGroup (g: ScimGroup) : string = toJson (fun w -> writeGroupBody w g)

    let private encodeList
        (writeBody: Utf8JsonWriter -> 'a -> unit)
        (page: ScimPage)
        (totalResults: int)
        (resources: 'a list)
        : string =
        toJson (fun w ->
            writeSchemas w [ ScimSchemas.ListResponse ]
            w.WriteNumber("totalResults", totalResults)
            w.WriteNumber("startIndex", page.StartIndex)
            w.WriteNumber("itemsPerPage", List.length resources)
            w.WriteStartArray "Resources"

            for r in resources do
                w.WriteStartObject()
                writeBody w r
                w.WriteEndObject()

            w.WriteEndArray())

    let encodeUserList (page: ScimPage) (totalResults: int) (users: ScimUser list) : string =
        encodeList writeUserBody page totalResults users

    let encodeGroupList (page: ScimPage) (totalResults: int) (groups: ScimGroup list) : string =
        encodeList writeGroupBody page totalResults groups

    /// RFC 7644 §3.12. `status` is a STRING on the wire — see the
    /// `ScimError` doc comment.
    let encodeError (e: ScimError) : string =
        toJson (fun w ->
            writeSchemas w [ ScimSchemas.Error ]
            writeOptString w "scimType" e.ScimType
            w.WriteString("detail", e.Detail)
            w.WriteString("status", string e.Status))