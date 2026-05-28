module EnvironmentSecretStore

open System
open ToolUp.Platform.Secrets

/// Secret store that reads from environment variables, scoped by caller.
///
/// Lookup rules:
///   - `_platform` scope reads `{KEY}` directly (e.g. `ANTHROPIC_API_KEY`)
///     for backward compatibility with existing deployments
///   - Other scopes read `TOOLUP_{SCOPE}_{KEY}` where `SCOPE` is the scope id
///     uppercased and sanitised (hyphens to underscores)
///
/// Example: `GetSecret("team-abc123", "SLACK_TOKEN")` reads
/// `TOOLUP_TEAM_ABC123_SLACK_TOKEN`. This naming prevents a team's key from
/// ever being returned for another team — no shared global namespace.
type EnvironmentSecretStore() =
    let sanitiseScope (scopeId: string) =
        scopeId.Replace('-', '_').ToUpperInvariant()

    let envVarName scopeId key =
        if scopeId = "_platform" then
            key
        else
            $"TOOLUP_{sanitiseScope scopeId}_{key}"

    interface ISecretStore with
        member _.GetSecret(scopeId, key) = async {
            let name = envVarName scopeId key

            match Environment.GetEnvironmentVariable name with
            | null
            | "" -> return None
            | value -> return Some value
        }

        // Environment variables are read-only at runtime — Set and
        // Delete would require spawning a subprocess or modifying
        // the host machine's env vars, neither of which is in scope
        // for a secret store. BYOK deployments should pair
        // EnvironmentSecretStore (for platform-level keys baked into
        // deployment config) with a writable store (e.g.
        // FileSecretStore) for user/team-scoped keys.
        member _.SetSecret(_scopeId, _key, _value) = async { return Error "EnvironmentSecretStore is read-only" }

        member _.DeleteSecret(_scopeId, _key) = async { return Error "EnvironmentSecretStore is read-only" }

        // Env var naming is open-ended; there is no reliable way to
        // enumerate keys for a given scope without scanning every
        // variable in the process environment. Rotation helpers
        // should pair EnvironmentSecretStore with a writable store
        // (FileSecretStore) for user/team-scoped keys and leave the
        // platform-scoped env vars untouched.
        member _.ListKeys(_scopeId) = async { return [] }