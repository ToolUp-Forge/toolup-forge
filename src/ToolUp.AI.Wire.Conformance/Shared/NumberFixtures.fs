// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The number fixtures at the precision boundary, run on both
/// hosts. Two families, and the second is the reason the first exists.
module ToolUp.AI.Wire.Conformance.NumberFixtures

open ToolUp.AI.Wire
open ToolUp.Remoting.Json

/// `(name, value built through the builders, wire bytes)`. Every number here
/// is one a double represents exactly, and every golden is the text the
/// float-backed writer emitted for it before that model was retired (pinned
/// against that writer first, then held against its successor): the
/// wire bytes of such a number did not move.
let builtPairs: (string * JsonValue * string) list = [
    "int32 max", jint 2147483647, "2147483647"
    "int32 min", jint -2147483648, "-2147483648"
    "zero", jint 0, "0"
    "integral float", jnum 42.0, "42"
    "negative integral float", jnum -7.0, "-7"
    "largest integral below 1e15", jnum 999999999999999.0, "999999999999999"
    "half", jnum 0.5, "0.5"
    "negative fraction", jnum -2.25, "-2.25"
    "three and a quarter", jnum 3.25, "3.25"
    "shortest round-trip of 0.1", jnum 0.1, "0.1"
    "a usage block",
    jobj [ "input_tokens", jint 1234; "output_tokens", jint 56; "temperature", jnum 0.7 ],
    "{\"input_tokens\":1234,\"output_tokens\":56,\"temperature\":0.7}"
]

/// Wire texts that must survive `JsonHost.parse` then `JsonHost.serialize`
/// byte for byte. Each sits at or past a boundary a double cannot hold: the
/// first integer above 2^53, the 64-bit limits, a decimal written to thirty
/// places, an integer wider than any machine word, an exponent past a
/// double's range. A large id or a money amount in a tool call is exactly
/// this shape.
let roundTrips: (string * string) list = [
    "2^53 + 1", "9007199254740993"
    "-(2^53 + 1)", "-9007199254740993"
    "int64 max", "9223372036854775807"
    "int64 min", "-9223372036854775808"
    "uint64 max", "18446744073709551615"
    "0.1 to thirty places", "0.100000000000000000000000000000"
    "pi to thirty places", "3.141592653589793238462643383279"
    "thirty-digit integer", "123456789012345678901234567890"
    "exponent past a double", "1e400"
    "integral in value, not in form", "1.0"
    "negative zero", "-0"
    "upper-case exponent", "1E+20"
    "money in a tool call",
    "{\"amount\":12345678901234567.89,\"id\":9007199254740993,\"rate\":0.100000000000000000000000000000}"
]