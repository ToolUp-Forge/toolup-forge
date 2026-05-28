module ToolUp.Platform.Tests.InProcess.LocalSecretFilePermissionsValidatorTests

open System
open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

let private validate (baseDir: string) : ValidationResult =
    let v =
        LocalSecretFilePermissionsValidator.LocalSecretFilePermissionsValidator(baseDir) :> IConfigValidator

    v.Validate() |> Async.RunSynchronously

let private withTempDir (body: string -> unit) =
    let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory tempDir |> ignore

    try
        body tempDir
    finally
        try
            Directory.Delete(tempDir, true)
        with _ ->
            ()

[<Tests>]
let tests =
    testList "Phase 6l.J — Local secret-file permissions validator" [

        // Windows cannot probe the DACL without a managed ACL package
        // the SDK floor doesn't carry, so the validator no longer
        // silently returns Ok (which left operators blind to plaintext
        // secrets on disk). Instead: no secret files → Ok; secret files
        // present → an actionable Warning.

        test "Windows: empty directory → Ok" {
            if OperatingSystem.IsWindows() then
                withTempDir (fun dir ->
                    let result = validate dir
                    Expect.equal result Ok "nothing on disk means nothing to warn about")
        }

        test "Windows: secrets.json present → Warning (cannot verify ACL here)" {
            if OperatingSystem.IsWindows() then
                withTempDir (fun dir ->
                    File.WriteAllText(Path.Combine(dir, "secrets.json"), "{}")

                    match validate dir with
                    | Warning msg ->
                        Expect.stringContains msg "secrets.json" "names the file"
                        Expect.stringContains msg "icacls" "points at the verification/hardening tool"
                    | other -> failtestf "expected Warning, got %A" other)
        }

        test "Unix: empty directory → Ok" {
            if not (OperatingSystem.IsWindows()) then
                withTempDir (fun dir ->
                    let result = validate dir
                    Expect.equal result Ok "no secret files means nothing to warn about")
        }

        test "Unix: non-existent directory → Ok" {
            if not (OperatingSystem.IsWindows()) then
                let nonExistent = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))

                let result = validate nonExistent
                Expect.equal result Ok "missing dir is not a problem at this stage"
        }

        test "Unix: secrets.json with mode 600 → Ok" {
            if not (OperatingSystem.IsWindows()) then
                withTempDir (fun dir ->
                    let path = Path.Combine(dir, "secrets.json")
                    File.WriteAllText(path, "{}")
                    File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
                    let result = validate dir
                    Expect.equal result Ok "user-only-readable file passes")
        }

        test "Unix: secrets.json with mode 644 (group/world readable) → Warning" {
            if not (OperatingSystem.IsWindows()) then
                withTempDir (fun dir ->
                    let path = Path.Combine(dir, "secrets.json")
                    File.WriteAllText(path, "{}")

                    let permissive =
                        UnixFileMode.UserRead
                        ||| UnixFileMode.UserWrite
                        ||| UnixFileMode.GroupRead
                        ||| UnixFileMode.OtherRead

                    File.SetUnixFileMode(path, permissive)

                    match validate dir with
                    | Warning msg ->
                        Expect.stringContains msg "secrets.json" "names the offending file"
                        Expect.stringContains msg "chmod 600" "documents the manual fix"
                    | other -> failtestf "expected Warning, got %A" other)
        }

        test "Unix: per-scope secrets-team-abc.json with mode 644 → Warning" {
            if not (OperatingSystem.IsWindows()) then
                withTempDir (fun dir ->
                    let path = Path.Combine(dir, "secrets-team-abc.json")
                    File.WriteAllText(path, "{}")

                    let permissive =
                        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.OtherRead

                    File.SetUnixFileMode(path, permissive)

                    match validate dir with
                    | Warning msg -> Expect.stringContains msg "secrets-team-abc.json" "names the offending file"
                    | other -> failtestf "expected Warning, got %A" other)
        }

        test "FileSecretStore.SetSecret tightens permissions on Unix" {
            if not (OperatingSystem.IsWindows()) then
                withTempDir (fun dir ->
                    let store = FileSecretStore.FileSecretStore(baseDir = dir) :> Secrets.ISecretStore

                    let result =
                        store.SetSecret("_platform", "TEST_KEY", "value") |> Async.RunSynchronously

                    match result with
                    | Result.Ok() ->
                        let path = Path.Combine(dir, "secrets.json")
                        Expect.isTrue (File.Exists path) "file written"
                        let mode = File.GetUnixFileMode path

                        Expect.isFalse (mode.HasFlag UnixFileMode.GroupRead) "group-read bit is cleared"

                        Expect.isFalse (mode.HasFlag UnixFileMode.OtherRead) "other-read bit is cleared"

                        Expect.isTrue (mode.HasFlag UnixFileMode.UserRead) "user-read still set"
                        Expect.isTrue (mode.HasFlag UnixFileMode.UserWrite) "user-write still set"
                    | Result.Error msg -> failtestf "SetSecret failed: %s" msg)
        }

        test "Validator metadata is well-formed" {
            let v =
                LocalSecretFilePermissionsValidator.LocalSecretFilePermissionsValidator() :> IConfigValidator

            Expect.equal v.Name "local-secret-file-permissions" "stable identifier"
            Expect.isGreaterThan v.Timeout.TotalMilliseconds 0.0 "non-zero timeout"
        }
    ]