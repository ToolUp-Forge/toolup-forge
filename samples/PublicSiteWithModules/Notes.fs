module PublicSiteWithModules.Notes

open System
open System.Collections.Concurrent
open Giraffe
open Giraffe.ViewEngine
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.Server
open ToolUp.BrandKit

/// Phase 82 — the demo domain module. Picks a thin but realistic
/// shape (notes with a slug, title, summary, body, timestamp) that
/// resists demo-rot the way "items" / "todos" doesn't.
///
/// Demonstrates the hybrid pattern's USP: the SSR detail-page route
/// + the (future) Fable admin API live on the same composition
/// pipeline reading from the same in-memory store. Operator action
/// in the admin surfaces seconds later at the public read URL.

// ─── Domain ────────────────────────────────────────────────────────

type Note = {
    Slug: string
    Title: string
    Summary: string
    Body: string
    PublishedAt: DateTime
}

// ─── In-memory store ──────────────────────────────────────────────

let private notesStore: ConcurrentDictionary<string, Note> = ConcurrentDictionary()

/// Seed two demo notes so the route handlers have content to render
/// on first boot. Real consumers swap this for any IEntityStore-
/// backed implementation; the wire shape (slug → Note) is the same.
let seedNotes () : unit =
    let nowMinus1d = DateTime(2026, 6, 4, 9, 30, 0, DateTimeKind.Utc)
    let nowMinus2d = DateTime(2026, 6, 3, 14, 15, 0, DateTimeKind.Utc)

    let demoOne = {
        Slug = "phase-82-shipped"
        Title = "Phase 82 — the hybrid sample shipped"
        Summary = "The canonical reference for composing PublicRendering (SSR) + a domain module on one pipeline."
        Body =
            "Wave 13's worked example demonstrates the additive composition pattern: \
             `withPublicRendering` over a base ServerApp carrying a Notes module. The \
             two surfaces share the same in-memory store; operator writes in the admin \
             surface seconds later at the public read URL."
        PublishedAt = nowMinus1d
    }

    let demoTwo = {
        Slug = "brandkit-primitives-explained"
        Title = "What ToolUp.BrandKit gives you"
        Summary = "Eight semantic primitives. Zero opinionated styling. CSS variables for everything."
        Body =
            "BrandKit ships the structural markup every brand needs — wordmark, eyebrow, \
             card, pill, persona avatar, icon set, page chrome. It ships ZERO styling: \
             every brand-shape decision (colour, font, spacing, radius, shadow) lives in \
             the consumer's CSS-variable definitions. Same primitive, different brand, no \
             primitive change."
        PublishedAt = nowMinus2d
    }

    notesStore[demoOne.Slug] <- demoOne
    notesStore[demoTwo.Slug] <- demoTwo

let allNotes () : Note list =
    notesStore.Values |> Seq.sortByDescending _.PublishedAt |> List.ofSeq

let tryGet (slug: string) : Note option =
    match notesStore.TryGetValue slug with
    | true, note -> Some note
    | false, _ -> None

// ─── SSR detail-page handler ──────────────────────────────────────
//
// The "live-store-backed SSR" half of the hybrid demonstration. The
// Giraffe routef captures the slug, the handler reads from the in-
// memory store, and the page renders via a BrandKit-styled layout.
// PublicRendering's markdown-driven landing page sits alongside —
// both surfaces share the composition pipeline.

let private renderNoteDetail (note: Note) : XmlNode =
    let timestamp = note.PublishedAt.ToString("u")

    html [ _lang "en" ] [
        head [] [
            meta [ _charset "utf-8" ]
            meta [ _name "viewport"; _content "width=device-width, initial-scale=1" ]
            tag "title" [] [ str (sprintf "%s — PublicSiteWithModules" note.Title) ]
            link [ _rel "stylesheet"; _href "/css/brand-tokens.css" ]
        ]
        body [
            attr
                "style"
                "background:var(--bk-paper);color:var(--bk-ink);font-family:var(--bk-font-ui);margin:0;min-height:100vh;"
        ] [
            PageChrome.pageHeader {
                Brand =
                    PageChrome.BrandLockup.Wordmark {
                        Stem = "Public"
                        Emphasis = "Site"
                        EmphasisColour = "#2563EB"
                        Tail = Some "WithModules"
                    }
                Nav = [ { Label = "Home"; Href = "/" }; { Label = "All notes"; Href = "/" } ]
                Right = []
            }
            main [ attr "style" "max-width:720px;margin:48px auto;padding:0 24px;" ] [
                Text.eyebrow (sprintf "NOTE · %s" timestamp)
                Text.displayLarge note.Title
                Card.card [
                    p [
                        attr "style" "color:var(--bk-ink-mute);font-size:18px;line-height:1.6;margin:0 0 16px 0;"
                    ] [ str note.Summary ]
                    Text.hRule
                    p [ attr "style" "margin:16px 0 0 0;line-height:1.6;" ] [ str note.Body ]
                ]
            ]
            PageChrome.pageFooter {
                Copyright = "Sample app — ToolUp.Forge Phase 82"
                Links = [ { Label = "← Home"; Href = "/" } ]
            }
        ]
    ]

let detailHandler: HttpHandler =
    routef "/n/%s" (fun slug ->
        fun next ctx -> task {
            match tryGet slug with
            | Some note ->
                let html =
                    Giraffe.ViewEngine.RenderView.AsString.htmlDocument (renderNoteDetail note)

                return! htmlString html next ctx
            | None -> return! (setStatusCode 404 >=> text (sprintf "Note '%s' not found" slug)) next ctx
        })

/// Build the Notes module's server-side surface. v1 ships the SSR
/// detail route only; the Fable admin API (CRUD over `INotesApi`)
/// lands when Phase 82 grows its client tier.
let serverModule: ServerModule =
    ServerModule.create "Notes" |> ServerModule.withHandlers [ detailHandler ]