module ToolUp.Platform.Tests.InProcess.IdentitySanitiserTests

open Expecto
open ToolUp.Platform.Auth
open ToolUp.Platform.Config.Auth

[<Tests>]
let tests =
    testList "Phase 6l.H — Identity sanitiser" [

        // ─── Acceptance cases — legitimate identifiers ──────────────

        test "alphanumeric is accepted" {
            Expect.equal (IdentitySanitiser.sanitiseScopeId "abc123") (Result.Ok "abc123") "alphanumeric ok"
        }

        test "UUID-shaped is accepted" {
            let uuid = "e87c8a26-9a2f-4f1e-9b6a-1234567890ab"
            Expect.equal (IdentitySanitiser.sanitiseScopeId uuid) (Result.Ok uuid) "UUID with hyphens"
        }

        test "underscore is accepted" {
            Expect.equal (IdentitySanitiser.sanitiseScopeId "user_123") (Result.Ok "user_123") "underscore preserved"
        }

        test "period in middle is accepted (email-shaped subject)" {
            Expect.equal (IdentitySanitiser.sanitiseScopeId "alice.smith") (Result.Ok "alice.smith") "period mid-string"
        }

        // ─── Path-traversal attack vectors ──────────────────────────

        test "double-dot is rejected" {
            match IdentitySanitiser.sanitiseScopeId ".." with
            | Result.Ok _ -> failtest "expected rejection"
            | Result.Error _ -> ()
        }

        test "path traversal up directories is rejected" {
            // Leading period catches this before path-separator check —
            // either rejection class is acceptable.
            match IdentitySanitiser.sanitiseScopeId "../../etc/passwd" with
            | Result.Ok _ -> failtest "expected rejection"
            | Result.Error _ -> ()
        }

        test "path traversal mid-string (no leading period) is rejected" {
            match IdentitySanitiser.sanitiseScopeId "user/../etc" with
            | Result.Ok _ -> failtest "expected rejection"
            | Result.Error reason -> Expect.stringContains reason "path separator" "names the issue category"
        }

        test "forward slash is rejected" {
            match IdentitySanitiser.sanitiseScopeId "a/b" with
            | Result.Ok _ -> failtest "expected rejection"
            | Result.Error _ -> ()
        }

        test "backslash is rejected" {
            match IdentitySanitiser.sanitiseScopeId "a\\b" with
            | Result.Ok _ -> failtest "expected rejection"
            | Result.Error _ -> ()
        }

        // ─── Control characters ─────────────────────────────────────

        test "NUL byte is rejected" {
            match IdentitySanitiser.sanitiseScopeId "a\u0000b" with
            | Result.Ok _ -> failtest "expected rejection"
            | Result.Error _ -> ()
        }

        test "carriage return is rejected" {
            match IdentitySanitiser.sanitiseScopeId "a\rb" with
            | Result.Ok _ -> failtest "expected rejection"
            | Result.Error _ -> ()
        }

        test "tab is rejected" {
            match IdentitySanitiser.sanitiseScopeId "a\tb" with
            | Result.Ok _ -> failtest "expected rejection"
            | Result.Error _ -> ()
        }

        // ─── Edge cases ─────────────────────────────────────────────

        test "empty string is rejected" {
            match IdentitySanitiser.sanitiseScopeId "" with
            | Result.Ok _ -> failtest "expected rejection"
            | Result.Error reason -> Expect.stringContains reason "empty" "names the issue"
        }

        test "null is rejected" {
            match IdentitySanitiser.sanitiseScopeId (null: string) with
            | Result.Ok _ -> failtest "expected rejection"
            | Result.Error reason -> Expect.stringContains reason "null" "names the issue"
        }

        test "leading period (Unix dotfile) is rejected" {
            match IdentitySanitiser.sanitiseScopeId ".bashrc" with
            | Result.Ok _ -> failtest "expected rejection"
            | Result.Error reason -> Expect.stringContains reason "leading period" "names the issue"
        }

        test "overlong string is rejected" {
            let value = String.replicate (IdentitySanitiser.MaxLength + 1) "a"

            match IdentitySanitiser.sanitiseScopeId value with
            | Result.Ok _ -> failtest "expected rejection"
            | Result.Error reason -> Expect.stringContains reason "exceeds maximum" "names the issue"
        }

        // ─── Windows reserved names ─────────────────────────────────

        test "Windows CON is rejected" {
            match IdentitySanitiser.sanitiseScopeId "CON" with
            | Result.Ok _ -> failtest "expected rejection"
            | Result.Error reason -> Expect.stringContains reason "Windows reserved" "names the issue"
        }

        test "Windows reserved is case-insensitive" {
            match IdentitySanitiser.sanitiseScopeId "nul" with
            | Result.Ok _ -> failtest "expected rejection"
            | Result.Error _ -> ()
        }

        test "Windows reserved with extension is rejected" {
            match IdentitySanitiser.sanitiseScopeId "PRN.txt" with
            | Result.Ok _ -> failtest "expected rejection"
            | Result.Error _ -> ()
        }

        test "Windows reserved COM1 is rejected" {
            match IdentitySanitiser.sanitiseScopeId "COM1" with
            | Result.Ok _ -> failtest "expected rejection"
            | Result.Error _ -> ()
        }
    ]