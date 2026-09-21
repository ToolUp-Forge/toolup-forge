module ElmishRing
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

type slot<'a> =
| Placeholder
| Written of 'a


let uu___is_Placeholder = (fun ( projectee  :  slot<'a> ) -> (match (projectee) with
| Placeholder -> begin
     true
     end
| uu___ -> begin
     false
     end))


let uu___is_Written = (fun ( projectee  :  slot<'a> ) -> (match (projectee) with
| Written (value) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__Written__item__value = (fun ( projectee  :  slot<'a> ) -> (match (projectee) with
| Written (value) -> begin
     value
     end))


let rec length = (fun ( xs  :  Prims.list<'a> ) -> (match (xs) with
| [] -> begin
     (Prims.parse_int "0")
     end
| (uu___)::rest -> begin
     ((Prims.parse_int "1") + (length rest))
     end))


let rec nth = (fun ( xs  :  Prims.list<slot<'a>> ) ( i  :  Prims.nat ) -> (match (xs) with
| [] -> begin
     Placeholder
     end
| (x)::rest -> begin
      
if (Prims.op_Equals i (Prims.parse_int "0")) then begin
     x
     end else begin
     (nth rest (i - (Prims.parse_int "1")))
     end
     end))


let rec set = (fun ( xs  :  Prims.list<slot<'a>> ) ( i  :  Prims.nat ) ( v  :  slot<'a> ) -> (match (xs) with
| [] -> begin
     []
     end
| (x)::rest -> begin
      
if (Prims.op_Equals i (Prims.parse_int "0")) then begin
     (v)::rest
     end else begin
     (x)::(set rest (i - (Prims.parse_int "1")) v)
     end
     end))


let rec append = (fun ( xs  :  Prims.list<'a> ) ( ys  :  Prims.list<'a> ) -> (match (xs) with
| [] -> begin
     ys
     end
| (x)::rest -> begin
     (x)::(append rest ys)
     end))


let rec skip = (fun ( n  :  Prims.nat ) ( xs  :  Prims.list<'a> ) ->  
if (Prims.op_Equals n (Prims.parse_int "0")) then begin
     xs
     end else begin
     (match (xs) with
| [] -> begin
     []
     end
| (uu___)::rest -> begin
     (skip (n - (Prims.parse_int "1")) rest)
     end)
     end)


let rec take = (fun ( n  :  Prims.nat ) ( xs  :  Prims.list<'a> ) ->  
if (Prims.op_Equals n (Prims.parse_int "0")) then begin
     []
     end else begin
     (match (xs) with
| [] -> begin
     []
     end
| (x)::rest -> begin
     (x)::(take (n - (Prims.parse_int "1")) rest)
     end)
     end)


let rec replicate = (fun ( n  :  Prims.nat ) ( v  :  'a ) ->  
if (Prims.op_Equals n (Prims.parse_int "0")) then begin
     []
     end else begin
     (v)::(replicate (n - (Prims.parse_int "1")) v)
     end)


let succ : Prims.nat  ->  Prims.nat  ->  Prims.nat = (fun ( n  :  Prims.nat ) ( i  :  Prims.nat ) ->  
if ((i + (Prims.parse_int "1")) >= n) then begin
     (Prims.parse_int "0")
     end else begin
     (i + (Prims.parse_int "1"))
     end)

type ring<'a> =
| Writable of Prims.list<slot<'a>> * Prims.nat
| ReadWritable of Prims.list<slot<'a>> * Prims.nat * Prims.nat


let uu___is_Writable = (fun ( projectee  :  ring<'a> ) -> (match (projectee) with
| Writable (items, ix) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__Writable__item__items = (fun ( projectee  :  ring<'a> ) -> (match (projectee) with
| Writable (items, ix) -> begin
     items
     end))


let __proj__Writable__item__ix = (fun ( projectee  :  ring<'a> ) -> (match (projectee) with
| Writable (items, ix) -> begin
     ix
     end))


let uu___is_ReadWritable = (fun ( projectee  :  ring<'a> ) -> (match (projectee) with
| ReadWritable (items, wix, rix) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__ReadWritable__item__items = (fun ( projectee  :  ring<'a> ) -> (match (projectee) with
| ReadWritable (items, wix, rix) -> begin
     items
     end))


let __proj__ReadWritable__item__wix = (fun ( projectee  :  ring<'a> ) -> (match (projectee) with
| ReadWritable (items, wix, rix) -> begin
     wix
     end))


let __proj__ReadWritable__item__rix = (fun ( projectee  :  ring<'a> ) -> (match (projectee) with
| ReadWritable (items, wix, rix) -> begin
     rix
     end))


let minimum_capacity : Prims.nat = (Prims.parse_int "2")


let capacity_of : Prims.int  ->  Prims.nat = (fun ( size  :  Prims.int ) ->  
if (size > minimum_capacity) then begin
     size
     end else begin
     minimum_capacity
     end)


let create = (fun ( size  :  Prims.int ) -> Writable ((replicate (capacity_of size) Placeholder), (Prims.parse_int "0")))


let double_size = (fun ( ix  :  Prims.nat ) ( items  :  Prims.list<slot<'a>> ) -> (append (skip ix items) (append (take ix items) (replicate ((length items) + (Prims.parse_int "1")) Placeholder))))


let pop = (fun ( r  :  ring<'a> ) -> (match (r) with
| ReadWritable (items, wix, rix) -> begin
     (

let rix' = (succ (length items) rix)
in  
if (Prims.op_Equals rix' wix) then begin
     Pair (Writable (items, wix), OSome ((nth items rix)))
     end else begin
     Pair (ReadWritable (items, wix, rix'), OSome ((nth items rix)))
     end)
     end
| Writable (uu___, uu___1) -> begin
     Pair (r, ONone)
     end))


let push = (fun ( item  :  'a ) ( r  :  ring<'a> ) -> (match (r) with
| Writable (items, ix) -> begin
     (

let items' = (set items ix (Written (item)))
in ReadWritable (items', (succ (length items) ix), ix))
     end
| ReadWritable (items, wix, rix) -> begin
     (

let items' = (set items wix (Written (item)))
in (

let wix' = (succ (length items) wix)
in  
if (Prims.op_Equals wix' rix) then begin
     ReadWritable ((double_size rix items'), (length items'), (Prims.parse_int "0"))
     end else begin
     ReadWritable (items', wix', rix)
     end))
     end))

type op<'a> =
| Push of 'a
| Pop


let uu___is_Push = (fun ( projectee  :  op<'a> ) -> (match (projectee) with
| Push (item) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__Push__item__item = (fun ( projectee  :  op<'a> ) -> (match (projectee) with
| Push (item) -> begin
     item
     end))


let uu___is_Pop = (fun ( projectee  :  op<'a> ) -> (match (projectee) with
| Pop -> begin
     true
     end
| uu___ -> begin
     false
     end))


let rec run = (fun ( r  :  ring<'a> ) ( ops  :  Prims.list<op<'a>> ) -> (match (ops) with
| [] -> begin
     Pair (r, [])
     end
| (Push (item))::rest -> begin
     (run (push item r) rest)
     end
| (Pop)::rest -> begin
     (

let uu___ = (pop r)
in (match (uu___) with
| Pair (r', out) -> begin
     (

let uu___1 = (run r' rest)
in (match (uu___1) with
| Pair (r'', outs) -> begin
     Pair (r'', (out)::outs)
     end))
     end))
     end))




