// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// `toolup describe` — the offline door onto a deployment's
/// self-description (Phase 782).
///
/// Two forms:
///
///   * `toolup describe --json [--project <path> | --dll <path>]` runs the
///     composition root's own process in its `--describe` startup mode,
///     which composes the application, emits the composition inspector's
///     panel, the verification report and the per-component seam reach as
///     JSON, and exits without binding a listener. The documents are the
///     same bytes the running deployment's reflection tools return,
///     because both doors call the same projections. The exit code is the
///     verification report's: 0 when every composed section verified or
///     was not composed, non-zero when a composed section failed or would
///     not answer.
///   * `toolup describe --module <assembly|package> --json` emits one built
///     module's `ModuleSurface` with no composition root: the server
///     registration the module exports, described by the SDK version the
///     module was built against.
///
/// The CLI stays dependency-free (FSharp.Core only). The first form runs
/// the application rather than linking it; the second loads the module and
/// the `ToolUp.Platform.Server` it was built against into an isolated,
/// collectible load context and calls `ModuleSurface.describeJson` there by
/// reflection — so the descriptor is the module's own SDK's, never this
/// tool's idea of it.
module ToolUp.Cli.DescribeCommand

open System
open System.Diagnostics
open System.IO
open System.IO.Compression
open System.Reflection
open System.Runtime.InteropServices
open System.Runtime.Loader
open System.Text
open System.Text.Json
open ToolUp.Cli.Dispatch

// ── Options ───────────────────────────────────────────────────────────

/// What `describe` is pointed at.
type DescribeTarget =
    /// A composition root's project directory or `.fsproj`, run with
    /// `dotnet run`.
    | RootProject of path: string
    /// A composition root's built entry assembly, run with `dotnet <dll>`.
    | RootAssembly of path: string
    /// A built module assembly (`.dll`), a package file (`.nupkg`), or a
    /// package id resolved from the NuGet global packages folder.
    | Module of reference: string

type Options = {
    Json: bool
    Target: DescribeTarget
    /// `--section` values: which documents a root describe emits.
    Sections: string list
    /// `--panel`: which inspector panel the composition document carries.
    Panel: string option
    /// `--member`: the static `ServerModule` value a module describe reads,
    /// when the assembly exports more than one.
    Member: string option
    /// `--version`: the package version, when `--module` names a package id.
    Version: string option
    /// `--timeout`: seconds to wait for the composition root to exit.
    TimeoutSeconds: int
}

/// The defaults: the composition root in the current directory.
let defaultOptions: Options = {
    Json = false
    Target = RootProject "."
    Sections = []
    Panel = None
    Member = None
    Version = None
    TimeoutSeconds = 300
}

/// Parse the argument tokens that follow `describe`.
let parse (args: string list) : Result<Options, string> =
    let rec go (targets: DescribeTarget list) (opts: Options) (rest: string list) =
        match rest with
        | [] ->
            match targets with
            | [] -> Ok opts
            | [ target ] -> Ok { opts with Target = target }
            | _ -> Error "name at most one of --project, --dll and --module"
        | "--json" :: tail -> go targets { opts with Json = true } tail
        | "--project" :: path :: tail -> go (RootProject path :: targets) opts tail
        | "--dll" :: path :: tail -> go (RootAssembly path :: targets) opts tail
        | "--module" :: reference :: tail -> go (Module reference :: targets) opts tail
        | "--section" :: section :: tail ->
            go
                targets
                {
                    opts with
                        Sections = opts.Sections @ [ section ]
                }
                tail
        | "--panel" :: panel :: tail -> go targets { opts with Panel = Some panel } tail
        | "--member" :: name :: tail -> go targets { opts with Member = Some name } tail
        | "--version" :: version :: tail -> go targets { opts with Version = Some version } tail
        | "--timeout" :: seconds :: tail ->
            match Int32.TryParse seconds with
            | true, n when n > 0 -> go targets { opts with TimeoutSeconds = n } tail
            | _ -> Error(sprintf "--timeout takes a positive number of seconds, not '%s'" seconds)
        | [ flag ] when flag.StartsWith "--" -> Error(sprintf "%s needs a value" flag)
        | unknown :: _ -> Error(sprintf "unknown argument '%s'" unknown)

    match go [] defaultOptions args with
    | Error e -> Error e
    | Ok opts when not opts.Json ->
        Error "describe emits JSON only; pass --json (reserved so a later text rendering is not a breaking change)"
    | Ok({ Target = Module _ } as opts) when not opts.Sections.IsEmpty || opts.Panel.IsSome ->
        Error "--section and --panel describe a composition root; --module describes one module"
    | Ok({
             Target = (RootProject _ | RootAssembly _)
         } as opts) when opts.Member.IsSome || opts.Version.IsSome ->
        Error "--member and --version apply to --module only"
    | ok -> ok

