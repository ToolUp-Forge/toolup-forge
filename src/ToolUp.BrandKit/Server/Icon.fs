module ToolUp.BrandKit.Icon

open Giraffe.ViewEngine

/// Icon primitive — emits the `<svg>` shell every consumer icon set
/// shares. The shell follows the convention every brand-book icon set
/// uses: 24px grid, 1.5px stroke width, round joins, `fill="none"`,
/// `stroke="currentColor"` (so icon colour inherits text colour).
///
/// Consumer apps load their icon set from a JSON manifest or inline F#
/// map (each entry is a list of SVG path strings + optional filled
/// dots), and call `iconSvg` with the matching `IconSpec`. BrandKit
/// ships zero icons — brand-specific iconography stays consumer-side.
/// The icon spec. `Paths` is the list of SVG `d` strings; each
/// rendered as `<path d="..." />`. `Dots` is an optional list of
/// `(cx, cy, r)` tuples for small filled circles (status indicators
/// inside an outline, etc.) — emitted as `<circle fill="currentColor"
/// stroke="none">`.
type IconSpec = {
    Paths: string list
    Dots: (float * float * float) list option
}

/// Default icon size in pixels — 24 matches the convention every
/// brand-book icon grid uses. Override at render time via the `size`
/// parameter on `iconSvgSized`.
[<Literal>]
let DefaultSize = 24

/// Render an icon at a specific pixel size.
let iconSvgSized (size: int) (spec: IconSpec) : XmlNode =
    let dotElements =
        match spec.Dots with
        | None -> []
        | Some dots ->
            dots
            |> List.map (fun (cx, cy, r) ->
                tag "circle" [
                    attr "cx" (sprintf "%g" cx)
                    attr "cy" (sprintf "%g" cy)
                    attr "r" (sprintf "%g" r)
                    attr "fill" "currentColor"
                    attr "stroke" "none"
                ] [])

    let pathElements = spec.Paths |> List.map (fun d -> tag "path" [ attr "d" d ] [])

    tag
        "svg"
        [
            attr "viewBox" "0 0 24 24"
            attr "width" (string size)
            attr "height" (string size)
            attr "fill" "none"
            attr "stroke" "currentColor"
            attr "stroke-width" "1.5"
            attr "stroke-linecap" "round"
            attr "stroke-linejoin" "round"
            attr "aria-hidden" "true"
        ]
        (pathElements @ dotElements)

/// Render an icon at the default 24px size.
let iconSvg (spec: IconSpec) : XmlNode = iconSvgSized DefaultSize spec