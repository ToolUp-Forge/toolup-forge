// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.Config

open ToolUp.Platform
open ToolUp.Platform.Infra

open System
open ToolUp.Platform.ConfigKeys
open ToolUp.Platform.Narrative

// ─── Env-var-driven config construction ───────────────────────
//
// The server half of the `ServerConfig` companion module. The `defaults`
// half stays in Core (it is Fable-visible); F# resolves a name under
// `ServerConfig` across both modules, so `ServerConfig.fromEnv` and
// `ServerConfig.defaults` read exactly as before at every call site.
//
// **Every key is read THROUGH its registry entry** (`ConfigKeys.keys`). A
// binding below names a key by its `ConfigKeys.Names` constant, and the
// helpers take the parser, the default and the bounds from the key's
// `ConfigKeyKind`: a boolean's direction decides whether a malformed value
// refuses the boot or reads as `false`, a union's direction whether an
// unrecognised token refuses or warns, an integer's bounds what it accepts,
// and every default is the key's typed default. Log messages take the key's
// name and default from the same entry. The few parsers that survive below
// (the surface list, the bind address, the public base URL, the module
// binding anchors, the eviction minutes, the server port) each take their
// key from the registry and read its name, default and bounds from it.

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ServerConfig =
    /// The Fable-visible half's value, for the fields `fromEnv` does not
    /// bind from a key.
    let private defaults = ServerConfig.defaults

    /// Every key resolves through the `ConfigResolution` seam, so a
    /// deployment configuration manifest can supply the declared base and
    /// the environment stays the per-instance override lane. With no
    /// manifest installed this is the plain environment read.
    let private rawValue (key: ConfigKey) : string option = ConfigResolution.tryValue key.EnvVar

    /// The raw text of a key, surrounding whitespace trimmed. A value that is
    /// only whitespace reads as unset, like the empty string the seam already
    /// folds to `None`.
    let private rawText (key: ConfigKey) : string option =
        rawValue key
        |> Option.map _.Trim()
        |> Option.filter (not << String.IsNullOrEmpty)

    let private kindMismatch (key: ConfigKey) (expected: string) : 'T =
        failwithf "%s is bound as %s, but the registry declares another kind for it." key.EnvVar expected

    let private requireDefault (key: ConfigKey) (value: 'T option) : 'T =
        match value with
        | Some v -> v
        | None -> failwithf "%s is bound with its default, but the registry declares none for it." key.EnvVar

    // ── Booleans ────────────────────────────────────────────────────

    let private booleanOf (key: ConfigKey) : Direction * bool option =
        match key.Kind with
        | ConfigKeyKind.BoolKey(direction, defaultValue) -> direction, defaultValue
        | _ -> kindMismatch key "a boolean"

    /// A boolean read by the key's direction. `Protective`: an accepted
    /// spelling binds, unset binds `configured`, anything else refuses the
    /// boot naming the key, the value as written and the accepted sets.
    /// `Permissive`: an on spelling binds `true`, unset binds `configured`,
    /// anything else reads as `false`, which leaves the check it would have
    /// switched off running.
    let private readBoolean (key: ConfigKey) (configured: bool option) (unsetMeans: string) : bool option =
        let direction, _ = booleanOf key

        match rawText key with
        | None -> configured
        | Some raw ->
            let token = raw.ToLowerInvariant()

            if trueTokens.Contains token then
                Some true
            else
                match direction with
                | Direction.Permissive -> Some false
                | Direction.Protective when falseTokens.Contains token -> Some false
                | Direction.Protective ->
                    failwithf
                        "%s=%s is not a recognised boolean value. Expected one of: 1, true, yes, on (case-insensitive) → on; 0, false, no, off → off. Unset the variable to %s."
                        key.EnvVar
                        raw
                        unsetMeans

    /// A boolean key, unset binding its typed default.
    let private flag (name: string) : bool =
        let key = ConfigKeys.key name
        let fallback = booleanOf key |> snd |> requireDefault key

        readBoolean key (Some fallback) (sprintf "use the default (%b)" fallback)
        |> Option.defaultValue fallback

    /// A boolean key with an override-record middle tier: the key, else the
    /// override, else the typed default.
    let private flagWith (name: string) (overrideValue: bool option) : bool =
        let key = ConfigKeys.key name

        let configured =
            overrideValue
            |> Option.defaultWith (fun () -> booleanOf key |> snd |> requireDefault key)

        readBoolean key (Some configured) (sprintf "use the configured value (%b)" configured)
        |> Option.defaultValue configured

    /// A boolean key with no typed default: `Some` when set, `None` when
    /// unset.
    let private flagOption (name: string) : bool option =
        let key = ConfigKeys.key name
        readBoolean key (booleanOf key |> snd) "leave it unset"

    // ── Unions ──────────────────────────────────────────────────────

    let private choicesOf (key: ConfigKey) : string list * Direction * string option =
        match key.Kind with
        | ConfigKeyKind.EnumKey(choices, direction, defaultValue) -> choices, direction, defaultValue
        | _ -> kindMismatch key "a union"

    /// A union key. `cases` maps each token to `Ok` the case it selects, or
    /// to `Error` why the case cannot be selected from a single variable (it
    /// carries a payload a token cannot express); such a token refuses the
    /// boot saying how to supply it. The `Ok` tokens ARE the key's declared
    /// choices, and a table that drifts from them raises before any value is
    /// read. Unset binds the override when there is one, else the case the
    /// typed default token selects. An unrecognised token follows the key's
    /// direction: `Protective` refuses the boot naming the key, the value as
    /// written and the tokens; `Permissive` warns through `warn` and keeps
    /// the configured value.
    let private unionWith
        (warn: string -> unit)
        (name: string)
        (cases: (string * Result<'T, string>) list)
        (overrideValue: 'T option)
        : 'T =
        let key = ConfigKeys.key name
        let choices, direction, defaultToken = choicesOf key

        let selectable =
            cases
            |> List.choose (fun (token, case) ->
                match case with
                | Ok _ -> Some token
                | Error _ -> None)

        if Set.ofList selectable <> Set.ofList choices then
            failwithf
                "%s: the binder maps the tokens [%s] but the registry declares [%s]. The two must name the same set."
                key.EnvVar
                (String.concat ", " selectable)
                (String.concat ", " choices)

        let selected (token: string) =
            cases |> List.tryFind (fun (t, _) -> t = token) |> Option.map snd

        let configured () =
            match overrideValue with
            | Some v -> v
            | None ->
                match defaultToken |> requireDefault key |> selected with
                | Some(Ok v) -> v
                | _ -> failwithf "%s: the registry's default token selects no case." key.EnvVar

        match rawText key with
        | None -> configured ()
        | Some raw ->
            match selected (raw.ToLowerInvariant()) with
            | Some(Ok v) -> v
            | Some(Error why) -> failwithf "%s=%s cannot be selected via env var: %s" key.EnvVar raw why
            | None ->
                let valid = String.concat ", " selectable

                match direction with
                | Direction.Protective ->
                    failwithf
                        "%s=%s is not a recognised value. Expected one of: %s (case-insensitive). Unset the variable to use the configured value."
                        key.EnvVar
                        raw
                        valid
                | Direction.Permissive ->
                    let using =
                        match overrideValue, defaultToken with
                        | None, Some token -> sprintf "the default (%s)" token
                        | _ -> "the configured value"

                    warn $"{key.EnvVar}={raw} not recognised. Valid: {valid}. Using {using}."
                    configured ()

    /// `unionWith` over tokens that each select a case.
    let private union (logger: ILogger) (name: string) (cases: (string * 'T) list) (overrideValue: 'T option) : 'T =
        unionWith logger.Warn name (cases |> List.map (fun (token, case) -> token, Ok case)) overrideValue

    /// The common `No* | Enabled*` binary shape, over the six spellings
    /// `ConfigKeys.enabledDisabledChoices` declares.
    let private enabledDisabled
        (logger: ILogger)
        (name: string)
        (disabledCase: 'T)
        (enabledCase: 'T)
        (overrideValue: 'T option)
        : 'T =
        let cases =
            enabledDisabledChoices
            |> List.map (fun token ->
                token,
                (if token = "enabled" || token = "on" || token = "yes" then
                     enabledCase
                 else
                     disabledCase))

        union logger name cases overrideValue

    // ── Integers ────────────────────────────────────────────────────

    let private boundsOf (key: ConfigKey) : IntBounds * int64 option =
        match key.Kind with
        | ConfigKeyKind.IntKey(bounds, defaultValue) -> bounds, defaultValue
        | _ -> kindMismatch key "an integer"

    /// What an integer key holds: unset, one of its accepted tokens, a
    /// number inside its bounds, or a value it does not accept.
    type private IntegerRead =
        | IntegerUnset
        | IntegerToken
        | IntegerValue of int64
        | IntegerRefused of raw: string

    let private readInteger (key: ConfigKey) : IntegerRead =
        let bounds, _ = boundsOf key

        match rawValue key with
        | None -> IntegerUnset
        | Some raw when
            bounds.AcceptedTokens
            |> List.exists (fun t -> String.Equals(t, raw.Trim(), StringComparison.OrdinalIgnoreCase))
            ->
            IntegerToken
        | Some raw ->
            match Int64.TryParse raw with
            | true, n when
                (bounds.Min |> Option.forall (fun lo -> n >= lo))
                && (bounds.Max |> Option.forall (fun hi -> n <= hi))
                ->
                IntegerValue n
            | _ -> IntegerRefused raw

    /// The phrase a warning quotes for what `key` accepts.
    let private accepted (key: ConfigKey) : string =
        match ConfigKeys.intKeyRuleFor key.EnvVar |> IntKeyRule.describe with
        | Some contract -> "an integer, " + contract
        | None -> "an integer"

    /// An integer key, as the key's default when it is unset, holds one of
    /// its tokens, or is refused (with a warning naming the key, the value,
    /// what the key accepts and the default used). A token reads as unset.
    let private integerOption (logger: ILogger) (name: string) : int64 option =
        let key = ConfigKeys.key name
        let _, defaultValue = boundsOf key

        match readInteger key with
        | IntegerValue n -> Some n
        | IntegerUnset -> defaultValue
        | IntegerToken -> None
        | IntegerRefused raw ->
            let using =
                match defaultValue with
                | Some d -> sprintf "Using the default (%d)." d
                | None -> "Leaving it unset."

            logger.Warn $"{key.EnvVar}={raw} is not {accepted key}. {using}"
            defaultValue

    /// An integer key whose typed default always applies: a refused value or
    /// a token binds the default.
    let private integer (logger: ILogger) (name: string) : int64 =
        let key = ConfigKeys.key name
        let fallback = boundsOf key |> snd |> requireDefault key
        integerOption logger name |> Option.defaultValue fallback

    /// An integer key bound as an `int`: a value outside `Int32` is refused
    /// like any other out-of-range value.
    let private int32Key (logger: ILogger) (name: string) : int =
        let key = ConfigKeys.key name
        let n = integer logger name

        if n < int64 Int32.MinValue || n > int64 Int32.MaxValue then
            let fallback = boundsOf key |> snd |> requireDefault key
            logger.Warn $"{key.EnvVar}={n} is outside the range of an int. Using the default ({fallback})."
            int fallback
        else
            int n

    let private int32Option (logger: ILogger) (name: string) : int option =
        integerOption logger name
        |> Option.bind (fun n ->
            if n > int64 Int32.MaxValue then
                logger.Warn $"{ConfigKeys.key name |> _.EnvVar}={n} is outside the range of an int. Leaving it unset."
                None
            else
                Some(int n))

    /// A millisecond integer key as a `TimeSpan`.
    let private milliseconds (logger: ILogger) (name: string) : TimeSpan =
        TimeSpan.FromMilliseconds(float (integer logger name))

    /// The server port. Fails loud rather than warning: a deployment that
    /// asked for a port and silently listens on another is unreachable where
    /// it was told to be.
    let private serverPort () : int =
        let key = ConfigKeys.key Names.serverPort
        let fallback = boundsOf key |> snd |> requireDefault key

        match readInteger key with
        | IntegerValue n -> int n
        | IntegerUnset
        | IntegerToken -> int fallback
        | IntegerRefused raw ->
            failwithf
                "%s=%s is not a valid TCP port. Expected %s (unset %s to use the default %d)."
                key.EnvVar
                raw
                (accepted key)
                key.EnvVar
                fallback

    /// The ephemeral-store eviction window, in minutes. The key's form is
    /// `IntegerOrDecimal` (a fractional value is honoured), and a positive
    /// decimal has no inclusive integer bound, so the `> 0` test is stated
    /// here.
    let private evictionMinutes (logger: ILogger) : float =
        let key = ConfigKeys.key Names.storeEvictionMinutes
        let fallback = boundsOf key |> snd |> requireDefault key |> float

        match rawValue key with
        | None -> fallback
        | Some raw ->
            match Double.TryParse raw with
            | true, n when n > 0.0 -> n
            | _ ->
                logger.Warn $"{key.EnvVar}={raw} not a positive number. Using the default ({fallback})."
                fallback

    // ── Text ────────────────────────────────────────────────────────

    let private textDefaultOf (key: ConfigKey) : string option =
        match key.Kind with
        | ConfigKeyKind.StringKey defaultValue -> defaultValue
        | _ -> kindMismatch key "text"

    /// A text key, as resolved.
    let private text (name: string) : string option =
        let key = ConfigKeys.key name
        rawValue key |> Option.orElse (textDefaultOf key)

    /// A text key with an override-record middle tier.
    let private textWith (name: string) (overrideValue: string option) : string =
        let key = ConfigKeys.key name

        rawValue key
        |> Option.orElse overrideValue
        |> Option.orElse (textDefaultOf key)
        |> requireDefault key

    /// Splits on comma, semicolon and space, dropping empty entries.
    let private tokens (raw: string) : string list =
        raw.Split([| ','; ';'; ' ' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.map _.Trim()
        |> Array.filter (fun s -> s <> "")
        |> Array.toList

    /// A list-valued text key. Unset or empty → `[]`.
    let private textList (name: string) : string list =
        let key = ConfigKeys.key name

        match key.Kind with
        | ConfigKeyKind.StringKey _ -> rawValue key |> Option.map tokens |> Option.defaultValue []
        | _ -> kindMismatch key "a list of text"

    // ── The parsers that survive, each reached through its key ──────

    /// One token of the surface list, separator-tolerant aliases included.
    let private parseSurfaceProfile (raw: string) : Result<SurfaceProfile, string> =
        match raw with
        | "anonymous" -> Ok SurfaceProfile.anonymous
        | "anonymous_persistent"
        | "anonymous-persistent"
        | "anonymouspersistent" -> Ok SurfaceProfile.anonymousPersistent
        | "trial"
        | "authephemeral"
        | "auth-ephemeral"
        | "auth_ephemeral" -> Ok SurfaceProfile.trial
        | "individual" -> Ok SurfaceProfile.individual
        | "team" -> Ok SurfaceProfile.team
        | "multiteam"
        | "multi-team"
        | "multi_team" -> Ok SurfaceProfile.multiTeam
        | "claimbearer"
        | "claim-bearer"
        | "claim_bearer" -> Ok SurfaceProfile.claimBearer
        | other -> Error other

    /// The surface list: the key when at least one token parses and none is
    /// unrecognised, else the override record's non-empty list, else the
    /// defaults. Consumer-authored literals never traverse this, so they
    /// still win at the highest altitude.
    let private surfaces (logger: ILogger) (overrides: ServerConfigOverrides) : SurfaceProfile list =
        let key = ConfigKeys.key Names.platformSurfaces

        let fallback =
            match overrides.Surfaces with
            | Some s when not (List.isEmpty s) -> s
            | _ -> defaults.Surfaces

        let validTokens =
            "anonymous, anonymous_persistent, trial, individual, team, multi_team, claim_bearer"

        match rawValue key with
        | None -> fallback
        | Some raw ->
            let parsed =
                tokens raw |> List.map (fun t -> parseSurfaceProfile (t.ToLowerInvariant()))

            let errors =
                parsed
                |> List.choose (function
                    | Error e -> Some e
                    | _ -> None)

            match errors with
            | [] ->
                let resolved =
                    parsed
                    |> List.choose (function
                        | Ok s -> Some s
                        | _ -> None)

                if List.isEmpty resolved then
                    logger.Warn
                        $"{key.EnvVar}={raw} resolved to an empty surface list. Valid tokens: {validTokens}. Falling back to the library-default override value (or defaults)."

                    fallback
                else
                    resolved
            | bad ->
                let badList = String.concat ", " bad

                logger.Warn
                    $"{key.EnvVar}={raw} contains unrecognised token(s): {badList}. Valid tokens: {validTokens}. Falling back to the library-default override value (or defaults)."

                fallback

    /// The log level and the trace categories. The level warns on stderr
    /// rather than through the logger: the logger is built from the same key,
    /// and an operator misreading it once should not see the warning twice.
    let private logging () : LogLevel * Set<string> =
        let level =
            let cases = [
                "trace", Ok LogLevel.Trace
                "debug", Ok LogLevel.Debug
                "info", Ok LogLevel.Info
                "warn", Ok LogLevel.Warn
                "warning", Ok LogLevel.Warn
                "error", Ok LogLevel.Error
            ]

            unionWith (fun message -> eprintfn $"[WRN] {message}") Names.logLevel cases None

        level, textList Names.traceCategories |> Set.ofList

    /// The module-binding trust anchors: a `;`-separated list of
    /// `mac:<keyId>:<scope>:<key>` (symmetric; the key resolved via
    /// `ISecretStore` at compose time) or `asym:<keyId>:<alg>:<base64pubkey>`.
    /// A malformed entry is warned about and skipped; an unresolvable
    /// symmetric secret is the compose-time validator's fail-closed concern.
    let private moduleBindingTrust (logger: ILogger) : ModuleBindingTrustConfig =
        let key = ConfigKeys.key Names.moduleBindingAnchors

        let anchors =
            match rawValue key with
            | None -> []
            | Some raw ->
                raw.Split([| ';' |], StringSplitOptions.RemoveEmptyEntries)
                |> Array.choose (fun entry ->
                    match entry.Trim().Split(':') with
                    | [| "mac"; keyId; scope; key |] -> Some(SymmetricAnchorRef(keyId, scope, key))
                    | [| "asym"; keyId; alg; pub |] -> Some(AsymmetricAnchorRef(keyId, alg, pub))
                    | _ ->
                        logger.Warn
                            $"{key.EnvVar} entry '{entry}' is malformed (expected 'mac:<keyId>:<scope>:<key>' or 'asym:<keyId>:<alg>:<base64pubkey>'); skipped."

                        None)
                |> Array.toList

        {
            Anchors = anchors
            AllowUnbound = flag Names.moduleBindingAllowUnbound
        }

    /// The listener's bind address. An address `ServerBinding.parseBindAddress`
    /// refuses fails loud, naming it. Unset → loopback (`None`).
    let private bindAddress () : string option =
        match text Names.serverBindAddress with
        | None -> None
        | Some raw ->
            match ServerBinding.parseBindAddress raw with
            | Ok address -> Some address
            | Error reason -> failwith reason

    /// The public base URL. Empty / whitespace is ambiguous → warn and leave
    /// it unset. A trailing slash is stripped (token issuers append their own
    /// `/r/{token}`-style segment). A value that is not an absolute http(s)
    /// URL is warned about and ignored, because it would otherwise produce
    /// broken share-token / public / OAuth-redirect links that only fail at
    /// link-follow time.
    let private publicBaseUrl (logger: ILogger) : string option =
        let key = ConfigKeys.key Names.publicBaseUrl

        match rawValue key with
        | None -> textDefaultOf key
        | Some raw ->
            let trimmed = raw.Trim()

            if trimmed = "" then
                logger.Warn
                    $"{key.EnvVar} is set but empty/whitespace; the ambiguous empty value is ignored. Unset the variable or give it a value. Falling back to no public base URL."

                textDefaultOf key
            else
                let noTrailing = trimmed.TrimEnd('/')

                if noTrailing <> trimmed then
                    logger.Warn
                        $"{key.EnvVar}={raw} had a trailing slash; stripped to {noTrailing} (token issuers append their own path segment)."

                match Uri.TryCreate(noTrailing, UriKind.Absolute) with
                | true, uri when uri.Scheme = Uri.UriSchemeHttp || uri.Scheme = Uri.UriSchemeHttps -> Some noTrailing
                | _ ->
                    logger.Warn
                        $"{key.EnvVar}={raw} is not a valid absolute http(s) URL; ignoring it (public links fall back to relative). Set a value like https://app.example.com."

                    textDefaultOf key

    /// The three rate-limit keys configure one uniform policy, or none: all
    /// three valid → the policy; none set → no limit; anything between →
    /// a warning and no limit. Per-shape overrides are a code-level concern
    /// (`RateLimitConfig.perShape` / `.withOverrides`).
    let private rateLimit (logger: ILogger) : RateLimitConfig =
        let keys =
            [ Names.rateLimitPermits; Names.rateLimitWindowSeconds; Names.rateLimitQueue ]
            |> List.map ConfigKeys.key

        match
            int32Option logger Names.rateLimitPermits,
            int32Option logger Names.rateLimitWindowSeconds,
            int32Option logger Names.rateLimitQueue
        with
        | Some permits, Some windowSeconds, Some queue ->
            RateLimitConfig.uniform {
                PermitLimit = permits
                WindowSeconds = windowSeconds
                QueueLimit = queue
            }
        | None, None, None -> RateLimitConfig.none
        | _ ->
            let names = keys |> List.map _.EnvVar |> String.concat " / "

            logger.Warn
                $"Rate limit requires all three of {names}. Partial configuration ignored — rate limit disabled."

            RateLimitConfig.none

    /// Build a `ServerConfig` from `TOOLUP_*` keys + a curated overrides
    /// record. A key the deployment set beats the override record, which
    /// beats the key's typed default; a field no key binds keeps
    /// `ServerConfig.defaults` (or the override record's value, where it
    /// carries one).
    let fromEnv (logger: ILogger) (overrides: ServerConfigOverrides) : ServerConfig =
        let logLevel, traceCategories = logging ()

        {
            defaults with
                PublicPath = textWith Names.publicPath overrides.PublicPath
                Surfaces = surfaces logger overrides
                ModuleFilter = text Names.moduleFilter
                ModuleBindingTrust = moduleBindingTrust logger
                SecurityPosture = {
                    defaults.SecurityPosture with
                        RequireHttps = flag Names.requireHttps
                        TrustForwardedHeaders = flag Names.trustForwardedHeaders
                        // The trusted-proxy CIDR allowlist is validated (fail-loud on
                        // a malformed CIDR) by the preflight validator and the
                        // pipeline's options builder, not here, so the error surfaces
                        // with one message whichever path built the config.
                        TrustedProxyCidrs = textList Names.trustedProxyCidrs
                        SecurityHardening =
                            union
                                logger
                                Names.securityHardening
                                [
                                    "no", NoSecurityHardening
                                    "off", NoSecurityHardening
                                    "disabled", NoSecurityHardening
                                    "default", DefaultSecurityHardening
                                    "on", DefaultSecurityHardening
                                    "strict", StrictSecurityHardening
                                ]
                                overrides.SecurityHardening
                        // `cookies` and `cookieonly` select the cookie mode here as
                        // at `AuthProviderFromEnv.tokenLocationFromEnv`: two readers
                        // of one key must agree on what it says.
                        SseAuthMode =
                            union
                                logger
                                Names.sseAuth
                                [
                                    "cookie", CookieRequired
                                    "cookies", CookieRequired
                                    "cookieonly", CookieRequired
                                    "fallback", QueryParamFallback
                                    "queryparam", QueryParamFallback
                                ]
                                None
                        AuthCookieIssuance =
                            union
                                logger
                                Names.authCookieIssuance
                                [
                                    "enabled", FeatureMode.Enabled
                                    "on", FeatureMode.Enabled
                                    "1", FeatureMode.Enabled
                                    "disabled", FeatureMode.Disabled
                                    "off", FeatureMode.Disabled
                                    "0", FeatureMode.Disabled
                                ]
                                None
                        MaxRequestBodyBytes = integerOption logger Names.maxRequestBodyBytes
                        WebhookUrlAllowedHosts = textList Names.webhookUrlAllowedHosts
                }
                RiskAcknowledgements = {
                    defaults.RiskAcknowledgements with
                        AcceptForwardedHeadersFromAnyProxy = flag Names.acceptForwardedHeadersFromAnyProxy
                        AcceptHeaderAuthWhenAuthRequired = flag Names.acceptHeaderAuthInAuthMode
                        // Two spellings, one acknowledgement: the shorter name is
                        // what the at-rest-posture validator's refusal prints, and
                        // ORing here keeps it from becoming a second, separately
                        // honoured opt-out.
                        AcceptPlaintextSecretsWhenAuthRequired =
                            flag Names.acceptPlaintextSecretsInAuthMode || flag Names.acceptPlaintextSecrets
                        AcceptInProcessSchedulerInMultiInstance = flag Names.acceptInProcessSchedulerMultiInstance
                        AcceptNoRateLimitWhenAuthRequired = flag Names.acceptNoRateLimitInAuthMode
                        AcceptUnsignedPublishable = flag Names.acceptUnsignedPublishable
                        AcceptQueryParamSseAuthWhenAuthRequired = flag Names.acceptQueryParamSseAuthInAuthMode
                        AcceptSameSiteOnlyCsrfWhenAuthRequired = flag Names.acceptSameSiteOnlyCsrfInAuthMode
                        AcceptInMemoryShareTokenRateLimiterInMultiInstance =
                            flag Names.acceptInMemoryShareTokenRateLimiterMultiInstance
                        AcceptInProcessIngestionInMultiInstance = flag Names.acceptInProcessIngestionMultiInstance
                        AcceptSharedEmbeddingCacheInTeamMode = flag Names.acceptSharedEmbeddingCacheInTeamMode
                        AcceptEphemeralRagIndex = flag Names.acceptEphemeralRagIndex
                        AcceptLocalEmbedderAtScale = flag Names.acceptLocalEmbedderAtScale
                        AcceptStickyRoutedAiInMultiInstance = flag Names.acceptStickyRoutedAiMultiInstance
                        AcceptAnonymousModeWithAI = flag Names.acceptAnonymousModeWithAi
                        AcceptUnboundAudienceWhenAuthRequired = flag Names.acceptUnboundAudienceInAuthMode
                        AcceptInMemoryOAuthStateInMultiInstance = flag Names.acceptInMemoryOAuthStateMultiInstance
                        AcceptPendingInviteStoreInMultiInstance = flag Names.acceptPendingInviteStoreMultiInstance
                        AcceptInviteByEmailWithoutDirectory = flag Names.acceptInviteByEmailWithoutDirectory
                        AcceptEphemeralShareTokenKey = flag Names.acceptEphemeralShareTokenKey
                }
                StaticPathBehaviour =
                    union
                        logger
                        Names.staticPathBehaviour
                        [
                            "warn", Warn
                            "require", RequireExist
                            "requireexist", RequireExist
                            "require-exist", RequireExist
                            "skip", SkipSilent
                            "skipsilent", SkipSilent
                            "skip-silent", SkipSilent
                        ]
                        None
                Observability = {
                    defaults.Observability with
                        SlowRequestThresholdOverrides =
                            overrides.SlowRequestThresholdOverrides
                            |> Option.defaultValue defaults.Observability.SlowRequestThresholdOverrides
                        LogLevel = logLevel
                        TraceCategories = traceCategories
                        HealthStateTracking = flag Names.healthStateTracking
                        MetricsEndpoint =
                            enabledDisabled logger Names.metricsEndpoint FeatureMode.Disabled FeatureMode.Enabled None
                        SlowRequestThreshold = milliseconds logger Names.slowRequestMs
                }
                EnableDevEndpoints = flagWith Names.enableDevEndpoints overrides.EnableDevEndpoints
                AutoBootstrapDevAdmin = overrides.AutoBootstrapDevAdmin
                IncludePlatformDefaults = flagWith Names.includePlatformDefaults overrides.IncludePlatformDefaults
                Stores = {
                    defaults.Stores with
                        ShareTokenStore =
                            enabledDisabled
                                logger
                                Names.shareTokenStore
                                FeatureMode.Disabled
                                FeatureMode.Enabled
                                overrides.ShareTokenStore
                        ResultStore =
                            union
                                logger
                                Names.resultStore
                                [
                                    "no", NoResultStore
                                    "inmemory", InMemoryResultStore
                                    "in-memory", InMemoryResultStore
                                    "persistent", PersistentResultStore
                                ]
                                None
                        ExternalContactStore =
                            enabledDisabled
                                logger
                                Names.externalContactStore
                                FeatureMode.Disabled
                                FeatureMode.Enabled
                                None
                        EntityStore =
                            enabledDisabled logger Names.entityStore FeatureMode.Disabled FeatureMode.Enabled None
                        EntityOutbox =
                            enabledDisabled logger Names.entityOutbox FeatureMode.Disabled FeatureMode.Enabled None
                        AssetStore =
                            enabledDisabled logger Names.assetStore FeatureMode.Disabled FeatureMode.Enabled None
                        ConsentStateStore =
                            union
                                logger
                                Names.consentStateStore
                                [
                                    "no", NoConsentStateStore
                                    "off", NoConsentStateStore
                                    "disabled", NoConsentStateStore
                                    "inmemory", InMemoryConsentStateStore
                                    "in-memory", InMemoryConsentStateStore
                                    "entity", EntityBackedConsentStateStore
                                    "entity-backed", EntityBackedConsentStateStore
                                ]
                                None
                        RateLimitStore =
                            union
                                logger
                                Names.rateLimitStore
                                [
                                    "no", NoRateLimitStore
                                    "off", NoRateLimitStore
                                    "disabled", NoRateLimitStore
                                    "inmemory", InMemoryRateLimitStore
                                    "in-memory", InMemoryRateLimitStore
                                    "external", ExternalRateLimitStore
                                ]
                                None
                        EventStore =
                            union
                                logger
                                Names.eventStore
                                [
                                    "inmemory", InMemoryOnly
                                    "in-memory", InMemoryOnly
                                    "persistent", PersistentBlobBacked EventRetentionPolicy.ninetyDays
                                ]
                                None
                        // A case carrying a payload a token cannot express refuses
                        // the boot naming how to supply it.
                        ConversationStore =
                            unionWith
                                logger.Warn
                                Names.conversationStore
                                [
                                    "no", Ok NoConversationStore
                                    "off", Ok NoConversationStore
                                    "disabled", Ok NoConversationStore
                                    "enabled",
                                    Error
                                        "EnabledConversationStore requires a retentionDays value; set ServerConfig.Stores.ConversationStore via overrides or a `{ defaults with ... }` literal"
                                ]
                                None
                }
                Webhooks =
                    enabledDisabled logger Names.webhooks FeatureMode.Disabled FeatureMode.Enabled overrides.Webhooks
                AuditLog =
                    enabledDisabled logger Names.auditLog FeatureMode.Disabled FeatureMode.Enabled overrides.AuditLog
                MultiInstance = {
                    defaults.MultiInstance with
                        ReplicaCount = int32Key logger Names.replicaCount
                        PeerRoutePrefixes = textList Names.peerRoutePrefixes
                        ProcessProfile =
                            union
                                logger
                                Names.processProfile
                                [
                                    "allinone", AllInOne
                                    "all-in-one", AllInOne
                                    "web", WebOnly
                                    "webonly", WebOnly
                                    "worker", WorkerOnly
                                    "workeronly", WorkerOnly
                                    "dispatcher", DispatcherOnly
                                    "dispatcheronly", DispatcherOnly
                                ]
                                None
                }
                NotifyInviterOnInviteExpiry = flag Names.notifyInviterOnInviteExpiry
                Port = serverPort ()
                BindAddress = bindAddress ()
                PublicBaseUrl = publicBaseUrl logger
                BackfillMissedTicks = flag Names.backfillMissedTicks
                EventTriggerCatchUp = flag Names.eventTriggerCatchUp
                MigrateWebhookSecretsAtRest = flag Names.migrateWebhookSecretsAtRest
                SkipPreflight = flag Names.skipPreflight
                EnableCitationDevEndpoint = flagOption Names.enableCitationDevEndpoint
                SlowRateLimitThreshold = milliseconds logger Names.slowRateLimitMs
                AuditFailurePolicy =
                    union
                        logger
                        Names.auditFailurePolicy
                        [
                            "log", LogAndContinue
                            "logandcontinue", LogAndContinue
                            "refuse", RefuseAction
                            "refuseaction", RefuseAction
                            "degrade", DegradeToFile
                            "degradetofile", DegradeToFile
                        ]
                        None
                Lineage = enabledDisabled logger Names.lineage FeatureMode.Disabled FeatureMode.Enabled None
                LineageDurability =
                    enabledDisabled logger Names.lineageDurability FeatureMode.Disabled FeatureMode.Enabled None
                DataIngestion = enabledDisabled logger Names.dataIngestion FeatureMode.Disabled FeatureMode.Enabled None
                ColumnMapping = enabledDisabled logger Names.columnMapping FeatureMode.Disabled FeatureMode.Enabled None
                MappingDryRun =
                    enabledDisabled
                        logger
                        Names.mappingDryRunBlock
                        WarnOnValidationFailure
                        BlockOnValidationFailure
                        None
                OAuthRefresher =
                    enabledDisabled logger Names.oauthRefresher FeatureMode.Disabled FeatureMode.Enabled None
                UsageMetering = enabledDisabled logger Names.usageMetering FeatureMode.Disabled FeatureMode.Enabled None
                ComputeBudget = enabledDisabled logger Names.computeBudget FeatureMode.Disabled FeatureMode.Enabled None
                PlatformKnowledgeBase =
                    enabledDisabled logger Names.platformKnowledgeBase FeatureMode.Disabled FeatureMode.Enabled None
                ConfigDriftDetection =
                    enabledDisabled logger Names.configDriftDetection FeatureMode.Disabled FeatureMode.Enabled None
                RateLimiter = enabledDisabled logger Names.rateLimiter FeatureMode.Disabled FeatureMode.Enabled None
                SmokeTest = enabledDisabled logger Names.smokeTest FeatureMode.Disabled FeatureMode.Enabled None
                DeploymentReadiness =
                    enabledDisabled logger Names.deploymentReadiness FeatureMode.Disabled FeatureMode.Enabled None
                DeploymentVerification =
                    enabledDisabled logger Names.deploymentVerification FeatureMode.Disabled FeatureMode.Enabled None
                ConsentAudit = enabledDisabled logger Names.consentAudit FeatureMode.Disabled FeatureMode.Enabled None
                AdAnalytics = enabledDisabled logger Names.adAnalytics FeatureMode.Disabled FeatureMode.Enabled None
                ServerlessHost =
                    union logger Names.serverlessHost [ "kestrel", KestrelHost; "serverless", ServerlessHost ] None
                TeamCreationPolicy =
                    union
                        logger
                        Names.teamCreationPolicy
                        [
                            "platformadminonly", PlatformAdminOnly
                            "platform-admin-only", PlatformAdminOnly
                            "admin", PlatformAdminOnly
                            "anyauthenticateduser", AnyAuthenticatedUser
                            "any", AnyAuthenticatedUser
                            "authenticated", AnyAuthenticatedUser
                        ]
                        None
                DirectAddIdentityProof =
                    union
                        logger
                        Names.requireDirectoryProofForDirectAdd
                        [
                            "0", NoIdentityProof
                            "false", NoIdentityProof
                            "no", NoIdentityProof
                            "off", NoIdentityProof
                            "disabled", NoIdentityProof
                            "1", RequireDirectoryProof
                            "true", RequireDirectoryProof
                            "yes", RequireDirectoryProof
                            "on", RequireDirectoryProof
                            "enabled", RequireDirectoryProof
                        ]
                        None
                JobScheduler = enabledDisabled logger Names.jobScheduler NoJobScheduler InProcessJobScheduler None
                PublicRendering =
                    unionWith
                        logger.Warn
                        Names.publicRendering
                        [
                            "no", Ok NoPublicRendering
                            "off", Ok NoPublicRendering
                            "disabled", Ok NoPublicRendering
                            "enabled",
                            Error
                                "EnabledPublicRendering requires a ContentRoot path; set ServerConfig.PublicRendering via overrides"
                        ]
                        None
                // Permissive: every token the environment can select only
                // switches DSR off, so a typo that keeps the configured value
                // leaves the compliance surface running.
                DataSubjectRequests =
                    unionWith
                        logger.Warn
                        Names.dataSubjectRequests
                        [
                            "disabled", Ok DataSubjectRequestMode.Disabled
                            "no", Ok DataSubjectRequestMode.Disabled
                            "off", Ok DataSubjectRequestMode.Disabled
                            "enabled",
                            Error
                                "Enabling DSR requires an explicit ErasurePolicy (a compliance decision, not defaulted); set ServerConfig.DataSubjectRequests via overrides"
                        ]
                        None
                EphemeralStoreEvictionMinutes = evictionMinutes logger
                RateLimit = rateLimit logger
                DefaultTeamStorageQuotaBytes = integerOption logger Names.defaultStorageQuotaBytes
                MaxSseConnectionsPerScope = int32Option logger Names.maxSseConnectionsPerScope
        }

/// The preflight row for an unrecognised token on an escape hatch.
///
/// Every `TOOLUP_ACCEPT_*` acknowledgement is read by the lenient boolean
/// parser: a typo reads as `false`, so the refusal it would have
/// acknowledged keeps standing. That is the safe direction, which is why
/// it does not refuse the boot — but without this row the only trace of the
/// typo was the refusal itself, which names the check and not the
/// variable the operator mistyped. This validator names it: a `Warning`
/// listing each escape-hatch key whose value is not a recognised boolean
/// token, so `TOOLUP_ACCEPT_EPHEMERAL_RAG_INDEX=ture` is reported as a typo
/// on the line an operator reads first, not inferred from a refusal.
module EscapeHatchTokenValidator =

    /// The escape-hatch keys: the registry's boolean keys under
    /// `ConfigKeys.EscapeHatchCategory`, read off the registry so a new
    /// hatch is covered the moment it is described.
    let escapeHatchKeys: string list =
        ConfigKeys.keys
        |> List.filter (fun k ->
            k.Category = ConfigKeys.EscapeHatchCategory
            && (match k.Kind with
                | ConfigKeyKind.BoolKey _ -> true
                | _ -> false))
        |> List.map _.EnvVar

    /// The outcome over `read` (an environment lookup): `Ok` when every
    /// set escape hatch holds a recognised token, else a `Warning` naming
    /// each `KEY=value` as written. Whitespace is trimmed and a
    /// whitespace-only value reads as unset, as the parsers read it.
    let evaluate (read: string -> string option) : ConfigValidation.ValidationResult =
        let malformed = [
            for key in escapeHatchKeys do
                match read key |> Option.map _.Trim() with
                | Some raw when
                    raw <> ""
                    && not (
                        let token = raw.ToLowerInvariant()
                        trueTokens.Contains token || falseTokens.Contains token
                    )
                    ->
                    sprintf "%s=%s" key raw
                | _ -> ()
        ]

        match malformed with
        | [] -> ConfigValidation.ValidationResult.Ok
        | entries ->
            ConfigValidation.ValidationResult.Warning(
                sprintf
                    "%d escape-hatch variable(s) hold a value that is not a recognised boolean token, so each reads as false and the refusal it acknowledges still stands: %s. Expected one of: 1, true, yes, on (case-insensitive) to acknowledge; 0, false, no, off or unset to leave the check running."
                    entries.Length
                    (String.concat ", " entries)
            )

    /// Stable registration name.
    [<Literal>]
    let Name = "escape-hatch-token"

    /// The validator over the resolved configuration (environment, then
    /// profile, then manifest — the ladder `fromEnv` reads).
    let validator () : ConfigValidation.IConfigValidator =
        { new ConfigValidation.IConfigValidator with
            member _.Name = Name
            member _.Timeout = ConfigValidation.IConfigValidator.defaultTimeout
            member _.Validate() = async { return evaluate ConfigResolution.tryValue }
        }