// ── The composition-root form ─────────────────────────────────────────

/// The command line that runs a composition root in its `--describe`
/// startup mode, writing the description to `outFile`.
let rootInvocation (opts: Options) (outFile: string) : string * string list =
    let describeArgs =
        [ "--describe"; "--describe-out"; outFile ]
        @ (opts.Sections |> List.collect (fun s -> [ "--describe-section"; s ]))
        @ (opts.Panel
           |> Option.map (fun p -> [ "--describe-panel"; p ])
           |> Option.defaultValue [])

    match opts.Target with
    | RootAssembly path -> "dotnet", path :: describeArgs
    | RootProject path -> "dotnet", [ "run"; "--project"; path; "--no-launch-profile"; "--" ] @ describeArgs
    | Module _ -> invalidArg (nameof opts) "a module describe runs no process"

/// Runs a process to completion: the file name, its arguments and a
/// timeout in seconds, returning its exit code or `None` on timeout.
type ProcessRunner = string -> string list -> int -> int option

/// The real runner. The child's stdout is the composition root's own
/// startup logging, so it is forwarded to THIS process's stderr: stdout is
/// reserved for the description. A child that outlives the timeout is
/// killed with its process tree — an application on an SDK that predates
/// `--describe` ignores the flag and starts serving.
let processRunner: ProcessRunner =
    fun fileName args timeoutSeconds ->
        let psi = ProcessStartInfo(fileName)

        for a in args do
            psi.ArgumentList.Add a

        psi.UseShellExecute <- false
        psi.RedirectStandardOutput <- true
        use child = new Process(StartInfo = psi)

        child.OutputDataReceived.Add(fun e ->
            if not (isNull e.Data) then
                eprintfn "%s" e.Data)

        child.Start() |> ignore
        child.BeginOutputReadLine()

        if child.WaitForExit(timeoutSeconds * 1000) then
            child.WaitForExit()
            Some child.ExitCode
        else
            child.Kill true
            None

/// Describe a composition root through `runner`, writing the description
/// to `out` and returning the exit code the root returned.
let runRoot (runner: ProcessRunner) (out: TextWriter) (opts: Options) : int =
    let outFile =
        Path.Combine(Path.GetTempPath(), sprintf "toolup-describe-%s.json" (Guid.NewGuid().ToString "N"))

    let fileName, args = rootInvocation opts outFile

    try
        match runner fileName args opts.TimeoutSeconds with
        | None ->
            eprintfn
                "describe: the composition root did not exit within %d s. An application on a ToolUp.Platform.Server that predates the --describe startup mode ignores the flag and starts serving; raise --timeout only if composition itself is slow."
                opts.TimeoutSeconds

            ExitRuntimeError
        | Some code when File.Exists outFile ->
            out.WriteLine(File.ReadAllText outFile)
            out.Flush()
            code
        | Some code ->
            eprintfn
                "describe: the composition root exited %d without writing a description. It may have failed before composing (see the output above), or predate the --describe startup mode."
                code

            if code = ExitOk then ExitRuntimeError else code
    finally
        if File.Exists outFile then
            File.Delete outFile

// ── The module form ───────────────────────────────────────────────────

/// The fully-qualified name of the SDK's server-tier module registration.
[<Literal>]
let ServerModuleTypeName = "ToolUp.Platform.ServerModule"

/// The note every module description carries: what it is the surface OF.
[<Literal>]
let ModuleDeclaredNote =
    "This is the surface the module itself declares: the server registration it exports, as ModuleSurface.describe projects it (the server half of what the module contract pack reads). What a consuming composition root applies to that registration, and the client registration, which lives in the Fable-compiled tier, are not part of it."

/// The NuGet global packages folder: `NUGET_PACKAGES`, else the
/// per-user default.
let globalPackagesFolder () : string =
    match Environment.GetEnvironmentVariable "NUGET_PACKAGES" with
    | null
    | "" -> Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".nuget", "packages")
    | folder -> folder

/// Target frameworks a module's `lib/` may carry, most preferred first.
let private libFrameworks = [ "net10.0"; "net9.0"; "net8.0"; "netstandard2.1"; "netstandard2.0" ]

