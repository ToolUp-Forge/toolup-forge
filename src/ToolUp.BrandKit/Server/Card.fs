module ToolUp.BrandKit.Card

open Giraffe.ViewEngine

/// Card primitives — paper-shaped surfaces grouping related content.
/// All variants share the `.bk-card` base class for the common border +
/// radius + padding shape; specialised variants add modifier classes.
/// Consumer styles `.bk-card`, `.bk-card-tight`, `.bk-card-deep`,
/// `.bk-card-outlined` per their brand tokens.
/// Standard card — primary surface for content groupings.
/// Default padding + radius come from `--bk-radius-lg` + consumer CSS.
let card (children: XmlNode list) : XmlNode = section [ _class "bk-card" ] children

/// Tight card — smaller padding for compact rows (transport bars,
/// horizontally-arranged stats, etc.). Same border + radius shape as
/// `card`.
let cardTight (children: XmlNode list) : XmlNode =
    section [ _class "bk-card bk-card-tight" ] children

/// Deep-tinted card — same shape as `card` but with the
/// `--bk-panel` background colour for visual contrast against a base
/// `--bk-paper` page. Use sparingly to flag a particular section as
/// "elevated" without breaking out of the surface system.
let cardDeep (children: XmlNode list) : XmlNode =
    section [ _class "bk-card bk-card-deep" ] children

/// Outlined card — 1px `--bk-rule` border, no shadow, no fill. For
/// listing items where the card needs to read as a tile rather than a
/// raised surface.
let cardOutlined (children: XmlNode list) : XmlNode =
    section [ _class "bk-card bk-card-outlined" ] children