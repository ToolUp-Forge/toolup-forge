module ToolUp.AI.Wire.Tests.JsonValueTests

open Expecto
open ToolUp.AI.Wire

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
            test "tryField / asString / asInt navigate an object" {
                let v = jobj [ "name", jstr "Ada"; "age", jint 36 ]
                Expect.equal (JsonValue.tryField "name" v |> Option.bind JsonValue.asString) (Some "Ada") "name"
                Expect.equal (JsonValue.tryField "age" v |> Option.bind JsonValue.asInt) (Some 36) "age"
                Expect.isNone (JsonValue.tryField "missing" v) "absent field → None"
            }

            test "accessors return None on type mismatch rather than throwing" {
                Expect.isNone (JsonValue.asString (jint 1)) "asString on a number"
                Expect.isNone (JsonValue.asInt (jstr "x")) "asInt on a string"
                Expect.isNone (JsonValue.asArray jnull) "asArray on null"
                Expect.isNone (JsonValue.tryField "k" (jarr [])) "tryField on an array"
            }

            test "asInt rejects non-integral and out-of-range numbers" {
                Expect.equal (JsonValue.asInt (jnum 7.0)) (Some 7) "integral float → Some"
                Expect.isNone (JsonValue.asInt (jnum 1.5)) "non-integral → None"
                Expect.isNone (JsonValue.asInt (jnum 1e30)) "out of Int32 range → None"
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