// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module PrerenderApp.Build

open System.IO
open System.Text
open Fake.Core
open ToolUp.Platform
open ToolUp.Platform.Build

// ─── Phase 57 worked example — FAKE Build driver ──────────────────
//
// Composes the standard SDK build targets with the Phase 57
// `Prerender` target factory plus a sibling `Sitemap` target.
// Three commands matter for this sample:
//
//   dotnet run -- Build       — restore + compile the .NET side
//   dotnet run -- Prerender   — Fable + emit dist/{slug}.html
//   dotnet run -- Sitemap     — emit dist/sitemap.xml from routes

let buildConfig = {
    BuildConfig.defaults with
        ServerProject = "src/Server/Server.fsproj"
        ClientProject = "src/Client/Client.fsproj"
        Port = 13930
}

let prerenderOptions = {
    Prerender.PrerenderTargetOptions.defaults with
        // Defaults are already what this sample uses, but
        // spelling them out makes the seam visible:
        BundleDirectory = "src/Client/output"
        OutputDirectory = "src/Client/dist"
        BundleScriptUrl = "/output/Client.js"
}

// ─── Sitemap target ───────────────────────────────────────────────
//
// Demonstrates how a consumer composes a sibling FAKE target that
// shares the same `PrerenderRoute list` source of truth. Emits
// `dist/sitemap.xml` in the standard <urlset> shape crawlers
// consume. A real deployment then references the sitemap from
// `/robots.txt` (`Sitemap: https://your-deploy/sitemap.xml`).

let private hostBaseUrl = "https://acme.example"

let private renderSitemap (host: string) (routes: PrerenderRoute list) : string =
    let sb = StringBuilder()
    sb.AppendLine "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" |> ignore

    sb.AppendLine "<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">"
    |> ignore

    // Sort by Path so the emitted sitemap is byte-stable across
    // runs (same input ⇒ same output). The prerender pass's
    // determinism property propagates here.
    for route in routes |> List.sortBy (fun r -> r.Path) do
        let url = if route.Path = "/" then host else host + route.Path

        sb.AppendLine "  <url>" |> ignore
        sb.AppendLine(sprintf "    <loc>%s</loc>" url) |> ignore
        sb.AppendLine "  </url>" |> ignore

    sb.AppendLine "</urlset>" |> ignore
    sb.ToString()

[<EntryPoint>]
let main args =
    init args
    registerTargets buildConfig

    // Register the Phase 57 Prerender target. Wires
    // `Build ==> Prerender` automatically; the consumer just
    // needs to call this once.
    Prerender.registerTarget buildConfig prerenderOptions SharedTypes.routes

    Target.create "Sitemap" (fun _ ->
        let outDir = Path.GetFullPath prerenderOptions.OutputDirectory
        Directory.CreateDirectory outDir |> ignore
        let target = Path.Combine(outDir, "sitemap.xml")
        File.WriteAllText(target, renderSitemap hostBaseUrl SharedTypes.routes)
        Trace.tracefn "[sitemap] wrote %s (%d urls)" target (List.length SharedTypes.routes))

    // `Prerender ==> Sitemap` so a full prerender pass produces
    // both the HTML files AND the sitemap pointing at them in
    // one command (`dotnet run -- Sitemap`).
    let (==>) a b = Fake.Core.TargetOperators.(==>) a b
    "Prerender" ==> "Sitemap" |> ignore

    execute args