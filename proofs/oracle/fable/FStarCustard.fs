// Derived by proofs/check.ps1 from the FStarCustard.fs Custard wrote beside the
// extractions (Phase 957). Do not edit: the leg re-derives and byte-holds it.
//
// The Fable host's stand-in for Custard's F# support library. The generated file
// does not transpile (128-bit integer arithmetic, console writers, exit, a strict
// UTF-8 decoder), and the Elmish extractions this host compiles reach only Prims,
// so this is that block, verbatim, and nothing else: an extraction that came to
// need another name from the support library fails to compile here, naming it.
module FStarCustard

module Prims =
  let op_Plus (x : bigint) (y : bigint) : bigint = x + y
  let op_Minus (x : bigint) (y : bigint) : bigint = x - y
  let op_Star (x : bigint) (y : bigint) : bigint = x * y
  let op_Slash (x : bigint) (y : bigint) : bigint = x / y
  let op_Percent (x : bigint) (y : bigint) : bigint = x % y
  let op_Minus_Minus (x : bigint) : bigint = -x
  let op_Less (x : bigint) (y : bigint) : bool = x < y
  let op_Less_Equals (x : bigint) (y : bigint) : bool = x <= y
  let op_Greater (x : bigint) (y : bigint) : bool = x > y
  let op_Greater_Equals (x : bigint) (y : bigint) : bool = x >= y
  let abs (x : bigint) : bigint = if x < 0I then -x else x
  let pow2 (n : bigint) : bigint =
    System.Numerics.BigInteger.Pow (2I, int n)
  let strcat (x : string) (y : string) : string = x + y
  let string_of_bool (b : bool) : string = if b then "true" else "false"
  let string_of_int (x : bigint) : string = x.ToString ()
  let string_of_int8 (x : uint8) : string = x.ToString ()
  let string_of_int16 (x : uint16) : string = x.ToString ()
  let string_of_int32 (x : uint32) : string = x.ToString ()
  let string_of_int64 (x : uint64) : string = x.ToString ()
  let string_of_sint8 (x : int8) : string = x.ToString ()
  let string_of_sint16 (x : int16) : string = x.ToString ()
  let string_of_sint32 (x : int32) : string = x.ToString ()
  let string_of_sint64 (x : int64) : string = x.ToString ()
