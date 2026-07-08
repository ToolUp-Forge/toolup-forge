module AzureSpeechTranscriptionProviderHealth

open System
open ToolUp.Platform.HealthChecks
open ToolUp.Platform.Secrets

// ─── Azure Speech transcription provider health probe ────────────────
//
// Verifies that both `azure-speech-key` and `azure-speech-region` resolve
// to non-empty values in the `_platform` scope. As with the AI /
// embedding provider probes we do NOT call Azure on every probe; a
// bad-but-non-empty key surfaces at first transcription-call time via the
// classified error path. The unique probe name lets it coexist with the
// other transcription / AI / embedding probes.

type AzureSpeechTranscriptionProviderHealthCheck(secretStore: ISecretStore) =
    interface IHealthCheck with
        member _.Name = "transcription_provider:azure-speech"
        member _.Kind = Readiness
        member _.Timeout = IHealthCheck.defaultTimeout

        member _.Check() = async {
            try
                let! key = secretStore.GetSecret("_platform", AzureSpeechTranscriptionProvider.KeySecretName)
                let! region = secretStore.GetSecret("_platform", AzureSpeechTranscriptionProvider.RegionSecretName)

                let present (v: string option) =
                    match v with
                    | Some s -> not (String.IsNullOrWhiteSpace s)
                    | None -> false

                match present key, present region with
                | true, true -> return Healthy
                | false, _ -> return Unhealthy "azure-speech-key not configured in secret store"
                | _, false -> return Unhealthy "azure-speech-region not configured in secret store"
            with ex ->
                return Unhealthy ex.Message
        }

let create (secretStore: ISecretStore) : IHealthCheck =
    AzureSpeechTranscriptionProviderHealthCheck(secretStore) :> IHealthCheck