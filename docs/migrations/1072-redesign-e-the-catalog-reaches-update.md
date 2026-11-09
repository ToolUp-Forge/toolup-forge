# Phase 1072 — the catalog reaches `update`

**Class:** breaking (rides the 0.42.0 draft slot). Additive for a consumer that builds `ClientModuleContext`
through `ClientModuleContext.empty` and only renders the SDK's modules; the breaks are named below, each with
its replacement.

## What changes

- **`ClientModuleContext.Messages: MessageCatalog`** is new: the catalog the shell resolved for the session
  (locale + `ClientConfig.MessageCatalogOverride`), the same value `MessageCatalogProvider` publishes to the
  view tree. `ClientModuleContext.empty` carries `MessageCatalog.english`. A locale switch re-initialises every
  module, so each `init` receives the new language's catalog.
- **The SDK's built-in modules author reducer text from it.** The eleven admin modules (`PermissionsAdminUI`,
  `MappingDataManagerUI`, `FileManagerUI`, `TeamManagerUI`, `DataSubjectRequestAdminUI`, `PlatformAdminUI`,
  `ModuleVisibilityAdminUI`, `PlatformUsersUI`, `ServiceStatusBoardUI`, `SessionSecurityUI`,
  `WebhookAdminUI`) and the Knowledge Base client keep their catalog section on `Model.Messages` from `init`, so
  a banner, a notice or a validation sentence written by `update` follows the deployment's `Locale`. Each
  module's `init` takes a `ClientModuleContext` (ten of them took `unit`).
- **`MessageCatalog` moved assembly.** `LocalizationTypes.fs` (the record and its section records) and
  `MessageCatalog.fs` (`MessageCatalog.english`, locale resolution, `LocaleRequest`) are compiled into
  `ToolUp.Platform.Core` instead of `ToolUp.Platform.Client`, namespace unchanged (`ToolUp.Platform`). Source
  compiles as before, because Platform.Client references Core; a binary compiled against Platform.Client for
  these types is rebuilt.
- **The English-only bare arities are removed** from the three auth-provider client UIs and the session
  security module. Each rendered the built-in English catalog whatever the locale, and none had a caller.
- **`OriginalDocumentBridge.OriginalDocumentOpener` takes the caller's catalog** first, so a "view original"
  refusal is in the reader's language. The Knowledge Base registers the only opener, and the AI Sources panel
  is the only caller.

## If you construct `ClientModuleContext` as a full record literal

Add the field (or build from `empty`):

```fsharp skip=fragment
{ ClientModuleContext.empty with UserId = userId }      // unchanged
{ Config = …; …; OnAccessibleModulesChanged = None
  Messages = MessageCatalog.english }                    // a full literal adds Messages
```

## If you call a built-in module's `init` or build its `Model`

`init ()` is `init ctx`; a test passes `ClientModuleContext.empty` (English) or
`{ ClientModuleContext.empty with Messages = catalog }`. A full `Model` literal adds
`Messages = MessageCatalog.english.<Section>` (for example `MessageCatalog.english.PermissionsAdmin`).
`ClientModel.init` (Knowledge Base) is registered through `ClientModule.withContextInit`.

## If you called a removed arity

| Removed | Use |
|---|---|
| `GoogleIdentityAuthUI.LoadingScreen ()`, `OidcAuthUI.LoadingScreen ()`, `PasskeyAuthUI.LoadingScreen ()` | `LoadingScreenWith msgs.Auth` |
| `GoogleIdentityAuthUI.ErrorScreen`, `OidcAuthUI.ErrorScreen`, `PasskeyAuthUI.ErrorScreen` | `ErrorScreenWith msgs.Auth …` |
| `OidcAuthUI.SignInScreen`, `OidcAuthUI.SignInScreenWithSecondary` | `SignInScreenWith msgs.Auth secondary onSignIn onSecondary` |
| `OidcTokenStore.describeError` | `describeErrorWith msgs.Auth.Errors` |
| `SessionSecurityUI.lastSeenLabel` | `lastSeenLabelWith msgs.SessionSecurity` |

where `msgs` is `MessageCatalogProvider.useMessages ()` in a component, or `ctx.Messages` in a module.

## If you register an `OriginalDocumentBridge` opener

```fsharp skip=fragment
OriginalDocumentBridge.register (fun (catalog: MessageCatalog) (originalRef: OriginalDocumentRef) -> async { … })
```

## Your own modules

A consumer module may now read `ctx.Messages` in `init` and keep the section it needs on its model, instead of
pinning `MessageCatalog.english` in `update`. `docs/platform/client-localization.md` ("Reading the catalog from
your own module") shows the shape.

## Verification

`dotnet build ToolUp.Forge.sln`; `LocalizationTests` ("no module reducer pins the English catalog") and
`MessageCatalogContractTests` in the Remoting pack; `ShellUpdateEffectsTests` in the Node client pack (a module
the shell initialises reads the catalog the provider is handed; a module initialised under a French catalog
writes its banner in French).

## Rollback

Revert the phase's commits. The field is the only new member a consumer can have adopted; a consumer that read
`ctx.Messages` returns to `MessageCatalog.english`.
