# Phase 1240 — browser history and a URL per view

**Status:** additive, opt-in. An app that does not set the new field is unchanged: the shell neither reads nor writes
the URL, and no `popstate` listener is attached. The one source-level effect is that `ClientConfig` gains a field, so a
full record literal must name it. `ClientConfig.defaults with …` is unaffected.

## What changes

Without this, the active view lives only in the shell's model. Back leaves the application instead of returning to the
previous view, and a reload lands on the default surface.

With `ClientConfig.BrowserHistory = HashHistory`:

- each view has the URL `#/{moduleId}{pageRoute}`, the same sidebar id a click sends;
- selecting a view adds one history entry, and Back and Forward move between views without adding any;
- a reload or a pasted URL restores the view, including a page of a multi-page module;
- a redirect the shell makes for itself, such as the no-active-team gate sending a caller to the landing module, replaces
  the current entry, so Back does not land on a view that redirects again;
- a signed-out reader who opens a view's URL, signs in and returns in the same tab lands on the view they were on when
  they left (Phase 1241). The view waits in `sessionStorage`, is used once, and is removed if the URL names a view.

A URL or a stored view is reader input, so restoring one goes through the route guard. A module the reader may not see renders the typed
"not authorised" view with its `Init` not run. An unknown id is ignored, and an undeclared page falls back to the
module's first page.

The URL is a fragment because a path cannot work: SDK module ids contain a dot, and the SPA fallback reads a path with an
extension as an asset, so a reload would 404. A fragment never reaches the server, so auth callbacks, server-rendered
routes, prerendered routes and any other path your server answers still resolve first. The pathname and query string are
left alone, and a fragment not starting `#/` stays an ordinary in-page anchor.

## Diff per consumer

**Opt in** with one field on your `ClientConfig`:

```diff
 let config = {
     ClientConfig.defaults with
+        BrowserHistory = HashHistory
 }
```

Nothing else is needed. `AIClientConfig.withSidePanel` and any other composer over `Client.update` and
`Client.withShellLifetimeEffects` take it through those two functions.

## Limits to check before you opt in

- **A prerendered page.** If a prerendered route hydrates with a fragment naming a different view from the one rendered,
  React reports a hydration mismatch and re-renders on the client. The view is still correct.
- **Bookmarks name module ids.** A view's URL is built from its module id and page route, so renaming either breaks URLs
  saved under the old name. They fall back as above rather than failing.

## Verification

1. Select two views, then press Back twice and Forward twice. The view follows each press and the URL matches it.
2. Reload on a page of a multi-page module. The same page returns.
3. Paste the URL of an admin module as a non-admin. The "not authorised" view renders and its API calls are not made.
4. Open a path your server answers, such as an auth callback or a server-rendered page. It resolves as before.
5. Signed out, open a view's URL and sign in. The same view returns. Open `/` again in that tab and the default surface
   does.

## Rollback

Remove the field. Saved `#/…` URLs then open the default surface, since the fragment is no longer read.
