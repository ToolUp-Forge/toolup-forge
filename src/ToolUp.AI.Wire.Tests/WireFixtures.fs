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
// ─── Phase 508 — the rich tool-schema pass-through fixture ────────
//
// Phase 508 let a tool parameter declare a nested object, an array or a
// closed enum instead of a bare type name, which put a materially bigger
// JSON Schema on the wire than any mapper had carried before. Each
// mapper embeds the schema rather than re-flattening it, so what has to
// be pinned is that the embedded document survives the round trip
// through each mapper's own JSON host BYTE for byte — member order
// included, because a mapper that sorted keys or re-encoded escapes
// would still emit valid JSON and would still parse, and nothing else
// would notice.
//
// It lives here rather than in one mapper's fixtures because all three
// take it, and both hosts compile this file first.

/// The exact `AIProviderToolDef.InputSchema` the tool registry renders
/// for a declaration carrying a nested object (with an enum member and
/// an optional integer member) plus an array-of-enum parameter.
///
/// Hand-authored here and asserted equal to the renderer's output by the
/// SDK-side pack, so the two cannot drift: this pack cannot reference
/// the Core tier that owns the renderer, and a golden copied from the
/// thing it is checking proves nothing anyway.
let nestedToolInputSchema =
    """{"type":"object","properties":{"filter":{"type":"object","description":"Row filter.","properties":{"metric":{"type":"string","description":"Metric.","enum":["revenue","units"]},"weeks":{"type":"integer"}},"required":["metric"]},"units":{"type":"array","description":"Units.","items":{"type":"string","enum":["metric","imperial"]}}},"required":["filter"]}"""