/// The ASP.NET Core shared framework beside the running runtime. A module
/// built against `ToolUp.Platform.Server` references it, and this tool —
/// a console application — does not load it by default.
let private aspNetCoreFrameworkDirectory () : string option =
    let runtimeDir =
        RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)

    let version = Path.GetFileName runtimeDir
    let shared = Path.GetDirectoryName(Path.GetDirectoryName runtimeDir)
    let aspNet = Path.Combine(shared, "Microsoft.AspNetCore.App")
    let exact = Path.Combine(aspNet, version)

    if Directory.Exists exact then
        Some exact
    elif Directory.Exists aspNet then
        let major = version.Split('.').[0] + "."

        Directory.GetDirectories aspNet
        |> Array.filter (fun d -> (Path.GetFileName d).StartsWith major)
        |> Array.sortDescending
        |> Array.tryHead
    else
        None

/// Version directories under a package folder, best first: `preferred`
/// when present, then descending by numeric version.
let private versionDirectories (packageFolder: string) (preferred: string option) : string list =
    if not (Directory.Exists packageFolder) then
        []
    else
        let numeric (dir: string) =
            let name = Path.GetFileName dir
            let core = name.Split('-').[0]

            match Version.TryParse core with
            | true, v -> v
            | _ -> Version(0, 0)

        let all =
            Directory.GetDirectories packageFolder
            |> Array.sortByDescending numeric
            |> List.ofArray

        match preferred with
        | Some v ->
            let exact = Path.Combine(packageFolder, v.ToLowerInvariant())

            (if Directory.Exists exact then [ exact ] else [])
            @ (all |> List.filter ((<>) exact))
        | None -> all

/// The best `lib/<tfm>` directory of an extracted package, if it has one.
let private libDirectory (packageVersionDir: string) : string option =
    libFrameworks
    |> List.map (fun tfm -> Path.Combine(packageVersionDir, "lib", tfm))
    |> List.tryFind Directory.Exists

/// Resolve an assembly by scanning the global packages folder for a
/// package of the same name, preferring the version the reference names.
let private scanPackages (packagesRoot: string) (name: AssemblyName) : string option =
    let preferred =
        match name.Version with
        | null -> None
        | v -> Some(sprintf "%d.%d.%d" v.Major v.Minor (max v.Build 0))

    versionDirectories (Path.Combine(packagesRoot, name.Name.ToLowerInvariant())) preferred
    |> List.tryPick (fun dir ->
        libDirectory dir
        |> Option.map (fun lib -> Path.Combine(lib, name.Name + ".dll"))
        |> Option.filter File.Exists)

/// The runtime-asset map of a module's `.deps.json`: assembly simple name
/// (lower-cased) to the file that serves it — package assets under the
/// global packages folder, project assets beside the module.
let private depsAssets (mainPath: string) (packagesRoot: string) : Map<string, string> =
    let depsPath = Path.ChangeExtension(mainPath, ".deps.json")

    if not (File.Exists depsPath) then
        Map.empty
    else
        use doc = JsonDocument.Parse(File.ReadAllText depsPath)
        let root = doc.RootElement
        let moduleDir = Path.GetDirectoryName mainPath

        let libraries =
            match root.TryGetProperty "libraries" with
            | true, libs ->
                libs.EnumerateObject()
                |> Seq.map (fun p -> p.Name, p.Value.Clone())
                |> Map.ofSeq
            | _ -> Map.empty

        match root.TryGetProperty "targets" with
        | true, targets ->
            [
                for target in targets.EnumerateObject() do
                    for library in target.Value.EnumerateObject() do
                        match library.Value.TryGetProperty "runtime" with
                        | true, runtime ->
                            for asset in runtime.EnumerateObject() do
                                let file =
                                    match libraries.TryFind library.Name with
                                    | Some info when
                                        (match info.TryGetProperty "type" with
                                         | true, t -> t.GetString() = "package"
                                         | _ -> false)
                                        ->
                                        match info.TryGetProperty "path" with
                                        | true, p -> Path.Combine(packagesRoot, p.GetString(), asset.Name)
                                        | _ -> Path.Combine(moduleDir, Path.GetFileName asset.Name)
                                    | _ -> Path.Combine(moduleDir, Path.GetFileName asset.Name)

                                (Path.GetFileNameWithoutExtension asset.Name).ToLowerInvariant(), file
                        | _ -> ()
            ]
            |> List.filter (snd >> File.Exists)
            |> List.distinctBy fst
            |> Map.ofList
        | _ -> Map.empty

