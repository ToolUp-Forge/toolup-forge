module ToolUp.Platform.Tests.InProcess.ConfigReferenceTests

open System
open System.IO
open System.Reflection
open System.Text.Json
open System.Text.RegularExpressions
open Expecto
open ToolUp.Platform.ConfigKeys

// ─── Phase 214 — config-key registry coverage + reference-doc golden ──
//
// Three guarantees over the central `ConfigKeys` registry:
//
//   1. The committed `docs/reference/config-reference.md` — and, since
//      Phase 697, `docs/reference/toolup.config.schema.json` beside it —
//      is exactly what the generator produces from `ConfigKeys.all`, so
//      both are regenerable and never hand-maintained. Set
//      `TOOLUP_REGEN_CONFIG_REFERENCE=1` to rewrite them instead of
//      comparing (mirrors the `TOOLUP_APPROVE_API` idiom). One flag
//      covers both projections: they read one registry, so a regen that
//      refreshed only one would leave the other lying.
//   2. Every `TOOLUP_*` string literal in shipped (non-test) source
//      carries a descriptor — a key without one fails here.
//   3. The registry itself is well-formed (unique names, non-empty
//      descriptions, all `TOOLUP_`-prefixed).

/// Repo root (toolup-forge): bin/<Config>/net10.0/<dll> → up 5.
let private repoRoot () =
    let assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
    Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

let private referenceDocPath () =
    Path.Combine(repoRoot (), "docs", "reference", "config-reference.md")

let private schemaPath () =
    Path.Combine(repoRoot (), "docs", "reference", "toolup.config.schema.json")

let private regenModeOn () =
    match Environment.GetEnvironmentVariable "TOOLUP_REGEN_CONFIG_REFERENCE" with
    | null
    | "" -> false
    | v -> v = "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase)

let private normalise (s: string) = s.Replace("\r\n", "\n")

/// Every non-test source file under `src/` is scanned: a `TOOLUP_*`
/// *string literal* anywhere in shipped source must carry a descriptor.
///
/// This used to be a four-file allow-list, which structurally could not
/// see the largest reader in the codebase — `ServerConfig.fromEnv` in
/// `SDK.Shared.fs` reads 87 vars, 72 of which had no descriptor while
/// this test reported clean. Enumerating readers is exactly the thing
/// that drifts, so the gate now quantifies over the tree instead.
///
/// Test sources are excluded: they set vars to exercise the readers, and
/// a fixture var is not deployment configuration.
let private scanRoots = [ "src" ]

/// Literals that are env-var *prefixes* rather than variables in their
/// own right — the suffix is supplied at runtime. Each is registered
/// under its prefix, so the descriptor exists and the doc explains the
/// shape; this set only stops the scanner demanding a second one.
let private prefixLiterals =
    Set.ofList [ "TOOLUP_COMPONENT__"; "TOOLUP_EXTERNAL_COMPUTE_HTTP_" ]

/// Matches a `TOOLUP_*` token only inside a double-quoted string, so a
/// var named in a doc comment is not mistaken for one that is read.
/// Scanning raw text instead would demand descriptors for prose
/// mentions — including `TOOLUP_MODULE_BINDING_*` (a glob) and
/// `TOOLUP_PLATFORM_MODE` (retired in Phase 66, read nowhere).
let private envVarLiteralPattern =
    Regex("\"(TOOLUP_[A-Z0-9_]+)\"", RegexOptions.Compiled)

let private shippedSourceFiles (root: string) =
    scanRoots
    |> List.collect (fun rel ->
        let dir = Path.Combine(root, rel)

        if Directory.Exists dir then
            Directory.EnumerateFiles(dir, "*.fs", SearchOption.AllDirectories)
            |> Seq.filter (fun f ->
                let norm = f.Replace('\\', '/')

                not (norm.Contains "/obj/")
                && not (norm.Contains "/bin/")
                && not (norm.Contains ".Tests/")
                && not (norm.Contains "/Tests/"))
            |> List.ofSeq
        else
            [])

