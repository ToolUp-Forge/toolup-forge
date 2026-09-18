// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.DevVersionEndpointTests

// ─── Phase 9a follow-up — the `/dev/version` endpoint ────────────────
//
// Four claims, each pinned where it is actually decided:
//
//   1. **The payload shape.** `Sdk` / `Companions` / `Commit` /
//      `DeployedAt` / `StartedAt`, with the metadata split — `Sdk`
//      carries no `+`, `Commit` is the suffix or `null` — asserted on
//      the JSON a client sees, not on the F# record.
//   2. **Zero-registration coverage.** `ToolUp.Platform.Core` and
//      `ToolUp.Platform.Server` appear in `Companions` without anyone
//      registering anything, and the `Sdk` slot is the Server tier's
//      version. This is the property that makes the endpoint useful
//      the day it ships: every companion already composed is listed.
//   3. **The DI extension.** A `ComponentVersion` registered as a
//      singleton — the route for a native library's version — is
//      appended to the same list.
//   4. **The gate.** The route is mounted by
//      `BuildRouteHandlers.buildRouteHandlers`'s `DevDiagnosticsRoutes`
//      under `ServerConfig.EnableDevEndpoints`, and that is the function
//      exercised here, not a re-statement of it: with the flag off the
//      builder returns NO dev routes and `/dev/version` is a 404 from
//      the terminal middleware; with it on, 200.
//
// The gate test drives the real route list through a `TestServer`
// rather than calling `DevDiagnosticsHandler.routes` directly, because
// the gate does not live in `routes` — it lives one level up, and a
// test of the inner list would pass unchanged if the outer gate were
// deleted.

open System
open System.Net
open System.Net.Http
open System.Text.Json
open Expecto
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Giraffe
open ToolUp.Platform

// ─── Fixtures ─────────────────────────────────────────────────────────

/// An empty compose-time capture: `/dev/version` reads none of it, and
/// the gate under test does not either. Built by literal so the test
/// needs no `IServiceCollection` snapshot.
let private emptyCapture: DevDiagnosticsHandler.DevDiagnosticsCapture = {
    Modules = []
    Services = []
    TotalRouteHandlers = 0
    IndexInspectors = []
    LightweightFeatures = []
    CompositionSeam = {
        PreMiddlewareCount = 0
        PostMiddlewareCount = 0
        SecurityHeaderKeys = []
        CorsConfigured = false
        NotificationConsumers = []
    }
}

/// The dev-diagnostics route list exactly as `compose` mounts it: the
/// real `buildRouteHandlers` over `ServerConfig.defaults` with only
/// `EnableDevEndpoints` varied. Every other argument is the empty /
/// absent value, which is what a minimal deployment passes.
let private devRoutesFor (enableDevEndpoints: bool) : HttpHandler list =
    let config = {
        ServerConfig.defaults with
            EnableDevEndpoints = enableDevEndpoints
    }

    let routeHandlers =
        BuildRouteHandlers.buildRouteHandlers
            config
            []
            []
            ComposeExtensions.empty
            []
            None
            NoNotifications
            (ref None)
            (ref None)

    routeHandlers.DevDiagnosticsRoutes emptyCapture

/// A `TestServer` host mounting the given routes behind Giraffe's
/// `choose`, so an unmatched path falls through to the terminal
/// middleware's 404 exactly as it does in a composed app.
let private buildHost (registrations: IServiceCollection -> unit) (routes: HttpHandler list) : IHost =
    Host
        .CreateDefaultBuilder()
        .ConfigureWebHostDefaults(fun webHost ->
            webHost
                .UseTestServer()
                .ConfigureServices(fun services ->
                    services.AddGiraffe() |> ignore
                    registrations services)
                .Configure(fun (app: IApplicationBuilder) -> app.UseGiraffe(choose routes))
            |> ignore)
        .Build()

/// GET a path against a fresh host; returns the status and body.
let private get (registrations: IServiceCollection -> unit) (routes: HttpHandler list) (path: string) = async {
    use host = buildHost registrations routes
    do! host.StartAsync() |> Async.AwaitTask
    use client = host.GetTestClient()
    let! resp = client.GetAsync(path) |> Async.AwaitTask
    let! text = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
    do! host.StopAsync() |> Async.AwaitTask
    return resp.StatusCode, text
}

let private noRegistrations (_: IServiceCollection) = ()

let private companionsOf (doc: JsonDocument) =
    doc.RootElement.GetProperty("Companions").EnumerateArray()
    |> Seq.map (fun e -> e.GetProperty("Name").GetString(), e.GetProperty("Version").GetString())
    |> List.ofSeq

