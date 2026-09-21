module RemotingDecode
type opt<'a> =
| ONone
| OSome of 'a


let uu___is_ONone = (fun ( projectee  :  opt<'a> ) -> (match (projectee) with
| ONone -> begin
     true
     end
| uu___ -> begin
     false
     end))


let uu___is_OSome = (fun ( projectee  :  opt<'a> ) -> (match (projectee) with
| OSome (item) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__OSome__item__item = (fun ( projectee  :  opt<'a> ) -> (match (projectee) with
| OSome (item) -> begin
     item
     end))

type pair<'a, 'b> =
| Pair of 'a * 'b


let uu___is_Pair = (fun ( projectee  :  pair<'a, 'b> ) -> true)


let __proj__Pair__item__first = (fun ( projectee  :  pair<'a, 'b> ) -> (match (projectee) with
| Pair (first, second) -> begin
     first
     end))


let __proj__Pair__item__second = (fun ( projectee  :  pair<'a, 'b> ) -> (match (projectee) with
| Pair (first, second) -> begin
     second
     end))

type triple<'a, 'b, 'c> =
| Triple of 'a * 'b * 'c


let uu___is_Triple = (fun ( projectee  :  triple<'a, 'b, 'c> ) -> true)


let __proj__Triple__item__first = (fun ( projectee  :  triple<'a, 'b, 'c> ) -> (match (projectee) with
| Triple (first, second, third) -> begin
     first
     end))


let __proj__Triple__item__second = (fun ( projectee  :  triple<'a, 'b, 'c> ) -> (match (projectee) with
| Triple (first, second, third) -> begin
     second
     end))


let __proj__Triple__item__third = (fun ( projectee  :  triple<'a, 'b, 'c> ) -> (match (projectee) with
| Triple (first, second, third) -> begin
     third
     end))

type quad<'a, 'b, 'c, 'd> =
| Quad of 'a * 'b * 'c * 'd


let uu___is_Quad = (fun ( projectee  :  quad<'a, 'b, 'c, 'd> ) -> true)


let __proj__Quad__item__first = (fun ( projectee  :  quad<'a, 'b, 'c, 'd> ) -> (match (projectee) with
| Quad (first, second, third, fourth) -> begin
     first
     end))


let __proj__Quad__item__second = (fun ( projectee  :  quad<'a, 'b, 'c, 'd> ) -> (match (projectee) with
| Quad (first, second, third, fourth) -> begin
     second
     end))


let __proj__Quad__item__third = (fun ( projectee  :  quad<'a, 'b, 'c, 'd> ) -> (match (projectee) with
| Quad (first, second, third, fourth) -> begin
     third
     end))


let __proj__Quad__item__fourth = (fun ( projectee  :  quad<'a, 'b, 'c, 'd> ) -> (match (projectee) with
| Quad (first, second, third, fourth) -> begin
     fourth
     end))

type refusal = {path : Prims.list<Prims.string>; expected : Prims.string; found : Prims.string}


let __proj__Mkrefusal__item__path : refusal  ->  Prims.list<Prims.string> = (fun ( projectee  :  refusal ) -> (match (projectee) with
| {path = path; expected = expected; found = found} -> begin
     path
     end))


let __proj__Mkrefusal__item__expected : refusal  ->  Prims.string = (fun ( projectee  :  refusal ) -> (match (projectee) with
| {path = path; expected = expected; found = found} -> begin
     expected
     end))


let __proj__Mkrefusal__item__found : refusal  ->  Prims.string = (fun ( projectee  :  refusal ) -> (match (projectee) with
| {path = path; expected = expected; found = found} -> begin
     found
     end))

type outcome<'a> =
| Accepted of 'a
| Refused of refusal


let uu___is_Accepted = (fun ( projectee  :  outcome<'a> ) -> (match (projectee) with
| Accepted (value) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__Accepted__item__value = (fun ( projectee  :  outcome<'a> ) -> (match (projectee) with
| Accepted (value) -> begin
     value
     end))


let uu___is_Refused = (fun ( projectee  :  outcome<'a> ) -> (match (projectee) with
| Refused (error) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__Refused__item__error = (fun ( projectee  :  outcome<'a> ) -> (match (projectee) with
| Refused (error) -> begin
     error
     end))

type integer_width =
| Fixnum
| Bits8
| Bits16
| Bits32
| Bits64


let uu___is_Fixnum : integer_width  ->  Prims.bool = (fun ( projectee  :  integer_width ) -> (match (projectee) with
| Fixnum -> begin
     true
     end
| uu___ -> begin
     false
     end))


let uu___is_Bits8 : integer_width  ->  Prims.bool = (fun ( projectee  :  integer_width ) -> (match (projectee) with
| Bits8 -> begin
     true
     end
| uu___ -> begin
     false
     end))


let uu___is_Bits16 : integer_width  ->  Prims.bool = (fun ( projectee  :  integer_width ) -> (match (projectee) with
| Bits16 -> begin
     true
     end
| uu___ -> begin
     false
     end))


let uu___is_Bits32 : integer_width  ->  Prims.bool = (fun ( projectee  :  integer_width ) -> (match (projectee) with
| Bits32 -> begin
     true
     end
| uu___ -> begin
     false
     end))


let uu___is_Bits64 : integer_width  ->  Prims.bool = (fun ( projectee  :  integer_width ) -> (match (projectee) with
| Bits64 -> begin
     true
     end
| uu___ -> begin
     false
     end))


let width_bits : integer_width  ->  Prims.nat = (fun ( w  :  integer_width ) -> (match (w) with
| Fixnum -> begin
     (Prims.parse_int "8")
     end
| Bits8 -> begin
     (Prims.parse_int "8")
     end
| Bits16 -> begin
     (Prims.parse_int "16")
     end
| Bits32 -> begin
     (Prims.parse_int "32")
     end
| Bits64 -> begin
     (Prims.parse_int "64")
     end))


let width_label : integer_width  ->  Prims.string = (fun ( w  :  integer_width ) -> (match (w) with
| Fixnum -> begin
     "fixnum"
     end
| Bits8 -> begin
     "8-bit"
     end
| Bits16 -> begin
     "16-bit"
     end
| Bits32 -> begin
     "32-bit"
     end
| Bits64 -> begin
     "64-bit"
     end))

type float_width =
| Single
| Double


let uu___is_Single : float_width  ->  Prims.bool = (fun ( projectee  :  float_width ) -> (match (projectee) with
| Single -> begin
     true
     end
| uu___ -> begin
     false
     end))


let uu___is_Double : float_width  ->  Prims.bool = (fun ( projectee  :  float_width ) -> (match (projectee) with
| Double -> begin
     true
     end
| uu___ -> begin
     false
     end))

type value<'raw, 'flt> =
| VNil
| VBool of Prims.bool
| VInt of Prims.int * integer_width
| VUInt of Prims.nat * integer_width
| VFloat of 'flt * float_width * Prims.string
| VStr of Prims.string * Prims.nat
| VBin of 'raw * Prims.nat
| VArr of Prims.list<value<'raw, 'flt>>
| VMap of Prims.list<pair<value<'raw, 'flt>, value<'raw, 'flt>>>


let uu___is_VNil = (fun ( projectee  :  value<'raw, 'flt> ) -> (match (projectee) with
| VNil -> begin
     true
     end
| uu___ -> begin
     false
     end))


let uu___is_VBool = (fun ( projectee  :  value<'raw, 'flt> ) -> (match (projectee) with
| VBool (payload) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__VBool__item__payload = (fun ( projectee  :  value<'raw, 'flt> ) -> (match (projectee) with
| VBool (payload) -> begin
     payload
     end))


let uu___is_VInt = (fun ( projectee  :  value<'raw, 'flt> ) -> (match (projectee) with
| VInt (payload, width) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__VInt__item__payload = (fun ( projectee  :  value<'raw, 'flt> ) -> (match (projectee) with
| VInt (payload, width) -> begin
     payload
     end))


let __proj__VInt__item__width = (fun ( projectee  :  value<'raw, 'flt> ) -> (match (projectee) with
| VInt (payload, width) -> begin
     width
     end))


let uu___is_VUInt = (fun ( projectee  :  value<'raw, 'flt> ) -> (match (projectee) with
| VUInt (payload, width) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__VUInt__item__payload = (fun ( projectee  :  value<'raw, 'flt> ) -> (match (projectee) with
| VUInt (payload, width) -> begin
     payload
     end))


let __proj__VUInt__item__width = (fun ( projectee  :  value<'raw, 'flt> ) -> (match (projectee) with
| VUInt (payload, width) -> begin
     width
     end))


let uu___is_VFloat = (fun ( projectee  :  value<'raw, 'flt> ) -> (match (projectee) with
| VFloat (payload, width, rendered) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__VFloat__item__payload = (fun ( projectee  :  value<'raw, 'flt> ) -> (match (projectee) with
| VFloat (payload, width, rendered) -> begin
     payload
     end))


let __proj__VFloat__item__width = (fun ( projectee  :  value<'raw, 'flt> ) -> (match (projectee) with
| VFloat (payload, width, rendered) -> begin
     width
     end))


let __proj__VFloat__item__rendered = (fun ( projectee  :  value<'raw, 'flt> ) -> (match (projectee) with
| VFloat (payload, width, rendered) -> begin
     rendered
     end))


let uu___is_VStr = (fun ( projectee  :  value<'raw, 'flt> ) -> (match (projectee) with
| VStr (payload, length) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__VStr__item__payload = (fun ( projectee  :  value<'raw, 'flt> ) -> (match (projectee) with
| VStr (payload, length) -> begin
     payload
     end))


let __proj__VStr__item__length = (fun ( projectee  :  value<'raw, 'flt> ) -> (match (projectee) with
| VStr (payload, length) -> begin
     length
     end))


let uu___is_VBin = (fun ( projectee  :  value<'raw, 'flt> ) -> (match (projectee) with
| VBin (payload, length) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__VBin__item__payload = (fun ( projectee  :  value<'raw, 'flt> ) -> (match (projectee) with
| VBin (payload, length) -> begin
     payload
     end))


let __proj__VBin__item__length = (fun ( projectee  :  value<'raw, 'flt> ) -> (match (projectee) with
| VBin (payload, length) -> begin
     length
     end))


let uu___is_VArr = (fun ( projectee  :  value<'raw, 'flt> ) -> (match (projectee) with
| VArr (items) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__VArr__item__items = (fun ( projectee  :  value<'raw, 'flt> ) -> (match (projectee) with
| VArr (items) -> begin
     items
     end))


let uu___is_VMap = (fun ( projectee  :  value<'raw, 'flt> ) -> (match (projectee) with
| VMap (entries) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__VMap__item__entries = (fun ( projectee  :  value<'raw, 'flt> ) -> (match (projectee) with
| VMap (entries) -> begin
     entries
     end))


let rec count = (fun ( xs  :  Prims.list<'a> ) -> (match (xs) with
| [] -> begin
     (Prims.parse_int "0")
     end
| (uu___)::tail -> begin
     ((Prims.parse_int "1") + (count tail))
     end))


let describe = (fun ( v  :  value<'raw, 'flt> ) -> (match (v) with
| VNil -> begin
     "nil"
     end
| VBool (true) -> begin
     "bool true"
     end
| VBool (false) -> begin
     "bool false"
     end
| VInt (n, w) -> begin
     (Prims.strcat (width_label w) (Prims.strcat " signed integer " (Prims.string_of_int n)))
     end
| VUInt (n, w) -> begin
     (Prims.strcat (width_label w) (Prims.strcat " unsigned integer " (Prims.string_of_int n)))
     end
| VFloat (uu___, Single, rendered) -> begin
     (Prims.strcat "float32 " rendered)
     end
| VFloat (uu___, Double, rendered) -> begin
     (Prims.strcat "float64 " rendered)
     end
| VStr (uu___, length) -> begin
     (Prims.strcat "string of " (Prims.strcat (Prims.string_of_int length) " character(s)"))
     end
| VBin (uu___, length) -> begin
     (Prims.strcat "bin of " (Prims.strcat (Prims.string_of_int length) " byte(s)"))
     end
| VArr (items) -> begin
     (Prims.strcat "array of " (Prims.strcat (Prims.string_of_int (count items)) " element(s)"))
     end
| VMap (entries) -> begin
     (Prims.strcat "map of " (Prims.strcat (Prims.string_of_int (count entries)) " entry(ies)"))
     end))


let signed_fits : Prims.nat  ->  Prims.nat  ->  Prims.int  ->  Prims.nat  ->  Prims.int  ->  Prims.bool = (fun ( source_bits  :  Prims.nat ) ( target_bits  :  Prims.nat ) ( lo  :  Prims.int ) ( hi  :  Prims.nat ) ( n  :  Prims.int ) ->  
if (n < (Prims.parse_int "0")) then begin
     ((lo < (Prims.parse_int "0")) && (n >= lo))
     end else begin
      
if (source_bits <= target_bits) then begin
     true
     end else begin
     (n <= hi)
     end
     end)


let unsigned_fits : Prims.nat  ->  Prims.nat  ->  Prims.nat  ->  Prims.nat  ->  Prims.bool = (fun ( source_bits  :  Prims.nat ) ( target_bits  :  Prims.nat ) ( hi  :  Prims.nat ) ( n  :  Prims.nat ) ->  
if (source_bits <= target_bits) then begin
     true
     end else begin
     (n <= hi)
     end)


let refusal_at : Prims.string  ->  Prims.string  ->  refusal = (fun ( expected  :  Prims.string ) ( found  :  Prims.string ) -> {path = []; expected = expected; found = found})


let under : Prims.string  ->  refusal  ->  refusal = (fun ( segment  :  Prims.string ) ( e  :  refusal ) -> {path = (segment)::e.path; expected = e.expected; found = e.found})


let refuse = (fun ( expected  :  Prims.string ) ( v  :  value<'raw, 'flt> ) -> Refused ((refusal_at expected (describe v))))


let refuse_with = (fun ( expected  :  Prims.string ) ( found  :  Prims.string ) -> Refused ((refusal_at expected found)))


type decoder<'raw, 'flt, 'a> = value<'raw, 'flt>  ->  outcome<'a>


let succeed = (fun ( x  :  'a ) ( uu___  :  value<'raw, 'flt> ) -> Accepted (x))


let fail = (fun ( expected  :  Prims.string ) ( v  :  value<'raw, 'flt> ) -> (refuse expected v))


let map = (fun ( f  :  'a  ->  'b ) ( d  :  decoder<'raw, 'flt, 'a> ) ( v  :  value<'raw, 'flt> ) -> (match ((d v)) with
| Accepted (x) -> begin
     Accepted ((f x))
     end
| Refused (e) -> begin
     Refused (e)
     end))


let bind = (fun ( f  :  'a  ->  decoder<'raw, 'flt, 'b> ) ( d  :  decoder<'raw, 'flt, 'a> ) ( v  :  value<'raw, 'flt> ) -> (match ((d v)) with
| Accepted (x) -> begin
     (f x v)
     end
| Refused (e) -> begin
     Refused (e)
     end))


let apply = (fun ( argument  :  decoder<'raw, 'flt, 'a> ) ( fn  :  decoder<'raw, 'flt, 'a  ->  'b> ) ( v  :  value<'raw, 'flt> ) -> (match ((fn v)) with
| Refused (e) -> begin
     Refused (e)
     end
| Accepted (f) -> begin
     (match ((argument v)) with
| Refused (e) -> begin
     Refused (e)
     end
| Accepted (x) -> begin
     Accepted ((f x))
     end)
     end))


let op_Bar_Greater_Greater = (fun ( fn  :  decoder<'raw, 'flt, 'a  ->  'b> ) ( arg  :  decoder<'raw, 'flt, 'a> ) -> (apply arg fn))


let as_bool = (fun ( v  :  value<'raw, 'flt> ) -> (match (v) with
| VBool (b) -> begin
     Accepted (b)
     end
| uu___ -> begin
     (refuse "bool" v)
     end))


let as_unit = (fun ( v  :  value<'raw, 'flt> ) -> (match (v) with
| VNil -> begin
     Accepted (())
     end
| uu___ -> begin
     (refuse "nil" v)
     end))


let as_string = (fun ( v  :  value<'raw, 'flt> ) -> (match (v) with
| VStr (text, uu___) -> begin
     Accepted (text)
     end
| uu___ -> begin
     (refuse "string" v)
     end))


let as_char_with = (fun ( pick  :  Prims.string  ->  'a ) ( v  :  value<'raw, 'flt> ) -> (match (v) with
| VStr (text, length) -> begin
      
if (Prims.op_Equals length (Prims.parse_int "1")) then begin
     Accepted ((pick text))
     end else begin
     (refuse_with "a one-character string" (Prims.strcat "string of " (Prims.strcat (Prims.string_of_int length) " character(s)")))
     end
     end
| uu___ -> begin
     (refuse "char" v)
     end))


let integer = (fun ( type_name  :  Prims.string ) ( target_bits  :  Prims.nat ) ( lo  :  Prims.int ) ( hi  :  Prims.nat ) ( of_signed  :  Prims.int  ->  'a ) ( of_unsigned  :  Prims.nat  ->  'a ) ( v  :  value<'raw, 'flt> ) -> (match (v) with
| VInt (n, width) -> begin
      
if (signed_fits (width_bits width) target_bits lo hi n) then begin
     Accepted ((of_signed n))
     end else begin
     (refuse_with type_name (Prims.strcat "out-of-range integer " (Prims.string_of_int n)))
     end
     end
| VUInt (n, width) -> begin
      
if (unsigned_fits (width_bits width) target_bits hi n) then begin
     Accepted ((of_unsigned n))
     end else begin
     (refuse_with type_name (Prims.strcat "out-of-range integer " (Prims.string_of_int n)))
     end
     end
| uu___ -> begin
     (refuse type_name v)
     end))


let keep_signed : Prims.int  ->  Prims.int = (fun ( n  :  Prims.int ) -> n)


let keep_unsigned : Prims.nat  ->  Prims.int = (fun ( n  :  Prims.nat ) -> n)


let as_int32 = (fun ( uu___  :  unit ) -> (integer "Int32" (Prims.parse_int "32") (Prims.parse_int "-2147483648") (Prims.parse_int "2147483647") keep_signed keep_unsigned))


let as_int64 = (fun ( uu___  :  unit ) -> (integer "Int64" (Prims.parse_int "64") (Prims.parse_int "-9223372036854775808") (Prims.parse_int "9223372036854775807") keep_signed keep_unsigned))


let as_int16 = (fun ( uu___  :  unit ) -> (integer "Int16" (Prims.parse_int "16") (Prims.parse_int "-32768") (Prims.parse_int "32767") keep_signed keep_unsigned))


let as_sbyte = (fun ( uu___  :  unit ) -> (integer "SByte" (Prims.parse_int "8") (Prims.parse_int "-128") (Prims.parse_int "127") keep_signed keep_unsigned))


let as_byte = (fun ( uu___  :  unit ) -> (integer "Byte" (Prims.parse_int "8") (Prims.parse_int "0") (Prims.parse_int "255") keep_signed keep_unsigned))


let as_uint16 = (fun ( uu___  :  unit ) -> (integer "UInt16" (Prims.parse_int "16") (Prims.parse_int "0") (Prims.parse_int "65535") keep_signed keep_unsigned))


let as_uint32 = (fun ( uu___  :  unit ) -> (integer "UInt32" (Prims.parse_int "32") (Prims.parse_int "0") (Prims.parse_int "4294967295") keep_signed keep_unsigned))


let as_uint64 = (fun ( uu___  :  unit ) -> (integer "UInt64" (Prims.parse_int "64") (Prims.parse_int "0") (Prims.parse_int "18446744073709551615") keep_signed keep_unsigned))


let as_time_span_with = (fun ( make  :  Prims.int  ->  'a ) -> (integer "TimeSpan" (Prims.parse_int "64") (Prims.parse_int "-9223372036854775808") (Prims.parse_int "9223372036854775807") make (fun ( n  :  Prims.nat ) -> (make n))))


let as_float = (fun ( v  :  value<'raw, 'flt> ) -> (match (v) with
| VFloat (n, uu___, uu___1) -> begin
     Accepted (n)
     end
| uu___ -> begin
     (refuse "Double" v)
     end))


let as_float32 = (fun ( v  :  value<'raw, 'flt> ) -> (match (v) with
| VFloat (n, Single, uu___) -> begin
     Accepted (n)
     end
| VFloat (uu___, Double, rendered) -> begin
     (refuse_with "Single" (Prims.strcat "float64 " (Prims.strcat rendered ", which a Single cannot carry without loss")))
     end
| uu___ -> begin
     (refuse "Single" v)
     end))


let as_bytes = (fun ( v  :  value<'raw, 'flt> ) -> (match (v) with
| VBin (payload, uu___) -> begin
     Accepted (payload)
     end
| uu___ -> begin
     (refuse "Byte[]" v)
     end))


let as_guid_with = (fun ( make  :  'raw  ->  'a ) ( v  :  value<'raw, 'flt> ) -> (match (v) with
| VBin (payload, length) -> begin
      
if (Prims.op_Equals length (Prims.parse_int "16")) then begin
     Accepted ((make payload))
     end else begin
     (refuse_with "Guid" (Prims.strcat "bin of " (Prims.strcat (Prims.string_of_int length) " byte(s), not 16")))
     end
     end
| uu___ -> begin
     (refuse "Guid" v)
     end))


let items = (fun ( v  :  value<'raw, 'flt> ) -> (match (v) with
| VArr (elements) -> begin
     Accepted (elements)
     end
| uu___ -> begin
     (refuse "array" v)
     end))


let exactly = (fun ( arity  :  Prims.nat ) ( v  :  value<'raw, 'flt> ) -> (

let expected = (Prims.strcat "an array of " (Prims.strcat (Prims.string_of_int arity) " element(s)"))
in (match (v) with
| VArr (elements) -> begin
      
if (Prims.op_Equals (count elements) arity) then begin
     Accepted (elements)
     end else begin
     (refuse_with expected (Prims.strcat "an array of " (Prims.strcat (Prims.string_of_int (count elements)) " element(s)")))
     end
     end
| uu___ -> begin
     (refuse expected v)
     end)))


let rec try_item = (fun ( position  :  Prims.nat ) ( xs  :  Prims.list<value<'raw, 'flt>> ) -> (match (xs) with
| [] -> begin
     ONone
     end
| (head)::tail -> begin
      
if (Prims.op_Equals position (Prims.parse_int "0")) then begin
     OSome (head)
     end else begin
     (try_item (position - (Prims.parse_int "1")) tail)
     end
     end))


let element_at = (fun ( position  :  Prims.nat ) ( v  :  value<'raw, 'flt> ) -> (match (v) with
| VArr (elements) -> begin
     (try_item position elements)
     end
| uu___ -> begin
     ONone
     end))


let index = (fun ( position  :  Prims.nat ) ( d  :  decoder<'raw, 'flt, 'a> ) ( v  :  value<'raw, 'flt> ) -> (

let expected = (Prims.strcat "an array with an element at index " (Prims.string_of_int position))
in (match (v) with
| VArr (elements) -> begin
     (match ((try_item position elements)) with
| OSome (element) -> begin
     (match ((d element)) with
| Accepted (x) -> begin
     Accepted (x)
     end
| Refused (e) -> begin
     Refused ((under (Prims.strcat "[" (Prims.strcat (Prims.string_of_int position) "]")) e))
     end)
     end
| ONone -> begin
     (refuse_with expected (Prims.strcat "an array of " (Prims.strcat (Prims.string_of_int (count elements)) " element(s)")))
     end)
     end
| uu___ -> begin
     (refuse expected v)
     end)))


let field = (fun ( name  :  Prims.string ) ( position  :  Prims.nat ) ( d  :  decoder<'raw, 'flt, 'a> ) ( v  :  value<'raw, 'flt> ) -> (

let expected = (Prims.strcat "a record with a `" (Prims.strcat name (Prims.strcat "` field at index " (Prims.string_of_int position))))
in (match (v) with
| VArr (elements) -> begin
     (match ((try_item position elements)) with
| OSome (element) -> begin
     (match ((d element)) with
| Accepted (x) -> begin
     Accepted (x)
     end
| Refused (e) -> begin
     Refused ((under name e))
     end)
     end
| ONone -> begin
     (refuse_with expected (Prims.strcat "an array of " (Prims.strcat (Prims.string_of_int (count elements)) " element(s)")))
     end)
     end
| uu___ -> begin
     (refuse expected v)
     end)))


let rec rev_onto = (fun ( xs  :  Prims.list<'a> ) ( acc  :  Prims.list<'a> ) -> (match (xs) with
| [] -> begin
     acc
     end
| (head)::tail -> begin
     (rev_onto tail ((head)::acc))
     end))


let rec walk_items = (fun ( element  :  decoder<'raw, 'flt, 'a> ) ( position  :  Prims.nat ) ( remaining  :  Prims.list<value<'raw, 'flt>> ) ( accumulated  :  Prims.list<'a> ) -> (match (remaining) with
| [] -> begin
     Accepted ((rev_onto accumulated []))
     end
| (head)::tail -> begin
     (match ((element head)) with
| Accepted (x) -> begin
     (walk_items element (position + (Prims.parse_int "1")) tail ((x)::accumulated))
     end
| Refused (e) -> begin
     Refused ((under (Prims.strcat "[" (Prims.strcat (Prims.string_of_int position) "]")) e))
     end)
     end))


let list_of = (fun ( element  :  decoder<'raw, 'flt, 'a> ) ( v  :  value<'raw, 'flt> ) -> (match (v) with
| VArr (elements) -> begin
     (walk_items element (Prims.parse_int "0") elements [])
     end
| uu___ -> begin
     (refuse "array" v)
     end))


let rec walk_entries = (fun ( key  :  decoder<'raw, 'flt, 'k> ) ( entry  :  decoder<'raw, 'flt, 'w> ) ( position  :  Prims.nat ) ( remaining  :  Prims.list<pair<value<'raw, 'flt>, value<'raw, 'flt>>> ) ( accumulated  :  Prims.list<pair<'k, 'w>> ) -> (match (remaining) with
| [] -> begin
     Accepted ((rev_onto accumulated []))
     end
| (Pair (k1, v))::tail -> begin
     (match ((key k1)) with
| Refused (e) -> begin
     Refused ((under (Prims.strcat "[" (Prims.strcat (Prims.string_of_int position) "].key")) e))
     end
| Accepted (decoded_key) -> begin
     (match ((entry v)) with
| Refused (e) -> begin
     Refused ((under (Prims.strcat "[" (Prims.strcat (Prims.string_of_int position) "].value")) e))
     end
| Accepted (decoded_value) -> begin
     (walk_entries key entry (position + (Prims.parse_int "1")) tail ((Pair (decoded_key, decoded_value))::accumulated))
     end)
     end)
     end))


let entries_of = (fun ( key  :  decoder<'raw, 'flt, 'k> ) ( entry  :  decoder<'raw, 'flt, 'w> ) ( v  :  value<'raw, 'flt> ) -> (match (v) with
| VMap (pairs) -> begin
     (walk_entries key entry (Prims.parse_int "0") pairs [])
     end
| uu___ -> begin
     (refuse "map" v)
     end))


let tuple_of = (fun ( arity  :  Prims.nat ) ( v  :  value<'raw, 'flt> ) -> (

let expected = (Prims.strcat "a tuple of " (Prims.strcat (Prims.string_of_int arity) " element(s)"))
in (match (v) with
| VArr (elements) -> begin
      
if (Prims.op_Equals (count elements) arity) then begin
     Accepted (())
     end else begin
     (refuse_with expected (Prims.strcat "an array of " (Prims.strcat (Prims.string_of_int (count elements)) " element(s)")))
     end
     end
| uu___ -> begin
     (refuse expected v)
     end)))


let tuple2 = (fun ( first  :  decoder<'raw, 'flt, 'a> ) ( second  :  decoder<'raw, 'flt, 'b> ) -> (bind (fun ( uu___  :  unit ) -> (op_Bar_Greater_Greater (op_Bar_Greater_Greater (succeed (fun ( x  :  'a ) ( y  :  'b ) -> Pair (x, y))) (index (Prims.parse_int "0") first)) (index (Prims.parse_int "1") second))) (tuple_of (Prims.parse_int "2"))))


let tuple3 = (fun ( first  :  decoder<'raw, 'flt, 'a> ) ( second  :  decoder<'raw, 'flt, 'b> ) ( third  :  decoder<'raw, 'flt, 'c> ) -> (bind (fun ( uu___  :  unit ) -> (op_Bar_Greater_Greater (op_Bar_Greater_Greater (op_Bar_Greater_Greater (succeed (fun ( x  :  'a ) ( y  :  'b ) ( z  :  'c ) -> Triple (x, y, z))) (index (Prims.parse_int "0") first)) (index (Prims.parse_int "1") second)) (index (Prims.parse_int "2") third))) (tuple_of (Prims.parse_int "3"))))


let tuple4 = (fun ( first  :  decoder<'raw, 'flt, 'a> ) ( second  :  decoder<'raw, 'flt, 'b> ) ( third  :  decoder<'raw, 'flt, 'c> ) ( fourth  :  decoder<'raw, 'flt, 'd> ) -> (bind (fun ( uu___  :  unit ) -> (op_Bar_Greater_Greater (op_Bar_Greater_Greater (op_Bar_Greater_Greater (op_Bar_Greater_Greater (succeed (fun ( w  :  'a ) ( x  :  'b ) ( y  :  'c ) ( z  :  'd ) -> Quad (w, x, y, z))) (index (Prims.parse_int "0") first)) (index (Prims.parse_int "1") second)) (index (Prims.parse_int "2") third)) (index (Prims.parse_int "3") fourth))) (tuple_of (Prims.parse_int "4"))))


type union_case<'raw, 'flt, 'a> = opt<value<'raw, 'flt>>  ->  outcome<'a>


let case0 = (fun ( x  :  'a ) ( carried  :  opt<value<'raw, 'flt>> ) -> (match (carried) with
| ONone -> begin
     Accepted (x)
     end
| OSome (payload) -> begin
     (refuse_with "a union case with no fields" (describe payload))
     end))


let payload = (fun ( d  :  decoder<'raw, 'flt, 'a> ) ( carried  :  opt<value<'raw, 'flt>> ) -> (match (carried) with
| OSome (v) -> begin
     (d v)
     end
| ONone -> begin
     (refuse_with "a union case carrying a payload" "a union case with no payload")
     end))


let fields = (fun ( arity  :  Prims.nat ) ( d  :  decoder<'raw, 'flt, 'a> ) ( carried  :  opt<value<'raw, 'flt>> ) -> (

let expected = (Prims.strcat "a union case carrying " (Prims.strcat (Prims.string_of_int arity) " fields"))
in (match (carried) with
| OSome (v) -> begin
     (match (v) with
| VArr (inner) -> begin
      
if (Prims.op_Equals (count inner) arity) then begin
     (d v)
     end else begin
     (refuse_with expected (Prims.strcat "an array of " (Prims.strcat (Prims.string_of_int (count inner)) " element(s)")))
     end
     end
| uu___ -> begin
     (refuse expected v)
     end)
     end
| ONone -> begin
     (refuse_with expected "a union case with no payload")
     end)))


let union = (fun ( type_name  :  Prims.string ) ( cases  :  Prims.int  ->  opt<union_case<'raw, 'flt, 'a>> ) ( v  :  value<'raw, 'flt> ) -> (

let dispatch = (fun ( tag  :  Prims.int ) ( carried  :  opt<value<'raw, 'flt>> ) -> (match ((cases tag)) with
| OSome (decode_case) -> begin
     (decode_case carried)
     end
| ONone -> begin
     (refuse_with type_name (Prims.strcat "union tag " (Prims.strcat (Prims.string_of_int tag) ", which names no case")))
     end))
in (match (v) with
| VArr ((tag)::[]) -> begin
     (match (((as_int32 ()) tag)) with
| Refused (e) -> begin
     Refused (e)
     end
| Accepted (t) -> begin
     (dispatch t ONone)
     end)
     end
| VArr ((tag)::(carried)::[]) -> begin
     (match (((as_int32 ()) tag)) with
| Refused (e) -> begin
     Refused (e)
     end
| Accepted (t) -> begin
     (dispatch t (OSome (carried)))
     end)
     end
| uu___ -> begin
     (refuse (Prims.strcat type_name " (a union term of [tag] or [tag; payload])") v)
     end)))


let rec try_pick_case = (fun ( cases  :  Prims.list<pair<Prims.string, 'a>> ) ( name  :  Prims.string ) -> (match (cases) with
| [] -> begin
     ONone
     end
| (Pair (label, case))::tail -> begin
      
if (Prims.op_Equals label name) then begin
     OSome (case)
     end else begin
     (try_pick_case tail name)
     end
     end))


let string_enum = (fun ( type_name  :  Prims.string ) ( cases  :  Prims.list<pair<Prims.string, 'a>> ) ( v  :  value<'raw, 'flt> ) -> (match (v) with
| VStr (name, uu___) -> begin
     (match ((try_pick_case cases name)) with
| OSome (case) -> begin
     Accepted (case)
     end
| ONone -> begin
     (refuse_with type_name (Prims.strcat "case name `" (Prims.strcat name "`, which names no case")))
     end)
     end
| uu___ -> begin
     (refuse type_name v)
     end))


let as_option = (fun ( inner  :  decoder<'raw, 'flt, 'a> ) -> (union "Option" (fun ( tag  :  Prims.int ) ->  
if (Prims.op_Equals tag (Prims.parse_int "0")) then begin
     OSome ((case0 ONone))
     end else begin
      
if (Prims.op_Equals tag (Prims.parse_int "1")) then begin
     OSome ((payload (map (fun ( x  :  'a ) -> OSome (x)) inner)))
     end else begin
     ONone
     end
     end)))


let as_date_time_with = (fun ( make  :  Prims.int  ->  Prims.int  ->  'a ) ( v  :  value<'raw, 'flt> ) -> (match ((exactly (Prims.parse_int "2") v)) with
| Refused (e) -> begin
     Refused ((under "DateTime" e))
     end
| Accepted (uu___) -> begin
     (op_Bar_Greater_Greater (op_Bar_Greater_Greater (succeed make) (field "Ticks" (Prims.parse_int "0") (as_int64 ()))) (field "Kind" (Prims.parse_int "1") (as_int64 ())) v)
     end))


let as_date_time_offset_with = (fun ( make  :  Prims.int  ->  Prims.int  ->  'a ) ( v  :  value<'raw, 'flt> ) -> (match ((exactly (Prims.parse_int "2") v)) with
| Refused (e) -> begin
     Refused ((under "DateTimeOffset" e))
     end
| Accepted (uu___) -> begin
     (op_Bar_Greater_Greater (op_Bar_Greater_Greater (succeed make) (field "Ticks" (Prims.parse_int "0") (as_int64 ()))) (field "OffsetMinutes" (Prims.parse_int "1") (as_int64 ())) v)
     end))


let as_decimal_with = (fun ( make  :  Prims.int  ->  Prims.int  ->  Prims.int  ->  Prims.int  ->  'a ) ( v  :  value<'raw, 'flt> ) -> (match ((exactly (Prims.parse_int "4") v)) with
| Refused (e) -> begin
     Refused ((under "Decimal" e))
     end
| Accepted (uu___) -> begin
     (op_Bar_Greater_Greater (op_Bar_Greater_Greater (op_Bar_Greater_Greater (op_Bar_Greater_Greater (succeed make) (index (Prims.parse_int "0") (as_int32 ()))) (index (Prims.parse_int "1") (as_int32 ()))) (index (Prims.parse_int "2") (as_int32 ()))) (index (Prims.parse_int "3") (as_int32 ())) v)
     end))


let run = (fun ( d  :  decoder<'raw, 'flt, 'a> ) ( v  :  value<'raw, 'flt> ) -> (d v))


type i32 = Prims.int

type ref_address = {line1 : Prims.string; postcode : Prims.string; country : Prims.string}


let __proj__Mkref_address__item__line1 : ref_address  ->  Prims.string = (fun ( projectee  :  ref_address ) -> (match (projectee) with
| {line1 = line1; postcode = postcode; country = country} -> begin
     line1
     end))


let __proj__Mkref_address__item__postcode : ref_address  ->  Prims.string = (fun ( projectee  :  ref_address ) -> (match (projectee) with
| {line1 = line1; postcode = postcode; country = country} -> begin
     postcode
     end))


let __proj__Mkref_address__item__country : ref_address  ->  Prims.string = (fun ( projectee  :  ref_address ) -> (match (projectee) with
| {line1 = line1; postcode = postcode; country = country} -> begin
     country
     end))

type ref_consignment = {reference : Prims.string; origin : ref_address; weight : i32; urgent : Prims.bool}


let __proj__Mkref_consignment__item__reference : ref_consignment  ->  Prims.string = (fun ( projectee  :  ref_consignment ) -> (match (projectee) with
| {reference = reference; origin = origin; weight = weight; urgent = urgent} -> begin
     reference
     end))


let __proj__Mkref_consignment__item__origin : ref_consignment  ->  ref_address = (fun ( projectee  :  ref_consignment ) -> (match (projectee) with
| {reference = reference; origin = origin; weight = weight; urgent = urgent} -> begin
     origin
     end))


let __proj__Mkref_consignment__item__weight : ref_consignment  ->  i32 = (fun ( projectee  :  ref_consignment ) -> (match (projectee) with
| {reference = reference; origin = origin; weight = weight; urgent = urgent} -> begin
     weight
     end))


let __proj__Mkref_consignment__item__urgent : ref_consignment  ->  Prims.bool = (fun ( projectee  :  ref_consignment ) -> (match (projectee) with
| {reference = reference; origin = origin; weight = weight; urgent = urgent} -> begin
     urgent
     end))


let encode_address = (fun ( str_len  :  Prims.string  ->  Prims.nat ) ( a  :  ref_address ) -> VArr ((VStr (a.line1, (str_len a.line1)))::(VStr (a.postcode, (str_len a.postcode)))::(VStr (a.country, (str_len a.country)))::[]))


let encode_consignment = (fun ( str_len  :  Prims.string  ->  Prims.nat ) ( c  :  ref_consignment ) -> VArr ((VStr (c.reference, (str_len c.reference)))::((encode_address str_len c.origin))::(VInt (c.weight, Bits32))::(VBool (c.urgent))::[]))


let decode_address = (fun ( uu___  :  unit ) -> (op_Bar_Greater_Greater (op_Bar_Greater_Greater (op_Bar_Greater_Greater (succeed (fun ( l  :  Prims.string ) ( p  :  Prims.string ) ( c  :  Prims.string ) -> {line1 = l; postcode = p; country = c})) (field "Line1" (Prims.parse_int "0") as_string)) (field "Postcode" (Prims.parse_int "1") as_string)) (field "Country" (Prims.parse_int "2") as_string)))


let reinterpret_i32 : Prims.int  ->  i32 = (fun ( n  :  Prims.int ) ->  
if (n < (Prims.parse_int "-2147483648")) then begin
     (Prims.parse_int "0")
     end else begin
      
if (n <= (Prims.parse_int "2147483647")) then begin
     n
     end else begin
      
if (n <= (Prims.parse_int "4294967295")) then begin
     (n - (Prims.parse_int "4294967296"))
     end else begin
     (Prims.parse_int "0")
     end
     end
     end)


let decode_consignment = (fun ( uu___  :  unit ) -> (op_Bar_Greater_Greater (op_Bar_Greater_Greater (op_Bar_Greater_Greater (op_Bar_Greater_Greater (succeed (fun ( r  :  Prims.string ) ( o  :  ref_address ) ( w  :  Prims.int ) ( u  :  Prims.bool ) -> {reference = r; origin = o; weight = (reinterpret_i32 w); urgent = u})) (field "Reference" (Prims.parse_int "0") as_string)) (field "Origin" (Prims.parse_int "1") (decode_address ()))) (field "Weight" (Prims.parse_int "2") (as_int32 ()))) (field "Urgent" (Prims.parse_int "3") as_bool)))

type ref_status =
| Planned
| Delayed of Prims.string * i32
| Arrived of i32


let uu___is_Planned : ref_status  ->  Prims.bool = (fun ( projectee  :  ref_status ) -> (match (projectee) with
| Planned -> begin
     true
     end
| uu___ -> begin
     false
     end))


let uu___is_Delayed : ref_status  ->  Prims.bool = (fun ( projectee  :  ref_status ) -> (match (projectee) with
| Delayed (reason, minutes) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__Delayed__item__reason : ref_status  ->  Prims.string = (fun ( projectee  :  ref_status ) -> (match (projectee) with
| Delayed (reason, minutes) -> begin
     reason
     end))


let __proj__Delayed__item__minutes : ref_status  ->  i32 = (fun ( projectee  :  ref_status ) -> (match (projectee) with
| Delayed (reason, minutes) -> begin
     minutes
     end))


let uu___is_Arrived : ref_status  ->  Prims.bool = (fun ( projectee  :  ref_status ) -> (match (projectee) with
| Arrived (at) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__Arrived__item__at : ref_status  ->  i32 = (fun ( projectee  :  ref_status ) -> (match (projectee) with
| Arrived (at) -> begin
     at
     end))

type ref_leg = {hop : pair<i32, Prims.string>; status : ref_status}


let __proj__Mkref_leg__item__hop : ref_leg  ->  pair<i32, Prims.string> = (fun ( projectee  :  ref_leg ) -> (match (projectee) with
| {hop = hop; status = status} -> begin
     hop
     end))


let __proj__Mkref_leg__item__status : ref_leg  ->  ref_status = (fun ( projectee  :  ref_leg ) -> (match (projectee) with
| {hop = hop; status = status} -> begin
     status
     end))


let encode_status = (fun ( str_len  :  Prims.string  ->  Prims.nat ) ( s  :  ref_status ) -> (match (s) with
| Planned -> begin
     VArr ((VInt ((Prims.parse_int "0"), Fixnum))::[])
     end
| Delayed (reason, minutes) -> begin
     VArr ((VInt ((Prims.parse_int "1"), Fixnum))::(VArr ((VStr (reason, (str_len reason)))::(VInt (minutes, Bits32))::[]))::[])
     end
| Arrived (at) -> begin
     VArr ((VInt ((Prims.parse_int "2"), Fixnum))::(VInt (at, Bits32))::[])
     end))


let encode_leg = (fun ( str_len  :  Prims.string  ->  Prims.nat ) ( l  :  ref_leg ) -> VArr (((match (l.hop) with
| Pair (n, s) -> begin
     VArr ((VInt (n, Bits32))::(VStr (s, (str_len s)))::[])
     end))::((encode_status str_len l.status))::[]))


let decode_status = (fun ( uu___  :  unit ) -> (union "RefStatus" (fun ( tag  :  Prims.int ) ->  
if (Prims.op_Equals tag (Prims.parse_int "0")) then begin
     OSome ((case0 Planned))
     end else begin
      
if (Prims.op_Equals tag (Prims.parse_int "1")) then begin
     OSome ((fields (Prims.parse_int "2") (op_Bar_Greater_Greater (op_Bar_Greater_Greater (succeed (fun ( r  :  Prims.string ) ( m  :  Prims.int ) -> Delayed (r, (reinterpret_i32 m)))) (field "reason" (Prims.parse_int "0") as_string)) (field "minutes" (Prims.parse_int "1") (as_int32 ())))))
     end else begin
      
if (Prims.op_Equals tag (Prims.parse_int "2")) then begin
     OSome ((payload (map (fun ( a  :  Prims.int ) -> Arrived ((reinterpret_i32 a))) (as_int32 ()))))
     end else begin
     ONone
     end
     end
     end)))


let decode_leg = (fun ( uu___  :  unit ) -> (op_Bar_Greater_Greater (op_Bar_Greater_Greater (succeed (fun ( h  :  pair<i32, Prims.string> ) ( s  :  ref_status ) -> {hop = h; status = s})) (field "Hop" (Prims.parse_int "0") (tuple2 (map reinterpret_i32 (as_int32 ())) as_string))) (field "Status" (Prims.parse_int "1") (decode_status ()))))




