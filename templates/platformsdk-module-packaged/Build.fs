module Build

open System.IO
open ToolUp.Platform
open Fake.Core
open Fake.Core.TargetOperators
open Fake.IO

// ─── Packaged-module build pipeline ──────────────────────────────
//
// `dotnet run --project Build.fsproj -- <Target>`
//
//   Format                 fantomas over src/ and tests/
//   FormatCheck            the same, as a gate
//   Build                  the module + its test project
//   Test                   the conformance test project
//   VerifyPackagedModule   the packaging layout laws (from the SDK)
//   Pack                   nupkg into the local feed
//
// The dependency chain wires the two conformance layers AHEAD of Pack,
// which is the whole point of the shape: neither layer is something a
// release can skip by forgetting to run it.

let private mainProject = "src/MyModule/MyModule.fsproj"
let private testProject = "tests/MyModule.Tests/MyModule.Tests.fsproj"

/// Where `Pack` writes. Configurable at scaffold time; a relative path
/// is resolved against this repo's root.
let private localFeed = @"LOCAL_FEED_PATH"

let private dotnet args dir =
    CreateProcess.fromRawCommand "dotnet" args
    |> CreateProcess.withWorkingDirectory dir
    |> CreateProcess.ensureExitCode

let private run args dir = dotnet args dir |> Proc.run |> ignore

// ─── The packaged layout contract ────────────────────────────────
//
// What this module DECLARES about its own packed shape. Nothing is
// inferred: the SDK's four laws (shadow-subset, server-exclusion,
// compile-order, asset-path) are checked against these declarations,
// which is what keeps the check from being tautological.
//
// Add a client file? It goes in the main project, in the shadow
// project, and in the `fable\` Content declaration. Miss one and this
// target names which law broke over which file — here, rather than in
// a consumer's Fable build.
let private layout = {
    PackagedModuleCheckOptions.forProject mainProject with
        ShadowProject = "src/MyModule/fable/MyModule.fsproj"
        Contract = {
            PackagedModuleContract.create "MyModule" "MyModule.fsproj" with
                ServerOnlyFiles = [ "Server.fs" ]
                RequiredAssets = [ "icons/module-icon.svg" ]
        }
}

[<EntryPoint>]
let main args =
    let execContext = Context.FakeExecutionContext.Create false "Build.fsproj" []
    Context.setExecutionContext (Context.RuntimeContext.Fake execContext)

    Target.initEnvironment ()

    Target.create "Format" (fun _ -> run [ "fantomas"; "src/"; "tests/" ] ".")

    Target.create "FormatCheck" (fun _ -> run [ "fantomas"; "--check"; "src/"; "tests/" ] ".")

    Target.create "Build" (fun _ ->
        run [ "build"; mainProject; "-c"; "Release"; "--nologo" ] "."
        run [ "build"; testProject; "-c"; "Release"; "--nologo" ] ".")

    Target.create "Test" (fun _ -> run [ "run"; "--project"; testProject; "-c"; "Release"; "--no-build" ] ".")

    // The SDK's shadow-project conformance target. Pure comparison over
    // the two project files plus the pack declarations — no MSBuild
    // evaluation, no Fable invocation, no consumer app, so it runs in
    // milliseconds and BEFORE anything is packed.
    PackagedModuleConformance.registerTarget layout

    Target.create "Pack" (fun _ ->
        let outputDir = Path.GetFullPath localFeed
        Directory.ensure outputDir
        run [ "pack"; mainProject; "-c"; "Release"; "-o"; outputDir; "--nologo" ] ".")

    "Build" ==> "Test" |> ignore
    "Build" ==> "VerifyPackagedModule" ==> "Pack" |> ignore
    "Test" ==> "Pack" |> ignore

    let target =
        args
        |> Array.tryHead
        |> Option.filter (fun t -> not (t.StartsWith "--"))
        |> Option.defaultValue "Build"

    Target.runOrDefault target
    0