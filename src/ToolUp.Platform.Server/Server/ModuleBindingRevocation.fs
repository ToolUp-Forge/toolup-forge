// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes

// ─── Phase 215 — module-binding revocation list + transparency log ──────
//
// Two deploy-time artefacts that make signing (165) + the detachable stamp
// manifest (166) *trustworthy at scale*:
//
//   * a **revocation list** — a signed sidecar (`module-revocations.json`)
//     naming revoked anchors (whole-key kill switch for a compromised
//     signing key) and/or revoked individual stamps. The Phase 165 verifier
//     consults it after a stamp verifies and before it admits the module, so
//     a cryptographically-valid stamp under a revoked anchor is denied.
//   * a **transparency log** — an append-only record of every admit/deny the
//     gate made, so post-hoc "what loaded here, and when?" is answerable.
//
// This file is the **crypto-free** half (same posture as `ModuleBindingManifest`
// in Phase 166): the on-disk format, the parser, the `stampId` derivation
// the revocation author and the verifier must agree on, the in-memory
// `RevocationSet` → `IBindingRevocationList` adapter, and a simple
// file-backed `IBindingTransparencyLog`. The list is only *trustworthy* when
// its signature is verified — that detached-JWS verification reuses the
// Phase 40 primitives and therefore lives server-side in
// `ToolUp.ArtefactSigning` (`SignedRevocationList`), above this tier. A
// deployment that wants the convenience without a signature can `parse` /
// `load` directly here, accepting that an unsigned list is only as
// trustworthy as the filesystem it sits on.
//
// **GP 13 byte-identical:** no revocation list ⇒ the verifier is built via
// the existing `DefaultModuleBindingVerifier.create` (no list, no log) and
// behaves exactly as pre-215; an empty list (`RevocationSet.empty`) revokes
// nothing.

/// The deploy-time set of revoked anchors + stamps, parsed from
/// `module-revocations.json`. `RevokedAnchors` is a whole-key kill switch
/// (every stamp minted under that anchor id is denied); `RevokedStamps`
/// revokes one `(anchorId, stampId)` pair.
type RevocationSet = {
    RevokedAnchors: Set<string>
    RevokedStamps: Set<string * string>
}

