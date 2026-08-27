// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// `toolup stamp` — write/refresh module-binding manifest entries (the
/// Phase 166 deploy-time stamper). Given a key + anchor id and a set of
/// modules, it mints each module's stamp over the module's identifier
/// bytes and merges it into `module-bindings.json`. Re-keying is just
/// re-running with a different key; `--unbind` removes entries (so the
/// same module artefact ships unbound, bound to A, or re-bound to B with
/// no rebuild).
///
/// Crypto is pure BCL (GP 2 — the base CLI carries no vendor dependency):
/// the symmetric path is an `HMACSHA256` tag, the asymmetric path an ES256
/// detached JWS produced with `System.Security.Cryptography.ECDsa` over a
/// NIST P-256 key. The JWS shape matches the Phase 40 `JwsBuilder` the
/// `DefaultModuleBindingVerifier` validates against — a round-trip test
/// (stamp here → verify there) pins the two to the same wire shape. The
/// BouncyCastle-only `Ed25519` mint path is deferred (the verifier already
/// accepts Ed25519 anchors; only CLI-side Ed25519 *minting* is unshipped).
module ToolUp.Cli.StampCommand

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Security.Cryptography
open ToolUp.Cli.Dispatch

[<Literal>]
let private CurrentVersion = 1

/// The canonical bytes a stamp covers: the UTF-8 module identifier. MUST
/// match `DefaultModuleBindingVerifier.canonicalBytes` so a stamp minted
/// here verifies there.
let private canonicalBytes (moduleId: string) : byte[] = Encoding.UTF8.GetBytes moduleId

/// base64url (RFC 4648 §5, no padding) — the JWS / tag segment encoding.
let private base64Url (bytes: byte[]) : string =
    Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

/// Sign arbitrary `bytes` into a symmetric (HMAC-SHA256) manifest entry.
let private signMacBytes (key: byte[]) (keyId: string) (bytes: byte[]) : JsonObject =
    use hmac = new HMACSHA256(key)
    let tag = base64Url (hmac.ComputeHash bytes)
    let entry = JsonObject()
    entry["kind"] <- JsonValue.Create "mac"
    entry["keyId"] <- JsonValue.Create keyId
    entry["tag"] <- JsonValue.Create tag
    entry

/// Sign arbitrary `bytes` into an asymmetric (ES256 detached-JWS) manifest
/// entry. The JWS protected header is `{"alg":"ES256","kid":<keyId>,
/// "typ":"JOSE"}`; the detached JWS is `base64url(header) + ".." +
/// base64url(r‖s)`.
let private signJwsBytes (ec: ECDsa) (keyId: string) (bytes: byte[]) : JsonObject =
    let header = JsonObject()
    header["alg"] <- JsonValue.Create "ES256"
    header["kid"] <- JsonValue.Create keyId
    header["typ"] <- JsonValue.Create "JOSE"
    let encodedHeader = base64Url (Encoding.UTF8.GetBytes(header.ToJsonString()))

    let signingInput = Encoding.UTF8.GetBytes(encodedHeader + "." + base64Url bytes)

    let rawSig =
        ec.SignData(signingInput, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)

    let detachedJws = encodedHeader + ".." + base64Url rawSig
    let entry = JsonObject()
    entry["kind"] <- JsonValue.Create "jws"
    entry["detachedJws"] <- JsonValue.Create detachedJws
    entry

/// Mint a symmetric (HMAC-SHA256) manifest entry over `moduleId`.
let mintMac (key: byte[]) (keyId: string) (moduleId: string) : JsonObject =
    signMacBytes key keyId (canonicalBytes moduleId)

/// Mint an asymmetric (ES256 detached-JWS) manifest entry over `moduleId`
/// with the supplied P-256 private key.
let mintJws (ec: ECDsa) (keyId: string) (moduleId: string) : JsonObject =
    signJwsBytes ec keyId (canonicalBytes moduleId)

