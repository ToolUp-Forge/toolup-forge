// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.ScimRoutes

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Giraffe
open ToolUp.Platform
open ToolUp.Platform.TeamManagement
open ToolUp.Platform.PermissionStore
open ToolUp.Platform.Secrets
open ToolUp.AuthProviders.ScimTypes
open ToolUp.AuthProviders.ScimHandler

// ─── SCIM 2.0 endpoint host ──────────────────────────────────────────
//
// The Giraffe surface over `ScimHandler`, plus the companion
// composition root. Nothing semantic lives here — this file resolves
// substrate from DI, gates the bearer token, parses the query surface,
// and marshals `Result<_, ScimError>` onto HTTP.
//
// **Opt-in (GP 13).** `ScimServerApp.create` starts at `NoScim`, and
// `run` on a `NoScim` app is `ServerApp.run app.Base` with an
// untouched `ComposeExtensions` — no handler is appended, no DI
// singleton is registered, no allocation is made. A deployment that
// does not call `withScim` is byte-for-byte the deployment it was
// before this companion existed.
//
// The mode lives on THIS record rather than on `ServerConfig`
// deliberately: `ServerConfig` is `ToolUp.Platform.Core` surface, and
// widening it to name a companion would put SCIM into the dependency
// graph of every consumer that has never heard of it — the inverse of
// GP 1. `PeerCompose` reads its mode from `ServerConfig` because the
// peer substrate is composed by the SDK's own strip-imports gate;
// nothing in the SDK needs to know whether SCIM is mounted.

[<Literal>]
let ScimContentType = "application/scim+json; charset=utf-8"

// ─── Substrate resolution ────────────────────────────────────────────

let private resolve<'T when 'T: not struct> (ctx: HttpContext) : 'T option =
    match ctx.RequestServices.GetService(typeof<'T>) with
    | :? 'T as s -> Some s
    | _ -> None

// ─── Responses ───────────────────────────────────────────────────────

let private scimJson (status: int) (payload: string) : HttpHandler =
    setStatusCode status
    >=> setHttpHeader "Content-Type" ScimContentType
    >=> setBodyFromString payload

let private scimError (e: ScimError) : HttpHandler =
    scimJson e.Status (ScimJson.encodeError e)

let private noContent: HttpHandler = setStatusCode 204

// ─── Bearer gate ─────────────────────────────────────────────────────
//
// Fail-closed by construction: the token is read from `ISecretStore`
// on every request (so a rotation takes effect immediately, matching
// the audit-sink convention), and every path that cannot produce a
// configured token refuses. There is no "no token configured means open"
// branch — a misconfigured deployment serves 401s rather than an
// unauthenticated provisioning endpoint.

/// Constant-time comparison of the presented token against the
/// configured one. `FixedTimeEquals` requires equal-length spans and
/// throws otherwise, so the length check is separate — which does leak
/// the token LENGTH by timing, and is the standard, accepted shape
/// (`JwtPeerAuthProvider` and the Stripe webhook verifier both take it):
/// the alternative is hashing both sides, and a length oracle on a
/// random 32-byte token buys an attacker nothing.
let tokensMatch (presented: string) (configured: string) : bool =
    if String.IsNullOrEmpty presented || String.IsNullOrEmpty configured then
        false
    else
        let a = Encoding.UTF8.GetBytes presented
        let b = Encoding.UTF8.GetBytes configured

        a.Length = b.Length
        && CryptographicOperations.FixedTimeEquals(ReadOnlySpan a, ReadOnlySpan b)

/// Extract the bearer token from an `Authorization` header. Returns
/// `None` for a missing header, a non-Bearer scheme, or an empty token.
let bearerToken (ctx: HttpContext) : string option =
    match ctx.Request.Headers.TryGetValue "Authorization" with
    | true, values ->
        let raw = (string values).Trim()

        if raw.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) then
            let token = raw.Substring("Bearer ".Length).Trim()

            if String.IsNullOrEmpty token then None else Some token
        else
            None
    | _ -> None

