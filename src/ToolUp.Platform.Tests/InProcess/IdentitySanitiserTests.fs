module ToolUp.Platform.Tests.InProcess.IdentitySanitiserTests

open Expecto
open ToolUp.Platform.Auth
open ToolUp.Platform.Config.Auth

[<Tests>]
let tests =
    let accepted = [
        "alphanumeric is accepted", "abc123"
        "UUID-shaped is accepted", "e87c8a26-9a2f-4f1e-9b6a-1234567890ab"
        "underscore is accepted", "user_123"
        "period in middle is accepted (email-shaped subject)", "alice.smith"
    ]

    // `reason` is a fragment the rejection must name; `None` accepts either
    // rejection class (e.g. "../../etc/passwd" trips the leading-period
    // check before the path-separator one).
    let rejected: (string * string * string option) list = [
        // path-traversal attack vectors
        "double-dot is rejected", "..", None
        "path traversal up directories is rejected", "../../etc/passwd", None
        "path traversal mid-string (no leading period) is rejected", "user/../etc", Some "path separator"
        "forward slash is rejected", "a/b", None
        "backslash is rejected", "a\\b", None
        // control characters
        "NUL byte is rejected", "a\u0000b", None
        "carriage return is rejected", "a\rb", None
        "tab is rejected", "a\tb", None
        // edge cases
        "empty string is rejected", "", Some "empty"
        "null is rejected", (null: string), Some "null"
        "leading period (Unix dotfile) is rejected", ".bashrc", Some "leading period"
        "overlong string is rejected", String.replicate (IdentitySanitiser.MaxLength + 1) "a", Some "exceeds maximum"
        // Windows reserved names
        "Windows CON is rejected", "CON", Some "Windows reserved"
        "Windows reserved is case-insensitive", "nul", None
        "Windows reserved with extension is rejected", "PRN.txt", None
        "Windows reserved COM1 is rejected", "COM1", None
    ]

    testList "Phase 6l.H — Identity sanitiser" [
        for name, input in accepted do
            test name { Expect.equal (IdentitySanitiser.sanitiseScopeId input) (Result.Ok input) "accepted unchanged" }

        for name, input, reason in rejected do
            test name {
                match IdentitySanitiser.sanitiseScopeId input, reason with
                | Result.Ok _, _ -> failtest "expected rejection"
                | Result.Error _, None -> ()
                | Result.Error actual, Some fragment -> Expect.stringContains actual fragment "names the issue"
            }
    ]