module ToolUp.Platform.Tests.InProcess.ValidationTests

open System
open Expecto
open ToolUp.Remoting.Server

// ─── Phase 69e — typed validation on input records ────────────────────
//
// Contract tests for the validation engine residuals:
//   * per-attribute predicate evaluation + multi-attribute collect-then-
//     emit (every violation from one bad input in a single pass);
//   * nested-record traversal (dotted path) + list-element traversal
//     (indexed path) + option-of-record traversal — the v0 engine was
//     top-level-only;
//   * family-agnostic recognition — the tier-shared `ToolUp.Platform.*`
//     mirrors that Fable API records carry are honoured identically to
//     the server-tier family (pre-69e residual: the mirrors were dead —
//     validation on a Core API record silently never fired);
//   * the `[<Custom>]` IFieldValidator escape hatch + IValidationContext
//     plumbing (subject id reaches the validator);
//   * the new MinValue / MaxValue / Uri / Range attributes;
//   * classify recognises a method whose validations live ONLY in a
//     nested record.

// ── Fixtures ────────────────────────────────────────────────────────

type private Address = {
    [<NotEmpty>]
    [<MaxLength 8>]
    Postcode: string
}

type private LineItem = {
    [<NotEmpty>]
    Sku: string
    [<MinValue 1.0>]
    Qty: int
}

type private OrderInput = {
    [<MinLength 3>]
    Name: string
    [<Email>]
    Contact: string
    Address: Address
    Lines: LineItem list
    Billing: Address option
}

type private OrderApi = {
    PlaceOrder: OrderInput -> Async<int>
    NoValidation: int -> Async<int>
}

/// Validations live ONLY in a nested record — classify must still
/// recognise the method (the recursive `recordHasValidations`).
type private NestedOnlyInput = { Where: Address }
type private NestedOnlyApi = { Go: NestedOnlyInput -> Async<int> }

type private MustEqualHi() =
    interface IFieldValidator with
        member _.Validate(value, ctx) =
            match value with
            | :? string as s when s <> "hi" -> Some(sprintf "expected 'hi' (subject %s)" ctx.SubjectId)
            | _ -> None

type private CustomInput = {
    [<Custom(typeof<MustEqualHi>)>]
    Greeting: string
}

/// Mirror-family input — the Fable-safe `ToolUp.Platform.*` attributes.
type private MirrorInput = {
    [<ToolUp.Platform.MinLength 3>]
    [<ToolUp.Platform.Email>]
    Field: string
}

let private paths (violations: FieldViolation list) =
    violations |> List.map (fun v -> v.Path) |> List.sort

[<Tests>]
let tests =
    testList "Phase 69e — typed validation" [

        test "a single bad field reports one violation with its path and code" {
            let input = {
                Name = "ab"
                Contact = "ok@example.com"
                Address = { Postcode = "AB1 2CD" }
                Lines = []
                Billing = None
            }

            let violations = Validation.evaluate ValidationContext.none typeof<OrderInput> input
            Expect.equal (paths violations) [ "Name" ] "only the too-short Name violates"
            Expect.equal violations.Head.Code "MinLength" "code is the attribute name minus suffix"
        }

        test "collect-then-emit: every violation across the tree in one pass, with dotted/indexed paths" {
            let input = {
                Name = "ab" // MinLength 3
                Contact = "nope" // Email
                Address = { Postcode = "" } // NotEmpty
                Lines = [ { Sku = ""; Qty = 0 } ] // NotEmpty + MinValue 1
                Billing = Some { Postcode = "TOOLONGPOSTCODE" } // MaxLength 8
            }

            let violations = Validation.evaluate ValidationContext.none typeof<OrderInput> input

            Expect.equal
                (paths violations)
                [ "Address.Postcode"; "Billing.Postcode"; "Contact"; "Lines[0].Qty"; "Lines[0].Sku"; "Name" ]
                "nested record (Address.Postcode), list element (Lines[0].*), option record (Billing.Postcode), and \
                 top-level fields all surface in one collect-then-emit pass"
        }

        test "list-element paths carry the element index" {
            let input = {
                Name = "valid"
                Contact = "ok@example.com"
                Address = { Postcode = "AB1 2CD" }
                Lines = [ { Sku = "ok"; Qty = 1 }; { Sku = ""; Qty = 0 } ]
                Billing = None
            }

            let violations = Validation.evaluate ValidationContext.none typeof<OrderInput> input
            Expect.equal (paths violations) [ "Lines[1].Qty"; "Lines[1].Sku" ] "only the second line element violates"
        }

        test "classify recognises a method whose validations are nested-only" {
            let cls = Validation.classify typeof<OrderApi>
            Expect.isTrue (cls.ContainsKey "PlaceOrder") "PlaceOrder's OrderInput has validations"
            Expect.isFalse (cls.ContainsKey "NoValidation") "an int-input method has none"

            let nested = Validation.classify typeof<NestedOnlyApi>
            Expect.isTrue (nested.ContainsKey "Go") "Go's input validates only through its nested Address — must still classify"
        }

        test "mirror-family attributes are recognised identically (the dead-mirror fix)" {
            let bad = { Field = "x" } // too short AND not an email
            let violations = Validation.evaluate ValidationContext.none typeof<MirrorInput> bad

            Expect.equal (paths violations) [ "Field"; "Field" ] "both the MinLength and Email mirrors fire"
            Expect.isTrue (violations |> List.exists (fun v -> v.Code = "MinLength")) "the mirror MinLength normalised to the server-tier rule"
            Expect.isTrue (violations |> List.exists (fun v -> v.Code = "Email")) "the mirror Email normalised to the server-tier rule"
        }

        test "[<Custom>] IFieldValidator runs with the request context" {
            let ctx =
                { new IValidationContext with
                    member _.SubjectId = "user:bob"
                    member _.CorrelationId = Some "corr-1" }

            let violations = Validation.evaluate ctx typeof<CustomInput> { Greeting = "bye" }
            Expect.equal (paths violations) [ "Greeting" ] "the custom validator rejects"
            Expect.equal violations.Head.Code "Custom" "code is Custom"
            Expect.stringContains violations.Head.Message "user:bob" "the validator saw the subject id from the context"

            let ok = Validation.evaluate ctx typeof<CustomInput> { Greeting = "hi" }
            Expect.isEmpty ok "a passing custom validator yields no violation"
        }

        test "new attributes: Range / MinValue / MaxValue / Uri" {
            Expect.isSome (MinValueAttribute(1.0).Validate(box 0)) "0 is below min 1"
            Expect.isNone (MinValueAttribute(1.0).Validate(box 5)) "5 is above min 1"
            Expect.isSome (MaxValueAttribute(10.0).Validate(box 11)) "11 is above max 10"
            Expect.isNone (MaxValueAttribute(10.0).Validate(box 3)) "3 is within max 10"
            Expect.isSome (RangeAttribute(1.0, 5.0).Validate(box 9)) "9 is outside [1,5]"
            Expect.isNone (RangeAttribute(1.0, 5.0).Validate(box 3)) "3 is within [1,5]"
            Expect.isSome (UriAttribute().Validate(box "not a uri")) "plain text is not an absolute URI"
            Expect.isNone (UriAttribute().Validate(box "https://example.com/x")) "an absolute URL passes"
        }
    ]