let private authorize (config: ScimConfig) (ctx: HttpContext) : Async<Result<unit, ScimError>> = async {
    match bearerToken ctx with
    | None -> return Error(ScimError.unauthorized "A Bearer token is required on every SCIM request")
    | Some presented ->
        match resolve<ISecretStore> ctx with
        | None ->
            // No secret store composed: the endpoint cannot know its own
            // token, so it refuses. Fail closed.
            return Error(ScimError.unauthorized "SCIM provisioning is not configured on this deployment")
        | Some secrets ->
            let! configured = secrets.GetSecret(config.SecretScope, config.TokenKey)

            match configured with
            | Some expected when tokensMatch presented expected -> return Ok()
            | _ -> return Error(ScimError.unauthorized "Invalid SCIM Bearer token")
}

// ─── Request plumbing ────────────────────────────────────────────────

let private deps (config: ScimConfig) (ctx: HttpContext) : Result<ScimDeps, ScimError> =
    match resolve<ITeamStore> ctx with
    | None ->
        Error(ScimError.create 500 "SCIM provisioning requires an ITeamStore, which this deployment has not composed")
    | Some teams ->
        Ok {
            Teams = teams
            Permissions = resolve<IPermissionStore> ctx
            Audit = resolve<IAuditLog> ctx
            Config = config
        }

let private queryValue (ctx: HttpContext) (name: string) : string option =
    match ctx.Request.Query.TryGetValue name with
    | true, values ->
        let v = string values

        if String.IsNullOrWhiteSpace v then None else Some v
    | _ -> None

let private readPage (ctx: HttpContext) : ScimPage =
    let readInt (name: string) (fallback: int) =
        match queryValue ctx name with
        | Some raw ->
            match Int32.TryParse raw with
            | true, v -> v
            | _ -> fallback
        | None -> fallback

    ScimPage.create (readInt "startIndex" 1) (readInt "count" ScimPage.DefaultCount)

let private readFilter (ctx: HttpContext) : ScimFilter =
    match queryValue ctx "filter" with
    | Some expr -> ScimFilter.parse expr
    | None -> NoFilter

/// Run `work` behind the bearer gate with resolved dependencies.
/// Everything that reaches `work` is authorised and has substrate.
let private gated (config: ScimConfig) (work: ScimDeps -> HttpContext -> Async<HttpHandler>) : HttpHandler =
    fun (next: HttpFunc) (ctx: HttpContext) -> task {
        let! authorised = authorize config ctx |> Async.StartImmediateAsTask

        match authorised with
        | Error e -> return! scimError e next ctx
        | Ok() ->
            match deps config ctx with
            | Error e -> return! scimError e next ctx
            | Ok d ->
                let! handler = work d ctx |> Async.StartImmediateAsTask
                return! handler next ctx
    }

let private readBody (ctx: HttpContext) : Task<string> = ctx.ReadBodyFromRequestAsync()

let private respond (result: Result<string, ScimError>) (successStatus: int) : HttpHandler =
    match result with
    | Ok payload -> scimJson successStatus payload
    | Error e -> scimError e

// ─── Discovery documents (RFC 7644 §4) ───────────────────────────────
//
// An IdP reads these BEFORE it provisions anything, to decide which
// operations to send. They are static because this provider's
// capabilities are static — declaring `patch: true` and `filter:
// supported` with a `maxResults` an IdP can plan against, and declaring
// the three things it genuinely does not do (bulk, sort, ETag) as
// `supported: false` rather than omitting them, because an omitted
// capability is read as "unknown" and provokes a probe.

// The documents are built with `Utf8JsonWriter`, the same way
// `ScimJson` builds every other response, rather than written as string
// literals. JSON is dense in doubled braces and in quotes, which makes a
// literal of this size a standing quoting hazard in either direction —
// an interpolated string reads the braces as delimiters, and a
// triple-quoted one cannot end on a quote. Building it also keeps the
// URNs single-sourced from `ScimSchemas` instead of spelled out twice.

let private buildDoc (write: Utf8JsonWriter -> unit) : string =
    use stream = new IO.MemoryStream()
    use w = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = false))
    w.WriteStartObject()
    write w
    w.WriteEndObject()
    w.Flush()
    Encoding.UTF8.GetString(stream.ToArray())

let private writeSchemasArray (w: Utf8JsonWriter) (urn: string) =
    w.WriteStartArray "schemas"
    w.WriteStringValue urn
    w.WriteEndArray()

