# 0.42.0 — one view contract, a toolkit that takes the locale, and one `AppShell`

A module's view is `'Model -> RenderContext<'Msg> -> PageContent`; the SDK's built-ins are `BuiltIn` values; the
toolkit's number formatters take the caller's locale; `Layout.AppShell` is the one shell entry. Breaking, in one
0.x minor, with a codemod.

**Run the codemod first:** `dotnet run --project <forge>/Build.fsproj -- Codemod <your-src> --phase 1075`. It
rewrites the mechanical sites and lists the rest for review.

## The view contract

`RenderContext<'Msg> = { Dispatch: 'Msg -> unit; Messages: MessageCatalog; Locale: string }` is built by the shell
once per render from the session's resolved catalog. `ClientModule.withView`, `withPages` and the `View` /
`PageViews` fields take it, and every view returns `PageContent`.

```fsharp skip=fragment
// before
let view (model: Model) (dispatch: Msg -> unit) : ReactElement * ReactElement = left, right
ClientModule.create spec |> ClientModule.withView view

// after
let view (model: Model) (ctx: RenderContext<Msg>) : PageContent =
    let dispatch = ctx.Dispatch
    PageContent.SplitPanel(left, right)

ClientModule.create spec |> ClientModule.withView view
```

- **`withFullWidthView view` is gone** (and its `Route = ""` page): `withView` returns `PageContent.FullWidth body`.
  The codemod writes `withView (fun model ctx -> PageContent.FullWidth(view model ctx.Dispatch))`.
- **A tuple view** is wrapped by the codemod as `withView (fun model ctx -> PageContent.SplitPanel(view model
  ctx.Dispatch))`; rewrite the view itself when convenient.
- **A `withPages` page** takes `model ctx` the same way (reviewed, not rewritten).
- **Strings:** a view reads `ctx.Messages.<Section>`; a `[<ReactComponent>]` that existed only to call
  `MessageCatalogProvider.useMessages ()` can be a plain function over the section (reviewed). A component that
  holds state of its own stays a component.
- The hosted-tree builders (`ClientHostView.*`, `ClientBoundHostView.*`) keep their signatures.

## Built-ins are `BuiltIn` values

Each SDK module exports `builtIn: BuiltIn<Model, Msg>` (`{ Id; Defaults; Group; NavRole; Area; Visibility; Init;
Update; View; Extend }`) in place of `create`; `ShellSlots.builtIn b label` builds the `ErasedModule`. A deployment
that built one by hand writes the second form:

```fsharp skip=fragment
// before
SlotFill.External(DataIngestionUI.create None |> ErasedModule.withGroup "Operations")
// after
SlotFill.External(ShellSlots.builtIn DataIngestionUI.builtIn None |> ErasedModule.withGroup "Operations")
```

Parameterised built-ins take their parameter: `FileManagerUI.builtIn displays config`,
`PlatformAdminUI.builtIn publicUtility`, `Home.builtIn recents`, `DatadogReadbackUI.builtIn config`,
`AIAssistantUI.builtIn branding`. `PlatformKnowledgeAdminUI.register ()` is
`ShellSlots.builtIn PlatformKnowledgeAdminUI.builtIn None`; `NarrativePromptAdminUI.create ()` is
`ShellSlots.builtIn NarrativePromptAdminUI.builtIn None`. `NoActiveTeamLandingUI.create` is gone: set
`Chrome.NoActiveTeamLanding = ChromeFill.On(NoActiveTeamLandingFill.BuiltIn cfg)`. `SlotExtension.relabel` is gone
(`ShellSlots.builtIn` takes the label).

## The toolkit takes the locale

`OutputFormatting.formatDecimal locale value`, `formatInt locale value`, `formatNumber locale minDigits maxDigits
value`, `formatCurrency locale currencyCode value` (the currency is no longer fixed to GBP),
`Forms.Input.currency locale …` and `Forms.Input.integerInput locale …`; `OutputFormatting.fallbackLocale` is gone.
A view passes `ctx.Locale`. The two inputs read a typed number back in the locale's form
(`OutputFormatting.parseDecimal`). Reviewed by the codemod.

## One shell entry, and the English twins go

- `Layout.AppShell` takes one `AppShellProps` record (`Sidebar`, `Content`, `SidePanel`, `HeaderAction`,
  `IdentityAction`, `InputsWidth`); the fourteen-parameter form and `AppShellWith` are gone. `Layout.userButton`
  is gone: the identity button an auth companion registers renders through `IdentityAction`
  (`Layout.headerIdentityAction ()`).
- `Sidebar.buildSections modules prefs` (English) is gone; `buildSectionsWith messages` is now `buildSections
  messages`. `BootDegradation.banner` (English) is gone; `bannerWith` is now `banner`. The codemod renames the
  twins. The public-utility widgets' English entries go the same way (`trafficDashboardWith` → `trafficDashboard`,
  …), and `PublicUtilityWidgets.render` takes the catalog section.

## Verify

`dotnet build` your solution; a remaining tuple view or `withFullWidthView` is a compile error naming the site.
Rollback: pin the previous SDK version.
