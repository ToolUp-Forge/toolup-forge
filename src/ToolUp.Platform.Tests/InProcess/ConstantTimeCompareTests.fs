module ToolUp.Platform.Tests.InProcess.ConstantTimeCompareTests

open System
open System.IO
open System.Text
open System.Text.RegularExpressions
open System.Threading.Tasks
open Expecto
open ToolUp.Platform

// ─── Phase 467 — constant-time token compare + admin-token throttle ────
//
// Two defects, one pack.
//
// **A/B — the compare.** `EncryptionAdminHandler` and `SmokeTestHandler`
// each carried a private char-XOR loop (`int a[i] ^^^ int b[i]` folded
// over the strings, length-checked on CHARS). That is a *correct*
// equality test but not a byte-correct constant-time one: it compares
// UTF-16 code units, so for a non-ASCII token the work it does — and the
// point at which it short-circuits — tracks the token's char shape
// rather than the bytes the token actually is. `CsrfMiddleware` carried a
// third copy, byte-correct but private. All three now route through
// `JwtCrypto.fixedTimeEqualsUtf8`, the single sanctioned string-token
// comparison (BCL `CryptographicOperations.FixedTimeEquals` over UTF-8).
//
// **A timing assertion would be flaky**, and this pack deliberately does
// not attempt one. It pins the two things that ARE decidable: the
// helper's byte-level *behaviour* (section A — including the cases where
// char-length and byte-length diverge, which is exactly what the old loop
// got wrong), and the *implementation* at each call site (section B — the
// defective shape is absent from the SDK core tiers, and each gate names
// the sanctioned helper). Section B is a source-level fitness check in
// the `AdversarialFailClosedTests` / `ArchitectureFitness` tradition: the
// gate functions are `private`, so their call sites cannot be reached by
// reflection, and a behavioural test could not distinguish a correct
// compare from a correct-but-char-shaped one anyway.
//
// **C — the throttle.** The per-IP failure window read `DateTime.UtcNow`
// twice inside one logical update — once to test whether the caller's
// window was still open, again to stamp its replacement. Two reads are
// two instants, so a window judged expired against the first could be
// re-stamped from the second, sliding forward and losing the attempt that
// cleared it. `TokenAttemptThrottle` takes the instant as a parameter;
// section C pins that the window transition observes exactly one instant,
// that concurrent failures on one key are all counted (so the cap cannot
// be walked past by racing), and the ordinary window semantics.

// ── Section A — byte-normalised constant-time comparison ───────────────

let private utf8 (s: string) = Encoding.UTF8.GetBytes s

// Non-ASCII fixtures are written as escapes rather than literal glyphs:
// the exact code points are the point of these cases, and an escape
// cannot be silently re-encoded or normalised by an editor, a tool
// payload, or a checkout.
[<Literal>]
let private eAcute = "tok\u00E9n" // 5 chars, 6 UTF-8 bytes

[<Literal>]
let private eGrave = "tok\u00E8n" // 5 chars, 6 UTF-8 bytes, differs in one code point

[<Literal>]
let private eCombining = "toke\u0301n" // NFD form of `eAcute`: 6 chars, 7 bytes

[<Literal>]
let private cjkKey = "\u79D8\u5BC6\u9375" // 3 chars, 9 UTF-8 bytes

[<Literal>]
let private cjkLock = "\u79D8\u5BC6\u9320" // differs in the final code point

[<Literal>]
let private lockEmoji = "\U0001F510" // astral: 2 UTF-16 units, 4 UTF-8 bytes

[<Literal>]
let private keyEmoji = "\U0001F512"

/// Pairs exercised through both the string helper and the byte helper.
/// Deliberately mixes ASCII, Latin-1 accents, CJK, and an astral-plane
/// emoji (a surrogate PAIR in UTF-16 — two chars, four UTF-8 bytes),
/// because the surrogate case is where char-shaped and byte-shaped
/// reasoning diverge most sharply.
let private comparisonMatrix = [
    "", ""
    "", "a"
    "a", ""
    "token", "token"
    "token", "tokeN"
    "short", "a-considerably-longer-presented-token"
    // 5 chars either side, 6 vs 5 UTF-8 bytes.
    eAcute, "token"
    eAcute, eAcute
    eAcute, eGrave
    eAcute, eCombining
    cjkKey, cjkKey
    cjkKey, cjkLock
    // Astral emoji: 2 chars, 4 UTF-8 bytes — vs 2 ASCII chars, 2 bytes.
    lockEmoji, lockEmoji
    lockEmoji, "ab"
    "tok" + lockEmoji + "en", "tok" + lockEmoji + "en"
    "tok" + lockEmoji + "en", "tok" + keyEmoji + "en"
]