// ── Phase 216 — SBOM minting ──────────────────────────────────────────
//
// `--sbom-file` / `--sbom-package` build a `ModuleSbom` describing what's
// inside the module being stamped; the SBOM is signed under the SAME key as
// the stamp, over canonical bytes that MUST match
// `ToolUp.ArtefactSigning.ModuleSbomSigning.canonicalBytes` (a round-trip
// test pins the two). A re-stamp regenerates the SBOM from scratch.

/// One SBOM component: (name, version, base64url-sha256).
type SbomComponent = string * string * string

/// base64url SHA-256 of a file's content — the per-component content hash.
let private fileSha256 (path: string) : string =
    use sha = SHA256.Create()
    base64Url (sha.ComputeHash(File.ReadAllBytes path))

/// Canonical bytes the SBOM signature covers. MUST byte-match
/// `ModuleSbomSigning.canonicalBytes` (server side): module id, then the
/// components sorted, each rendered `Name⟨0x1f⟩Version⟨0x1f⟩Sha256`, joined by
/// `0x1d`, separated from the module id by `0x1e`.
let sbomCanonicalBytes (moduleId: string) (components: SbomComponent list) : byte[] =
    let unitSep = string (char 0x1f)
    let groupSep = string (char 0x1d)
    let recordSep = string (char 0x1e)

    let body =
        components
        |> List.map (fun (n, v, h) -> String.concat unitSep [ n; v; h ])
        |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))
        |> String.concat groupSep

    Encoding.UTF8.GetBytes(moduleId + recordSep + body)

/// The `sbom` JSON object: `{ "components": [ {name,version,sha256}, … ] }`.
let sbomJson (components: SbomComponent list) : JsonObject =
    let arr = JsonArray()

    for (n, v, h) in components do
        let o = JsonObject()
        o["name"] <- JsonValue.Create n
        o["version"] <- JsonValue.Create v
        o["sha256"] <- JsonValue.Create h
        arr.Add o

    let o = JsonObject()
    o["components"] <- arr
    o

// ── Phase 589 — certified-surface minting ─────────────────────────────
//
// `--certified-surface` embeds the module's CERTIFIED SURFACE: the canonical
// surface-projection JSON the module repo's conformance run produced
// (`ModuleSurface.certificationJson`), its hash, and optionally that run's
// verdict (`--conformance-verdict`). It is signed under the SAME key as the
// stamp, over canonical bytes that MUST match
// `ToolUp.ArtefactSigning.ModuleCertificationSigning.canonicalBytes` (a
// round-trip test pins the two).
//
// Unlike an SBOM, a certified surface belongs to exactly ONE module — it IS
// that module's declaration set — so `--certified-surface` with more than one
// `--module` is a usage error rather than a shared payload.

/// The surface JSON is embedded and hashed VERBATIM (after trimming the
/// surrounding whitespace a file write adds), because it is already canonical
/// when the certifying run emits it. Re-serialising it here would let the
/// stamper and the verifier disagree about what "canonical" means.
let certifiedSurfaceHash (surfaceJson: string) : string =
    base64Url (SHA256.HashData(Encoding.UTF8.GetBytes surfaceJson))

/// A parsed conformance verdict, in the shape the canonical bytes render:
/// (packVersion, runStamp, laws as (law, passed, detail)).
type ConformanceVerdict = {
    PackVersion: string
    RunStamp: string
    Laws: (string * bool * string) list
}

/// Canonical bytes the certification signature covers. MUST byte-match
/// `ModuleCertificationSigning.canonicalBytes` (server side): module id,
/// surface hash, surface JSON, and the rendered verdict, joined by `0x1e`. The
/// law list is sorted (order-independent, the SBOM precedent); an absent
/// verdict renders `""`.
let certificationCanonicalBytes
    (moduleId: string)
    (surfaceJson: string)
    (surfaceHash: string)
    (verdict: ConformanceVerdict option)
    : byte[] =
    let unitSep = string (char 0x1f)
    let groupSep = string (char 0x1d)
    let recordSep = string (char 0x1e)

    let renderedVerdict =
        match verdict with
        | None -> ""
        | Some v ->
            let laws =
                v.Laws
                |> List.map (fun (law, passed, detail) ->
                    String.concat unitSep [ law; (if passed then "pass" else "fail"); detail ])
                |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))
                |> String.concat groupSep

            String.concat unitSep [ v.PackVersion; v.RunStamp; laws ]

    Encoding.UTF8.GetBytes(String.concat recordSep [ moduleId; surfaceHash; surfaceJson; renderedVerdict ])