/// `{"supported": <b>}` — the shape every capability flag in
/// `ServiceProviderConfig` takes.
let private writeSupported (w: Utf8JsonWriter) (name: string) (supported: bool) =
    w.WriteStartObject name
    w.WriteBoolean("supported", supported)
    w.WriteEndObject()

let private serviceProviderConfig (config: ScimConfig) : string =
    let location =
        config.BaseUrl
        |> Option.map (fun (b: string) -> b.TrimEnd '/' + "/scim/v2/ServiceProviderConfig")
        |> Option.defaultValue "/scim/v2/ServiceProviderConfig"

    buildDoc (fun w ->
        writeSchemasArray w ScimSchemas.ServiceProviderConfig
        w.WriteString("documentationUri", "https://datatracker.ietf.org/doc/html/rfc7644")
        writeSupported w "patch" true

        w.WriteStartObject "bulk"
        w.WriteBoolean("supported", false)
        w.WriteNumber("maxOperations", 0)
        w.WriteNumber("maxPayloadSize", 0)
        w.WriteEndObject()

        w.WriteStartObject "filter"
        w.WriteBoolean("supported", true)
        w.WriteNumber("maxResults", ScimPage.MaxCount)
        w.WriteEndObject()

        writeSupported w "changePassword" false
        writeSupported w "sort" false
        writeSupported w "etag" false

        w.WriteStartArray "authenticationSchemes"
        w.WriteStartObject()
        w.WriteString("type", "oauthbearertoken")
        w.WriteString("name", "OAuth Bearer Token")
        w.WriteString("description", "Authentication scheme using the OAuth Bearer Token Standard")
        w.WriteString("specUri", "https://datatracker.ietf.org/doc/html/rfc6750")
        w.WriteBoolean("primary", true)
        w.WriteEndObject()
        w.WriteEndArray()

        w.WriteStartObject "meta"
        w.WriteString("resourceType", "ServiceProviderConfig")
        w.WriteString("location", location)
        w.WriteEndObject())

let private writeResourceType (w: Utf8JsonWriter) (name: string) (description: string) (schema: string) =
    w.WriteStartObject()
    writeSchemasArray w ScimSchemas.ResourceType
    w.WriteString("id", name)
    w.WriteString("name", name)
    w.WriteString("endpoint", "/" + name + "s")
    w.WriteString("description", description)
    w.WriteString("schema", schema)
    w.WriteStartObject "meta"
    w.WriteString("resourceType", "ResourceType")
    w.WriteString("location", "/scim/v2/ResourceTypes/" + name)
    w.WriteEndObject()
    w.WriteEndObject()

let private resourceTypes: string =
    buildDoc (fun w ->
        writeSchemasArray w ScimSchemas.ListResponse
        w.WriteNumber("totalResults", 2)
        w.WriteNumber("startIndex", 1)
        w.WriteNumber("itemsPerPage", 2)
        w.WriteStartArray "Resources"
        writeResourceType w "User" "User Account" ScimSchemas.User
        writeResourceType w "Group" "Group" ScimSchemas.Group
        w.WriteEndArray())

/// One `attributes[]` entry of a `/Schemas` document.
let private writeAttribute
    (w: Utf8JsonWriter)
    (name: string)
    (attrType: string)
    (multiValued: bool)
    (required: bool)
    (uniqueness: string option)
    =
    w.WriteStartObject()
    w.WriteString("name", name)
    w.WriteString("type", attrType)
    w.WriteBoolean("multiValued", multiValued)
    w.WriteBoolean("required", required)
    w.WriteBoolean("caseExact", false)
    w.WriteString("mutability", "readWrite")
    w.WriteString("returned", "default")

    match uniqueness with
    | Some u -> w.WriteString("uniqueness", u)
    | None -> ()

    w.WriteEndObject()