let compareTests =
    testList "ConstantTimeCompare.fixedTimeEqualsUtf8" [

        testCase "identical ASCII tokens compare equal"
        <| fun _ -> Expect.isTrue (JwtCrypto.fixedTimeEqualsUtf8 "secret-abc" "secret-abc") "identical tokens are equal"

        testCase "a single differing character compares unequal"
        <| fun _ ->
            Expect.isFalse (JwtCrypto.fixedTimeEqualsUtf8 "secret-abc" "secret-abd") "one-char difference is unequal"

        testCase "different-length tokens compare unequal without throwing"
        <| fun _ ->
            // The BCL primitive throws on mismatched SPAN lengths; the
            // helper's contract is that the caller needs no pre-check.
            Expect.isFalse (JwtCrypto.fixedTimeEqualsUtf8 "short" "much-longer-token") "unequal lengths are unequal"
            Expect.isFalse (JwtCrypto.fixedTimeEqualsUtf8 "" "any") "empty vs non-empty is unequal"
            Expect.isFalse (JwtCrypto.fixedTimeEqualsUtf8 "any" "") "non-empty vs empty is unequal"

        testCase "two empty tokens compare equal"
        <| fun _ ->
            // Degenerate but reachable (an env var set to the empty
            // string). Equal, and reached without throwing.
            Expect.isTrue (JwtCrypto.fixedTimeEqualsUtf8 "" "") "empty equals empty"

        testCase "identical non-ASCII tokens compare equal (accents, CJK, astral emoji)"
        <| fun _ ->
            Expect.isTrue
                (JwtCrypto.fixedTimeEqualsUtf8 "tok\u00E9n-\u03A9" "tok\u00E9n-\u03A9")
                "accented + Greek token is equal to itself"

            Expect.isTrue
                (JwtCrypto.fixedTimeEqualsUtf8 "\u79D8\u5BC6\u9375-42" "\u79D8\u5BC6\u9375-42")
                "CJK token is equal to itself"

            Expect.isTrue
                (JwtCrypto.fixedTimeEqualsUtf8 "tok\U0001F510en" "tok\U0001F510en")
                "token carrying a surrogate pair is equal to itself"

        testCase "non-ASCII tokens differing in one code point compare unequal"
        <| fun _ ->
            Expect.isFalse (JwtCrypto.fixedTimeEqualsUtf8 "tok\u00E9n" "tok\u00E8n") "differing accent is unequal"

            Expect.isFalse
                (JwtCrypto.fixedTimeEqualsUtf8 "\u79D8\u5BC6\u9375" "\u79D8\u5BC6\u9320")
                "differing CJK code point is unequal"

            Expect.isFalse
                (JwtCrypto.fixedTimeEqualsUtf8 "tok\U0001F510en" "tok\U0001F512en")
                "differing astral code point is unequal"

        testCase "equal CHAR length with different UTF-8 BYTE length compares unequal"
        <| fun _ ->
            // The case the char-XOR loop reasoned about wrongly: both
            // sides are five UTF-16 units, so its length pre-check passed
            // and it proceeded to compare code units. Byte-wise these are
            // six bytes against five.
            Expect.equal "tok\u00E9n".Length "token".Length "fixture precondition: equal char length"

            Expect.notEqual
                (utf8 "tok\u00E9n").Length
                (utf8 "token").Length
                "fixture precondition: unequal UTF-8 byte length"

            Expect.isFalse (JwtCrypto.fixedTimeEqualsUtf8 "tok\u00E9n" "token") "compared as bytes, these are unequal"

        testCase "an astral code point is compared as four bytes, not two UTF-16 units"
        <| fun _ ->
            // A single emoji is two chars and four bytes; two ASCII chars
            // are two chars and two bytes. Char-shaped reasoning sees a
            // length match here, byte-shaped reasoning does not.
            Expect.equal "\U0001F510".Length "ab".Length "fixture precondition: equal char length"
            Expect.equal (utf8 "\U0001F510").Length 4 "emoji is four UTF-8 bytes"
            Expect.equal (utf8 "ab").Length 2 "two ASCII chars are two UTF-8 bytes"
            Expect.isFalse (JwtCrypto.fixedTimeEqualsUtf8 "\U0001F510" "ab") "compared as bytes, these are unequal"

        testCase "the compare is over raw bytes and does NOT Unicode-normalise"
        <| fun _ ->
            // Composed (NFC, U+00E9) vs decomposed (NFD, "e" + U+0301).
            // Visually identical, different bytes — and therefore a
            // different token. Pinned so no caller assumes normalisation
            // it would then have to trust the compare to perform.
            let composed = "tok\u00E9n"
            let decomposed = "toke\u0301n"
            Expect.notEqual (utf8 composed) (utf8 decomposed) "fixture precondition: different UTF-8 bytes"

            Expect.isFalse (JwtCrypto.fixedTimeEqualsUtf8 composed decomposed) "NFC and NFD forms are different tokens"

        testCase "fixedTimeEqualsUtf8 agrees with fixedTimeEquals over the UTF-8 encoding, on every fixture"
        <| fun _ ->
            // The byte-normalisation claim itself: the string helper is
            // exactly the byte helper applied to the UTF-8 encoding. If
            // anyone reintroduces a char-wise path, this fails on the
            // fixtures where char length and byte length diverge.
            for a, b in comparisonMatrix do
                let viaString = JwtCrypto.fixedTimeEqualsUtf8 a b
                let viaBytes = JwtCrypto.fixedTimeEquals (utf8 a) (utf8 b)

                Expect.equal viaString viaBytes (sprintf "string and byte paths agree for (%A, %A)" a b)

        testCase "the fixtures agree with ordinal string equality (no false accept or reject)"
        <| fun _ ->
            // Correctness backstop: constant-time is a timing property,
            // not a licence to return the wrong answer.
            for a, b in comparisonMatrix do
                let actual = JwtCrypto.fixedTimeEqualsUtf8 a b
                let expected = String.Equals(a, b, StringComparison.Ordinal)

                Expect.equal actual expected (sprintf "ordinal equality for (%A, %A)" a b)
    ]