/// The simple names of the shared-framework assemblies this process already
/// runs on — every trusted-platform assembly under the runtime's `shared`
/// root. These are never loaded a second time into a module's context: two
/// copies of a framework assembly in one process is a type-identity split
/// (a second `System.Text.Json` fails its own type initializer), and the
/// framework is the one dependency a module cannot have pinned differently.
let private sharedFrameworkNames: Lazy<Set<string>> =
    lazy
        (let runtimeDir =
            RuntimeEnvironment
                .GetRuntimeDirectory()
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)

         let sharedRoot = Path.GetDirectoryName(Path.GetDirectoryName runtimeDir)

         match AppContext.GetData "TRUSTED_PLATFORM_ASSEMBLIES" with
         | :? string as paths ->
             paths.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
             |> Array.filter (fun p -> p.StartsWith(sharedRoot, StringComparison.OrdinalIgnoreCase))
             |> Array.map (fun p -> (Path.GetFileNameWithoutExtension p).ToLowerInvariant())
             |> Set.ofArray
         | _ -> Set.empty)

/// An isolated, collectible load context for one module and the SDK it was
/// built against. A shared-framework assembly the process already runs on
/// is left to the default context; anything else resolves beside the
/// module, then from the module's `.deps.json`, the ASP.NET Core shared
/// framework, and the global packages folder.
type private ModuleLoadContext(mainPath: string, packagesRoot: string) =
    inherit AssemblyLoadContext("toolup-describe-module", true)

    let moduleDir = Path.GetDirectoryName mainPath
    let deps = depsAssets mainPath packagesRoot
    let aspNet = aspNetCoreFrameworkDirectory ()

    let probe (name: AssemblyName) : string option =
        let fileName = name.Name + ".dll"

        if sharedFrameworkNames.Value.Contains(name.Name.ToLowerInvariant()) then
            None
        else
            [
                fun () -> Some(Path.Combine(moduleDir, fileName))
                fun () -> deps.TryFind(name.Name.ToLowerInvariant())
                fun () -> aspNet |> Option.map (fun dir -> Path.Combine(dir, fileName))
                fun () -> scanPackages packagesRoot name
            ]
            |> List.tryPick (fun candidate -> candidate () |> Option.filter File.Exists)

    override this.Load(name: AssemblyName) : Assembly =
        match probe name with
        | Some path -> this.LoadFromAssemblyPath path
        | None -> null

/// One public static `ServerModule` an assembly exports: its dotted name
/// and how to obtain it.
type private ExportedServerModule = { Name: string; Get: unit -> obj }

let private exportedTypes (assembly: Assembly) : Type list =
    try
        assembly.GetExportedTypes() |> List.ofArray
    with :? ReflectionTypeLoadException as ex ->
        ex.Types |> Array.filter (isNull >> not) |> List.ofArray

let private dotted (t: Type) = t.FullName.Replace('+', '.')

/// Every public static `ServerModule` value an assembly exports — an F#
/// module-level `let` (a static property) or a parameterless function
/// (a static method taking nothing, or `unit`) — plus the builders that
/// return one but take arguments, named so a refusal can say why it could
/// not use them.
let private serverModulesOf (assembly: Assembly) : ExportedServerModule list * string list =
    let isServerModule (t: Type) =
        not (isNull t) && t.FullName = ServerModuleTypeName

    let found =
        exportedTypes assembly
        |> List.collect (fun t ->
            let properties =
                try
                    t.GetProperties(BindingFlags.Public ||| BindingFlags.Static) |> List.ofArray
                with _ -> []

            let methods =
                try
                    t.GetMethods(BindingFlags.Public ||| BindingFlags.Static)
                    |> Array.filter (fun m -> not m.IsSpecialName && not m.ContainsGenericParameters)
                    |> List.ofArray
                with _ -> []

            [
                for p in properties do
                    if p.CanRead && p.GetIndexParameters().Length = 0 && isServerModule p.PropertyType then
                        Choice1Of2 {
                            Name = dotted t + "." + p.Name
                            Get = fun () -> p.GetValue null
                        }
                for m in methods do
                    if isServerModule m.ReturnType then
                        match m.GetParameters() with
                        | [||] ->
                            Choice1Of2 {
                                Name = dotted t + "." + m.Name
                                Get = fun () -> m.Invoke(null, [||])
                            }
                        | [| p |] when p.ParameterType.FullName = "Microsoft.FSharp.Core.Unit" ->
                            Choice1Of2 {
                                Name = dotted t + "." + m.Name
                                Get = fun () -> m.Invoke(null, [| null |])
                            }
                        | _ -> Choice2Of2(dotted t + "." + m.Name)
            ])

    (found
     |> List.choose (function
         | Choice1Of2 m -> Some m
         | _ -> None)
     |> List.sortBy _.Name),
    (found
     |> List.choose (function
         | Choice2Of2 n -> Some n
         | _ -> None)
     |> List.sort)

