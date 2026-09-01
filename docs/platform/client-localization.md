# Client-shell localization

The SDK's client shell — its chrome and its built-in modules — renders every
string through a **typed message catalog**. A deployment ships a second
language by supplying one value: a function from the built-in catalog to the
catalog it wants rendered.

This is deliberately not a string-key table. The SDK also carries one of those
(`Translations` / the `tr` hook), and it is the right shape for strings a
*module* contributes: an open set, resolved at runtime, a missing entry
degrading to the key text. The shell's own strings are the opposite — a closed
set, known at compile time, where a missing translation is a defect rather
than a degradation. So the shell catalog is a plain F# record, and **the
record is the schema**: a string your translation forgot is a field the
compiler names.

## The two fields

```fsharp skip=fragment
{ ClientConfig.create handlers with
    Locale = TeamDefault "en"
    MessageCatalogOverride = Some catalog }
```

`Locale` decides **which** language is asked for. `MessageCatalogOverride`
supplies it. Neither does anything on its own: the built-in catalog is English
at every locale, so setting `Locale` alone changes nothing visible, and an
override with no `Locale` is only ever asked for `"en"`.

Both default to off — `FixedLocale "en"` and `None` — so a deployment that
touches neither renders exactly as it did before this substrate existed: the
resolution collapses to a constant, no browser preference or team config is
read, and the built-in catalog is returned by reference.

## Choosing the locale

| `Locale` | Resolution order |
|---|---|
| `FixedLocale "de"` | `"de"`. Nothing else is consulted. |
| `BrowserLocale "en"` | `navigator.language` → `"en"` → `"en"` |
| `TeamDefault "en"` | the team's `_platform.locale` → `navigator.language` → `"en"` |

`TeamDefault` reads the same `_platform` config key the server-side
`LocaleResolver` reads, so **one team setting drives both tiers** — the SSR
pages and the client shell agree without the operator configuring each.

The browser link sits inside `TeamDefault` on purpose. A team that has set no
default is not asking for English; it is asking for nothing, and the visitor's
own preference is the better answer. A team that *has* set one wins over the
browser, because that setting was an explicit act by someone who administers
the team.

Resolution is total: every arm ends at `"en"`, so no configuration produces a
blank tag. (A blank tag matters — `new Intl.NumberFormat("")` throws.)

## Writing a translation

The override is handed the built-in catalog **stamped with the resolved
locale**, and returns the catalog to render. That stamp is what lets one
function serve several languages:

```fsharp skip=fragment
let private french (c: MessageCatalog) = {
    c with
        Shell = {
            c.Shell with
                SignOut = "Se déconnecter"
                SelectTeam = "Choisir une équipe"
                NoTeamHeading = "Vous n'êtes encore dans aucune équipe"
                // A message with a parameter is a FUNCTION field, so the
                // substitution point is part of the type rather than a
                // `{0}` a translation can quietly drop.
                ResultsAvailableIn = fun moduleName -> $"Résultats disponibles dans {moduleName}"
        }
        Toast = {
            Info = "Info"
            Warning = "Avertissement"
            Error = "Erreur"
        }
}

let private german (c: MessageCatalog) = { c with Shell = { c.Shell with SignOut = "Abmelden" } }

/// One override, several languages — match on the locale it was asked for.
let catalog (c: MessageCatalog) =
    if c.Locale.StartsWith "fr" then french c
    elif c.Locale.StartsWith "de" then german c
    else c
```

Three properties are worth naming, because they are the whole design:

- **A partial translation is ordinary record-update syntax.** Fields you do
  not set keep the built-in English string. There is no per-field lookup, no
  missing-key state, and nothing to register.
- **Returning the argument unchanged IS the fallback to English.** The
  fallback chain is the identity function, not a resolution algorithm — which
  is what keeps the surface total.
- **A field you never mention still exists.** Add a string to the shell and
  every translation keeps compiling with English in that slot; *remove or
  rename* one and every translation that set it fails to compile, naming the
  field. That asymmetry is intentional: the compiler is loud where silence
  would be wrong, and quiet where the built-in string is a reasonable answer.

An override that raises is swallowed back to the built-in catalog. A
translation bug degrades the shell's language, never its availability.

## Switching language in-session

A settings page, or a language picker in your own chrome, asks the shell to
switch:

```fsharp skip=fragment
LocaleRequest.request "fr"
```

