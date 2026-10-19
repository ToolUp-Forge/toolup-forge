module MyDataPlatform.Server.LocalRun

open System
open System.IO
open System.Security.Cryptography

// ─── The local run's settings ─────────────────────────────────────
//
// A team deployment refuses to start on an unverified identity, plaintext
// secrets or wide-open forwarded headers. A local run uses forge's dev-only
// header identity, so it states the acknowledgements a header identity
// needs, explicitly, here and nowhere else. `--local` applies them (only to
// variables that are unset); a deployment never passes it.

/// The settings, for a server reachable at `baseUrl`.
let settings (baseUrl: string) : (string * string) list = [
    // One team, created on first start; its owner is the platform admin.
    "TOOLUP_INITIAL_TEAM_NAME", "Trading"
    "TOOLUP_OAUTH_REDIRECT_BASE", baseUrl
    // Dev only: the header identity is not cryptographically verified.
    "TOOLUP_ACCEPT_HEADER_AUTH_IN_AUTH_MODE", "1"
    "TOOLUP_ACCEPT_QUERYPARAM_SSE_AUTH_IN_AUTH_MODE", "1"
    // Nothing fronts a local run, so no forwarded header is trusted.
    "TOOLUP_TRUST_FORWARDED_HEADERS", "0"
]

let private setIfUnset (name: string) (value: string) =
    if String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable name) then
        Environment.SetEnvironmentVariable(name, value)

/// Apply the settings, plus a secrets master key kept under `dataRoot` so
/// what the local run encrypts stays readable across restarts.
let apply (baseUrl: string) (dataRoot: string) : unit =
    for name, value in settings baseUrl do
        setIfUnset name value

    let keyFile = Path.Combine(dataRoot, "dev-master-key")

    if not (File.Exists keyFile) then
        Directory.CreateDirectory dataRoot |> ignore
        File.WriteAllText(keyFile, Convert.ToBase64String(RandomNumberGenerator.GetBytes 32))

    setIfUnset "TOOLUP_SECRETS_MASTER_KEY" (File.ReadAllText(keyFile).Trim())