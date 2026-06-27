// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System.IO
open System.Text.Json

// ─── Phase 166 — detachable module-binding manifest ─────────────────────
//
// A module's binding is a *deployment* property, not a build-time property
// of the module. The Phase 165 gate verifies a `ModuleBindingStamp` a
// module presents via `ServerModule.BindingStamp`; this manifest is where
// that stamp comes from at deploy time. It is a signed sidecar
// (`module-bindings.json`) mapping `moduleId → stamp`, read at startup and
// applied to the matching modules just before `addModule`. The same module
// artefact can therefore ship unbound, bound to deployment A, or re-bound
// to deployment B with no rebuild — re-keying is just re-stamping the
// manifest (the `toolup stamp` CLI command writes it).
//
// **GP 13 byte-identical:** a deployment with no manifest file loads an
// empty map; `applyTo` is then the identity on every module, so an
// unstamped/unconfigured deployment is unchanged.
//
// **No crypto here.** The manifest only *carries* the opaque stamp strings;
// verification stays in the `IModuleBindingVerifier` the gate consults. The
// stamp is signed over the module's identifier bytes, so the `moduleId` key
// is also what the gate recomputes the canonical bytes from — a stamp
// filed under module A cannot bind module B.

/// The on-disk manifest format:
///
/// ```json
/// {
///   "version": 1,
///   "bindings": {
///     "Sales":     { "kind": "mac", "keyId": "anchor-1", "tag": "<base64url>" },
///     "Inventory": { "kind": "jws", "detachedJws": "<header>..<sig>" }
///   }
/// }
/// ```
module ModuleBindingManifest =

    /// Conventional manifest file name (alongside the deployed binary).
    [<Literal>]
    let DefaultFileName = "module-bindings.json"

    /// Current manifest schema version. A reader rejects a higher major it
    /// does not understand rather than silently mis-binding.
    [<Literal>]
    let CurrentVersion = 1

    let private parseEntry (moduleId: string) (entry: JsonElement) : Result<ModuleBindingStamp, string> =
        let prop (name: string) =
            match entry.TryGetProperty name with
            | true, v when v.ValueKind = JsonValueKind.String -> Ok(v.GetString())
            | _ -> Error(sprintf "binding for '%s' is missing the string field '%s'" moduleId name)

        match entry.TryGetProperty "kind" with
        | true, k when k.ValueKind = JsonValueKind.String ->
            match k.GetString() with
            | "mac" ->
                match prop "keyId", prop "tag" with
                | Ok keyId, Ok tag -> Ok(MacStamp(keyId, tag))
                | Error e, _
                | _, Error e -> Error e
            | "jws" -> prop "detachedJws" |> Result.map JwsStamp
            | other -> Error(sprintf "binding for '%s' has unknown kind '%s' (expected 'mac' or 'jws')" moduleId other)
        | _ -> Error(sprintf "binding for '%s' is missing the string field 'kind'" moduleId)

    /// Parse a manifest JSON document into a `moduleId → stamp` map.
    let parse (json: string) : Result<Map<string, ModuleBindingStamp>, string> =
        try
            use doc = JsonDocument.Parse json
            let root = doc.RootElement

            let versionOk =
                match root.TryGetProperty "version" with
                | true, v when v.ValueKind = JsonValueKind.Number -> v.GetInt32() <= CurrentVersion
                | _ -> true // version is advisory; absent ⇒ assume current

            if not versionOk then
                Error(
                    sprintf "module-binding manifest version is newer than this SDK understands (max %d)" CurrentVersion
                )
            else
                match root.TryGetProperty "bindings" with
                | true, bindings when bindings.ValueKind = JsonValueKind.Object ->
                    (Ok Map.empty, bindings.EnumerateObject())
                    ||> Seq.fold (fun acc prop ->
                        match acc with
                        | Error _ -> acc
                        | Ok m ->
                            match parseEntry prop.Name prop.Value with
                            | Ok stamp -> Ok(Map.add prop.Name stamp m)
                            | Error e -> Error e)
                | true, _ -> Error "module-binding manifest 'bindings' must be a JSON object"
                | false, _ -> Ok Map.empty // no bindings ⇒ empty (valid)
        with ex ->
            Error(sprintf "module-binding manifest is not valid JSON: %s" ex.Message)

    /// Load a manifest from `path`. An absent file yields an empty map (the
    /// GP-13 "no manifest" path); a present-but-malformed file is an
    /// `Error` the caller fails closed on rather than silently ignoring.
    let load (path: string) : Result<Map<string, ModuleBindingStamp>, string> =
        if not (File.Exists path) then
            Ok Map.empty
        else
            try
                parse (File.ReadAllText path)
            with ex ->
                Error(sprintf "failed to read module-binding manifest '%s': %s" path ex.Message)

    /// Load the conventional `module-bindings.json` from a directory.
    let loadFromDir (dir: string) : Result<Map<string, ModuleBindingStamp>, string> =
        load (Path.Combine(dir, DefaultFileName))

    /// Apply a manifest to one module: attach the stamp filed under the
    /// module's name, or leave the module unchanged when it has no entry.
    let applyTo (manifest: Map<string, ModuleBindingStamp>) (m: ServerModule) : ServerModule =
        match Map.tryFind m.Name manifest with
        | Some stamp -> ServerModule.withBindingStamp stamp m
        | None -> m

    /// Apply a manifest across a module list (the composition root's
    /// `modules |> ModuleBindingManifest.applyToAll manifest` before
    /// `addModules`).
    let applyToAll (manifest: Map<string, ModuleBindingStamp>) (modules: ServerModule list) : ServerModule list =
        modules |> List.map (applyTo manifest)