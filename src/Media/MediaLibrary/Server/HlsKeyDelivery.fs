// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.MediaLibrary.HlsKeyDelivery

open System
open System.Security.Cryptography
open System.Text
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Primitives
open Giraffe
open ToolUp.Platform
open ToolUp.Platform.Secrets

// ─── Phase 471 — gated HLS: key material + scope-gated key delivery ───
//
// Phase 88 gave a gated media item a scope-signed URL, and Phase 86 gave
// a gated page an audience gate. Both protect ONE addressable thing.
// HLS does not have one addressable thing: a rendition is a manifest
// plus N segment blobs, and the instant those segments are statically
// exported or served from a CDN edge, the origin's route auth is not on
// the path any more. The bytes are simply there.
//
// So the segments are encrypted (AES-128, `#EXT-X-KEY`) and the KEY is
// what stays gated. Three pieces live here:
//
//   1. `MediaHlsKeyStore` — mints a per-media 16-byte key at transcode
//      time and keeps it in `ISecretStore` UNDER THE OWNING SCOPE,
//      never beside the segments (GP 4). A scope cannot address another
//      scope's key, because the scope container is the secret's own
//      first coordinate.
//   2. `decideAccess` — the pure gate. Two admitted routes, matching the
//      two ways the media itself is reachable: a resolved scope (the
//      same gate `streamHandler` applies) or a valid, unexpired
//      `SignedUrl` token for THIS media id (the same machinery
//      `signedHandler` applies). Pure so the matrix is exhaustively
//      testable without an HTTP context — the shape Phase 86's
//      `AudienceGate` established.
//   3. `keyHandler` — `GET /api/media/hls-key/{mediaId}`, plus the
//      manifest rewrite the serve path applies so key URIs survive
//      export and CDN caching.
//
// **Key rotation is a re-transcode.** There is deliberately no rotate
// verb. A key is bound to the ciphertext of the segments that were
// produced with it; handing out a new key without re-encrypting would
// make the rendition unplayable, and keeping both would make revocation
// meaningless. To rotate, re-upload (or re-derive) the item: the new
// pass mints a fresh key and overwrites the old one, and the old
// segments are replaced in the same act. `IMediaLibrary.Delete` takes
// the key with the item, so a deleted video leaves no live secret.

/// AES-128 — 16 bytes. Fixed by the `METHOD=AES-128` `#EXT-X-KEY`
/// attribute, not a tunable.
[<Literal>]
let KeyLengthBytes = 16

/// The `ISecretStore` key a media item's HLS key is filed under, within
/// the owning scope's container. Namespaced so it cannot collide with a
/// deployment's own secrets.
let secretName (id: MediaId) : string = "media_hls_key:" + MediaId.value id

/// The route the key endpoint is mounted on.
[<Literal>]
let RoutePrefix = "/api/media/hls-key/"

/// The root-relative key URI baked into a manifest at transcode time.
/// The serve path rewrites it to an origin-absolute URI (see
/// `absoluteKeyUri`) so an exported or CDN-served manifest still points
/// at the origin's gate rather than at whatever host is serving it.
let relativeKeyUri (id: MediaId) : string = RoutePrefix + MediaId.value id

// ─── 471.A — key material ─────────────────────────────────────────────