/// The module assembly a `--module` reference names: an assembly path, a
/// `.nupkg` (extracted to `scratch`), or a package id in the global
/// packages folder.
let resolveModuleAssembly
    (packagesRoot: string)
    (scratch: string)
    (reference: string)
    (version: string option)
    : Result<string, string> =
    let pickDll (lib: string) (stem: string) : Result<string, string> =
        let dlls = Directory.GetFiles(lib, "*.dll")
        let named = Path.Combine(lib, stem + ".dll")

        if File.Exists named then
            Ok named
        elif dlls.Length = 1 then
            Ok dlls[0]
        else
            Error(sprintf "%s holds %d assemblies and none is named %s.dll" lib dlls.Length stem)

    if reference.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) then
        if File.Exists reference then
            Ok(Path.GetFullPath reference)
        else
            Error(sprintf "no assembly at %s" reference)
    elif reference.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase) then
        if not (File.Exists reference) then
            Error(sprintf "no package at %s" reference)
        else
            ZipFile.ExtractToDirectory(reference, scratch, true)

            let id =
                Directory.GetFiles(scratch, "*.nuspec")
                |> Array.tryHead
                |> Option.map Path.GetFileNameWithoutExtension
                |> Option.defaultValue (Path.GetFileNameWithoutExtension reference)

            match libDirectory scratch with
            | Some lib -> pickDll lib id
            | None -> Error(sprintf "%s carries no lib/ assets for %s" reference (String.concat ", " libFrameworks))
    else
        match versionDirectories (Path.Combine(packagesRoot, reference.ToLowerInvariant())) version with
        | [] ->
            Error(
                sprintf
                    "'%s' is not an assembly path, a .nupkg, or a package in %s (restore it first)"
                    reference
                    packagesRoot
            )
        | best :: _ ->
            match libDirectory best with
            | Some lib -> pickDll lib reference
            | None -> Error(sprintf "%s carries no lib/ assets" best)

/// Describe one built module: load it and the SDK it was built against in
/// an isolated context, find its exported `ServerModule`, and return the
/// description document.
let describeModule (assemblyPath: string) (memberName: string option) (packagesRoot: string) : Result<string, string> =
    let context = ModuleLoadContext(assemblyPath, packagesRoot)

    try
        try
            let assembly = context.LoadFromAssemblyPath assemblyPath
            let modules, builders = serverModulesOf assembly

            let chosen =
                match memberName, modules with
                | Some name, _ ->
                    match modules |> List.tryFind (fun m -> m.Name = name) with
                    | Some m -> Ok m
                    | None ->
                        Error(
                            sprintf
                                "%s exports no parameterless ServerModule named %s; it exports: %s"
                                (Path.GetFileName assemblyPath)
                                name
                                (if modules.IsEmpty then
                                     "none"
                                 else
                                     modules |> List.map _.Name |> String.concat ", ")
                        )
                | None, [ single ] -> Ok single
                | None, [] ->
                    let builderNote =
                        if builders.IsEmpty then
                            ""
                        else
                            sprintf
                                " It exports ServerModule builders that take arguments (%s); a describe needs a value the module declares by itself."
                                (String.concat ", " builders)

                    Error(
                        sprintf
                            "%s exports no ServerModule value: there is no module-declared surface to describe. A module whose registration is assembled in its consumer's composition root (ServerModule.create ... inside the host) declares none of its own.%s"
                            (Path.GetFileName assemblyPath)
                            builderNote
                    )
                | None, many ->
                    Error(
                        sprintf
                            "%s exports %d ServerModule values; name one with --member: %s"
                            (Path.GetFileName assemblyPath)
                            many.Length
                            (many |> List.map _.Name |> String.concat ", ")
                    )

            match chosen with
            | Error e -> Error e
            | Ok exported ->
                let value = exported.Get()
                let serverModuleType = value.GetType()

                let describeJson =
                    serverModuleType.Assembly.GetTypes()
                    |> Array.tryPick (fun t ->
                        if
                            t.FullName = "ToolUp.Platform.ModuleSurfaceModule"
                            || t.FullName = "ToolUp.Platform.ModuleSurface"
                        then
                            match t.GetMethod("describeJson", [| serverModuleType |]) with
                            | null -> None
                            | m -> Some m
                        else
                            None)

                match describeJson with
                | None ->
                    Error(
                        sprintf
                            "the %s this module was built against has no ModuleSurface.describeJson; describe needs a module built against a ToolUp.Platform.Server that ships it"
                            serverModuleType.Assembly.FullName
                    )
                | Some describe ->
                    let surface = describe.Invoke(null, [| value |]) :?> string
                    let str (s: string) = JsonSerializer.Serialize s

                    let document =
                        StringBuilder()
                            .Append("{\"assembly\":")
                            .Append(str (Path.GetFileName assemblyPath))
                            .Append(",\"member\":")
                            .Append(str exported.Name)
                            .Append(",\"declaredBy\":\"module\"")
                            .Append(",\"note\":")
                            .Append(str ModuleDeclaredNote)
                            .Append(",\"surface\":")
                            .Append(surface)
                            .Append('}')
                            .ToString()

                    Ok document
        with
        | :? TargetInvocationException as ex when not (isNull ex.InnerException) ->
            Error(sprintf "the module's ServerModule raised while being built: %s" ex.InnerException.Message)
        | :? FileNotFoundException as ex ->
            Error(sprintf "could not load %s or one of its dependencies: %s" (Path.GetFileName assemblyPath) ex.Message)
        | :? BadImageFormatException as ex -> Error(sprintf "%s is not a .NET assembly: %s" assemblyPath ex.Message)
    finally
        context.Unload()

