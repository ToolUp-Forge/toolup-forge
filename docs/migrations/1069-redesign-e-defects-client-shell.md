# Phase 1069 — the client shell's measured defects

**Class:** additive for a deployment that composes the shell as documented; one member removal
(`Toolup.UIToolkit.Layout.UserButton`, below). Nothing else changes for a consumer that does not author its own
`MessageCatalog`.

## What changes

- **`update` emits effects; it no longer runs them.** The five sidebar-preference arms persist through
  `SidebarPreferences.persist` (a `Cmd`), not an inline `localStorage.setItem`. A store that refuses the write
  (a full quota, a private window, blocked site data) used to throw out of the dispatch loop; it now shows a
  `"sidebar-prefs"` row in the boot-degradation banner, with no Retry button (there is no load to re-issue — the
  next preference change writes again). The two toasts (`SessionStoreReset`, an action for an inactive module)
  are `Cmd`s, and the module-state observer publish (`ModuleStateObserver.publishState`) is the shell program's
  post-update hook, installed through `Program.withTrace` by `Client.program`.
- **Every boot source has a label and, when retryable, a retry.** A failed `"data-snapshot"` load's Retry button
  now re-issues the load (it did nothing), and `"platform-info"` / `"data-snapshot"` rows read their labels from
  the catalog instead of rendering English under every locale.
- **The permissions banner shows the right colour.** A successful module-exposure change renders the success
  banner, a failed one the error banner (they were swapped).
- **Two TeamManager strings follow the locale** (the revoke-invitation prompt and the transfer modal's
  "the selected member" fallback), and the two TeamManager inputs that dispatched per keystroke (the
  ownership-transfer filter and the invite-by-email address) use `Forms.Input.text`, which commits on Enter or
  blur. The transfer candidate list therefore filters when the filter is committed, not on every keystroke.
- **The toolkit's number formats name their locale.** `OutputFormatting` and `Forms` format through
  `OutputFormatting.fallbackLocale` (`"en-GB"`, as before) until Phase 1075 passes the caller's locale; a
  caller that holds a locale formats with `MessageCatalogProvider.formatNumber locale`.
- **The shell bundle no longer imports `@clerk/react`.** Clerk's `UserButton` in the page header is rendered by
  the ClerkUI companion, which registers it as the header's identity action when its provider wraps the shell.
- **`SyncCoordinator.Stop()` detaches its `online` and `visibilitychange` listeners** (it attached and removed
  two different function objects, so nothing was removed).

## If you author a full `MessageCatalog` for another locale

Five fields are new. A catalog built with `{ MessageCatalog.english with … }` gains the English text and
compiles unchanged; a catalog written out in full adds:

```fsharp
BootDegradation = {
    // …
    Sources = {
        // …
        PlatformInfo = "Platform info"
        DataSnapshot = "Uploaded data"
        SidebarPrefs = "Sidebar preferences"
    }
}
TeamManager = {
    // …
    TransferSelectedMemberFallback = "the selected member"
    RevokeInvitePrompt = fun email teamName -> $"{email} will no longer auto-join {teamName} on sign-in."
}
```

`docs/platform/message-catalog-skeleton.fs` carries all five in the copy-and-update shape.

## If you referenced `Layout.UserButton`

`Toolup.UIToolkit.Layout.UserButton` is removed with the vendor import it needed. The identical wrapper is
`ToolUp.AuthProviders.ClerkAuthUI.UserButton` in the ClerkUI companion:

```diff
- Layout.UserButton [ … ]
+ ToolUp.AuthProviders.ClerkAuthUI.UserButton [ … ]
```

A Clerk deployment that only relied on the header button changes nothing: `ClerkAuthUI.wrapApp` (reached through
`ClerkRegister.handler`) registers it. Another auth companion can fill the same slot with
`Layout.registerHeaderIdentityAction`.

## If you compose the shell's pieces yourself

A composer that calls `Client.update` directly (rather than building on `Client.program`) installs the observer
hook itself, or modules declared with `ClientModule.withInspectState` stop reporting:

```fsharp
prog
|> Client.withModuleStateObserver allModules (function ShellMsg m -> Some m | _ -> None) _.Shell
```

ToolUp.AI's `AIClientConfig` does this.

## Verification

- The Fable pack (`dotnet run --project Build.fsproj -- VerifyFable`) runs `ShellUpdateEffectsTests` and the
  boot-source table cases in `BootDegradationTests`.
- The Platform pack's localisation gate and client-toolkit theming contract pin the composed-string sweep, the
  one fallback locale and the absence of `@clerk/` from `src/ToolUp.Platform.Client`.

## Rollback

Revert the phase's commit. The catalog fields are additive; a consumer catalog that added them keeps compiling
only against a build that has them, so roll the consumer back with it.