/// Read a verdict file into the canonical shape. A present-but-unreadable file
/// is an error — never a silently-dropped verdict, which would sign a
/// certification that claims less than the operator asked for.
let parseVerdictFile (path: string) : Result<ConformanceVerdict, string> =
    try
        match JsonNode.Parse(File.ReadAllText path) with
        | :? JsonObject as o ->
            let str (name: string) =
                match o[name] with
                | null -> ""
                | v -> v.GetValue<string>()

            let laws =
                match o["laws"] with
                | :? JsonArray as arr -> [
                    for node in arr do
                        match node with
                        | :? JsonObject as law ->
                            let s (name: string) =
                                match law[name] with
                                | null -> ""
                                | v -> v.GetValue<string>()

                            let passed =
                                match law["passed"] with
                                | null -> false
                                | v -> v.GetValue<bool>()

                            yield (s "law", passed, s "detail")
                        | _ -> ()
                  ]
                | _ -> []

            Ok {
                PackVersion = str "packVersion"
                RunStamp = str "runStamp"
                Laws = laws
            }
        | _ -> Error(sprintf "--conformance-verdict '%s' is not a JSON object" path)
    with ex ->
        Error(sprintf "--conformance-verdict '%s' is not readable: %s" path ex.Message)

/// The `certifiedSurface` JSON object written into the manifest entry.
let certifiedSurfaceJson (surfaceJson: string) (surfaceHash: string) (verdict: ConformanceVerdict option) : JsonObject =
    let o = JsonObject()
    o["surfaceJson"] <- JsonValue.Create surfaceJson
    o["surfaceHash"] <- JsonValue.Create surfaceHash

    match verdict with
    | None -> ()
    | Some v ->
        let laws = JsonArray()

        for (law, passed, detail) in v.Laws do
            let l = JsonObject()
            l["law"] <- JsonValue.Create law
            l["passed"] <- JsonValue.Create passed
            l["detail"] <- JsonValue.Create detail
            laws.Add l

        let verdictObject = JsonObject()
        verdictObject["packVersion"] <- JsonValue.Create v.PackVersion
        verdictObject["runStamp"] <- JsonValue.Create v.RunStamp
        verdictObject["laws"] <- laws
        o["verdict"] <- verdictObject

    o

// ── option model ─────────────────────────────────────────────────────

type KeySource =
    | NoKey
    | MacKeyBase64 of string
    | MacKeyFile of string
    | EcKeyFile of string

type Options = {
    Manifest: string option
    Modules: string list
    KeyId: string option
    Key: KeySource
    Unbind: bool
    /// Files whose content hashes become SBOM components (Phase 216).
    SbomFiles: string list
    /// `name@version` package references recorded in the SBOM (Phase 216).
    SbomPackages: SbomComponent list
    /// File holding the module's canonical surface-projection JSON, embedded
    /// and signed as the certified surface (Phase 589).
    CertifiedSurfaceFile: string option
    /// File holding the conformance verdict recorded alongside it (Phase 589).
    ConformanceVerdictFile: string option
}

let private defaults = {
    Manifest = None
    Modules = []
    KeyId = None
    Key = NoKey
    Unbind = false
    SbomFiles = []
    SbomPackages = []
    CertifiedSurfaceFile = None
    ConformanceVerdictFile = None
}