/// The `/Schemas` document, listing ONLY the attributes this provider
/// actually reads or writes. Declaring the RFC's full attribute set
/// would be a lie an IdP acts on — Entra maps every declared attribute
/// and then reports a per-attribute sync error for each one the provider
/// silently drops.
let private schemas: string =
    buildDoc (fun w ->
        writeSchemasArray w ScimSchemas.ListResponse
        w.WriteNumber("totalResults", 2)
        w.WriteNumber("startIndex", 1)
        w.WriteNumber("itemsPerPage", 2)
        w.WriteStartArray "Resources"

        w.WriteStartObject()
        w.WriteString("id", ScimSchemas.User)
        w.WriteString("name", "User")
        w.WriteString("description", "User Account")
        w.WriteStartArray "attributes"
        writeAttribute w "userName" "string" false true (Some "server")
        writeAttribute w "externalId" "string" false false (Some "none")
        writeAttribute w "displayName" "string" false false (Some "none")
        writeAttribute w "emails" "complex" true false None
        writeAttribute w "active" "boolean" false false None
        w.WriteEndArray()
        w.WriteStartObject "meta"
        w.WriteString("resourceType", "Schema")
        w.WriteString("location", "/scim/v2/Schemas/" + ScimSchemas.User)
        w.WriteEndObject()
        w.WriteEndObject()

        w.WriteStartObject()
        w.WriteString("id", ScimSchemas.Group)
        w.WriteString("name", "Group")
        w.WriteString("description", "Group")
        w.WriteStartArray "attributes"
        writeAttribute w "displayName" "string" false true (Some "none")
        writeAttribute w "members" "complex" true false None
        w.WriteEndArray()
        w.WriteStartObject "meta"
        w.WriteString("resourceType", "Schema")
        w.WriteString("location", "/scim/v2/Schemas/" + ScimSchemas.Group)
        w.WriteEndObject()
        w.WriteEndObject()

        w.WriteEndArray())


// ─── Routes ──────────────────────────────────────────────────────────