/// Mints, resolves and destroys per-media AES-128 HLS keys, held in
/// `ISecretStore` under the media item's own scope container.
///
/// Deliberately NOT memoised the way `MediaUrlSigner` memoises its
/// single deployment-wide signing key: there is one key PER MEDIA ITEM
/// and per scope, so a cache here would be an unbounded map of live key
/// material keyed by exactly the thing an attacker enumerates. The
/// secret store is the cache.
type MediaHlsKeyStore(secretStore: ISecretStore, logger: ILogger, options: MediaLibraryOptions) =

    /// Mint (or re-mint) this item's key and persist it under the
    /// owning scope. Re-minting is how rotation happens — see the
    /// module header: a fresh key is only ever paired with a fresh
    /// encryption pass over the segments.
    member _.Mint(scopeContainer: string, id: MediaId) : Async<Result<byte[], string>> = async {
        let key = RandomNumberGenerator.GetBytes KeyLengthBytes
        let encoded = Convert.ToBase64String key
        let! saved = secretStore.SetSecret(scopeContainer, secretName id, encoded)

        match saved with
        | Ok() -> return Ok key
        | Error e -> return Error(sprintf "couldn't persist the HLS key for %s: %s" (MediaId.value id) e)
    }

    /// Resolve this item's key within a scope. `Ok None` means "this
    /// scope has no such key" — which is the answer both for an
    /// unencrypted item and for a CROSS-SCOPE request, and the two are
    /// deliberately indistinguishable to the caller: the container is
    /// the isolation boundary, so a foreign scope learns nothing beyond
    /// "not here" (GP 4).
    member _.TryGet(scopeContainer: string, id: MediaId) : Async<Result<byte[] option, string>> = async {
        let! stored = secretStore.GetSecret(scopeContainer, secretName id)

        match stored with
        | None -> return Ok None
        | Some encoded ->
            try
                let bytes = Convert.FromBase64String encoded

                if bytes.Length = KeyLengthBytes then
                    return Ok(Some bytes)
                else
                    return Error(sprintf "the stored HLS key for %s is not %d bytes" (MediaId.value id) KeyLengthBytes)
            with _ ->
                return Error(sprintf "the stored HLS key for %s is not valid base64" (MediaId.value id))
    }

    /// Destroy this item's key. Best-effort and idempotent — called
    /// from `IMediaLibrary.Delete` so deleting a video does not leave a
    /// live secret behind it.
    member _.Delete(scopeContainer: string, id: MediaId) : Async<unit> = async {
        try
            let! _ = secretStore.DeleteSecret(scopeContainer, secretName id)
            ()
        with ex ->
            logger.Warn(sprintf "[MediaLibrary] HLS key delete failed for %s: %s" (MediaId.value id) ex.Message)
    }

    /// Log a granted key delivery. Gated on
    /// `MediaLibraryOptions.EmitAudit`.
    ///
    /// This is the STRUCTURED-LOG half of the trail and Phase 739 KEPT
    /// it rather than replacing it. The queryable half now exists —
    /// `AuditEvent.MediaKeyDelivered`, emitted unconditionally by
    /// `recordDelivery` below — and the two serve different readers: this
    /// line is what an operator tailing stdout sees while a playback
    /// problem is happening, the row is what a reviewer queries months
    /// later. Deleting it would remove behaviour a deployment already
    /// has, for no gain (GP 11); it costs nothing when the opt-in is off.
    member _.RecordDelivery(scopeContainer: string, id: MediaId, via: string) : unit =
        if options.EmitAudit then
            logger.Info(
                sprintf
                    "[MediaLibrary] hls-key delivered media=%s scope=%s via=%s"
                    (MediaId.value id)
                    scopeContainer
                    via
            )

// ─── 471.C — the gate, as a pure decision ─────────────────────────────

/// Outcome of gating one key request. `KeyAccessGranted` carries the
/// container the key must be resolved from — never a container the
/// caller supplied, always one the gate derived.
type HlsKeyAccess =
    /// Admitted. `container` is the scope to resolve the key in; `via`
    /// is the admitting route (`"scope"` / `"signature"`), carried for
    /// the audit trail.
    | KeyAccessGranted of container: string * via: string
    /// No credential at all — `401`.
    | KeyAccessUnauthenticated
    /// A credential that does not admit — `403`. `verdict` is the
    /// machine-readable denial code.
    | KeyAccessForbidden of verdict: string

