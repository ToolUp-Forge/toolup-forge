// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── IWhatsAppTemplateRegistry (Phase 827) ───────────────────────────
//
// WhatsApp only lets a business START a conversation with a template the
// vendor has approved in advance, and each approved template has a fixed
// number of parameters per component. The registry is the deployment's
// record of which templates it holds and what shape each one takes, so a
// malformed send is refused on the server — typed, audited, before any
// sink runs — rather than bouncing off the vendor as a billed
// `PermanentFailure`.
//
// Vendor-neutral on purpose: both WhatsApp companions resolve a template
// by the same NAME, and each maps that name onto its own vendor handle.
//
// Six-rule portable: identity by value (a template is its name), async at
// the boundary, stateless (an implementation reads per call; a cache is
// its own business and must stay correct cold).

/// The approved shape of one WhatsApp template: the language codes it is
/// approved in and its parameter arity per component. `WhatsAppEnvelope`
/// carries its parameters flat — header, then body, then buttons — so the
/// three counts say where each component's run ends.
type WhatsAppTemplateDescriptor = {
    /// The template name, as both vendors address it.
    Name: string
    /// Language codes the template is approved in (e.g. `"en_GB"`).
    /// Compared case-insensitively.
    Languages: string list
    /// Parameters the header component takes (0 when it has none).
    HeaderParameterCount: int
    /// Parameters the body component takes.
    BodyParameterCount: int
    /// Parameters the button components take, summed across buttons.
    ButtonParameterCount: int
}

/// Helpers over `WhatsAppTemplateDescriptor`.
module WhatsAppTemplateDescriptor =
    /// The number of `TemplateParameters` a send of this template must carry.
    let totalParameterCount (descriptor: WhatsAppTemplateDescriptor) : int =
        descriptor.HeaderParameterCount
        + descriptor.BodyParameterCount
        + descriptor.ButtonParameterCount

    /// `true` when the template is approved in `language`.
    let supportsLanguage (language: string) (descriptor: WhatsAppTemplateDescriptor) : bool =
        not (isNull (box descriptor.Languages))
        && descriptor.Languages
           |> List.exists (fun l -> String.Equals(l, language, StringComparison.OrdinalIgnoreCase))

    /// `true` when the descriptor is internally coherent: a non-blank
    /// name, at least one language, and no negative arity. A descriptor
    /// that fails this is a registry defect and reads as "not approved".
    let isWellFormed (descriptor: WhatsAppTemplateDescriptor) : bool =
        not (String.IsNullOrWhiteSpace descriptor.Name)
        && not (isNull (box descriptor.Languages))
        && not (List.isEmpty descriptor.Languages)
        && descriptor.HeaderParameterCount >= 0
        && descriptor.BodyParameterCount >= 0
        && descriptor.ButtonParameterCount >= 0

/// Lookup surface for the deployment's approved WhatsApp templates.
type IWhatsAppTemplateRegistry =
    /// The approved template called `name`, or `None` when the deployment
    /// holds no such template. An implementation that cannot read its
    /// backing store answers `None` too: an unreadable registry cannot
    /// vouch for a template, so the send is refused rather than risked.
    abstract GetTemplate: name: string -> Async<WhatsAppTemplateDescriptor option>