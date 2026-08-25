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

// ─── Phase 698 — the reader-migration sweep's two parsing needs ───────
//
// Both the bindability arm and the ratchet below have to answer "which
// registry key does this expression name?", and neither can do it by
// looking for `Names.*` alone. Readers routinely bind the name once:
//
//     [<Literal>]
//     let private AuthModeEnvVar = ConfigKeys.Names.authMode
//     ...
//     ConfigResolution.tryValue AuthModeEnvVar
//
// so that the read and the refusal message cite one string rather than
// two that can drift. A parse anchored only on `Names.*` sees none of
// those reads — which would have made the sweep look like it had
// migrated four keys when it had migrated twenty.

/// `Names.foo` → `TOOLUP_FOO`, parsed from the registry source.
let private bindingToVar (root: string) =
    let registrySource =
        Path.Combine(root, "src", "ToolUp.Platform.Core", "Shared", "Types", "ConfigKeyDescriptor.fs")
        |> File.ReadAllText

    let start = registrySource.IndexOf "module Names ="
    let stop = registrySource.IndexOf "let all: ConfigKeyDescriptor list ="
    let namesModule = registrySource.Substring(start, stop - start)

    Regex.Matches(namesModule, "let\\s+(\\w+)\\s*=\\s*\"(TOOLUP_[A-Z0-9_]+)\"")
    |> Seq.map (fun m -> m.Groups[1].Value, m.Groups[2].Value)
    |> Map.ofSeq

/// A binding of a local identifier to a registry key — `let private
/// FooEnvVar = ConfigKeys.Names.foo`, or the static-member form some
/// contributors use. Record FIELDS (`EnvVar = Names.adminToken`) are
/// deliberately not matched: the `let` / `member val` prefix is what
/// distinguishes a name-alias from the registry describing itself.
let private aliasPattern =
    Regex(
        "(?:let|member\\s+val)\\s+(?:private\\s+)?(\\w+)\\s*=\\s*(?:ConfigKeys\\.)?Names\\.(\\w+)",
        RegexOptions.Compiled
    )

let private aliasesIn (text: string) =
    aliasPattern.Matches text
    |> Seq.map (fun m -> m.Groups[1].Value, m.Groups[2].Value)
    |> Map.ofSeq

/// One shipped source file, with everything the arms below ask of it.
type private ShippedSource = {
    /// Repo-relative, forward-slashed — the form findings are reported in.
    RelPath: string
    Text: string
    Aliases: Map<string, string>
    PlatformSide: bool
}

/// Every shipped source file, read ONCE for the whole pack.
///
/// The two arms below and the alias map each used to walk the tree
/// independently, so one test pack made roughly four passes over 1,413
/// files. That is wasteful on its own terms, and it is a poor neighbour:
/// Expecto runs cases in parallel, and a burst of file I/O from a test
/// that is merely READING SOURCE is no reason to perturb one that is
/// measuring a race. (The Phase 312 fan-out test next door decides a
/// cancellation against a ten-second budget; it is flaky for its own
/// reasons, but this pack should not be adding to the noise.)
let private shippedSources =
    lazy
        (let root = repoRoot ()

         shippedSourceFiles root
         |> List.map (fun full ->
             let text = File.ReadAllText full
             let norm = full.Replace('\\', '/')

             {
                 RelPath = norm.Substring(norm.IndexOf "src/")
                 Text = text
                 Aliases = aliasesIn text
                 PlatformSide =
                     norm.Contains "/src/ToolUp.Platform.Core/"
                     || norm.Contains "/src/ToolUp.Platform.Server/"
             }))

/// Alias → the binding(s) it names, across every shipped file. Needed
/// because a reader legitimately cites another module's alias
/// (`BootstrapTeam.initialAdminEnvVar` from two different validators),
/// so a file-local map alone cannot resolve it.
///
/// A SET rather than a single binding, because `EnvVar` is bound in two
/// files to two different keys. An ambiguous alias resolves file-locally
/// or not at all — guessing between two keys is exactly how a sweep
/// declares a key bindable that nothing reads.
let private globalAliases =
    lazy
        (shippedSources.Value
         |> List.collect (fun f -> Map.toList f.Aliases)
         |> List.fold
             (fun acc (alias, binding) ->
                 let existing = acc |> Map.tryFind alias |> Option.defaultValue Set.empty
                 Map.add alias (Set.add binding existing) acc)
             Map.empty)

