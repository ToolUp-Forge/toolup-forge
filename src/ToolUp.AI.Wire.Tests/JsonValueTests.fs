module ToolUp.AI.Wire.Tests.JsonValueTests

open Expecto
open ToolUp.AI.Wire
open ToolUp.Remoting.Json

let tests =
    testList "ToolUp.AI.Wire" [
        testList "serialize (canonical, byte-stable)" [
            for name, value, golden in WireFixtures.fixtures ->
                test (sprintf "serialize %s" name) {
                    Expect.equal (JsonHost.serialize value) golden "canonical serialization"
                }
        ]

        testList "round-trip (serialize ∘ parse = id over the golden bytes)" [
            for name, _, golden in WireFixtures.fixtures ->
                test (sprintf "round-trip %s" name) {
                    match JsonHost.parse golden with
                    | Some v -> Expect.equal (JsonHost.serialize v) golden "serialize ∘ parse identity"
                    | None -> failtestf "parse returned None for %s" golden
                }
        ]

        testList "accessors are total (never throw)" [
            test "tryMember / asString / asInt navigate an object" {
                let v = jobj [ "name", jstr "Ada"; "age", jint 36 ]
                Expect.equal (JsonValue.tryMember "name" v |> Option.bind JsonValue.asString) (Some "Ada") "name"
                Expect.equal (JsonValue.tryMember "age" v |> Option.bind JsonValue.asInt) (Some 36) "age"
                Expect.isNone (JsonValue.tryMember "missing" v) "absent field → None"
            }

            test "accessors return None on type mismatch rather than throwing" {
                Expect.isNone (JsonValue.asString (jint 1)) "asString on a number"
                Expect.isNone (JsonValue.asInt (jstr "x")) "asInt on a string"
                Expect.isNone (JsonValue.asArray jnull) "asArray on null"
                Expect.isNone (JsonValue.tryMember "k" (jarr [])) "tryMember on an array"
            }

            test "asInt rejects non-integral and out-of-range numbers" {
                Expect.equal (JsonValue.asInt (jnum 7.0)) (Some 7) "integral float → Some"
                Expect.isNone (JsonValue.asInt (jnum 1.5)) "non-integral → None"
                Expect.isNone (JsonValue.asInt (jnum 1e30)) "out of Int32 range → None"
            }

            test "numeric accessors parse the token on demand, exactly" {
                let big = JsonValue.Number "9007199254740993"
                Expect.equal (JsonValue.asInt64 big) (Some 9007199254740993L) "int64 past 2^53"
                Expect.isNone (JsonValue.asInt big) "beyond Int32 → None"
                Expect.equal (JsonValue.asNumberText big) (Some "9007199254740993") "the token as written"
                Expect.equal (JsonValue.asInt (JsonValue.Number "7.0")) (Some 7) "integral in value → Some"
                Expect.equal (JsonValue.asInt64 (JsonValue.Number "1e3")) (Some 1000L) "exponent form → Some"

                Expect.equal
                    (JsonValue.asDecimal (JsonValue.Number "12345678901234567.89"))
                    (Some 12345678901234567.89m)
                    "decimal exactly"

                Expect.equal (JsonValue.asFloat (JsonValue.Number "0.1")) (Some 0.1) "float"
                Expect.equal (JsonHost.serialize (jint64 9007199254740993L)) "9007199254740993" "jint64 exact"
                Expect.equal (JsonHost.serialize (jdecimal 0.10m)) "0.10" "jdecimal keeps its scale"
                Expect.equal (JsonHost.serialize (jnum nan)) "null" "NaN is null, as before"
            }

            test "tryItem indexes arrays safely" {
                let arr = jarr [ jstr "a"; jstr "b" ]
                Expect.equal (JsonValue.tryItem 1 arr |> Option.bind JsonValue.asString) (Some "b") "in range"
                Expect.isNone (JsonValue.tryItem 5 arr) "out of range → None"
                Expect.isNone (JsonValue.tryItem -1 arr) "negative → None"
            }
        ]

        testList "parse rejects malformed input" [
            test "malformed JSON → None, not an exception" {
                Expect.isNone (JsonHost.parse "{not json") "truncated object"
                Expect.isNone (JsonHost.parse "") "empty string"
                Expect.isNone (JsonHost.parse "[1,2,") "truncated array"
            }
        ]
    ]