let rec private parse (opts: Options) (args: string list) : Result<Options, string> =
    match args with
    | [] -> Ok opts
    | "--manifest" :: v :: rest -> parse { opts with Manifest = Some v } rest
    | "--module" :: v :: rest ->
        parse
            {
                opts with
                    Modules = opts.Modules @ [ v ]
            }
            rest
    | "--key-id" :: v :: rest -> parse { opts with KeyId = Some v } rest
    | "--mac-key" :: v :: rest -> parse { opts with Key = MacKeyBase64 v } rest
    | "--mac-key-file" :: v :: rest -> parse { opts with Key = MacKeyFile v } rest
    | "--ec-key-file" :: v :: rest -> parse { opts with Key = EcKeyFile v } rest
    | "--unbind" :: rest -> parse { opts with Unbind = true } rest
    | "--sbom-file" :: v :: rest ->
        parse
            {
                opts with
                    SbomFiles = opts.SbomFiles @ [ v ]
            }
            rest
    | "--sbom-package" :: v :: rest ->
        // `name@version` (version optional → ""). Split on the last '@' so a
        // scoped name containing '@' keeps it.
        let name, version =
            match v.LastIndexOf '@' with
            | i when i > 0 -> v.Substring(0, i), v.Substring(i + 1)
            | _ -> v, ""

        parse
            {
                opts with
                    SbomPackages = opts.SbomPackages @ [ (name, version, "") ]
            }
            rest
    | "--certified-surface" :: v :: rest ->
        parse
            {
                opts with
                    CertifiedSurfaceFile = Some v
            }
            rest
    | "--conformance-verdict" :: v :: rest ->
        parse
            {
                opts with
                    ConformanceVerdictFile = Some v
            }
            rest
    | ("--manifest" | "--module" | "--key-id" | "--mac-key" | "--mac-key-file" | "--ec-key-file" | "--sbom-file" | "--sbom-package" | "--certified-surface" | "--conformance-verdict") :: [] ->
        Error(sprintf "missing value for %s" (List.head args))
    | unknown :: _ -> Error(sprintf "unrecognised argument: %s" unknown)

let private helpText = [
    "Usage: toolup stamp --manifest <path> --module <Name> [--module <Name>...]"
    "                    --key-id <id> (--mac-key-file <f> | --mac-key <base64> | --ec-key-file <pem>)"
    "       toolup stamp --manifest <path> --module <Name> --unbind"
    ""
    "Writes/refreshes module-binding manifest entries. Each module's stamp is minted over"
    "the module's identifier bytes and merged into the manifest (other modules untouched)."
    "Re-key by re-running with a different key; --unbind removes the named modules' entries."
    ""
    "Options:"
    "  --manifest <path>        The module-bindings.json to create/update. (required)"
    "  --module <Name>          A module to stamp (repeatable). (required)"
    "  --key-id <id>            Anchor key id recorded with the stamp. (required to stamp)"
    "  --mac-key-file <f>       File holding a base64 HMAC-SHA256 key (symmetric anchor)."
    "  --mac-key <base64>       Inline base64 HMAC-SHA256 key (symmetric anchor)."
    "  --ec-key-file <pem>      PEM file holding a P-256 EC private key (asymmetric/ES256 anchor)."
    "  --unbind                 Remove the named modules' entries instead of stamping."
    "  --sbom-file <path>       File whose content hash becomes an SBOM component (repeatable)."
    "  --sbom-package <n@ver>   Package reference recorded in the SBOM, name@version (repeatable)."
    "  --certified-surface <f>  File holding the module's canonical surface-projection JSON"
    "                           (ModuleSurface.certificationJson). Exactly one --module."
    "  --conformance-verdict <f>  File holding the conformance verdict recorded with it."
    ""
    "An SBOM (--sbom-file / --sbom-package) is signed under the same key as the stamp and"
    "merged into the entry; re-stamping regenerates it. Without either flag the entry is a"
    "plain Phase-166 stamp (byte-for-byte unchanged)."
    ""
    "A certified surface (--certified-surface) is signed under the same key too, and the"
    "composing deployment re-derives the live surface and refuses the module if it has"
    "drifted. It belongs to one module, so it takes exactly one --module."
]