/// Resolve an expression appearing in argument position — `Names.foo`,
/// `ConfigKeys.Names.foo`, a fully-qualified `ToolUp.Platform.ConfigKeys
/// .Names.foo`, a bare alias, or a type-qualified one
/// (`OidcIssuerCspContributor.EnvVar`) — to its registry key.
let private resolveKeyExpression
    (varOfBinding: Map<string, string>)
    (fileAliases: Map<string, string>)
    (global': Map<string, Set<string>>)
    (expr: string)
    : string option =
    let namesForm = Regex.Match(expr, "(?:^|\\.)Names\\.(\\w+)$")

    if namesForm.Success then
        Map.tryFind namesForm.Groups[1].Value varOfBinding
    else
        let lastSegment = expr.Split('.') |> Array.last

        let binding =
            match Map.tryFind lastSegment fileAliases with
            | Some b -> Some b
            | None ->
                match Map.tryFind lastSegment global' with
                | Some candidates when Set.count candidates = 1 -> Some(Set.minElement candidates)
                | _ -> None

        binding |> Option.bind (fun b -> Map.tryFind b varOfBinding)

/// A seam resolution: `ConfigResolution.tryValue <expr>`.
let private seamCallPattern =
    Regex("ConfigResolution\\.(?:tryValue|tryResolve|sourceOf)\\s+\\(?\\s*([\\w.]+)", RegexOptions.Compiled)

/// A direct environment read: `Environment.GetEnvironmentVariable <expr>`,
/// with or without parentheses, and with or without a `System.` prefix.
let private directEnvReadPattern =
    Regex("Environment\\.GetEnvironmentVariable\\s*\\(?\\s*([\\w.]+|\"[^\"]*\")", RegexOptions.Compiled)

/// A private wrapper whose body opens with a direct environment read —
/// the shape every reader migrated by Phase 698 used
/// (`let private envVar (name: string) = match Environment.GetEnvironment
/// Variable name with ...`). Detected so the ratchet cannot be satisfied
/// by moving the read one call deep, which is the obvious way past a
/// check that only looks at what `GetEnvironmentVariable` is applied to.
let private envWrapperPattern =
    Regex(
        "let\\s+(?:private\\s+)?(\\w+)[^=\\r\\n]*=\\s*(?:\\r?\\n\\s*)*(?:match\\s+)?(?:System\\.)?Environment\\.GetEnvironmentVariable",
        RegexOptions.Compiled
    )

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
            let files = shippedSources.Value

            Expect.isGreaterThan
                (List.length files)
                100
                "scanned suspiciously few source files — the scan root is probably wrong, and an empty scan passes vacuously"

            let readVars =
                files
                |> List.collect (fun f ->
                    envVarLiteralPattern.Matches f.Text
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
            let bindingToVar = bindingToVar root

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
            // another purpose contributes nothing — a distinction Phase 698
            // made load-bearing rather than incidental: the readers it
            // migrated cite their key in refusal messages as often as they
            // read it, and several cite keys they never read at all.
            //
            // Phase 698 widened the ARGUMENT the pattern accepts (a local
            // alias, or another module's) without widening the anchor.
            let global' = globalAliases.Value

            let seamCallKeys =
                shippedSources.Value
                |> List.collect (fun f ->
                    seamCallPattern.Matches f.Text
                    |> Seq.choose (fun m -> resolveKeyExpression bindingToVar f.Aliases global' m.Groups[1].Value)
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

        testCase "no Platform-side reader consults the environment directly for a registered key"
        <| fun _ ->
            // Phase 698 — the ratchet. Everything above measures the
            // DECLARATION (`manifestBindable`) against the readers that
            // exist; this measures the readers against the seam, and it is
            // what makes the migration sweep terminate instead of decay.
            // Without it, the next reader added to the SDK reads the
            // environment directly because that is what the file beside it
            // used to do, the key silently stops honouring the manifest,
            // and nothing anywhere goes red — the declaration still matches
            // the OTHER readers that did migrate.
            //
            // Scope is Platform-side (`ToolUp.Platform.Core` /
            // `.Server`), matching the sweep. Companion packages keep their
            // own rule — they receive dependencies through `create` and are
            // not addressed here.
            //
            // Two shapes are refused, because closing only the first leaves
            // the obvious way around it open: applying
            // `Environment.GetEnvironmentVariable` to a registered key, and
            // applying a file-local wrapper over it to one.
            let root = repoRoot ()
            let varOfBinding = bindingToVar root
            let registered = all |> List.map _.EnvVar |> Set.ofList
            let global' = globalAliases.Value
            let platformFiles = shippedSources.Value |> List.filter _.PlatformSide

            Expect.isGreaterThan
                (List.length platformFiles)
                100
                "scanned suspiciously few Platform-side files — an empty scan passes vacuously"

            // The residue that legitimately reads the environment directly.
            // Deliberately per-FILE and deliberately short: every entry is a
            // read the seam cannot serve, not a reader that has not got
            // round to migrating.
            //
            //   * `ConfigResolver` performs the bootstrap read of
            //     `TOOLUP_CONFIG_FILE` — the variable naming the manifest,
            //     which by construction cannot be resolved THROUGH the
            //     manifest. The loader refuses that key inside a manifest
            //     for the same reason.
            //
            // Three further files read the environment and are NOT listed,
            // because their reads name no registered key and so never reach
            // this check: `EnvironmentSecretStore` and `FileSecretStore`
            // resolve the open-ended `TOOLUP_{SCOPE}_{KEY}` secret family,
            // `ComponentConfigResolver` the open-ended `TOOLUP_COMPONENT__*`
            // overrides, and `SDK.Server` the non-registry `SERVER_PORT`.
            // Listing them would claim an exemption none of them needs, and
            // would exempt their FUTURE reads too.
            let allowedDirectReaders =
                Set.ofList [ "src/ToolUp.Platform.Server/Server/ConfigResolver.fs" ]

            let findings =
                platformFiles
                |> List.collect (fun file ->
                    let relFromRoot = file.RelPath

                    if Set.contains relFromRoot allowedDirectReaders then
                        []
                    else
                        let text = file.Text
                        let fileAliases = file.Aliases

                        let resolveKey (expr: string) =
                            if expr.StartsWith "\"" then
                                let literal = expr.Trim '"'

                                if Set.contains literal registered then
                                    Some literal
                                else
                                    None
                            else
                                resolveKeyExpression varOfBinding fileAliases global' expr

                        let directHits =
                            directEnvReadPattern.Matches text
                            |> Seq.choose (fun m -> resolveKey m.Groups[1].Value)
                            |> Seq.map (fun key -> sprintf "%s reads %s directly" relFromRoot key)
                            |> List.ofSeq

                        // The indirection arm: a wrapper defined in this file
                        // over a direct read, then applied to a registered key.
                        let wrappers =
                            envWrapperPattern.Matches text
                            |> Seq.map (fun m -> m.Groups[1].Value)
                            |> Set.ofSeq

                        let wrapperHits =
                            wrappers
                            |> Set.toList
                            |> List.collect (fun w ->
                                Regex.Matches(text, sprintf "(?<![\\w.])%s\\s+\\(?\\s*([\\w.]+)" (Regex.Escape w))
                                |> Seq.choose (fun m -> resolveKey m.Groups[1].Value)
                                |> Seq.map (fun key ->
                                    sprintf
                                        "%s reads %s through the local environment wrapper `%s`"
                                        relFromRoot
                                        key
                                        w)
                                |> List.ofSeq)

                        directHits @ wrapperHits)
                |> List.distinct

            Expect.isEmpty
                findings
                (sprintf
                    "These Platform-side readers consult the environment directly for a key the registry describes, so a deployment configuration manifest that sets it would be silently ignored. Resolve through ConfigResolution.tryValue / tryResolve instead — with no manifest installed the seam IS the old read, so behaviour is unchanged (GP 11):%s%s"
                    Environment.NewLine
                    (findings |> List.map (sprintf "  - %s") |> String.concat Environment.NewLine))

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