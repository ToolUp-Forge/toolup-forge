// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.BuildIdentityEndpointTests

// ─── Phase 1137 — the production-safe `/version` route ───────────────
//
// `/dev/version` answers "which build is this?" but lists every loaded
// assembly and file timestamps, so it is mounted only under
// `EnableDevEndpoints`. A deployment check needs the one fact that is safe
// to serve: which commit is running. These tests pin, over a REAL composed
// host (a client bundle shipped, so the SPA shell is in play):
//
//   1. Off by default: without `ServerApp.withBuildIdentity` the route does
//      not exist, even with `EnableDevEndpoints` on.
//   2. On, it answers `version` and `commit` (the entry assembly's
//      informational version, split) with `no-store`, and nothing else —
//      no assembly name, no path, no timestamp.
//   3. The SPA shell does not swallow it, and does not lose any other path.
//   4. `/dev/version` is unchanged beside it (its own tests pin the shape).
//
// The reader is shared with `/dev/version`; its edge cases are pinned
// purely, on literals, where no build carries the shape under test.

open System
open System.IO
open System.Text.Json
open Expecto
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform

let private shellMarker = "SPA-SHELL-1137"

let private quietLogger: ILogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

type private LifetimeCapture
    (
        lifetime: Microsoft.Extensions.Hosting.IHostApplicationLifetime,
        sink: Threading.Tasks.TaskCompletionSource<Microsoft.Extensions.Hosting.IHostApplicationLifetime>
    ) =
    interface Microsoft.Extensions.Hosting.IHostedService with
        member _.StartAsync _ =
            lifetime.ApplicationStarted.Register(fun () -> sink.TrySetResult lifetime |> ignore)
            |> ignore

            Threading.Tasks.Task.CompletedTask

        member _.StopAsync _ = Threading.Tasks.Task.CompletedTask

let private freePort () =
    let listener = new Net.Sockets.TcpListener(Net.IPAddress.Loopback, 0)
    listener.Start()
    let port = (listener.LocalEndpoint :?> Net.IPEndPoint).Port
    listener.Stop()
    port

