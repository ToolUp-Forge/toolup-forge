module ToolUp.Platform.LocalSecretFilePermissionsValidator

open System
open System.IO
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Phase 6l.J — local secret-file permissions ─────────────────────
//
// `FileSecretStore` writes `secrets.json` and `secrets-{scope}.json`
// in the working directory via `File.WriteAllText`. On Unix-like
// systems, the file inherits the umask of the running process —
// typically 022, producing 644 (world-readable). On a multi-user
// CI runner, dev box, or local VM, every other user on the host
// can `cat` the secrets file.
//
// Phase 6l.J adds two protections:
//   1. `FileSecretStore.writeFile` now calls `File.SetUnixFileMode`
//      to tighten new writes to 600 (user-only).
//   2. This validator probes the working directory at startup for
//      pre-existing `secrets*.json` files and warns if any are
//      world- or group-readable. Catches files written before the
//      hardening landed AND files placed by deployment tooling
//      (config maps, init containers) that didn't tighten modes.
//
// Windows path: `FileSecretStore` now hardens ACLs best-effort via
// `icacls` (strip inherited ACEs + grant current user only), but that
// is unverifiable from here without the managed ACL package the SDK
// floor deliberately does not carry. So rather than the old silent
// `Ok` (which left operators with zero signal that plaintext secrets
// sit on disk under an inherited ACL), the validator emits an
// actionable `Warning` whenever local secret files exist on Windows,
// pointing at the best-effort hardening and the cloud-secret-manager
// alternative.

/// Phase 6l.J — config validator that probes the working directory
/// for pre-existing local secret files. On Unix it flags permissive
/// modes (group/other-readable); on Windows it warns that local
/// secret files are present and ACL-hardening cannot be verified here.
type LocalSecretFilePermissionsValidator(?baseDir: string, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    let resolvedBaseDir () =
        baseDir |> Option.defaultWith Directory.GetCurrentDirectory

    interface IConfigValidator with
        member _.Name = "local-secret-file-permissions"
        member _.Timeout = timeout

        member _.Validate() = async {
            try
                let dir = resolvedBaseDir ()

                if not (Directory.Exists dir) then
                    return Ok
                else
                    let candidates = [
                        Path.Combine(dir, "secrets.json")
                        // Per-scope files are wildcard — enumerate
                        // any `secrets-*.json` in the working dir.
                        yield! Directory.EnumerateFiles(dir, "secrets-*.json")
                    ]

                    if OperatingSystem.IsWindows() then
                        // No managed ACL package on the SDK floor, so we
                        // can't read the DACL to verify hardening here.
                        // Surface the presence of plaintext secret files
                        // as an actionable Warning (was: silent Ok, which
                        // left operators blind to disk-resident secrets).
                        match candidates |> List.filter File.Exists with
                        | [] -> return Ok
                        | files ->
                            let names = files |> List.map Path.GetFileName |> String.concat ", "

                            return
                                Warning(
                                    sprintf
                                        "Local secret file(s) present on Windows: %s. These hold plaintext secrets (encryption master key, share-token signing key, API keys). FileSecretStore hardens their ACL best-effort via icacls (inherited ACEs stripped, current user only) but that cannot be verified from here — an inherited ACL on a multi-user host would expose them to every local account. Verify with `icacls %s\\secrets*.json` that only the service account has access, or (preferred for production) use a cloud secret-manager ISecretStore (Azure Key Vault / AWS Secrets Manager / GCP Secret Manager) instead of local files."
                                        names
                                        (resolvedBaseDir ())
                                )
                    else
                        let leaky =
                            candidates
                            |> List.filter File.Exists
                            |> List.choose (fun path ->
                                try
                                    let mode = File.GetUnixFileMode path

                                    let permissive =
                                        mode.HasFlag UnixFileMode.GroupRead || mode.HasFlag UnixFileMode.OtherRead

                                    if permissive then
                                        Some(Path.GetFileName path, mode)
                                    else
                                        None
                                with _ ->
                                    None)

                        match leaky with
                        | [] -> return Ok
                        | files ->
                            let names =
                                files
                                |> List.map (fun (name, mode) -> sprintf "%s (%O)" name mode)
                                |> String.concat ", "

                            return
                                Warning(
                                    sprintf
                                        "Local secret file(s) with permissive Unix mode: %s. On a multi-user host, every other user can read these secrets. Phase 6l.J tightened FileSecretStore.writeFile to mode 600 for new writes — these files predate the hardening or were placed by deployment tooling. Run `chmod 600 %s/secrets*.json` (or equivalent for your shell) to tighten manually. For production deployments, prefer a cloud secret-manager ISecretStore (Azure Key Vault / AWS Secrets Manager / GCP Secret Manager) over local files."
                                        names
                                        (resolvedBaseDir ())
                                )
            with _ ->
                // Filesystem doesn't support Unix modes (FAT, exFAT,
                // some network mounts). Skip silently.
                return Ok
        }