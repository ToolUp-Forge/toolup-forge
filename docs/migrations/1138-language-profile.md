# A language profile for the assistant

**Ships in:** ToolUp.AI.Server and ToolUp.Facts.Presentation.Server 0.39.0. Opt-in and additive: a deployment that
registers no profile sends the system prompt it sent before, byte for byte. The AI tier still references no Fuaran-UI
assembly; the Fuaran-UI half lives in the presentation tier beside the render tools.

**Affected:** a deployment whose assistant should compose a small Fuaran-UI layout (a heading, a summary card, a callout
beside a chart) without the whole language in its prompt.

## What changes

1. **`LanguageProfile`.** A record of the kinds the assistant may compose and, per kind, the fields of it the model
   may write, built with `LanguageProfile.named`, `withKind` and `withNodeProperties`. Kinds are wire discriminators
   (`DataGrid`, not `Grid`).
2. **Registration against the published schema.** `LanguageProfile.register schema profile` checks every kind, field
   and node property against a `LanguageSchema`. `RenderTools.languageSchema` (ToolUp.Facts.Presentation.Server) is the
   vocabulary the pinned `Fuaran.UI` package publishes (`Generated.kindFieldNames`, `envelopeFieldNames`,
   `kindCategories`). An unknown kind, field or property, and a field that carries rows inline (`DataGrid.staticRows`),
   refuses the registration by name (`LanguageProfileRefusal`).
3. **One cached fragment.** Registration renders the profile into a prompt fragment once.
   `SystemPromptBuilder.languageProfile logger profile` serves that same string on every turn and logs its size when it
   is constructed. With `None` it contributes nothing.
4. **The grammar.** `RenderTools.validateComposedBlock profile requests blockJson` runs a model-composed block against
   the profile. Every node's kind, field and node property must be admitted (`LanguageProfile.check`, the AI tier's
   half). A `Chart`'s, `DataGrid`'s or `Sparkline`'s `source`, and a `Metric`'s `value`, must be
   `{"$type":"Query","name":"<key>"}` naming a key of `requests`, the presentation requests the caller holds, so rows come
   from a gated request and never inline. The block then decodes under a policy admitting exactly the profile's kinds,
   and the pre-emit validator must find no error. Each refusal (`ComposedBlockRefusal`) names the node, the kind and the
   field.

## What to do

Reference `ToolUp.Facts.Presentation.Server`, register the profile once at boot and compose its fragment into the
system prompt. A pilot deployment's set, a summary
card beside a chart or a table:

```fsharp skip=fragment
let profile =
    LanguageProfile.named "pilot"
    |> LanguageProfile.withKind "Box" [ "children"; "heading"; "layout"; "role" ]
    |> LanguageProfile.withKind "Heading" [ "level"; "text" ]
    |> LanguageProfile.withKind "Markdown" [ "text" ]
    |> LanguageProfile.withKind "Callout" [ "body"; "heading"; "tone" ]
    |> LanguageProfile.withKind "Chart" [ "kind"; "source"; "title"; "xField"; "yFields" ]
    |> LanguageProfile.withKind "DataGrid" [ "columns"; "rowKeyField"; "source" ]
    |> LanguageProfile.register RenderTools.languageSchema
    |> Result.defaultWith (fun refusals ->
        failwithf "language profile: %s" (refusals |> List.map LanguageProfileRefusal.describe |> String.concat "; "))

SystemPrompt = Some(SystemPromptBuilder.compose [
    SystemPromptBuilder.fromStatic "You are the analysts' assistant."
    SystemPromptBuilder.activeModuleContext
    SystemPromptBuilder.languageProfile (Some logger) (Some profile)
])
```

List every field the language requires of a kind you admit (a `Box`'s `layout` and `role`; a `Chart`'s `kind`,
`source`, `xField` and `yFields`). The decoder refuses a node without one, so a profile that leaves it out admits a kind
the model can never compose. Text kinds (`Markdown`, `Fact`, `LabelValueRow`) carry words the model writes; admit them
knowing the grammar checks their shape, not the figures in them.

Validate each block the model composes before it reaches the panel:

```fsharp skip=fragment
match RenderTools.validateComposedBlock profile requests blockJson with
| Ok node -> // draw it
| Error refusals -> // answer the model with refusals |> List.map ComposedBlockRefusal.describe
```

## Verification

The boot log carries `Language profile 'pilot': 6 kind(s), prompt fragment <n> bytes.` In the Platform pack,
`Language profile` checks registration, the fragment, that no profile leaves the prompt unchanged, and the grammar's
refusals.

## Rollback

Drop the `languageProfile` layer and the registration. The prompt returns to what it was.
