// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.WhatsAppTemplateRegistry

open System
open System.Text
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.BlobStorage

// ─── SDK-default implementations (Phase 827) ─────────────────────────
//
//   * `NoOpWhatsAppTemplateRegistry` — knows no template, so every
//     template send is refused. The fail-closed answer for a deployment
//     that registered a WhatsApp sink but no registry.
//
//   * `BlobWhatsAppTemplateRegistry` — reads
//     `_platform/whatsapp-templates/{name}.json` per lookup, the
//     `WhatsAppTemplateDescriptor` shape round-tripped through
//     `FableConverters`. An operator mirrors each vendor-approved
//     template into that path; the SDK ships no write surface, because
//     the source of truth is the vendor's approval, not this store.

/// Reserved container for SDK-platform blobs.
[<Literal>]
let private PlatformContainer = "_platform"

let private jsonOptions = FableConverters.create ()

/// `true` for a name that is safe to use as a blob-name segment: ASCII
/// letters, digits, `_` and `-`, at most 512 characters. Both vendors'
/// template names fit; anything else (a path separator, `..`) is refused
/// before it reaches storage.
let isValidTemplateName (name: string) : bool =
    not (String.IsNullOrEmpty name)
    && name.Length <= 512
    && name |> Seq.forall (fun c -> Char.IsAsciiLetterOrDigit c || c = '_' || c = '-')

/// Blob name (inside the `_platform` container) holding `name`'s
/// descriptor.
let blobName (name: string) : string = $"whatsapp-templates/{name}.json"

/// `IWhatsAppTemplateRegistry` that knows no template. Every lookup
/// answers `None`, so every template send is refused, audited.
type NoOpWhatsAppTemplateRegistry() =
    interface IWhatsAppTemplateRegistry with
        member _.GetTemplate _ = async { return None }

/// `IWhatsAppTemplateRegistry` over `IBlobStorage`, reading
/// `_platform/whatsapp-templates/{name}.json` per lookup. A missing or
/// unreadable blob, a record that does not decode, a record whose `Name`
/// disagrees with its path, or a malformed record all answer `None` —
/// the last three logged at `Warn`, because each is a registry defect an
/// operator should see rather than a routine miss.
type BlobWhatsAppTemplateRegistry(storage: IBlobStorage, logger: ILogger option) =

    let warn (message: string) =
        match logger with
        | Some l -> l.Warn message
        | None -> ()

    let decode (name: string) (bytes: byte[]) : WhatsAppTemplateDescriptor option =
        try
            let decoded =
                JsonSerializer.Deserialize<WhatsAppTemplateDescriptor>(Encoding.UTF8.GetString bytes, jsonOptions)

            if isNull (box decoded) then None else Some decoded
        with ex ->
            warn $"[WhatsAppTemplateRegistry] decode failed template=%s{name}: %s{ex.GetType().Name}: %s{ex.Message}"
            None

    let validate (name: string) (descriptor: WhatsAppTemplateDescriptor option) =
        match descriptor with
        | Some d when not (String.Equals(d.Name, name, StringComparison.Ordinal)) ->
            warn $"[WhatsAppTemplateRegistry] template=%s{name} holds a record named %s{d.Name}; refusing"
            None
        | Some d when not (WhatsAppTemplateDescriptor.isWellFormed d) ->
            warn $"[WhatsAppTemplateRegistry] template=%s{name} is malformed; refusing"
            None
        | other -> other

    interface IWhatsAppTemplateRegistry with
        member _.GetTemplate name = async {
            if not (isValidTemplateName name) then
                warn $"[WhatsAppTemplateRegistry] refused an unsafe template name (length %d{String.length name})"
                return None
            else
                try
                    match! storage.Download(PlatformContainer, blobName name) with
                    | Error _ ->
                        // Not found and a failed read are one answer:
                        // the registry cannot vouch for the template.
                        return None
                    | Ok bytes -> return decode name bytes |> validate name
                with ex ->
                    warn
                        $"[WhatsAppTemplateRegistry] storage read failed template=%s{name}: %s{ex.GetType().Name}: %s{ex.Message}"

                    return None
        }

/// The blob-backed default over `storage`.
let blobBacked (storage: IBlobStorage) (logger: ILogger option) : IWhatsAppTemplateRegistry =
    BlobWhatsAppTemplateRegistry(storage, logger) :> IWhatsAppTemplateRegistry