/// Compose and host the app (with a shipped client bundle) and hand `body` a
/// client on its port; the host is stopped, and the process-wide culture
/// `compose` sets is restored, whatever `body` does.
let private withHost (enableDev: bool) (configure: ServerApp -> ServerApp) (body: Net.Http.HttpClient -> Async<unit>) = async {
    let publicDir =
        Path.Combine(Path.GetTempPath(), "toolup-build-identity-tests", Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory publicDir |> ignore

    File.WriteAllText(
        Path.Combine(publicDir, "index.html"),
        "<!doctype html><html><body>" + shellMarker + "</body></html>"
    )

    let port = freePort ()

    let started =
        Threading.Tasks.TaskCompletionSource<Microsoft.Extensions.Hosting.IHostApplicationLifetime>(
            Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously
        )

    let capture: ComposeExtensions = {
        ComposeExtensions.empty with
            ServiceConfig =
                Some(fun services ->
                    services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(fun sp ->
                        LifetimeCapture(
                            sp.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>(),
                            started
                        )
                        :> Microsoft.Extensions.Hosting.IHostedService)
                    |> ignore

                    services)
    }

    let config = {
        ServerConfig.defaults with
            Port = port
            PublicPath = publicDir
            EnableDevEndpoints = enableDev
            TrustForwardedHeaders = false
            AcceptPlaintextSecretsWhenAuthRequired = true
            AcceptSameSiteOnlyCsrfWhenAuthRequired = true
    }

    let app =
        ServerApp.empty
        |> ServerApp.withConfig config
        |> ServerApp.withExtensions capture
        |> ServerApp.withStorage (ToolUp.Platform.Testing.Fakes.standardBlobStorage ())
        |> ServerApp.withLogger quietLogger
        |> configure

    let culture = Globalization.CultureInfo.DefaultThreadCurrentCulture
    let uiCulture = Globalization.CultureInfo.DefaultThreadCurrentUICulture

    let running =
        Threading.Tasks.Task.Factory.StartNew(
            (fun () -> ServerApp.run app),
            Threading.Tasks.TaskCreationOptions.LongRunning
        )

    try
        let! first =
            Threading.Tasks.Task.WhenAny(
                started.Task :> Threading.Tasks.Task,
                running :> Threading.Tasks.Task,
                Threading.Tasks.Task.Delay(TimeSpan.FromSeconds 60.0)
            )
            |> Async.AwaitTask

        if not (obj.ReferenceEquals(first, started.Task)) then
            if running.IsFaulted then
                raise running.Exception.InnerException

            failtest "the composed host did not start"

        use client = new Net.Http.HttpClient(BaseAddress = Uri $"http://127.0.0.1:{port}")
        do! body client
    finally
        if started.Task.IsCompletedSuccessfully then
            started.Task.Result.StopApplication()
            running.Wait(TimeSpan.FromSeconds 30.0) |> ignore

        Globalization.CultureInfo.DefaultThreadCurrentCulture <- culture
        Globalization.CultureInfo.DefaultThreadCurrentUICulture <- uiCulture

        try
            Directory.Delete(publicDir, true)
        with _ ->
            ()
}

/// GET a path as a browser navigation: (status, body, Cache-Control).
let private navigate (client: Net.Http.HttpClient) (path: string) = async {
    use request = new Net.Http.HttpRequestMessage(Net.Http.HttpMethod.Get, path)
    request.Headers.Add("Accept", "text/html")
    use! response = client.SendAsync request |> Async.AwaitTask
    let! text = response.Content.ReadAsStringAsync() |> Async.AwaitTask

    let cacheControl =
        match response.Headers.CacheControl with
        | null -> ""
        | c -> string c

    return int response.StatusCode, text, cacheControl
}

[<Tests>]
let tests =
    testList "Phase 1137 — /version build identity" [

        testList "the shared reader" [
            test "version and commit are split at the first +" {
                let id = DevDiagnosticsHandler.BuildIdentity.ofInformationalVersion "1.0.0+abc123"
                Expect.equal id.Version "1.0.0" "version carries no metadata"
                Expect.equal id.Commit (Some "abc123") "commit is the suffix"
            }

            test "a prerelease version keeps its label" {
                let id =
                    DevDiagnosticsHandler.BuildIdentity.ofInformationalVersion "0.37.0-alpha.1+9f04f29b"

                Expect.equal id.Version "0.37.0-alpha.1" "the prerelease label stays in the version"
                Expect.equal id.Commit (Some "9f04f29b") "commit is the suffix"
            }

            test "no + means no commit, never a guess" {
                let id = DevDiagnosticsHandler.BuildIdentity.ofInformationalVersion "1.0.0"
                Expect.equal id.Version "1.0.0" "version is verbatim"
                Expect.isNone id.Commit "no metadata, no commit"
            }

            test "an empty suffix is no commit" {
                let id = DevDiagnosticsHandler.BuildIdentity.ofInformationalVersion "1.0.0+"
                Expect.equal id.Version "1.0.0" "version is verbatim"
                Expect.isNone id.Commit "an empty suffix names no commit"
            }
        ]

        testAsync "off by default: without the builder /version does not exist, dev endpoints on or off" {
            for enableDev in [ false; true ] do
                do!
                    withHost enableDev id (fun client -> async {
                        let! status, body, _ = navigate client "/version"
                        // The shell answers an unclaimed extensionless GET, so the
                        // proof of absence is that the SERVER did not answer it.
                        Expect.equal status 200 "the SPA shell answers an unclaimed path"
                        Expect.stringContains body shellMarker "the shell, not a build identity"
                        Expect.isFalse (body.Contains "\"version\"") "no identity is disclosed"
                    })
        }

        testAsync "on: /version answers version and commit, no-store, and nothing else" {
            do!
                withHost false ServerApp.withBuildIdentity (fun client -> async {
                    let! status, body, cacheControl = navigate client "/version"
                    Expect.equal status 200 "the route answers"

                    Expect.stringContains
                        cacheControl
                        "no-store"
                        "a cached answer to 'which build?' is worse than none"

                    Expect.isFalse (body.Contains shellMarker) "the SPA shell did not swallow the route"

                    use doc = JsonDocument.Parse body
                    let props = doc.RootElement.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq

                    let entry = Reflection.Assembly.GetEntryAssembly()
                    let expected = DevDiagnosticsHandler.BuildIdentity.ofAssembly entry

                    Expect.equal
                        (doc.RootElement.GetProperty("version").GetString())
                        expected.Version
                        "the entry assembly's version"

                    match expected.Commit with
                    | Some commit ->
                        Expect.equal props (Set.ofList [ "version"; "commit" ]) "exactly the two fields"

                        Expect.equal
                            (doc.RootElement.GetProperty("commit").GetString())
                            commit
                            "the commit parsed from it"
                    | None -> Expect.equal props (Set.ofList [ "version" ]) "commit is absent, not guessed"

                    // Nothing that names the host: no assembly, no path, no timestamp.
                    let entryName = entry.GetName().Name

                    for forbidden in
                        [
                            entryName
                            "ToolUp."
                            Path.GetTempPath()
                            "DeployedAt"
                            "StartedAt"
                            "Companions"
                        ] do
                        Expect.isFalse (body.Contains forbidden) $"the body never contains '{forbidden}'"
                })
        }

        testAsync "on: it is independent of EnableDevEndpoints, and /dev/version stays gated" {
            do!
                withHost false ServerApp.withBuildIdentity (fun client -> async {
                    let! _, devBody, _ = navigate client "/dev/version"
                    Expect.isFalse (devBody.Contains "Companions") "no dev payload while the dev gate is off"
                })

            do!
                withHost true ServerApp.withBuildIdentity (fun client -> async {
                    let! status, body, _ = navigate client "/version"
                    Expect.equal status 200 "the route answers beside the dev routes"
                    Expect.isFalse (body.Contains "Companions") "and does not become /dev/version"

                    let! devStatus, devBody, _ = navigate client "/dev/version"
                    Expect.equal devStatus 200 "/dev/version answers when its gate is on"
                    Expect.stringContains devBody "Companions" "with its own, unchanged payload"
                })
        }

        testAsync "on: composing the builder twice mounts it once, and other paths still reach the shell" {
            do!
                withHost false (ServerApp.withBuildIdentity >> ServerApp.withBuildIdentity) (fun client -> async {
                    let! status, _, _ = navigate client "/version"
                    Expect.equal status 200 "still answers"

                    let! shellStatus, shellBody, _ = navigate client "/some/client/route"
                    Expect.equal shellStatus 200 "an unclaimed path still falls to the shell"
                    Expect.stringContains shellBody shellMarker "the exclusion is /version only"
                })
        }
    ]