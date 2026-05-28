namespace ToolUp.PublicRendering

/// Minimal flat YAML frontmatter parser. Supports `key: value` lines
/// with optional single- or double-quoted string values; comment
/// lines (`#`) and blank lines are skipped. Multi-line values, lists,
/// and nested objects are intentionally unsupported — the frontmatter
/// use case (`title`, `date`, `layout`, `description`, `og:image`, …)
/// doesn't need them, and a hand-rolled parser avoids the
/// `YamlDotNet` transitive-dependency surface for one feature.
module FrontmatterParser =
    open System

    let private stripQuotes (s: string) =
        let trimmed = s.Trim()

        if trimmed.Length >= 2 then
            let first = trimmed[0]
            let last = trimmed[trimmed.Length - 1]

            if (first = '"' && last = '"') || (first = '\'' && last = '\'') then
                trimmed.Substring(1, trimmed.Length - 2)
            else
                trimmed
        else
            trimmed

    /// Parse one frontmatter line into a `(key, value)` pair. Returns
    /// `None` for blank lines, comments, and malformed entries.
    ///
    /// Split heuristic: the canonical YAML separator is `": "`
    /// (colon-space). For keys with embedded colons (e.g.
    /// `og:image: /foo.jpg`) the *first colon-space* is the split, so
    /// the embedded colon stays with the key. A trailing bare `:`
    /// produces an empty value.
    let parseLine (line: string) : (string * string) option =
        let trimmed = line.Trim()

        if String.IsNullOrEmpty trimmed || trimmed.StartsWith "#" then
            None
        else
            let colonSpaceIdx = trimmed.IndexOf ": "

            if colonSpaceIdx > 0 then
                let key = trimmed.Substring(0, colonSpaceIdx).Trim()
                let value = trimmed.Substring(colonSpaceIdx + 2) |> stripQuotes
                Some(key, value)
            elif trimmed.EndsWith ":" && trimmed.Length > 1 then
                let key = trimmed.Substring(0, trimmed.Length - 1).Trim()
                Some(key, "")
            else
                None

    /// Parse a fenced frontmatter block (the text between the two
    /// `---` lines, exclusive). Returns an empty map on no input.
    let parse (yamlText: string) : Map<string, string> =
        if String.IsNullOrWhiteSpace yamlText then
            Map.empty
        else
            yamlText.Split([| '\n' |]) |> Array.choose parseLine |> Map.ofArray