/// The crypto-free revocation format + parser + `stampId` derivation + the
/// in-memory `IBindingRevocationList` adapter + a file-backed
/// `IBindingTransparencyLog`.
module ModuleBindingRevocation =

    /// Conventional revocation-list file name (alongside the deployed
    /// binary, beside `module-bindings.json`).
    [<Literal>]
    let DefaultFileName = "module-revocations.json"

    /// Conventional detached-JWS signature sidecar for the revocation list.
    [<Literal>]
    let DefaultSignatureFileName = "module-revocations.json.jws"

    /// Current revocation-list schema version. A reader rejects a higher
    /// major it does not understand rather than silently under-revoking.
    [<Literal>]
    let CurrentVersion = 1

    /// An empty revocation set — revokes nothing (the GP-13 default shape).
    let empty: RevocationSet = {
        RevokedAnchors = Set.empty
        RevokedStamps = Set.empty
    }

    /// A stable identifier for a presented stamp, agreed between the
    /// revocation-list author and the verifier: the base64url SHA-256 over
    /// the stamp's canonical material (the detached JWS for an asymmetric
    /// stamp; `keyId + ":" + tag` for a symmetric one). Recomputed from the
    /// stamp the gate is checking, so a revocation entry pins exactly one
    /// stamp value. Note this is the id of the *stamp*, independent of the
    /// module it is filed under — a revocation denies a specific stamp
    /// wherever it appears.
    let stampId (stamp: ModuleBindingStamp) : string =
        let material =
            match stamp with
            | JwsStamp detachedJws -> detachedJws
            | MacStamp(keyId, tag) -> keyId + ":" + tag

        let digest = SHA256.HashData(Encoding.UTF8.GetBytes material)
        Convert.ToBase64String(digest).TrimEnd('=').Replace('+', '-').Replace('/', '_')

    let private readStringArray (root: JsonElement) (name: string) : Result<string list, string> =
        match root.TryGetProperty name with
        | false, _ -> Ok [] // absent ⇒ empty (valid)
        | true, el when el.ValueKind = JsonValueKind.Array ->
            (Ok [], el.EnumerateArray())
            ||> Seq.fold (fun acc item ->
                match acc with
                | Error _ -> acc
                | Ok xs ->
                    if item.ValueKind = JsonValueKind.String then
                        Ok(item.GetString() :: xs)
                    else
                        Error(sprintf "revocation list '%s' must contain only strings" name))
            |> Result.map List.rev
        | true, _ -> Error(sprintf "revocation list '%s' must be a JSON array" name)

    let private readRevokedStamps (root: JsonElement) : Result<(string * string) list, string> =
        match root.TryGetProperty "revokedStamps" with
        | false, _ -> Ok []
        | true, el when el.ValueKind = JsonValueKind.Array ->
            (Ok [], el.EnumerateArray())
            ||> Seq.fold (fun acc item ->
                match acc with
                | Error _ -> acc
                | Ok pairs ->
                    let field (name: string) =
                        match item.TryGetProperty name with
                        | true, v when v.ValueKind = JsonValueKind.String -> Ok(v.GetString())
                        | _ -> Error(sprintf "each revokedStamps entry needs the string field '%s'" name)

                    match field "anchorId", field "stampId" with
                    | Ok aid, Ok sid -> Ok((aid, sid) :: pairs)
                    | Error e, _
                    | _, Error e -> Error e)
            |> Result.map List.rev
        | true, _ -> Error "revocation list 'revokedStamps' must be a JSON array"

    /// Parse a revocation-list JSON document into a `RevocationSet`. The
    /// format mirrors the Phase 166 manifest's shape:
    ///
    /// ```json
    /// {
    ///   "version": 1,
    ///   "revokedAnchors": [ "release-2025" ],
    ///   "revokedStamps":  [ { "anchorId": "release-2026", "stampId": "<base64url-sha256>" } ]
    /// }
    /// ```
    ///
    /// Both arrays are optional (absent ⇒ empty). A higher major version is
    /// rejected rather than silently under-revoked.
    let parse (json: string) : Result<RevocationSet, string> =
        try
            use doc = JsonDocument.Parse json
            let root = doc.RootElement

            let versionOk =
                match root.TryGetProperty "version" with
                | true, v when v.ValueKind = JsonValueKind.Number -> v.GetInt32() <= CurrentVersion
                | _ -> true // version is advisory; absent ⇒ assume current

            if not versionOk then
                Error(
                    sprintf "module-revocation list version is newer than this SDK understands (max %d)" CurrentVersion
                )
            else
                match readStringArray root "revokedAnchors", readRevokedStamps root with
                | Ok anchors, Ok stamps ->
                    Ok {
                        RevokedAnchors = Set.ofList anchors
                        RevokedStamps = Set.ofList stamps
                    }
                | Error e, _
                | _, Error e -> Error e
        with ex ->
            Error(sprintf "module-revocation list is not valid JSON: %s" ex.Message)

    /// Adapt a `RevocationSet` into the `IBindingRevocationList` the verifier
    /// consults. A stamp is revoked if its whole anchor is revoked OR the
    /// specific `(anchorId, stampId)` pair is listed.
    let toRevocationList (set: RevocationSet) : IBindingRevocationList =
        { new IBindingRevocationList with
            member _.IsRevoked(anchorId, stampId) =
                set.RevokedAnchors.Contains anchorId
                || set.RevokedStamps.Contains((anchorId, stampId))
        }

    /// Parse a revocation list straight into an `IBindingRevocationList`
    /// (convenience over `parse` + `toRevocationList`).
    let parseList (json: string) : Result<IBindingRevocationList, string> =
        parse json |> Result.map toRevocationList

    /// Load an **unsigned** revocation list from `path`. An absent file
    /// yields the empty set (the GP-13 "no list" path); a present-but-
    /// malformed file is an `Error` the caller fails closed on. **Prefer
    /// `SignedRevocationList.loadSigned` (`ToolUp.ArtefactSigning`)** — an
    /// unsigned list an attacker can overwrite would silently un-revoke a
    /// compromised key.
    let load (path: string) : Result<RevocationSet, string> =
        if not (File.Exists path) then
            Ok empty
        else
            try
                parse (File.ReadAllText path)
            with ex ->
                Error(sprintf "failed to read module-revocation list '%s': %s" path ex.Message)

    /// Load the conventional unsigned `module-revocations.json` from a
    /// directory (see `load`'s signed-list caveat).
    let loadFromDir (dir: string) : Result<RevocationSet, string> =
        load (Path.Combine(dir, DefaultFileName))

// ─── file-backed transparency log ───────────────────────────────────────

/// A simple append-only `IBindingTransparencyLog` that writes one JSON line
/// per decision to a local file (JSON Lines). This is the file flavour of
/// the "append-file / blob" implementation; a deployment that wants a
/// blob-backed log composes its own `IBindingTransparencyLog` over
/// `IBlobStorage` (the verifier consumes the interface, not this class).
///
/// Appends are serialised under a per-instance lock and flushed per record,
/// so a decision is durable before the gate proceeds. Distributed-readiness:
/// this is a single-host file sink (dev / single-instance); a multi-instance
/// deployment uses a blob/append-store-backed sink instead.
type FileBindingTransparencyLog(path: string) =
    let gate = obj ()

    let line (d: BindingDecision) : string =
        let o = JsonObject()
        o["module"] <- JsonValue.Create(d.ModuleId)
        o["admitted"] <- JsonValue.Create(d.Admitted)
        o["timestamp"] <- JsonValue.Create(d.TimestampUtc.ToString("O"))

        match d.AnchorId with
        | Some a -> o["anchorId"] <- JsonValue.Create(a)
        | None -> ()

        match d.StampId with
        | Some s -> o["stampId"] <- JsonValue.Create(s)
        | None -> ()

        match d.Reason with
        | Some r -> o["reason"] <- JsonValue.Create(r)
        | None -> ()

        o.ToJsonString()

    /// The file the log appends to.
    member _.Path = path

    interface IBindingTransparencyLog with
        member _.Record(decision) = async {
            let entry = line decision + "\n"

            lock gate (fun () ->
                Path.GetDirectoryName path
                |> fun dir ->
                    if not (String.IsNullOrEmpty dir) then
                        Directory.CreateDirectory dir |> ignore

                File.AppendAllText(path, entry))
        }