module PublicSiteWithModules.Layouts

open Giraffe.ViewEngine
open ToolUp.Platform
open ToolUp.PublicRendering
open ToolUp.BrandKit

/// Phase 82 — landing-page layout for the PublicRendering side of the
/// hybrid sample. PublicRendering loads `content/pages/index.md`
/// (frontmatter + body) and hands the parsed `PublicPage` to this
/// layout function; the layout composes the page chrome + summary
/// cards + a "What's here" list of Notes-detail links using BrandKit
/// primitives.
///
/// The layout reads from `PublicSiteWithModules.Notes.allNotes ()`
/// directly — same in-memory store the SSR detail-page handler reads
/// from — which proves the hybrid pattern's USP: the SSR-public
/// surface and the domain-module surface share a single engine, with
/// PublicRendering layouts able to compose live module state into
/// markdown-driven pages.

let private noteCard (note: Notes.Note) : XmlNode =
    let timestamp = note.PublishedAt.ToString "yyyy-MM-dd"
    let href = sprintf "/notes/%s" note.Slug

    Card.cardOutlined [
        Text.eyebrow timestamp
        h3 [
            attr "style" "font-family:var(--bk-font-display);font-size:24px;margin:8px 0 8px 0;line-height:1.2;"
        ] [
            a [ _href href; attr "style" "color:var(--bk-ink);text-decoration:none;" ] [ str note.Title ]
        ]
        p [
            attr "style" "color:var(--bk-ink-mute);font-size:14px;line-height:1.6;margin:0;"
        ] [ str note.Summary ]
    ]

let landingPage (page: PublicPage) : XmlNode =
    let notes = Notes.allNotes ()
    let noteCards = notes |> List.map noteCard

    html [ _lang "en" ] [
        head [] [
            meta [ _charset "utf-8" ]
            meta [ _name "viewport"; _content "width=device-width, initial-scale=1" ]
            tag "title" [] [ str page.Title ]
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
                Nav = [ { Label = "Home"; Href = "/" }; { Label = "Notes"; Href = "/" } ]
                Right = []
            }

            main [ attr "style" "max-width:840px;margin:48px auto;padding:0 24px;" ] [
                Text.eyebrow "WAVE 13 · WORKED EXAMPLE"

                Text.displayLarge page.Title

                div [ attr "style" "max-width:640px;margin-top:16px;" ] [
                    p [
                        attr "style" "color:var(--bk-ink-mute);font-size:18px;line-height:1.6;margin:0;"
                    ] [ str page.Description ]
                ]

                div [ attr "style" "margin-top:48px;" ] [
                    Text.eyebrow "RECENT NOTES (live from in-memory store)"
                    div
                        [
                            attr "style" "display:grid;grid-template-columns:1fr;gap:16px;margin-top:16px;"
                        ]
                        noteCards
                ]

                div [ attr "style" "margin-top:48px;" ] [ Text.hRule ]

                div [ attr "style" "margin-top:24px;" ] [
                    Text.eyebrowMute "ARCHITECTURE"

                    p [ attr "style" "line-height:1.6;margin:12px 0 0 0;" ] [
                        str "This page is rendered by "
                        Text.monoBody "ToolUp.PublicRendering"
                        str " from "
                        Text.monoBody "content/pages/index.md"
                        str ". The note cards above are composed from "
                        Text.monoBody "PublicSiteWithModules.Notes.allNotes ()"
                        str
                            " — the same in-memory store the SSR detail-page handler reads from. \
                                   The composition root layers both via "
                        Text.monoBody "PublicRenderingCompose.withPublicRendering"
                        str " (Phase 80c) over a base "
                        Text.monoBody "ServerApp"
                        str " carrying the Notes module."
                    ]
                ]
            ]

            PageChrome.pageFooter {
                Copyright = "Sample app — ToolUp.Forge Phase 82"
                Links = [
                    {
                        Label = "Migration doc"
                        Href = "/notes/phase-82-shipped"
                    }
                ]
            }
        ]
    ]