/// The gate. `verification` is `None` when the request carried no
/// token at all, and `Some result` when it carried one — so a present
/// but bad token is a `403` rather than a silent fall-through to the
/// scope gate. That ordering is load-bearing: falling through would let
/// a caller with an ordinary session laundering an expired signature
/// look like a success, and would make the expired-signature case
/// untestable from the outside.
///
/// The signed route additionally requires the token's own `MediaId` to
/// equal the route's, so a token minted for one item cannot unlock
/// another's key.
let decideAccess
    (scopeContainer: string option)
    (verification: Result<SignedUrl.MediaSignedPayload, SignedUrlError> option)
    (routeMediaId: string)
    : HlsKeyAccess =
    match verification with
    | Some(Ok payload) when payload.MediaId = routeMediaId -> KeyAccessGranted(payload.Container, "signature")
    | Some(Ok _) -> KeyAccessForbidden "media_id_mismatch"
    | Some(Error SignedUrlError.Expired) -> KeyAccessForbidden "expired_signature"
    | Some(Error SignedUrlError.Malformed) -> KeyAccessForbidden "malformed_signature"
    | Some(Error _) -> KeyAccessForbidden "invalid_signature"
    | None ->
        match scopeContainer with
        | Some container -> KeyAccessGranted(container, "scope")
        | None -> KeyAccessUnauthenticated

// ─── 471.D — manifest rewrite on serve ────────────────────────────────

/// Is this derived file an HLS manifest? Manifests are the only derived
/// blob the serve path rewrites; segments are opaque ciphertext.
let isManifest (relativePath: string) : bool =
    relativePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)

let private keyTagPrefixes = [| "#EXT-X-KEY:"; "#EXT-X-SESSION-KEY:" |]

let private isKeyTag (line: string) =
    keyTagPrefixes
    |> Array.exists (fun p -> line.StartsWith(p, StringComparison.Ordinal))

let private rewriteKeyLine (absolute: string) (line: string) =
    let marker = "URI=\""
    let i = line.IndexOf(marker, StringComparison.Ordinal)

    if i < 0 then
        line
    else
        let valueStart = i + marker.Length
        let close = line.IndexOf('"', valueStart)

        if close < 0 then
            line
        else
            line.Substring(0, valueStart) + absolute + line.Substring(close)

/// Rewrite every `#EXT-X-KEY` / `#EXT-X-SESSION-KEY` URI in an HLS
/// manifest to `absolute`.
///
/// **A manifest with no key tag comes back byte-for-byte identical** —
/// same string, same line endings, same trailing newline or absence of
/// one. That is the property the "unencrypted media is untouched"
/// acceptance criterion rests on, so the fast path is an explicit
/// early return rather than an accident of the rewriting loop.
///
/// Line endings are preserved individually (a manifest mixing LF and
/// CRLF keeps both), because a manifest is a byte artefact a player
/// may have hashed.
let rewriteKeyUris (absolute: string) (manifest: string) : string =
    let hasKeyTag =
        keyTagPrefixes
        |> Array.exists (fun p -> manifest.IndexOf(p, StringComparison.Ordinal) >= 0)

    if not hasKeyTag then
        manifest
    else
        let sb = StringBuilder(manifest.Length + 128)
        let mutable pos = 0

        while pos < manifest.Length do
            let nl = manifest.IndexOf('\n', pos)
            let lineEnd = if nl < 0 then manifest.Length else nl

            // Keep a trailing CR out of the line content so a CRLF
            // manifest's tags still match the `#EXT-X-…:` prefixes,
            // then re-emit it verbatim.
            let contentEnd =
                if lineEnd > pos && manifest[lineEnd - 1] = '\r' then
                    lineEnd - 1
                else
                    lineEnd

            let line = manifest.Substring(pos, contentEnd - pos)

            sb.Append(if isKeyTag line then rewriteKeyLine absolute line else line)
            |> ignore

            sb.Append(manifest.Substring(contentEnd, lineEnd - contentEnd)) |> ignore

            if nl >= 0 then
                sb.Append '\n' |> ignore

            pos <- lineEnd + 1

        sb.ToString()