let private usageError (message: string) =
    eprintfn "toolup stamp: %s" message
    eprintfn ""
    helpText |> List.iter (eprintfn "%s")
    ExitUsage

/// Load an existing manifest document, or a fresh one. A present-but-
/// malformed manifest is an error (never silently overwritten).
let private loadDocument (path: string) : Result<JsonObject, string> =
    if not (File.Exists path) then
        Ok(JsonObject())
    else
        try
            match JsonNode.Parse(File.ReadAllText path) with
            | :? JsonObject as o -> Ok o
            | _ -> Error(sprintf "existing manifest '%s' is not a JSON object" path)
        with ex ->
            Error(sprintf "existing manifest '%s' is not valid JSON: %s" path ex.Message)

let private bindingsObject (doc: JsonObject) : JsonObject =
    match doc["bindings"] with
    | :? JsonObject as b -> b
    | _ ->
        let b = JsonObject()
        doc["bindings"] <- b
        b

let private writeDocument (path: string) (doc: JsonObject) =
    doc["version"] <- JsonValue.Create CurrentVersion
    let opts = JsonSerializerOptions(WriteIndented = true)
    let dir = Path.GetDirectoryName path

    if not (String.IsNullOrEmpty dir) then
        Directory.CreateDirectory dir |> ignore

    File.WriteAllText(path, doc.ToJsonString opts)

/// A resolved signer: mint a module-stamp entry over a module id, and (for
/// the Phase 216 SBOM) sign arbitrary canonical bytes under the same key.
type private Signer = {
    MintModule: string -> JsonObject
    SignBytes: byte[] -> JsonObject
}

let private noopDisposable =
    { new IDisposable with
        member _.Dispose() = ()
    }

/// Resolve the key source into a `Signer` (+ a disposable for key material).
let private resolveMinter (keyId: string) (source: KeySource) : Result<Signer * IDisposable, string> =
    let decodeKey (raw: string) =
        try
            Ok(Convert.FromBase64String(raw.Trim()))
        with _ ->
            Error "key material is not valid base64"

    let macSigner key = {
        MintModule = fun m -> mintMac key keyId m
        SignBytes = fun bytes -> signMacBytes key keyId bytes
    }

    match source with
    | NoKey -> Error "a key source is required to stamp (--mac-key-file / --mac-key / --ec-key-file), or pass --unbind"
    | MacKeyBase64 b64 -> decodeKey b64 |> Result.map (fun key -> macSigner key, noopDisposable)
    | MacKeyFile f ->
        if not (File.Exists f) then
            Error(sprintf "--mac-key-file '%s' does not exist" f)
        else
            decodeKey (File.ReadAllText f)
            |> Result.map (fun key -> macSigner key, noopDisposable)
    | EcKeyFile f ->
        if not (File.Exists f) then
            Error(sprintf "--ec-key-file '%s' does not exist" f)
        else
            try
                let ec = ECDsa.Create()
                ec.ImportFromPem(File.ReadAllText f)

                let signer = {
                    MintModule = fun m -> mintJws ec keyId m
                    SignBytes = fun bytes -> signJwsBytes ec keyId bytes
                }

                Ok(signer, (ec :> IDisposable))
            with ex ->
                Error(sprintf "--ec-key-file '%s' is not a usable P-256 EC private key: %s" f ex.Message)

