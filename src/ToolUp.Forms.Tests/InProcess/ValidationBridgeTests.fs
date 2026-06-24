module ToolUp.Forms.Tests.InProcess.ValidationBridgeTests

open Expecto
open ToolUp.Forms.FormSchema
open ToolUp.Forms.Server

// ─── Phase 69e.F — Forms integration seam ─────────────────────────────
//
// `ValidationAttributeBridge.fieldsFromRecord` reads the SAME validation
// attributes that drive ToolUp.Remoting transport validation and emits a
// `FieldSchema` so client-side form rendering matches server-side
// enforcement. Reads family-agnostically — the tier-shared
// `ToolUp.Platform.*` mirrors a Fable API record carries are honoured
// here exactly like the server-tier family.

type private SignupInput = {
    [<ToolUp.Platform.NotEmpty>]
    [<ToolUp.Platform.MinLength 3>]
    Username: string
    [<ToolUp.Platform.Email>]
    Email: string
    [<ToolUp.Platform.Range(18.0, 120.0)>]
    Age: int
}

let private fieldByKey key (fields: FieldSchema list) =
    fields
    |> List.tryFind (fun f -> f.Key = key)
    |> Option.defaultWith (fun () -> failtestf "field %s not produced" key)

[<Tests>]
let tests =
    testList "Phase 69e.F — Forms validation-attribute bridge" [

        test "mirror-family validation attributes become FieldSchema validators" {
            let fields = ValidationAttributeBridge.fieldsFromRecord typeof<SignupInput>
            Expect.equal fields.Length 3 "one FieldSchema per record field"

            let username = fieldByKey "Username" fields
            Expect.isTrue username.Required "NotEmpty maps to Required"
            Expect.contains username.Validators (LengthRange(Some 3, None)) "MinLength 3 maps to a LengthRange"

            let email = fieldByKey "Email" fields

            Expect.isTrue
                (email.Validators
                 |> List.exists (fun v ->
                     match v with
                     | Regex(_, Some "email") -> true
                     | _ -> false))
                "Email maps to a Regex validator tagged 'email'"

            let age = fieldByKey "Age" fields
            Expect.equal age.Kind (NumberField(Some 18.0, Some 120.0)) "Range maps to NumberField bounds"

            Expect.contains
                age.Validators
                (NumberRange(Some 18.0, Some 120.0))
                "Range also surfaces as a NumberRange validator"
        }

        test "a non-record type yields no fields" {
            Expect.isEmpty (ValidationAttributeBridge.fieldsFromRecord typeof<int>) "primitive in, nothing out"
        }
    ]