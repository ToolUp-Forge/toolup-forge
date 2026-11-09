// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The one writer's string escaping, run on both hosts: the run-copy scan
/// must emit exactly the bytes a character-at-a-time escape would, wherever
/// the escapes fall. Control characters are built from their code, so this
/// source carries no raw control byte.
module ToolUp.AI.Wire.Conformance.StringFixtures

let private c (code: int) = string (char code)

/// `(name, string, its JSON literal)`.
let cases: (string * string * string) list = [
    "empty", "", "\"\""
    "clean", "hello, world", "\"hello, world\""
    "escape first", "\"abc", "\"\\\"abc\""
    "escape last", "abc\\", "\"abc\\\\\""
    "escape only", "\n", "\"\\n\""
    "runs between escapes", "ab\"cd\\ef\ngh", "\"ab\\\"cd\\\\ef\\ngh\""
    "adjacent escapes", "\"\"\\\\", "\"\\\"\\\"\\\\\\\\\""
    "short forms", c 8 + c 12 + c 10 + c 13 + c 9, "\"\\b\\f\\n\\r\\t\""
    "other controls", c 0 + "x" + c 1 + c 31, "\"\\u0000x\\u0001\\u001f\""
    "non-ASCII passes through", "café ☕", "\"café ☕\""
    "surrogate pair passes through", "a\U0001F600b", "\"a\U0001F600b\""
    "lone high surrogate passes through", "a" + c 0xD800 + "b", "\"a" + c 0xD800 + "b\""
    "lone low surrogate at the end", "a" + c 0xDC00, "\"a" + c 0xDC00 + "\""
    "delete is not a control", "a" + c 0x7F, "\"a" + c 0x7F + "\""
]