// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.ClerkAuthUI

open Feliz
open Fable.Core.JsInterop
open Toolup.UIToolkit

// ─── Clerk React bindings ────────────────────────────────────────────
//
// Wrapped components. Each accepts Feliz-style props (IReactProperty
// list) so call sites feel native alongside other Feliz components.
//
// Under Clerk Core 3 (@clerk/react), `SignedIn` / `SignedOut` were
// consolidated into a single `<Show>` component — see Clerk upgrade
// notes for the rationale.

let private clerk = importAll<obj> "@clerk/react"

let UserButton (props: IReactProperty list) : ReactElement =
    ReactLegacy.createElement (unbox<ReactElement> clerk?UserButton, createObj !!props)

let ClerkProvider (props: IReactProperty list) : ReactElement =
    ReactLegacy.createElement (unbox<ReactElement> clerk?ClerkProvider, createObj !!props)

let SignIn (props: IReactProperty list) : ReactElement =
    ReactLegacy.createElement (unbox<ReactElement> clerk?SignIn, createObj !!props)

let Show (props: IReactProperty list) : ReactElement =
    ReactLegacy.createElement (unbox<ReactElement> clerk?Show, createObj !!props)

// ─── Shell-wrapping helper ───────────────────────────────────────────

/// Wrap the app shell with Clerk's ClerkProvider + Show/SignIn gate.
/// The shell is only rendered once Clerk reports the user as signed
/// in; signed-out users see Clerk's themed SignIn screen.
///
/// Appearance overrides mirror the existing ToolUp brand so the
/// sign-in screen matches the rest of the app. Tailwind tokens come
/// from `Toolup.UIToolkit.Tokens`.
let wrapApp (publishableKey: string) (shell: ReactElement) =
    ReactLegacy.createElement (
        unbox<ReactElement> ClerkProvider,
        {| publishableKey = publishableKey |},
        [|
            ReactLegacy.createElement (unbox<ReactElement> Show, null, [| shell |])
            ReactLegacy.createElement (
                unbox<ReactElement> Show,
                null,
                [|
                    ReactLegacy.createElement (
                        unbox<ReactElement> SignIn,
                        {|
                            appearance = {|
                                elements = {|
                                    rootBox =
                                        "w-screen bg-bg-light font-sans min-h-screen flex items-center justify-center"
                                    headerTitle = "text-xl font-semibold mb-2 text-brand font-['Umami'] text-center"
                                    headerSubtitle = $"text-base {Tokens.Text.secondary} font-sans text-center"
                                    formButtonPrimary = $"{Tokens.Button.primary} w-full"
                                    formFieldInput =
                                        $"border border-border rounded-lg px-4 py-3 focus:outline-none focus:border-brand bg-white {Tokens.Text.primary} font-sans"
                                    footerActionLink =
                                        $"{Tokens.Colours.brand} hover:text-brand-dark hover:underline font-medium bg-transparent cursor-pointer"
                                |}
                            |}
                        |},
                        [||]
                    )
                |]
            )
        |]
    )