// ── Section B — implementation pin at each call site ───────────────────

let private repoRoot () =
    let assemblyDir =
        Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)

    Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

let private sourceFile (segments: string list) =
    let path = Path.Combine(repoRoot () :: segments |> List.toArray)

    if not (File.Exists path) then
        failtestf "Expected source file not found: %s" path

    File.ReadAllText path

/// The defective shape, as a pattern: an XOR fold over an INDEXED read
/// widened to `int` — `int a[i] ^^^ int b[i]`. Deliberately narrow. A
/// byte-array XOR (`int (a[i] ^^^ b[i])`) is a different, byte-correct
/// shape and is not matched; the SDK core tiers should carry neither, but
/// only the char-widening form is the defect this phase removes.
let private charXorFold =
    Regex(@"int\s+[A-Za-z_][A-Za-z0-9_']*\s*\[[^\]]+\]\s*\^\^\^", RegexOptions.Compiled)

/// Code only — everything from a `//` onward is dropped. The three sites
/// this phase fixed each carry a comment QUOTING the shape they no longer
/// use, which is exactly the note a future reader needs and exactly what
/// a naive whole-file scan would report as the defect. (Scanning the raw
/// text first is how that was found: the check went red on its own
/// explanatory comments.) A `//` inside a string literal would truncate
/// the line early, which can only cause a missed match on a line that
/// also carries the XOR — a shape no site has.
let private codeOnly (source: string) : string =
    source.Split('\n')
    |> Array.map (fun line ->
        match line.IndexOf "//" with
        | -1 -> line
        | i -> line.Substring(0, i))
    |> String.concat "\n"

let private coreTierFsFiles () =
    [ "ToolUp.Platform.Core"; "ToolUp.Platform.Server" ]
    |> List.collect (fun proj ->
        let dir = Path.Combine(repoRoot (), "src", proj)

        if Directory.Exists dir then
            Directory.EnumerateFiles(dir, "*.fs", SearchOption.AllDirectories)
            |> Seq.filter (fun p ->
                let normalised = p.Replace('\\', '/')

                not (normalised.Contains "/obj/" || normalised.Contains "/bin/"))
            |> List.ofSeq
        else
            [])

