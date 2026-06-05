module ToolUp.BrandKit.Persona

open Giraffe.ViewEngine

/// Persona primitives — the brand's illustrated portrait + the
/// "signed-by" attribution shape every editorial / summary surface
/// uses. The persona itself is consumer-supplied (URL); BrandKit only
/// ships the structural markup.
/// Avatar treatment — controls the `object-fit` crop + border-radius.
///   `Circle`  — radius 50% (the default); face & shoulders crop
///   `Rounded` — small border-radius for a softer rectangle
///   `Square`  — minimal radius for editorial / collage shapes
type AvatarTreatment =
    | Circle
    | Rounded
    | Square

/// The persona spec.
///   `ImageUrl`   — URL of the persona portrait
///   `AltText`    — accessibility text describing the persona
///   `Size`       — rendered width + height in pixels
///   `Treatment`  — Circle / Rounded / Square
///   `RingColour` — optional outer ring colour (e.g. "#D9C4A4" for a
///                  warm tan ring); None for no ring
type PersonaSpec = {
    ImageUrl: string
    AltText: string
    Size: int
    Treatment: AvatarTreatment
    RingColour: string option
}

/// Render a persona avatar. Markup shape:
///
///     <img class="bk-persona bk-persona-circle" src="..." alt="..."
///          width="56" height="56"
///          style="object-fit:cover;object-position:center 12%;
///                 border:2px solid #...;">
///
/// `object-position: center 12%` crops to face & shoulders at small
/// sizes — the small-avatar convention every persona-signed-summary
/// design hand-off names. Larger sizes (≥104px) the crop is essentially
/// a no-op.
let personaAvatar (spec: PersonaSpec) : XmlNode =
    let treatmentClass =
        match spec.Treatment with
        | Circle -> "bk-persona-circle"
        | Rounded -> "bk-persona-rounded"
        | Square -> "bk-persona-square"

    let ringStyle =
        match spec.RingColour with
        | Some colour -> sprintf "border:2px solid %s;" colour
        | None -> ""

    let style = sprintf "object-fit:cover;object-position:center 12%%;%s" ringStyle

    img [
        _class (sprintf "bk-persona %s" treatmentClass)
        _src spec.ImageUrl
        _alt spec.AltText
        attr "width" (string spec.Size)
        attr "height" (string spec.Size)
        attr "style" style
    ]

/// "Briefed by <Name>" footer — composes a small persona avatar with
/// a name label. The canonical editorial-summary attribution shape.
/// Consumer styles `.bk-persona-signature` for the surrounding flex
/// + spacing; `.bk-persona-signature-name` for the name weight.
let personaSignedSummary (persona: PersonaSpec) (preamble: string) (name: string) : XmlNode =
    div [ _class "bk-persona-signature" ] [
        personaAvatar persona
        span [] [ str preamble; span [ _class "bk-persona-signature-name" ] [ str name ] ]
    ]