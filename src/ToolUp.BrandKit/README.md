# ToolUp.BrandKit

Server-side branding primitives for `ToolUp.Platform` — SSR-friendly Giraffe ViewEngine helpers for emitting brand-consistent SVG icons, wordmarks, persona avatars, pills, cards, page chrome, and text styles. Consumers wire their brand tokens (palette + type scale) once and the primitives render every surface against them.

BrandKit ships zero brand-specific iconography or wordmarks; consumers supply their own icon manifests (`IconSpec`) and wordmark glyphs. The primitives carry the layout + sizing conventions every brand book shares (24px icon grid, 1.5px stroke width, `currentColor` fills, etc.).

Part of the ToolUp Platform SDK — see [github.com/ToolUp-Forge/toolup-forge](https://github.com/ToolUp-Forge/toolup-forge) for full documentation.

Licensed under Apache-2.0.