let implementationPinTests =
    testList "ConstantTimeCompare.implementation" [

        testCase "no char-widening XOR compare remains in Platform.Core or Platform.Server"
        <| fun _ ->
            let files = coreTierFsFiles ()
            Expect.isGreaterThan (List.length files) 100 "sanity: the source scan found the SDK core tiers"

            let offenders =
                files
                |> List.filter (fun path -> charXorFold.IsMatch(codeOnly (File.ReadAllText path)))
                |> List.map (fun path -> path.Substring(repoRoot().Length).Replace('\\', '/'))

            Expect.isEmpty
                offenders
                "a `int x[i] ^^^ int y[i]` fold compares UTF-16 code units — route the site through JwtCrypto.fixedTimeEqualsUtf8 instead"

        testCase "the encryption-admin token gate calls the sanctioned helper"
        <| fun _ ->
            let source =
                sourceFile [ "src"; "ToolUp.Platform.Server"; "Server"; "EncryptionAdminHandler.fs" ]

            Expect.stringContains
                source
                "JwtCrypto.fixedTimeEqualsUtf8 envToken headerToken"
                "the admin-token compare is the shared byte-correct helper"

        testCase "the smoke-token gate calls the sanctioned helper"
        <| fun _ ->
            let source =
                sourceFile [ "src"; "ToolUp.Platform.Server"; "Server"; "SmokeTestHandler.fs" ]

            Expect.stringContains
                source
                "JwtCrypto.fixedTimeEqualsUtf8 envToken headerToken"
                "the smoke-token compare is the shared byte-correct helper"

        testCase "the CSRF double-submit compare calls the sanctioned helper"
        <| fun _ ->
            let source =
                sourceFile [ "src"; "ToolUp.Platform.Server"; "Server"; "CsrfMiddleware.fs" ]

            Expect.stringContains
                source
                "JwtCrypto.fixedTimeEqualsUtf8"
                "the CSRF header/cookie compare is the shared byte-correct helper"

            Expect.isFalse
                (source.Contains "CryptographicOperations.FixedTimeEquals")
                "the private BCL copy is gone — the call reaches the primitive through JwtCrypto"
    ]

// ── Section C — the admin-token failure throttle ───────────────────────

let private t0 = DateTime(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc)
let private window = TimeSpan.FromMinutes 5.0

