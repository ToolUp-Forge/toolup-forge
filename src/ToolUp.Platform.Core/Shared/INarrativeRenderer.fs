// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.Narrative

/// Pluggable narrative-document renderer. Implementations declare a
/// MIME content type (so consumers can negotiate by accept-header or
/// dispatch by file extension) and a pure `NarrativeDocument → string`
/// transform.
///
/// The existing `NarrativeMarkdown.render` / `NarrativePlaintext.render`
/// / `NarrativeHtml.render` functions remain callable directly; the
/// interface exists for deployments that want to register additional
/// renderers (PDF via a companion package, JSON-LD for downstream
/// pipelines, etc.) without forking the SDK.
///
/// Renderers are pure values — no `Async`, no I/O. Companions that
/// require I/O (PDF rendering via a binary, vector emission into a
/// blob store) should expose their own builder and persist the rendered
/// bytes through `IBlobStorage` rather than implementing this
/// interface.
type INarrativeRenderer =
    /// MIME content type the renderer produces. Convention is the
    /// IANA media type (e.g. `text/markdown`, `text/html`, `text/plain`,
    /// `application/json`). Used by registry lookups and the future
    /// `accept`-shaped export endpoint.
    abstract member ContentType: string

    /// Short human-readable name. Surfaced in admin UIs and the
    /// renderer registry's diagnostics output. Defaults to the
    /// content type when not overridden by an implementation.
    abstract member Name: string

    /// Render the document. Pure — same input maps to the same string
    /// every call; renderers must not depend on ambient state.
    abstract member Render: doc: NarrativeDocument -> string