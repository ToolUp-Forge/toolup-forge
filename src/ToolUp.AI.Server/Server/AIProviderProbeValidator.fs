module ToolUp.AI.AIProviderProbeValidator

open System
open System.Net
open System.Net.Http
open System.Text.Json
open ToolUp.AI
open ToolUp.Platform.ConfigValidation

// ─── Phase 9m.A optional API-access startup probe ────────────────────
//
// Default-OFF network probe gated on `TOOLUP_AI_PROBE_ON_STARTUP=1`.
// Catches the "platform AI is misconfigured" class without forcing
// every deployment to pay an outbound call at boot (GP 13). Two
// classes of finding:
//
//   1. The provider's models endpoint refuses the configured API key
//      (HTTP 401 / 403) — `Warning` because the deployment may still
//      run some non-AI traffic and the operator should be the one
//      deciding whether to fail.
//   2. The provider's models endpoint is unreachable (DNS failure /
//      connection refused / HTTP 5xx) — `Error`. A misconfigured
//      base URL or a broken DNS posture is a clear deploy failure.
//
// A third class — "key works but the configured model is not in the
// list this key can access" — also produces `Warning` so the operator
// sees the mismatch before the first chat request fails.
//
// Built-in support: anthropic-claude, openai-gpt, google-gemini.
// Other provider ids (custom companions) → `Warning` ("probe does not
// know how to query this provider"). API keys are read from the
// provider's documented env vars (`ANTHROPIC_API_KEY` /
// `OPENAI_API_KEY` / `GEMINI_API_KEY`) — same source as each
// provider's pre-BYOK constructor. Probe runs against the platform
// descriptor (and the `TOOLUP_AI_PROVIDER` override when set).
//
// Test seam: `create factory specs` accepts a `Map<string,
// ProviderProbeSpec>` so probe behaviour can be faked unit-side
// without real network. Production callers use `tryFromEnv` which
// wires the built-in HTTP-backed specs.

/// Outcome of one provider's models-endpoint probe. The validator
/// translates this to `ValidationResult` per the class table above.
type ProbeOutcome =
    /// Endpoint responded 2xx; returned the list of model ids the key
    /// can see (may be empty if the API returns an empty page).
    | ProbeOk of modelIds: string list
    /// Endpoint returned 401 / 403. Key likely revoked / mistyped /
    /// scoped to a different org.
    | ProbeAuthError of message: string
    /// Network failure, DNS, connect refused, or HTTP 5xx. Provider
    /// effectively unreachable.
    | ProbeUnreachable of message: string

/// One provider's probe contract. `Fetch` takes the resolved API key
/// and returns the outcome — production specs close over a shared
/// `HttpClient`; tests substitute a pure function returning a canned
/// outcome.
type ProviderProbeSpec = {
    /// Stable provider id matching the descriptor's `Id` field.
    ProviderId: string
    /// Env var the production spec reads the API key from. Surfaced
    /// for the "key env var unset" Warning so the operator sees which
    /// var is expected. Test specs may leave this empty.
    ApiKeyEnvVar: string
    /// Probe the models endpoint with the supplied key.
    Fetch: string -> Async<ProbeOutcome>
}

let private providerEnvVarName = "TOOLUP_AI_PROVIDER"
let private modelEnvVarName = "TOOLUP_AI_MODEL"
let private probeEnvVarName = "TOOLUP_AI_PROBE_ON_STARTUP"

let private readEnv (name: string) =
    match Environment.GetEnvironmentVariable name with
    | null
    | "" -> None
    | v -> Some(v.Trim())

let private probeEnabled () =
    match readEnv probeEnvVarName with
    | Some "1"
    | Some "true"
    | Some "TRUE" -> true
    | _ -> false

let private resolveTargetDescriptor (factory: IAIProviderFactory) : AIProviderDescriptor option =
    match readEnv providerEnvVarName with
    | Some providerId ->
        let descriptors =
            (factory.PlatformDescriptor |> Option.toList) @ factory.Available
            |> List.distinctBy _.Id

        descriptors |> List.tryFind (fun d -> d.Id = providerId)
    | None -> factory.PlatformDescriptor

