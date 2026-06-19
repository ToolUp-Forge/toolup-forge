// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Default `INarrativeRenderer` registry. Three built-ins ship out of
/// the box — markdown, plaintext, HTML — each wrapping the matching
/// pure renderer module. Deployments wanting extra formats register
/// additional renderers via the SDK's renderer-registry hook (a future
/// `ServerApp.withNarrativeRenderer`); the lookup helpers below are the
/// shape that hook will key into.
module ToolUp.Platform.Narrative.NarrativeRenderers

let private markdownRenderer =
    { new INarrativeRenderer with
        member _.ContentType = "text/markdown"
        member _.Name = "Markdown"
        member _.Render(doc) = NarrativeMarkdown.render doc
    }

let private plaintextRenderer =
    { new INarrativeRenderer with
        member _.ContentType = "text/plain"
        member _.Name = "Plain text"
        member _.Render(doc) = NarrativePlaintext.render doc
    }

let private htmlRenderer =
    { new INarrativeRenderer with
        member _.ContentType = "text/html"
        member _.Name = "HTML"
        member _.Render(doc) = NarrativeHtml.render doc
    }

let private atomRenderer =
    { new INarrativeRenderer with
        member _.ContentType = "application/atom+xml"
        member _.Name = "Atom"
        member _.Render(doc) = NarrativeAtom.render doc
    }

/// Markdown renderer — wraps `NarrativeMarkdown.render`.
let markdown: INarrativeRenderer = markdownRenderer

/// Plaintext renderer — wraps `NarrativePlaintext.render`.
let plaintext: INarrativeRenderer = plaintextRenderer

/// HTML renderer — wraps `NarrativeHtml.render`. Emits an `<article>`
/// fragment; not wrapped in `<html>` / `<body>`.
let html: INarrativeRenderer = htmlRenderer

/// Atom renderer — wraps `NarrativeAtom.render`. Emits a single
/// `<entry>` Atom 1.0 element. Use `NarrativeAtom.renderFeed` directly
/// to wrap multiple entries in a complete `<feed>` document.
let atom: INarrativeRenderer = atomRenderer

/// The full default set, ordered markdown → plaintext → html → atom.
let defaults: INarrativeRenderer list = [ markdownRenderer; plaintextRenderer; htmlRenderer; atomRenderer ]

/// Look up a renderer by content type within the supplied registry.
/// Match is case-insensitive on the MIME type — `text/HTML` resolves
/// the same renderer as `text/html`.
let tryFindByContentType (contentType: string) (registry: INarrativeRenderer list) : INarrativeRenderer option =
    let target = contentType.ToLowerInvariant()
    registry |> List.tryFind (fun r -> r.ContentType.ToLowerInvariant() = target)

/// Resolve an effective registry from a deployment's additional
/// renderers. Custom renderers come first so a deployment that
/// registers its own `text/markdown` override wins over the default
/// implementation; default renderers fill in the gaps for content
/// types the deployment did not customise.
let resolve (additional: INarrativeRenderer list) : INarrativeRenderer list =
    let additionalContentTypes =
        additional |> List.map _.ContentType.ToLowerInvariant() |> Set.ofList

    let preservedDefaults =
        defaults
        |> List.filter (fun r -> not (additionalContentTypes.Contains(r.ContentType.ToLowerInvariant())))

    additional @ preservedDefaults