module WhisperTranscriptionProviderHealth

open System
open ToolUp.Platform.HealthChecks
open ToolUp.Platform.Secrets

// ─── Whisper transcription provider health probe ─────────────────────
//
// Verifies that `openai-api-key` resolves to a non-empty value in the
// `_platform` scope. As with the AI / embedding provider probes we do NOT
// call OpenAI on every probe (token cost + `/ready` polling frequency); a
// bad-but-non-empty key surfaces at first transcription-call time via the
// classified error path. The unique probe name lets it coexist with the
// AI / embedding provider probes under the per-name registration rule.

type WhisperTranscriptionProviderHealthCheck(secretStore: ISecretStore) =
    interface IHealthCheck with
        member _.Name = "transcription_provider:whisper"
        member _.Kind = Readiness
        member _.Timeout = IHealthCheck.defaultTimeout

        member _.Check() = async {
            try
                let! secret = secretStore.GetSecret("_platform", "openai-api-key")

                match secret with
                | Some value when not (String.IsNullOrWhiteSpace value) -> return Healthy
                | Some _ -> return Unhealthy "openai-api-key is set but empty"
                | None -> return Unhealthy "openai-api-key not configured in secret store"
            with ex ->
                return Unhealthy ex.Message
        }

let create (secretStore: ISecretStore) : IHealthCheck =
    WhisperTranscriptionProviderHealthCheck(secretStore) :> IHealthCheck