let private resolveTargetModel (descriptor: AIProviderDescriptor) : string =
    readEnv modelEnvVarName |> Option.defaultValue descriptor.DefaultModel

type private Impl(factory: IAIProviderFactory, specs: Map<string, ProviderProbeSpec>, ?timeout: TimeSpan) =
    // 8s default — well under the 10s aggregator budget but enough
    // headroom for a cold-DNS first hit.
    let timeout = defaultArg timeout (TimeSpan.FromSeconds 8.0)

    interface IConfigValidator with
        member _.Name = "ai-provider-probe"
        member _.Timeout = timeout

        member _.Validate() = async {
            match resolveTargetDescriptor factory with
            | None ->
                // No platform descriptor and TOOLUP_AI_PROVIDER unset
                // (or set to something the factory doesn't list and
                // PlatformDescriptor is also None). Nothing actionable
                // to probe; companion auto-registration already runs
                // the env-var-typo Warning via AIProviderEnvValidator.
                return Ok
            | Some descriptor ->
                let model = resolveTargetModel descriptor

                match Map.tryFind descriptor.Id specs with
                | None ->
                    return
                        Warning(
                            sprintf
                                "probe does not know how to query provider '%s' (model '%s'). Built-in probe support: %s. Disable %s to silence, or extend AIProviderProbeValidator with a ProviderProbeSpec for this provider."
                                descriptor.Id
                                model
                                (specs |> Map.toList |> List.map fst |> List.sort |> String.concat ", ")
                                probeEnvVarName
                        )
                | Some spec ->
                    match readEnv spec.ApiKeyEnvVar with
                    | None ->
                        return
                            Warning(
                                sprintf
                                    "cannot probe provider '%s' (model '%s'): env var %s is unset. The probe needs the API key to query the provider's models endpoint. Either set %s for the operator-level startup probe, or disable %s to silence."
                                    descriptor.Id
                                    model
                                    spec.ApiKeyEnvVar
                                    spec.ApiKeyEnvVar
                                    probeEnvVarName
                            )
                    | Some apiKey ->
                        let! outcome = spec.Fetch apiKey

                        match outcome with
                        | ProbeOk modelIds ->
                            if List.isEmpty modelIds || List.contains model modelIds then
                                return Ok
                            else
                                return
                                    Warning(
                                        sprintf
                                            "provider '%s' models endpoint responded but model '%s' is not in the list the configured key can access (%d models visible). Likely cause: key scoped to an account without that model, or stale model id. Set %s to a model from the visible list."
                                            descriptor.Id
                                            model
                                            modelIds.Length
                                            modelEnvVarName
                                    )
                        | ProbeAuthError msg ->
                            return
                                Warning(
                                    sprintf
                                        "provider '%s' refused the configured API key (HTTP 401/403) — %s. The key in %s may be revoked, mistyped, or scoped to a different organisation. AI requests will fail at runtime."
                                        descriptor.Id
                                        msg
                                        spec.ApiKeyEnvVar
                                )
                        | ProbeUnreachable msg ->
                            return
                                Error(
                                    sprintf
                                        "provider '%s' models endpoint unreachable — %s. Likely cause: DNS / network / 5xx outage at the upstream, or a misconfigured base URL. AI requests will fail at runtime; this aborts startup so the deploy pipeline notices."
                                        descriptor.Id
                                        msg
                                )
        }

let private parseModelIds (json: string) (idField: string) : string list =
    try
        use doc = JsonDocument.Parse json
        let root = doc.RootElement

        let tryArray (name: string) =
            match root.TryGetProperty name with
            | true, prop when prop.ValueKind = JsonValueKind.Array -> Some prop
            | _ -> None

        let arr =
            // OpenAI / Anthropic return {"data": [...]}; Gemini returns
            // {"models": [...]}.
            match tryArray "data" with
            | Some d -> Some d
            | None -> tryArray "models"

        match arr with
        | None -> []
        | Some array -> [
            for el in array.EnumerateArray() do
                match el.TryGetProperty idField with
                | true, idProp when idProp.ValueKind = JsonValueKind.String -> yield idProp.GetString()
                | _ -> ()
          ]
    with _ -> []

