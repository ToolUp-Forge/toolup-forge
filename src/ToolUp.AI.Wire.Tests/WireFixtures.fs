module ToolUp.AI.Wire.Tests.WireFixtures

open ToolUp.AI.Wire

/// `(name, value, canonical serialization)` triples shared by the .NET
/// Expecto pack and the Fable smoke. Both hosts compile this same file and
/// assert `JsonHost.serialize value = golden`, so a green run on each host
/// proves cross-host byte-parity transitively: both emit the identical
/// golden bytes for every fixture.
///
/// Fixtures stay within the numeric range and key shapes where the property
/// is unambiguous (integral numbers + simple decimals; no integer-like
/// object keys, which JS would reorder). The full numeric / escaping domain
/// is hardened by the later parity gate.
let fixtures: (string * JsonValue * string) list = [
    "null", jnull, "null"
    "true", jbool true, "true"
    "false", jbool false, "false"
    "int", jint 42, "42"
    "negInt", jint -7, "-7"
    "zero", jint 0, "0"
    "decimal", jnum 1.5, "1.5"
    "negDecimal", jnum -2.25, "-2.25"
    "string", jstr "hello", "\"hello\""
    // a: quote, backslash, newline, tab — every short escape exercised.
    "stringEscapes", jstr "a\"b\\c\n\t", "\"a\\\"b\\\\c\\n\\t\""
    // non-ASCII (BMP) emitted literally, never \u-escaped.
    "unicode", jstr "café ☕", "\"café ☕\""
    // a bare C0 control character (U+0001) takes the 6-char \u form. Built
    // via `char 1` so the source carries no literal control byte.
    "controlChar", jstr (string (char 1)), "\"\\u0001\""
    "emptyArray", jarr [], "[]"
    "emptyObject", jobj [], "{}"
    "array", jarr [ jint 1; jint 2; jint 3 ], "[1,2,3]"
    "mixedArray", jarr [ jnull; jbool true; jstr "x" ], "[null,true,\"x\"]"
    // Object members must emit in build order, NOT sorted — the
    // byte-stable-key-order property the parity gate depends on.
    "keyOrder", jobj [ "z", jint 1; "a", jint 2; "m", jint 3 ], "{\"z\":1,\"a\":2,\"m\":3}"
    "nested",
    jobj [
        "model", jstr "x"
        "messages", jarr [ jobj [ "role", jstr "user"; "content", jstr "hi" ] ]
        "stream", jbool true
        "n", jint 1
    ],
    "{\"model\":\"x\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],\"stream\":true,\"n\":1}"
]