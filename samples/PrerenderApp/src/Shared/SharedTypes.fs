// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module PrerenderApp.SharedTypes

open ToolUp.Platform

// ─── Phase 57 worked example — declared prerender routes ─────────
//
// Single source of truth for the routes the build-time prerender
// pass emits indexable HTML for. Consumed by:
//   - `Client.fs` via `ClientConfig.PrerenderRoutes = routes` (the
//     hydration bootstrap reads the per-route metadata from here
//     when navigating between SPA routes).
//   - `Build.fs` via `Prerender.registerTarget config options routes`
//     (the FAKE target emits one HTML file per route).
//   - `Build.fs` via the `Sitemap` sibling target (transforms the
//     same list into `dist/sitemap.xml`).
//
// Keeping the list in one place is load-bearing — drift between the
// Client-side hydration metadata and the prerender-emitted `<head>`
// would surface as React hydration mismatch warnings + crawlers
// seeing different titles than human visitors.

let private home = {
    Path = "/"
    InitStateKey = None
    Meta = {
        Title = "Acme Calculator — free public tax estimator"
        Description = "Estimate your 2025–26 income tax in 30 seconds. No signup, no email capture, no upsell."
        OpenGraph =
            Map.ofList [
                "title", "Acme Calculator"
                "description", "Free public tax estimator."
                "type", "website"
                "image", "https://acme.example/og/home.png"
                "url", "https://acme.example/"
            ]
        JsonLd = None
    }
}

let private individual = {
    Path = "/individual"
    InitStateKey = Some "individual"
    Meta =
        PrerenderMeta.basic
            "Individual tax calculator — Acme"
            "Tax estimate for individual filers. PAYE, self-employed, dividends."
}

let private family = {
    Path = "/family"
    InitStateKey = Some "family"
    Meta =
        PrerenderMeta.basic
            "Family tax calculator — Acme"
            "Joint and partnered tax estimates. Includes child benefit and marriage allowance."
}

let private company = {
    Path = "/company"
    InitStateKey = Some "company"
    Meta =
        PrerenderMeta.basic
            "Company tax calculator — Acme"
            "Corporation tax + director self-assessment, ranged by company size."
}

// SEO landing pages — high-intent search queries that point at the
// home calculator. Each is a thin route that prerenders its own
// metadata and links into the SPA flow. The body is a snippet of
// guidance + a CTA to the relevant calculator.
let private seoLanding (path: string) (title: string) (description: string) (image: string) = {
    Path = path
    InitStateKey = Some(path.TrimStart '/')
    Meta = {
        Title = title
        Description = description
        OpenGraph =
            Map.ofList [
                "title", title
                "description", description
                "type", "article"
                "image", sprintf "https://acme.example/og/%s.png" image
                "url", sprintf "https://acme.example%s" path
            ]
        JsonLd = None
    }
}

let routes: PrerenderRoute list = [
    home
    individual
    family
    company
    seoLanding
        "/guides/paye-2025"
        "PAYE explained for 2025–26"
        "PAYE tax bands, thresholds, and how to read a payslip in the 2025–26 tax year."
        "paye-2025"
    seoLanding
        "/guides/self-assessment"
        "Self-assessment guide"
        "What to file, when, and how the deadline differs from the tax-year end."
        "self-assessment"
    seoLanding
        "/guides/marriage-allowance"
        "Marriage allowance — claim a tax break"
        "When the marriage allowance applies, how much it saves, and how to claim it back."
        "marriage-allowance"
    seoLanding
        "/guides/child-benefit"
        "Child benefit & the high-income charge"
        "Who pays the High Income Child Benefit Charge and how to register for it."
        "child-benefit"
    seoLanding
        "/guides/corporation-tax"
        "Corporation tax rates 2025–26"
        "Small-profits rate, marginal relief, and how associated companies affect the band."
        "corporation-tax"
]