let private httpFetcher
    (client: HttpClient)
    (relativeUrl: string)
    (applyAuth: HttpRequestMessage -> unit)
    (idField: string)
    : Async<ProbeOutcome> =
    async {
        try
            use req = new HttpRequestMessage(HttpMethod.Get, relativeUrl)
            applyAuth req

            let! response = client.SendAsync req |> Async.AwaitTask
            use response = response

            if
                response.StatusCode = HttpStatusCode.Unauthorized
                || response.StatusCode = HttpStatusCode.Forbidden
            then
                return ProbeAuthError(sprintf "HTTP %d" (int response.StatusCode))
            elif int response.StatusCode >= 500 then
                return ProbeUnreachable(sprintf "HTTP %d" (int response.StatusCode))
            elif not response.IsSuccessStatusCode then
                return ProbeUnreachable(sprintf "HTTP %d" (int response.StatusCode))
            else
                let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                return ProbeOk(parseModelIds body idField)
        with ex ->
            return ProbeUnreachable ex.Message
    }

// ─── Built-in probe specs for the three shipped providers ────────────

let private claudeClient =
    lazy (new HttpClient(BaseAddress = Uri("https://api.anthropic.com")))

let private openAiClient =
    lazy (new HttpClient(BaseAddress = Uri("https://api.openai.com")))

let private geminiClient =
    lazy (new HttpClient(BaseAddress = Uri("https://generativelanguage.googleapis.com")))

let private claudeSpec: ProviderProbeSpec = {
    ProviderId = "anthropic-claude"
    ApiKeyEnvVar = "ANTHROPIC_API_KEY"
    Fetch =
        fun key ->
            httpFetcher
                claudeClient.Value
                "/v1/models"
                (fun req ->
                    req.Headers.Add("x-api-key", key)
                    req.Headers.Add("anthropic-version", "2023-06-01"))
                "id"
}

let private openAiSpec: ProviderProbeSpec = {
    ProviderId = "openai-gpt"
    ApiKeyEnvVar = "OPENAI_API_KEY"
    Fetch =
        fun key ->
            httpFetcher
                openAiClient.Value
                "/v1/models"
                (fun req -> req.Headers.Add("Authorization", "Bearer " + key))
                "id"
}

let private geminiSpec: ProviderProbeSpec = {
    ProviderId = "google-gemini"
    ApiKeyEnvVar = "GEMINI_API_KEY"
    Fetch =
        fun key ->
            httpFetcher geminiClient.Value "/v1beta/models" (fun req -> req.Headers.Add("x-goog-api-key", key)) "name"
}

/// Built-in probe specs for the three shipped providers. Exposed for
/// tests that want to substitute one entry without rebuilding the
/// whole map.
let defaultProbeSpecs: Map<string, ProviderProbeSpec> =
    [ claudeSpec; openAiSpec; geminiSpec ]
    |> List.map (fun s -> s.ProviderId, s)
    |> Map.ofList

/// Construct a validator with explicit probe specs. `composeAI` calls
/// `tryFromEnv` for the production wiring; tests use this entry point
/// to inject fake fetchers.
let create (factory: IAIProviderFactory) (specs: Map<string, ProviderProbeSpec>) : IConfigValidator =
    Impl(factory, specs) :> IConfigValidator

/// Return a validator iff `TOOLUP_AI_PROBE_ON_STARTUP=1` is set in the
/// environment. Mirrors the `OidcAuthValidator.tryFromEnv` pattern —
/// deployments that haven't opted in pay nothing (GP 13).
let tryFromEnv (factory: IAIProviderFactory) : IConfigValidator option =
    if probeEnabled () then
        Some(create factory defaultProbeSpecs)
    else
        None