let throttleTests =
    testList "ConstantTimeCompare.TokenAttemptThrottle" [

        testCase "failures below the cap do not throttle; reaching the cap does"
        <| fun _ ->
            let throttle = TokenAttemptThrottle(maxFailures = 5, window = window)
            Expect.isFalse (throttle.IsThrottled("1.2.3.4", t0)) "a key with no failures is not throttled"

            for i in 1..4 do
                let count = throttle.RecordFailure("1.2.3.4", t0)
                Expect.equal count i "each failure increments by exactly one"
                Expect.isFalse (throttle.IsThrottled("1.2.3.4", t0)) "below the cap, still admitted"

            Expect.equal (throttle.RecordFailure("1.2.3.4", t0)) 5 "fifth failure reaches the cap"
            Expect.isTrue (throttle.IsThrottled("1.2.3.4", t0)) "at the cap, throttled"

        testCase "concurrent failures on one key are all counted — the cap cannot be raced past"
        <| fun _ ->
            // The invariant that makes the cap enforceable: no failure is
            // lost to a compare-exchange race, so N racing attempts leave
            // the counter at N and the key throttled. (Attempts already
            // in flight when the cap is reached are inherently admitted —
            // the window is a fixed counter, not a mutual exclusion.)
            let throttle = TokenAttemptThrottle(maxFailures = 5, window = window)
            let attempts = 500

            Parallel.For(0, attempts, Action<int>(fun _ -> throttle.RecordFailure("10.0.0.1", t0) |> ignore))
            |> ignore

            Expect.equal
                (throttle.FailureCount("10.0.0.1", t0))
                attempts
                "every concurrent failure was counted — no lost increments"

            Expect.isTrue (throttle.IsThrottled("10.0.0.1", t0)) "the key is throttled"

        testCase "concurrent failures across many keys stay isolated"
        <| fun _ ->
            let throttle = TokenAttemptThrottle(maxFailures = 3, window = window)

            Parallel.For(
                0,
                400,
                Action<int>(fun i ->
                    let key = sprintf "10.0.0.%d" (i % 8)
                    throttle.RecordFailure(key, t0) |> ignore)
            )
            |> ignore

            for k in 0..7 do
                Expect.equal
                    (throttle.FailureCount(sprintf "10.0.0.%d" k, t0))
                    50
                    "each key counted only its own failures"

        testCase "the window reset is stamped with the SAME instant the expiry test used"
        <| fun _ ->
            // The Phase 467 race, made decidable. Under the old inline
            // form the expiry test and the reset stamp were two separate
            // `DateTime.UtcNow` reads, so the new window opened LATER than
            // the instant that judged the old one expired — sliding the
            // window forward by the gap between the reads. Threading one
            // instant through both halves means the reopened window is
            // stamped exactly at `reopenAt`, which is observable at its
            // far edge.
            let throttle = TokenAttemptThrottle(maxFailures = 5, window = window)
            throttle.RecordFailure("1.2.3.4", t0) |> ignore

            let reopenAt = t0 + window
            Expect.equal (throttle.RecordFailure("1.2.3.4", reopenAt)) 1 "an elapsed window reopens at 1"

            // Still inside the reopened window when measured from
            // `reopenAt` — and only if that is the stamp it carries.
            Expect.equal
                (throttle.FailureCount("1.2.3.4", reopenAt + window - TimeSpan.FromTicks 1L))
                1
                "the reopened window is stamped at the instant that judged the old one expired"

            Expect.equal
                (throttle.FailureCount("1.2.3.4", reopenAt + window))
                0
                "and it expires exactly one window after that instant"

        testCase "an elapsed window resets the count and lifts the throttle"
        <| fun _ ->
            let throttle = TokenAttemptThrottle(maxFailures = 2, window = window)
            throttle.RecordFailure("1.2.3.4", t0) |> ignore
            throttle.RecordFailure("1.2.3.4", t0) |> ignore
            Expect.isTrue (throttle.IsThrottled("1.2.3.4", t0)) "throttled inside the window"

            Expect.isFalse (throttle.IsThrottled("1.2.3.4", t0 + window)) "not throttled once the window elapses"

            Expect.equal (throttle.RecordFailure("1.2.3.4", t0 + window)) 1 "the next failure opens a fresh window"

        testCase "the window boundary is exclusive at exactly one window's age"
        <| fun _ ->
            let throttle = TokenAttemptThrottle(maxFailures = 5, window = window)
            throttle.RecordFailure("1.2.3.4", t0) |> ignore

            Expect.equal
                (throttle.FailureCount("1.2.3.4", t0 + window - TimeSpan.FromTicks 1L))
                1
                "one tick before the window ends, the failure still counts"

            Expect.equal (throttle.FailureCount("1.2.3.4", t0 + window)) 0 "at exactly one window, it has elapsed"

        testCase "reading an elapsed window does not mutate it"
        <| fun _ ->
            // `IsThrottled` / `FailureCount` are reads: a stale window
            // reads as 0 but is only re-stamped by the next
            // `RecordFailure`, so a read can never race a write.
            let throttle = TokenAttemptThrottle(maxFailures = 5, window = window)
            throttle.RecordFailure("1.2.3.4", t0) |> ignore

            Expect.equal (throttle.FailureCount("1.2.3.4", t0 + window)) 0 "elapsed window reads as 0"
            Expect.equal (throttle.FailureCount("1.2.3.4", t0)) 1 "the original window is still intact"
            Expect.equal (throttle.RecordFailure("1.2.3.4", t0)) 2 "and still accepts increments"

        testCase "failures on different keys never interfere"
        <| fun _ ->
            let throttle = TokenAttemptThrottle(maxFailures = 2, window = window)
            throttle.RecordFailure("1.2.3.4", t0) |> ignore
            throttle.RecordFailure("1.2.3.4", t0) |> ignore

            Expect.isTrue (throttle.IsThrottled("1.2.3.4", t0)) "the offending key is throttled"
            Expect.isFalse (throttle.IsThrottled("5.6.7.8", t0)) "an unrelated key is unaffected"
            Expect.equal (throttle.FailureCount("5.6.7.8", t0)) 0 "and has no recorded failures"

        testCase "Forget clears a key"
        <| fun _ ->
            let throttle = TokenAttemptThrottle(maxFailures = 1, window = window)
            throttle.RecordFailure("1.2.3.4", t0) |> ignore
            Expect.isTrue (throttle.IsThrottled("1.2.3.4", t0)) "throttled"

            throttle.Forget "1.2.3.4"
            Expect.isFalse (throttle.IsThrottled("1.2.3.4", t0)) "forgotten keys start clean"

        testCase "a nonsensical configuration is refused at construction"
        <| fun _ ->
            Expect.throwsT<ArgumentException>
                (fun () -> TokenAttemptThrottle(0, window) |> ignore)
                "a cap below one would throttle every caller immediately"

            Expect.throwsT<ArgumentException>
                (fun () -> TokenAttemptThrottle(5, TimeSpan.Zero) |> ignore)
                "a zero-length window never contains any failure"
    ]

let tests =
    testList "ConstantTimeCompareTests" [ compareTests; implementationPinTests; throttleTests ]