let private runModule (opts: Options) (reference: string) : int =
    let packagesRoot = globalPackagesFolder ()

    let scratch =
        Path.Combine(Path.GetTempPath(), sprintf "toolup-describe-%s" (Guid.NewGuid().ToString "N"))

    try
        match
            resolveModuleAssembly packagesRoot scratch reference opts.Version
            |> Result.bind (fun path -> describeModule path opts.Member packagesRoot)
        with
        | Ok document ->
            Console.Out.WriteLine document
            ExitOk
        | Error e ->
            eprintfn "describe: %s" e
            ExitRuntimeError
    finally
        if Directory.Exists scratch then
            try
                Directory.Delete(scratch, true)
            with _ ->
                ()

// ── The command ───────────────────────────────────────────────────────

let help = [
    "Usage: toolup describe --json [--project <path> | --dll <path>]"
    "                       [--section <document>]... [--panel <panel>] [--timeout <seconds>]"
    "       toolup describe --module <assembly.dll | package.nupkg | package-id> --json"
    "                       [--version <v>] [--member <Namespace.Module.value>]"
    ""
    "Emit a deployment's self-description as JSON, without a running server."
    ""
    "Composition root (default: the project in the current directory):"
    "  Runs the root in its --describe startup mode, which composes the"
    "  application and exits without binding a listener. The documents are"
    "  the same bytes the deployment's reflection tools return:"
    "    composition          the composition inspector panel (--panel: composition,"
    "                         surfaces, rules, disclosure, provenance)"
    "    verification-report  the deployment verification report"
    "    module-reach         per-component declared, derived and observed seam reach"
    "  One --section emits that document alone; otherwise one object keyed by document."
    "  Exit code: the verification report's - 0 when every composed section verified"
    "  or was not composed, non-zero when a composed section failed or would not"
    "  answer. 2 for a usage error."
    ""
    "Module (--module):"
    "  Loads one built module with the ToolUp.Platform.Server it was built against"
    "  and emits its ModuleSurface: the server registration the module itself"
    "  exports. What a consuming composition root applies on top is not part of it,"
    "  and the output says so. --member picks the value when the assembly exports"
    "  more than one."
]

let command = {
    Path = [ "describe" ]
    Summary = "Emit a deployment's (or one module's) self-description as JSON."
    Help = help
    Run =
        fun args ->
            match parse args with
            | Error e ->
                eprintfn "describe: %s" e
                eprintfn "Run 'toolup describe --help' for usage."
                ExitUsage
            | Ok({ Target = Module reference } as opts) -> runModule opts reference
            | Ok opts -> runRoot processRunner Console.Out opts
}