/// The whole `/scim/v2/*` surface for one configured endpoint.
///
/// Route ORDER is load-bearing: Giraffe's `choose` takes the first
/// match, and `routef "/scim/v2/Users/%s"` would otherwise swallow
/// `/scim/v2/Users` on a provider that allowed an empty capture. The
/// discovery documents are listed first because they are the most
/// specific literals, then the collection routes, then the
/// item routes.
let routes (config: ScimConfig) : HttpHandler =
    choose [
        // Discovery — unauthenticated by RFC 7644 §4 convention? No:
        // this provider gates them too. An IdP always presents its
        // token, and an ungated discovery document is a free
        // fingerprint of the deployment's provisioning posture.
        GET
        >=> route "/scim/v2/ServiceProviderConfig"
        >=> gated config (fun _ _ -> async { return scimJson 200 (serviceProviderConfig config) })

        GET
        >=> route "/scim/v2/ResourceTypes"
        >=> gated config (fun _ _ -> async { return scimJson 200 resourceTypes })

        GET
        >=> route "/scim/v2/Schemas"
        >=> gated config (fun _ _ -> async { return scimJson 200 schemas })

        // Users — collection
        GET
        >=> route "/scim/v2/Users"
        >=> gated config (fun d ctx -> async {
            let! result = listUsers d (readPage ctx) (readFilter ctx)
            return respond result 200
        })

        POST
        >=> route "/scim/v2/Users"
        >=> gated config (fun d ctx -> async {
            let! body = readBody ctx |> Async.AwaitTask
            let! result = createUser d body
            return respond result 201
        })

        // Users — item
        GET
        >=> routef "/scim/v2/Users/%s" (fun userId ->
            gated config (fun d _ -> async {
                let! result = getUser d (Uri.UnescapeDataString userId)
                return respond result 200
            }))

        PUT
        >=> routef "/scim/v2/Users/%s" (fun userId ->
            gated config (fun d ctx -> async {
                let! body = readBody ctx |> Async.AwaitTask
                let! result = replaceUser d (Uri.UnescapeDataString userId) body
                return respond result 200
            }))

        PATCH
        >=> routef "/scim/v2/Users/%s" (fun userId ->
            gated config (fun d ctx -> async {
                let! body = readBody ctx |> Async.AwaitTask
                let! result = patchUser d (Uri.UnescapeDataString userId) body

                // A PATCH that deprovisioned the user has no resource
                // left to return. RFC 7644 §3.5.2 permits `204` for a
                // PATCH whose result the client need not read, and it
                // is the honest answer here — a `200` carrying a
                // fabricated inactive user would claim a tombstone this
                // provider does not keep.
                match result with
                | Ok(Some payload) -> return scimJson 200 payload
                | Ok None -> return noContent
                | Error e -> return scimError e
            }))

        DELETE
        >=> routef "/scim/v2/Users/%s" (fun userId ->
            gated config (fun d _ -> async {
                let! result = deleteUser d (Uri.UnescapeDataString userId)

                match result with
                | Ok() -> return noContent
                | Error e -> return scimError e
            }))

        // Groups — collection
        GET
        >=> route "/scim/v2/Groups"
        >=> gated config (fun d ctx -> async {
            let! result = listGroups d (readPage ctx) (readFilter ctx)
            return respond result 200
        })

        // Group creation is deliberately refused. A team is a platform
        // concept with an Owner, a scope and a storage container; a
        // SCIM push cannot mint one meaningfully, and a provider that
        // accepted the POST and did nothing would leave the IdP
        // believing a group exists. `501` names the situation.
        POST
        >=> route "/scim/v2/Groups"
        >=> gated config (fun _ _ -> async {
            return
                scimError (
                    ScimError.create
                        501
                        "This service provider does not create groups; provision members into the pre-existing team this endpoint is bound to"
                )
        })

        // Groups — item
        GET
        >=> routef "/scim/v2/Groups/%s" (fun groupId ->
            gated config (fun d _ -> async {
                let! result = getGroup d (Uri.UnescapeDataString groupId)
                return respond result 200
            }))

        PUT
        >=> routef "/scim/v2/Groups/%s" (fun groupId ->
            gated config (fun d ctx -> async {
                let! body = readBody ctx |> Async.AwaitTask
                let! result = replaceGroup d (Uri.UnescapeDataString groupId) body
                return respond result 200
            }))

        PATCH
        >=> routef "/scim/v2/Groups/%s" (fun groupId ->
            gated config (fun d ctx -> async {
                let! body = readBody ctx |> Async.AwaitTask
                let! result = patchGroup d (Uri.UnescapeDataString groupId) body

                match result with
                | Ok(Some payload) -> return scimJson 200 payload
                | Ok None -> return noContent
                | Error e -> return scimError e
            }))

        // Deleting the team through SCIM is refused for the same reason
        // creation is.
        DELETE
        >=> routef "/scim/v2/Groups/%s" (fun _ ->
            gated config (fun _ _ -> async {
                return
                    scimError (
                        ScimError.create
                            501
                            "This service provider does not delete groups; remove the members instead"
                    )
            }))
    ]

// ─── Composition root ────────────────────────────────────────────────

/// Whether this deployment mounts the SCIM endpoint. `NoScim` is the
/// default and contributes nothing (GP 13).
type ScimMode =
    | NoScim
    | EnabledScim of ScimConfig

/// Companion composition root, in the shape `PeerServerApp` /
/// `RAGServerApp` take: wrap a base `ServerApp`, mount when enabled,
/// short-circuit to the base otherwise.
type ScimServerApp = { Base: ServerApp; Mode: ScimMode }

module ScimServerApp =

    let create () : ScimServerApp = {
        Base = ServerApp.empty
        Mode = NoScim
    }

    /// Wrap an already-composed `ServerApp`.
    let ofServerApp (app: ServerApp) : ScimServerApp = { Base = app; Mode = NoScim }

    /// Mount the SCIM endpoint for one team.
    let withScim (config: ScimConfig) (app: ScimServerApp) : ScimServerApp = { app with Mode = EnabledScim config }

    /// Compose and run. On `NoScim` this is `ServerApp.run app.Base`
    /// with an untouched `ComposeExtensions` — the whole point of the
    /// mode (GP 13).
    let run (app: ScimServerApp) : int =
        match app.Mode with
        | NoScim -> ServerApp.run app.Base
        | EnabledScim config ->
            let baseExt = app.Base.Extensions

            let merged: ComposeExtensions = {
                baseExt with
                    Handlers = baseExt.Handlers @ [ routes config ]
            }

            ServerApp.run { app.Base with Extensions = merged }