/// The origin-absolute key URI to write into a manifest being served
/// for `id`, derived from the request that asked for the manifest.
///
/// Absolute, not root-relative, because the manifest may be consumed
/// from somewhere that is not this origin — a static export, or a CDN
/// edge serving the cached segments (the Phase 472 scenario). A
/// root-relative URI would resolve against the CDN host, where there is
/// no key endpoint and no gate.
///
/// A `token` on the incoming request is CARRIED THROUGH onto the key
/// URI. That is what makes signed playback work end to end without a
/// second token species: the token that admitted the manifest fetch is
/// bound to the same media id, so it admits the key fetch too, for the
/// same TTL, from the same signing key.
let absoluteKeyUri (ctx: HttpContext) (id: MediaId) : string =
    let req = ctx.Request

    let pathBase = if req.PathBase.HasValue then req.PathBase.Value else ""

    let path =
        sprintf "%s://%s%s%s%s" req.Scheme (req.Host.Value) pathBase RoutePrefix (MediaId.value id)

    match req.Query.TryGetValue "token" with
    | true, v when not (String.IsNullOrEmpty(v.ToString())) ->
        sprintf "%s?token=%s" path (Uri.EscapeDataString(v.ToString()))
    | _ -> path

// ─── The endpoint ─────────────────────────────────────────────────────