The shell runs its full reset — the same one an active-team switch runs. That
is heavier than a re-render, and it is the point: a module that formatted a
string during `Init` holds it in its own state, where a re-render cannot reach
it. Passing a blank tag clears the in-session choice and falls back to
whatever `Locale` resolves to.

An in-session choice deliberately **survives a team switch**. A language is a
property of the person reading, not of the team they are looking at.

## Dates, numbers and currency

The SDK bundles no CLDR data. Locale-aware formatting delegates to the
browser's own `Intl`:

```fsharp skip=fragment
[<ReactComponent>]
let Total (amount: float) =
    let locale = MessageCatalogProvider.useLocale ()
    Html.span [ prop.text (MessageCatalogProvider.formatCurrency locale "EUR" amount) ]
```

`formatNumber`, `formatCurrency` and `formatDate` take the locale as a
parameter rather than reading the context themselves, so they are callable
from an ordinary helper as well as from a component body — pair them with
`useLocale ()` at the component boundary.

## Reading the catalog from your own module

Module views are invoked inline by the shell's render, so they are not hook
sites of their own. Read the catalog from a component:

```fsharp skip=fragment
[<ReactComponent>]
let private Body (model: Model) (dispatch: Msg -> unit) =
    let msgs = MessageCatalogProvider.useMessages ()
    Html.div [ prop.text msgs.Shell.SignOut ]

let private view (model: Model) (dispatch: Msg -> unit) = Body model dispatch
```

Outside a provider the hook returns the built-in English catalog, so an
isolated component render in a test harness neither crashes nor renders
blanks.

Note that the catalog covers the **SDK's** strings. Your own module's strings
are yours to localise — through this same mechanism if you thread your own
record, or through the `Translations` key table if an open, runtime-registered
set suits your module better.

## What is not in the catalog

Some strings the SDK renders are deliberately outside it:

- **Built-in modules' default display names** ("Teams", "Health Monitor", …)
  and their administration-landing tile blurbs. These are authored at compose
  time, outside any React tree, so no resolved catalog exists at that point.
  Every one of them is already overridable through the module's own config
  record (`TeamManagerConfig.Name` and friends), which is where a deployment
  should set them.
- **Values echoed from the server** — health-probe status strings, error
  messages a handler returned, a team's own name. These are data, not chrome.
  The server-side `ApiError` / `Translations` substrate is where a localised
  server message comes from.
- **Wire representations that happen to be human-readable** — `TeamRole`
  values, boot-degradation source keys, module ids. Localising these would
  break the round-trip they exist for. Where such a key is *displayed*, the
  display projection is localised and the key is not.
- **Date and number formats.** A format specifier baked into a translated
  template would make the format a property of the *language*. Formatting
  happens at the call site (see "Dates, numbers and currency" above) and the
  catalog function receives a plain string.

## Surfaces that render outside the provider (Phase 751)

Two SDK surfaces render *around* or *before* the shell's own view, so the
provider `Client.view` mounts does not reach them. Both are handled, and both
are worth knowing if you write a surface of the same shape:

- **The sign-in screens** (`OidcClient`, `PasskeyClient`).
  `AuthUIProvider.gate` WRAPS the shell — a
  signed-out visitor sees the companion's screen and none of `view`. So
  `Client.viewWithSignIn` mounts the catalog provider *outside* the gate as
  well. Without that, a deployment's override would have reached every page
  except the one a signed-out visitor actually sees. The screens themselves
  are localised through additive `…With` entry points
  (`OidcAuthUI.SignInScreenWith`, `PasskeyAuthUI.ErrorScreenWith`,
  `OidcTokenStore.describeErrorWith`), never through a widened arity — that
  would read as a removal in the public-API approval baseline. `ClerkUI`
  contributes nothing: Clerk renders its own themed screens.
- **The invitation-accept page.** `InviteAccept.render ()` mounts its own
  React root from a `PublicEntryDispatchers` short-circuit, before
  `Client.program` exists at all, so neither provider mount reaches it. Use
  **`InviteAccept.renderWith config`** from your dispatcher to get the
  resolved catalog; `render ()` keeps its arity and its English-only
  behaviour for consumers already calling it. The team default locale is not
  consulted there — there is no active team on an invite link — so
  `TeamDefault` falls through to the visitor's browser preference.

If you build a surface that mounts its own root, mount
`MessageCatalogProvider.provider` with it; a `useMessages ()` outside every
provider silently returns English rather than failing, which is the right
behaviour for a test harness and the wrong one to ship unnoticed.
