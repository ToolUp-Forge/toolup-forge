// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.I18nDefaults

// ─── SDK seed translations ────────────────────────────────────────────
//
// Phase 12a acceptance criterion: "SDK built-in strings translate
// correctly". A minimal English + French seed covers the SDK shell
// surfaces a non-English consumer would notice first — sign-in,
// sign-out, generic loading / error / save / cancel buttons, and
// the localised `ApiError` envelope's stock messages.
//
// French was chosen as the validation locale because:
//   - it exercises non-ASCII glyphs without UTF-8 round-trip risk
//     (`é`, `à`, `ç`),
//   - it has the same word ordering as English (verb-object) so
//     placeholder substitution doesn't need re-ordering logic in
//     v1, and
//   - a Quebec ("fr-CA") consumer can fall back to the language-
//     only "fr" entry via the `Translations.tryLookup` language
//     fallback path.
//
// Apps that need additional locales register their own translations
// on top of these by merging into `sdkTranslations` (server) or by
// merging into the client `LocaleProvider` (client).

let private withLocales (pairs: (LocaleCode * string) list) = pairs |> Map.ofList

/// SDK seed translations. Keys are SDK-namespaced (`sdk.*`) so
/// module-defined keys can't accidentally shadow them.
let sdkTranslations: Translations =
    Map.ofList [
        // ─── Shell surfaces ───
        "sdk.shell.signIn", withLocales [ LocaleCode.en, "Sign in"; LocaleCode.fr, "Connexion" ]
        "sdk.shell.signOut", withLocales [ LocaleCode.en, "Sign out"; LocaleCode.fr, "Déconnexion" ]
        "sdk.shell.loading", withLocales [ LocaleCode.en, "Loading…"; LocaleCode.fr, "Chargement…" ]
        "sdk.shell.save", withLocales [ LocaleCode.en, "Save"; LocaleCode.fr, "Enregistrer" ]
        "sdk.shell.cancel", withLocales [ LocaleCode.en, "Cancel"; LocaleCode.fr, "Annuler" ]
        "sdk.shell.delete", withLocales [ LocaleCode.en, "Delete"; LocaleCode.fr, "Supprimer" ]
        "sdk.shell.confirm", withLocales [ LocaleCode.en, "Confirm"; LocaleCode.fr, "Confirmer" ]
        "sdk.shell.error.unexpected",
        withLocales [
            LocaleCode.en, "Something went wrong. Please try again."
            LocaleCode.fr, "Une erreur est survenue. Veuillez réessayer."
        ]

        // ─── Localised ApiError messages ───
        "sdk.error.notAuthenticated",
        withLocales [
            LocaleCode.en, "You need to sign in to perform this action."
            LocaleCode.fr, "Vous devez vous connecter pour effectuer cette action."
        ]
        "sdk.error.notAuthorized",
        withLocales [
            LocaleCode.en, "You do not have permission to perform this action."
            LocaleCode.fr, "Vous n'avez pas la permission d'effectuer cette action."
        ]
        "sdk.error.notFound",
        withLocales [
            LocaleCode.en, "The requested item was not found."
            LocaleCode.fr, "L'élément demandé est introuvable."
        ]
        "sdk.error.conflict",
        withLocales [
            LocaleCode.en, "This action conflicts with the current state."
            LocaleCode.fr, "Cette action est en conflit avec l'état actuel."
        ]
        "sdk.error.validationFailed",
        withLocales [
            LocaleCode.en, "The submitted data did not pass validation: {reason}"
            LocaleCode.fr, "Les données soumises n'ont pas passé la validation : {reason}"
        ]
        "sdk.error.internal",
        withLocales [
            LocaleCode.en, "An internal error occurred. The operations team has been notified."
            LocaleCode.fr, "Une erreur interne est survenue. L'équipe d'exploitation a été informée."
        ]
        "sdk.error.rateLimited",
        withLocales [
            LocaleCode.en, "Too many requests. Try again shortly."
            LocaleCode.fr, "Trop de requêtes. Veuillez réessayer plus tard."
        ]

        // ─── File manager surfaces ───
        "sdk.fileManager.uploadButton",
        withLocales [ LocaleCode.en, "Upload file"; LocaleCode.fr, "Téléverser un fichier" ]
        "sdk.fileManager.empty",
        withLocales [
            LocaleCode.en, "No files uploaded yet."
            LocaleCode.fr, "Aucun fichier n'a encore été téléversé."
        ]
    ]

/// Resolve a stock `ErrorCode` to its SDK `TranslationKey`. Used by
/// the server-side `RemotingErrorMapper` when building an `ApiError`
/// from a thrown SDK exception, and by the client when surfacing a
/// raw `ErrorCode` without an explicit `MessageKey`.
let messageKeyFor (code: ErrorCode) : TranslationKey =
    match code with
    | NotAuthenticated -> "sdk.error.notAuthenticated"
    | NotAuthorized -> "sdk.error.notAuthorized"
    | NotFound -> "sdk.error.notFound"
    | Conflict -> "sdk.error.conflict"
    | ValidationFailed -> "sdk.error.validationFailed"
    | Internal -> "sdk.error.internal"
    | RateLimited _ ->
        // Phase 56 — the `RateLimitedBanner` Feliz component renders
        // its own copy from the typed `RateLimitedError` payload
        // (countdown, "try again in N seconds"); this fallback is
        // only used when a non-banner consumer surfaces the error
        // through generic toast UI.
        "sdk.error.rateLimited"
    | Module(_, _) ->
        // Module-defined codes carry their own MessageKey on the
        // ApiError envelope; this fallback is only used for the rare
        // case where a caller constructs an ApiError from just a
        // ModuleCode without a key. Renders as the literal label.
        "sdk.error.internal"

/// The SDK's default fallback locale. Used by `DefaultLocaleResolver`
/// when no team / user / `Accept-Language` selection resolves.
let defaultFallback: LocaleCode = LocaleCode.en