[<Tests>]
let tests =
    testList "Phase 9a follow-up — /dev/version" [

        testAsync "payload shape: Sdk, Companions, Commit, DeployedAt, StartedAt" {
            let! status, body = get noRegistrations (devRoutesFor true) "/dev/version"
            Expect.equal status HttpStatusCode.OK "the gate is on"

            use doc = JsonDocument.Parse body
            let root = doc.RootElement

            for field in [ "Sdk"; "Companions"; "Commit"; "DeployedAt"; "StartedAt" ] do
                let mutable value = Unchecked.defaultof<JsonElement>
                Expect.isTrue (root.TryGetProperty(field, &value)) (sprintf "field %s is present" field)

            let sdk = root.GetProperty("Sdk").GetString()
            Expect.isNotEmpty sdk "Sdk is a version string"
            Expect.isFalse (sdk.Contains '+') "Sdk carries no build metadata — that suffix is Commit"

            // Commit is the `+` suffix or null, never a fabricated value.
            // Both arms are legitimate: a git build carries the sha via
            // Source Link, a build from an exported tree carries none.
            let commit = root.GetProperty("Commit")

            match commit.ValueKind with
            | JsonValueKind.Null -> ()
            | JsonValueKind.String ->
                Expect.isNotEmpty (commit.GetString()) "a present Commit is non-empty"

                Expect.isTrue
                    (commit.GetString() |> Seq.forall Uri.IsHexDigit)
                    "a present Commit is the sha Source Link stamped, hex only"
            | other -> failtestf "Commit is a string or null, not %A" other

            // StartedAt is the process start, UTC ISO-8601, and it is in
            // the past.
            let startedAt = DateTimeOffset.Parse(root.GetProperty("StartedAt").GetString())
            Expect.isLessThanOrEqual startedAt DateTimeOffset.UtcNow "StartedAt is not in the future"

            // DeployedAt is the entry assembly's file time when the host
            // has one (this runner does), UTC ISO-8601.
            let deployedAt = root.GetProperty("DeployedAt")

            match deployedAt.ValueKind with
            | JsonValueKind.Null -> ()
            | JsonValueKind.String ->
                let parsed = DateTimeOffset.Parse(deployedAt.GetString())
                Expect.isLessThanOrEqual parsed DateTimeOffset.UtcNow "DeployedAt is not in the future"
            | other -> failtestf "DeployedAt is a string or null, not %A" other
        }

        testAsync "the platform tiers appear in Companions with zero registration, and Sdk is the Server tier's version" {
            let! status, body = get noRegistrations (devRoutesFor true) "/dev/version"
            Expect.equal status HttpStatusCode.OK "the gate is on"
            use doc = JsonDocument.Parse body
            let companions = companionsOf doc

            let names = companions |> List.map fst
            Expect.contains names "ToolUp.Platform.Core" "Core is loaded, so it is listed"
            Expect.contains names "ToolUp.Platform.Server" "Server is loaded, so it is listed"

            Expect.isTrue
                (names
                 |> List.forall (fun n -> n.StartsWith("ToolUp.", StringComparison.Ordinal)))
                "assembly-derived entries are the ToolUp.* assemblies only"

            Expect.equal names (names |> List.sort) "assembly-derived entries are sorted by name"

            // Sdk is the Server tier's informational version minus its
            // metadata — so the Server entry's version STARTS with Sdk.
            let sdk = doc.RootElement.GetProperty("Sdk").GetString()

            let serverVersion =
                companions |> List.find (fun (n, _) -> n = "ToolUp.Platform.Server") |> snd

            Expect.stringStarts serverVersion sdk "Sdk is the Server assembly's version (metadata stripped)"

            // And it is the same version the assembly attribute carries,
            // read here independently of the handler.
            let expected =
                typeof<DevDiagnosticsHandler.VersionReport>.Assembly
                    .GetCustomAttributes(typeof<System.Reflection.AssemblyInformationalVersionAttribute>, false)
                |> Seq.cast<System.Reflection.AssemblyInformationalVersionAttribute>
                |> Seq.head
                |> _.InformationalVersion

            Expect.equal serverVersion expected "the Server entry is the attribute value verbatim"
        }

        testAsync "a DI-registered ComponentVersion is appended to Companions" {
            let native: ComponentVersion = {
                Name = "libverovio (test)"
                Version = "4.3.1-native"
            }

            let register (services: IServiceCollection) =
                services.AddSingleton<ComponentVersion>(native) |> ignore

            let! status, body = get register (devRoutesFor true) "/dev/version"
            Expect.equal status HttpStatusCode.OK "the gate is on"
            use doc = JsonDocument.Parse body
            let companions = companionsOf doc

            Expect.contains companions (native.Name, native.Version) "the registered entry is listed verbatim"

            // Appended AFTER the assembly-derived entries, so the
            // zero-registration list keeps its sorted shape at the top.
            let lastName = companions |> List.last |> fst
            Expect.equal lastName native.Name "DI entries follow the assembly-derived ones"

            // And the rest is unchanged by the registration.
            let assemblyDerived =
                companions |> List.filter (fun (n, _) -> n <> native.Name) |> List.map fst

            Expect.contains assemblyDerived "ToolUp.Platform.Server" "assembly-derived entries still present"
        }

        testAsync "the route is absent when EnableDevEndpoints is false" {
            // The builder returns NO dev routes at all with the flag off —
            // the gate is one `if`, and this is it.
            Expect.isEmpty (devRoutesFor false) "no dev-diagnostics routes are built with the flag off"

            let! status, _ = get noRegistrations (devRoutesFor false) "/dev/version"
            Expect.equal status HttpStatusCode.NotFound "/dev/version falls through to the terminal 404"

            // Sanity on the positive arm through the same path, so a
            // 404 above cannot be a broken harness.
            let! statusOn, _ = get noRegistrations (devRoutesFor true) "/dev/version"
            Expect.equal statusOn HttpStatusCode.OK "and 200 with the flag on, through the identical host"
        }

        testAsync "the response is JSON and no-store, like /dev/inspect" {
            use host = buildHost noRegistrations (devRoutesFor true)
            do! host.StartAsync() |> Async.AwaitTask
            use client = host.GetTestClient()
            let! resp = client.GetAsync("/dev/version") |> Async.AwaitTask
            do! host.StopAsync() |> Async.AwaitTask

            Expect.equal resp.StatusCode HttpStatusCode.OK "200"

            Expect.equal (string resp.Content.Headers.ContentType) "application/json; charset=utf-8" "JSON content type"

            Expect.isTrue
                resp.Headers.CacheControl.NoStore
                "Cache-Control: no-store — a cached build id is worse than none"
        }
    ]