/// Resolve an optional service. `option` rather than a nullable
/// reference because these are F#-declared types, which do not admit
/// `null` as a value — and because every consumer below genuinely has
/// an "absent" branch: the endpoint degrades rather than throwing when
/// a deployment composed no audit hook or no signer.
let private service<'T> (ctx: HttpContext) : 'T option =
    match ctx.RequestServices with
    | null -> None
    | sp ->
        match sp.GetService typeof<'T> with
        | null -> None
        | resolved -> Some(resolved :?> 'T)

let private scopeContainerOf (ctx: HttpContext) : string option =
    match ctx.Items.TryGetValue "ToolUp.StorageScope" with
    | true, (:? StorageScope as s) -> Some s.Container
    | _ -> None

let private subjectOf (ctx: HttpContext) : Subject =
    match ctx.Items.TryGetValue "ToolUp.Subject" with
    | true, (:? Subject as s) -> s
    | _ -> AnonymousSession ""

/// Emit a denial through the uniform Phase 120 authz-denial seam, so a
/// key-endpoint probe lands in the same queryable trail as every other
/// authorization denial in the deployment (GP 6). Best-effort and
/// never on the response's critical path — a hook failure must not
/// change what the caller sees.
let private recordDenial (ctx: HttpContext) (verdict: string) (viaSignature: bool) =
    match service<IAuthAuditHook> ctx with
    | None -> ()
    | Some hook ->
        let denial: AuthDenial = {
            Route = "GET " + RoutePrefix
            Subject = subjectOf ctx
            Requirement =
                if viaSignature then
                    // A signed-token validation failure — the same
                    // requirement class the share-token gate reports.
                    ShareTokenDenialRequirement
                else
                    SurfaceDenialRequirement
            Verdict = verdict
            Reason = "HLS key delivery refused"
            ScopeId = scopeContainerOf ctx
            CorrelationId = None
        }

        try
            hook.RecordDenial denial |> Async.Start
        with _ ->
            ()

/// Phase 739 — emit the queryable GRANT row, the twin of the denial
/// above. Best-effort and detached, exactly like `recordDenial`: a
/// downed audit pipeline must not turn a successful key delivery into a
/// failed one, and must not delay the response.
///
/// **Unconditional, not gated on `MediaLibraryOptions.EmitAudit`, and
/// that is the deliberate half of this phase.** The asymmetry Phase 739
/// exists to close is "refusals are queryable, grants are not"; gating
/// the grant row on an opt-in the denial row does not respect would
/// reproduce precisely that asymmetry inside any deployment that turned
/// the opt-in off — and it would do so silently, at the moment the trail
/// mattered. `EmitAudit` gates the STRUCTURED LOG, which is a volume
/// knob; the security rows on this endpoint are both unconditional so
/// the two halves are always present or both absent together.
///
/// **`container`, not the request's scope, is the row's scope id.** It
/// is the container the key was actually resolved from, which on the
/// signed route is the one bound into the token rather than any the
/// caller holds — so the row lands in the trail of the scope that owns
/// the media, which is the scope that asks who fetched its key.
///
/// A deployment composing no `IAuditLog` emits nothing and pays nothing
/// (GP 13), like every other optional seam this endpoint reaches for.
let private recordDelivery (ctx: HttpContext) (container: string) (id: MediaId) (via: string) =
    match service<IAuditLog> ctx with
    | None -> ()
    | Some log ->
        let subjectKind, subjectId = AuditSubject.sanitise (subjectOf ctx)

        let payload: MediaKeyDeliveredPayload = {
            MediaId = MediaId.value id
            SubjectKind = subjectKind
            SubjectId = subjectId
            ScopeContainer = container
            AdmissionRoute = via
            At = DateTime.UtcNow
        }

        try
            log.Record(container, MediaKeyDelivered payload) |> Async.Start
        with _ ->
            ()

/// `GET /api/media/hls-key/{mediaId}` — the gated key endpoint.
///
/// Admits on the SAME two credentials the media bytes themselves are
/// reachable by (a resolved scope, or a valid `SignedUrl` token for
/// this media id), so the key is never easier to obtain than the video
/// it decrypts. Responses are `Cache-Control: no-store` — a key sitting
/// in a shared cache would undo the entire phase, and the segments this
/// key opens are, by design, the things that DO get cached.
let keyHandler: HttpHandler =
    GET
    >=> routef "/api/media/hls-key/%s" (fun raw ->
        fun (_: HttpFunc) (ctx: HttpContext) -> task {
            let token =
                match ctx.Request.Query.TryGetValue "token" with
                | true, v -> v.ToString()
                | _ -> ""

            let! verification =
                match service<SignedUrl.MediaUrlSigner> ctx with
                | Some signer when not (String.IsNullOrEmpty token) -> task {
                    let! r = signer.VerifyAsync(token, DateTimeOffset.UtcNow) |> Async.StartAsTask
                    return Some r
                  }
                | _ -> System.Threading.Tasks.Task.FromResult None

            match decideAccess (scopeContainerOf ctx) verification raw with
            | KeyAccessUnauthenticated ->
                recordDenial ctx "authentication_required" false
                ctx.SetStatusCode 401
                return Some ctx
            | KeyAccessForbidden verdict ->
                recordDenial ctx verdict true
                ctx.SetStatusCode 403
                return Some ctx
            | KeyAccessGranted(container, via) ->
                match service<MediaHlsKeyStore> ctx with
                | None ->
                    // No key store composed — this deployment has no
                    // encrypted media, so no key exists to hand over.
                    ctx.SetStatusCode 404
                    return Some ctx
                | Some store ->
                    let id = MediaId raw

                    match! store.TryGet(container, id) |> Async.StartAsTask with
                    | Error _ ->
                        // The key may well exist but could not be read —
                        // an outage, not an absence. Saying `404` here
                        // would tell a caller the item is unencrypted,
                        // which is both wrong and useful to the wrong
                        // person.
                        ctx.SetStatusCode 500
                        return Some ctx
                    | Ok None ->
                        // No key in THIS scope: either the item is not
                        // encrypted, or it belongs to another scope.
                        // One answer for both (GP 4) — a cross-scope
                        // caller learns nothing beyond "not here".
                        ctx.SetStatusCode 404
                        return Some ctx
                    | Ok(Some key) ->
                        // Both halves of the trail, at the one choke
                        // point, AFTER the key resolved and before it
                        // reaches the wire. The ordering is deliberate:
                        // the act being recorded is the RELEASE of key
                        // material, which is settled the moment the
                        // resolve succeeds — a row written only after a
                        // completed socket write would go missing on a
                        // client that disconnected mid-transfer, i.e.
                        // exactly the fetch a reviewer most wants to see.
                        store.RecordDelivery(container, id, via)
                        recordDelivery ctx container id via
                        ctx.Response.Headers["Cache-Control"] <- StringValues "no-store"
                        ctx.Response.Headers["Pragma"] <- StringValues "no-cache"
                        ctx.Response.ContentType <- "application/octet-stream"
                        ctx.Response.StatusCode <- 200
                        ctx.Response.ContentLength <- Nullable(int64 key.Length)
                        do! ctx.Response.Body.WriteAsync(key, 0, key.Length)
                        return Some ctx
        })