let tests =
    testList "ConfigReference" [
        testCase "config-reference.md matches the rendered registry (regenerable, covers every key)"
        <| fun _ ->
            let rendered = ReferenceDoc.render all
            let path = referenceDocPath ()

            if regenModeOn () then
                Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
                File.WriteAllText(path, rendered)
            else
                Expect.isTrue
                    (File.Exists path)
                    (sprintf "%s is missing. Generate it with `dev-scripts/generate-config-reference.ps1`." path)

                let committed = File.ReadAllText path |> normalise

                Expect.equal
                    committed
                    (normalise rendered)
                    "docs/reference/config-reference.md is stale. Regenerate with `dev-scripts/generate-config-reference.ps1`."

        testCase "toolup.config.schema.json matches the rendered registry (regenerable)"
        <| fun _ ->
            // Phase 697 — the schema is a second projection of the same
            // registry, so it rides the same regen flag and the same
            // byte-equality compare as the reference doc. One
            // `dev-scripts/generate-config-reference.ps1` refreshes both;
            // a registry edit that moves one and not the other cannot
            // reach a commit.
            let rendered = ConfigSchema.render all
            let path = schemaPath ()

            if regenModeOn () then
                Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
                File.WriteAllText(path, rendered)
            else
                Expect.isTrue
                    (File.Exists path)
                    (sprintf "%s is missing. Generate it with `dev-scripts/generate-config-reference.ps1`." path)

                let committed = File.ReadAllText path |> normalise

                Expect.equal
                    committed
                    (normalise rendered)
                    "docs/reference/toolup.config.schema.json is stale. Regenerate with `dev-scripts/generate-config-reference.ps1`."

        testCase "the generated schema admits exactly what a manifest may contain"
        <| fun _ ->
            // The properties the schema declares ARE the claim an editor
            // enforces, so they are asserted against the registry rather
            // than against the committed file — which the arm above has
            // already pinned to this render. Four things have to hold, and
            // the last two are the ones that would fail silently: a schema
            // listing a secret would invite an operator to commit one, and
            // a schema without `additionalProperties: false` would accept
            // every typo the loader refuses at boot.
            use doc = JsonDocument.Parse(ConfigSchema.render all)
            let root = doc.RootElement

            Expect.equal
                (root.GetProperty("$schema").GetString())
                ConfigSchema.Dialect
                "the schema must declare its dialect"

            Expect.isFalse
                (root.GetProperty("additionalProperties").GetBoolean())
                "additionalProperties must be false — an unknown key is a refusal at boot, so it is an error at edit time too"

            let properties =
                root.GetProperty("properties").EnumerateObject() |> Seq.map _.Name |> Set.ofSeq

            Expect.isTrue
                (Set.contains "$schema" properties)
                "the manifest's own $schema pointer is tolerated by the loader, so the schema must admit it"

            let declared = Set.remove "$schema" properties

            let expected =
                all
                |> List.filter (fun k -> not k.IsSecret && isManifestBindable k.EnvVar)
                |> List.map _.EnvVar
                |> Set.ofList

            Expect.equal
                declared
                expected
                "the schema must declare exactly the non-secret manifest-bindable keys — a missing one refuses a valid manifest in the editor, an extra one invites a manifest key nothing reads"

            let secrets = all |> List.filter _.IsSecret |> List.map _.EnvVar |> Set.ofList

            Expect.isEmpty
                (Set.intersect declared secrets)
                "no secret key may appear in the schema: the loader refuses it with no hatch, and the schema is what tells the operator that before they type the value"

        testCase "every TOOLUP_ env var in shipped source has a descriptor"
        <| fun _ ->
            let registered = all |> List.map _.EnvVar |> Set.ofList
            let root = repoRoot ()
            let files = shippedSourceFiles root

            Expect.isGreaterThan
                (List.length files)
                100
                "scanned suspiciously few source files — the scan root is probably wrong, and an empty scan passes vacuously"

            let readVars =
                files
                |> List.collect (fun full ->
                    File.ReadAllText full
                    |> envVarLiteralPattern.Matches
                    |> Seq.map (fun m -> m.Groups[1].Value)
                    |> List.ofSeq)
                |> Set.ofList

            let missing = Set.difference (Set.difference readVars registered) prefixLiterals

            Expect.isEmpty
                missing
                (sprintf
                    "These TOOLUP_ env vars appear as string literals in shipped source but have no ConfigKeyDescriptor in ConfigKeys.all, so `--print-config` omits them and docs/reference/config-reference.md does not document them: %s"
                    (missing |> Set.toList |> String.concat ", "))

        testCase "every ConfigKeys.Names binding has a descriptor"
        <| fun _ ->
            // Arm 1 scans string literals, so it stops seeing a var the moment a
            // reader switches to citing `Names.*` — which is exactly what the
            // registry's own header asks readers to do. A binding used by a
            // reader but never added to `all` would then be invisible to it.
            let registered = all |> List.map _.EnvVar |> Set.ofList
            let root = repoRoot ()

            let registryPath =
                Path.Combine(root, "src", "ToolUp.Platform.Core", "Shared", "Types", "ConfigKeyDescriptor.fs")

            Expect.isTrue (File.Exists registryPath) (sprintf "registry source not found: %s" registryPath)

            let namesModule =
                let text = File.ReadAllText registryPath
                let start = text.IndexOf "module Names ="
                let stop = text.IndexOf "let all: ConfigKeyDescriptor list ="
                Expect.isGreaterThan start -1 "Names module not found in the registry source"
                Expect.isGreaterThan stop start "`all` does not follow the Names module"
                text.Substring(start, stop - start)

            let bound =
                Regex.Matches(namesModule, "let\\s+\\w+\\s*=\\s*\"(TOOLUP_[A-Z0-9_]+)\"")
                |> Seq.map (fun m -> m.Groups[1].Value)
                |> Set.ofSeq

            Expect.isGreaterThan
                (Set.count bound)
                100
                "parsed suspiciously few Names bindings — the parse is probably wrong, and an empty parse passes vacuously"

            let orphaned = Set.difference bound registered

            Expect.isEmpty
                orphaned
                (sprintf
                    "These ConfigKeys.Names bindings have no matching descriptor in ConfigKeys.all, so a reader citing them is undocumented and absent from --print-config: %s"
                    (orphaned |> Set.toList |> String.concat ", "))

        testCase "declared manifest-bindability matches the reader that actually resolves through the seam"
        <| fun _ ->
            // Phase 696 — the ratchet that makes the reader-migration sweep
            // terminate instead of decay. `ConfigKeys.manifestBindable` is a
            // DECLARATION: a manifest key is honoured only if some reader
            // resolves it through `ConfigResolution`. If the declaration
            // over-claims, an operator writes a key that is silently ignored
            // — the failure mode this whole layer exists to prevent. If it
            // under-claims, a key that WOULD work warns pointlessly and the
            // generated reference tells the operator to use the environment
            // instead.
            //
            // The claim is checked against source rather than behaviour
            // because behaviour would need one boot per key. There are two
            // shapes of reader and the parse has to see both:
            //
            //   1. `ServerConfig.fromEnv` funnels ~90 keys through ONE
            //      private seam helper, so no key there names
            //      `ConfigResolution` itself. The whole `fromEnv` region is
            //      therefore the unit, and its `ConfigKeys.Names.*`
            //      citations ARE the bindable set.
            //   2. A reader elsewhere calls the seam directly, one key at a
            //      time — `ConfigResolution.tryValue Names.foo`. Phase 695's
            //      preflight guard is the first (it resolves its own strict
            //      -mode key), and it lives in `Platform.Server`, which the
            //      region parse structurally cannot see. Anchoring the
            //      pattern on the seam CALL rather than on the `Names.*`
            //      citation is what keeps this arm from claiming every key a
            //      file happens to mention: that same guard cites the two
            //      declared prefixes without resolving either.
            //
            // A reader that stops citing `Names.*` and inlines a literal is
            // already caught by the first arm of this file.
            let root = repoRoot ()

            let sharedPath =
                Path.Combine(root, "src", "ToolUp.Platform.Core", "Shared", "SDK.Shared.fs")

            Expect.isTrue (File.Exists sharedPath) (sprintf "ServerConfig source not found: %s" sharedPath)

            let text = File.ReadAllText sharedPath

            // The `fromEnv` region: everything from the server-only guard
            // (where the env helpers begin) to the end of the file.
            let regionStart = text.IndexOf "#if !FABLE_COMPILER"

            Expect.isGreaterThan regionStart -1 "the server-only `fromEnv` region was not found in SDK.Shared.fs"

            let region = text.Substring regionStart

            // `Names.foo` → the literal it binds, parsed from the registry
            // source (the same parse the arm above uses).
            let registrySource =
                Path.Combine(root, "src", "ToolUp.Platform.Core", "Shared", "Types", "ConfigKeyDescriptor.fs")
                |> File.ReadAllText

            let namesModule =
                let start = registrySource.IndexOf "module Names ="
                let stop = registrySource.IndexOf "let all: ConfigKeyDescriptor list ="
                registrySource.Substring(start, stop - start)

            let bindingToVar =
                Regex.Matches(namesModule, "let\\s+(\\w+)\\s*=\\s*\"(TOOLUP_[A-Z0-9_]+)\"")
                |> Seq.map (fun m -> m.Groups[1].Value, m.Groups[2].Value)
                |> Map.ofSeq

            // Secrets are excluded on both sides: they resolve through the
            // seam like any other key, but the loader refuses them in a
            // manifest with no hatch, so declaring one bindable would be a
            // claim nothing can honour.
            let secrets = all |> List.filter _.IsSecret |> List.map _.EnvVar |> Set.ofList

            let fromEnvRegionKeys =
                Regex.Matches(region, "ConfigKeys\\.Names\\.(\\w+)")
                |> Seq.choose (fun m -> Map.tryFind m.Groups[1].Value bindingToVar)
                |> Set.ofSeq

            Expect.isGreaterThan
                (Set.count fromEnvRegionKeys)
                50
                "parsed suspiciously few keys out of the fromEnv region — the region bounds are probably wrong, and an empty parse passes vacuously in both directions"

            // Direct seam calls anywhere in shipped source. Anchored on the
            // seam function so a file that merely mentions `Names.*` for
            // another purpose contributes nothing.
            let seamCallKeys =
                shippedSourceFiles root
                |> List.collect (fun full ->
                    Regex.Matches(
                        File.ReadAllText full,
                        "ConfigResolution\\.(?:tryValue|tryResolve|sourceOf)\\s+(?:ConfigKeys\\.)?Names\\.(\\w+)"
                    )
                    |> Seq.choose (fun m -> Map.tryFind m.Groups[1].Value bindingToVar)
                    |> List.ofSeq)
                |> Set.ofList

            let resolvedThroughSeam =
                Set.union fromEnvRegionKeys seamCallKeys |> fun s -> Set.difference s secrets

            let overClaimed = Set.difference manifestBindable resolvedThroughSeam

            Expect.isEmpty
                overClaimed
                (sprintf
                    "These keys are declared manifest-bindable but no reader resolves them through ConfigResolution, so a manifest setting them would be silently ignored: %s"
                    (overClaimed |> Set.toList |> String.concat ", "))

            let underClaimed = Set.difference resolvedThroughSeam manifestBindable

            Expect.isEmpty
                underClaimed
                (sprintf
                    "These keys resolve through ConfigResolution but are not declared in ConfigKeys.manifestBindable, so the manifest would refuse to honour a value it can in fact bind: %s"
                    (underClaimed |> Set.toList |> String.concat ", "))

        testCase "registry is well-formed (unique, TOOLUP_-prefixed, described)"
        <| fun _ ->
            let names = all |> List.map _.EnvVar

            Expect.equal
                (List.length names)
                (names |> List.distinct |> List.length)
                "duplicate EnvVar in ConfigKeys.all"

            for d in all do
                Expect.isTrue (d.EnvVar.StartsWith "TOOLUP_") (sprintf "env var %s is not TOOLUP_-prefixed" d.EnvVar)

                Expect.isFalse
                    (String.IsNullOrWhiteSpace d.Description)
                    (sprintf "%s has an empty description" d.EnvVar)

                Expect.isFalse (String.IsNullOrWhiteSpace d.Category) (sprintf "%s has an empty category" d.EnvVar)
    ]