let private runWith (opts: Options) : int =
    match opts.Manifest with
    | None -> usageError "--manifest is required"
    | Some manifestPath ->
        if List.isEmpty opts.Modules then
            usageError "at least one --module is required"
        else
            match loadDocument manifestPath with
            | Error e ->
                eprintfn "toolup stamp: %s" e
                ExitRuntimeError
            | Ok doc ->
                let bindings = bindingsObject doc

                if opts.Unbind then
                    for m in opts.Modules do
                        bindings.Remove m |> ignore
                        printfn "unbound %s" m

                    writeDocument manifestPath doc
                    ExitOk
                else
                    match opts.KeyId with
                    | None -> usageError "--key-id is required to stamp"
                    | Some keyId ->
                        // Phase 216 — build the SBOM (shared across the stamped
                        // modules) before opening the signer. A missing
                        // --sbom-file is a hard error, never a silent skip.
                        let missing = opts.SbomFiles |> List.filter (File.Exists >> not)

                        match missing with
                        | f :: _ -> usageError (sprintf "--sbom-file '%s' does not exist" f)
                        | [] ->
                            let sbomComponents = [
                                for f in opts.SbomFiles -> (Path.GetFileName f, "", fileSha256 f)
                                yield! opts.SbomPackages
                            ]

                            // Phase 589 — resolve the certified surface (and its
                            // optional verdict) before opening the signer. A
                            // certified surface is ONE module's declaration set,
                            // so more than one --module is a usage error rather
                            // than a payload silently shared across them.
                            let certification =
                                match opts.CertifiedSurfaceFile with
                                | None ->
                                    if opts.ConformanceVerdictFile.IsSome then
                                        Error "--conformance-verdict requires --certified-surface"
                                    else
                                        Ok None
                                | Some surfaceFile when not (File.Exists surfaceFile) ->
                                    Error(sprintf "--certified-surface '%s' does not exist" surfaceFile)
                                | Some _ when List.length opts.Modules <> 1 ->
                                    Error
                                        "--certified-surface certifies one module's surface, so exactly one --module is required"
                                | Some surfaceFile ->
                                    let surfaceJson = (File.ReadAllText surfaceFile).Trim()

                                    let verdict =
                                        match opts.ConformanceVerdictFile with
                                        | None -> Ok None
                                        | Some verdictFile when not (File.Exists verdictFile) ->
                                            Error(sprintf "--conformance-verdict '%s' does not exist" verdictFile)
                                        | Some verdictFile -> parseVerdictFile verdictFile |> Result.map Some

                                    verdict
                                    |> Result.map (fun v -> Some(surfaceJson, certifiedSurfaceHash surfaceJson, v))

                            match certification with
                            | Error e -> usageError e
                            | Ok certified ->

                                match resolveMinter keyId opts.Key with
                                | Error e -> usageError e
                                | Ok(signer, disposable) ->
                                    use _ = disposable

                                    for m in opts.Modules do
                                        let entry = signer.MintModule m

                                        if not (List.isEmpty sbomComponents) then
                                            entry["sbom"] <- sbomJson sbomComponents
                                            entry["sbomSig"] <- signer.SignBytes(sbomCanonicalBytes m sbomComponents)

                                            printfn
                                                "stamped %s (key-id %s, %d SBOM components)"
                                                m
                                                keyId
                                                sbomComponents.Length
                                        else
                                            printfn "stamped %s (key-id %s)" m keyId

                                        match certified with
                                        | None -> ()
                                        | Some(surfaceJson, surfaceHash, verdict) ->
                                            entry["certifiedSurface"] <-
                                                certifiedSurfaceJson surfaceJson surfaceHash verdict

                                            entry["certifiedSurfaceSig"] <-
                                                signer.SignBytes(
                                                    certificationCanonicalBytes m surfaceJson surfaceHash verdict
                                                )

                                            printfn
                                                "  certified surface %s (%d law results)"
                                                surfaceHash
                                                (verdict
                                                 |> Option.map (fun v -> List.length v.Laws)
                                                 |> Option.defaultValue 0)

                                        bindings[m] <- entry

                                    writeDocument manifestPath doc
                                    ExitOk

let command = {
    Path = [ "stamp" ]
    Summary = "Write/refresh module-binding manifest entries."
    Help = helpText
    Run =
        fun args ->
            match parse defaults args with
            | Error message -> usageError message
            | Ok opts -> runWith opts
}