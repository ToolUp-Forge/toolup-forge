module ElmishSub
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

type diff_result<'k, 'h, 's> =
| Diff of Prims.list<'k> * Prims.list<pair<'k, 'h>> * Prims.list<pair<'k, 'h>> * Prims.list<pair<'k, 's>>


let uu___is_Diff = (fun ( projectee  :  diff_result<'k, 'h, 's> ) -> true)


let __proj__Diff__item__dupes = (fun ( projectee  :  diff_result<'k, 'h, 's> ) -> (match (projectee) with
| Diff (dupes, to_stop, to_keep, to_start) -> begin
     dupes
     end))


let __proj__Diff__item__to_stop = (fun ( projectee  :  diff_result<'k, 'h, 's> ) -> (match (projectee) with
| Diff (dupes, to_stop, to_keep, to_start) -> begin
     to_stop
     end))


let __proj__Diff__item__to_keep = (fun ( projectee  :  diff_result<'k, 'h, 's> ) -> (match (projectee) with
| Diff (dupes, to_stop, to_keep, to_start) -> begin
     to_keep
     end))


let __proj__Diff__item__to_start = (fun ( projectee  :  diff_result<'k, 'h, 's> ) -> (match (projectee) with
| Diff (dupes, to_stop, to_keep, to_start) -> begin
     to_start
     end))

type accumulator<'k, 's> =
| Calc of Prims.list<'k> * Prims.list<'k> * Prims.list<pair<'k, 's>>


let uu___is_Calc = (fun ( projectee  :  accumulator<'k, 's> ) -> true)


let __proj__Calc__item__dupes = (fun ( projectee  :  accumulator<'k, 's> ) -> (match (projectee) with
| Calc (dupes, new_keys, new_subs) -> begin
     dupes
     end))


let __proj__Calc__item__new_keys = (fun ( projectee  :  accumulator<'k, 's> ) -> (match (projectee) with
| Calc (dupes, new_keys, new_subs) -> begin
     new_keys
     end))


let __proj__Calc__item__new_subs = (fun ( projectee  :  accumulator<'k, 's> ) -> (match (projectee) with
| Calc (dupes, new_keys, new_subs) -> begin
     new_subs
     end))


let rec mem = (fun ( key  :  'k ) ( keys  :  Prims.list<'k> ) -> (match (keys) with
| [] -> begin
     false
     end
| (x)::rest -> begin
     ((Prims.op_Equals x key) || (mem key rest))
     end))


let rec subset = (fun ( xs  :  Prims.list<'k> ) ( ys  :  Prims.list<'k> ) -> (match (xs) with
| [] -> begin
     true
     end
| (x)::rest -> begin
     ((mem x ys) && (subset rest ys))
     end))


let set_equal = (fun ( xs  :  Prims.list<'k> ) ( ys  :  Prims.list<'k> ) -> ((subset xs ys) && (subset ys xs)))


let rec keys_of = (fun ( xs  :  Prims.list<pair<'k, 'v>> ) -> (match (xs) with
| [] -> begin
     []
     end
| (Pair (key, uu___))::rest -> begin
     (key)::(keys_of rest)
     end))


let rec with_key_in = (fun ( keys  :  Prims.list<'k> ) ( xs  :  Prims.list<pair<'k, 'v>> ) -> (match (xs) with
| [] -> begin
     []
     end
| (Pair (key, value))::rest -> begin
      
if (mem key keys) then begin
     (Pair (key, value))::(with_key_in keys rest)
     end else begin
     (with_key_in keys rest)
     end
     end))


let rec with_key_not_in = (fun ( keys  :  Prims.list<'k> ) ( xs  :  Prims.list<pair<'k, 'v>> ) -> (match (xs) with
| [] -> begin
     []
     end
| (Pair (key, value))::rest -> begin
      
if (mem key keys) then begin
     (with_key_not_in keys rest)
     end else begin
     (Pair (key, value))::(with_key_not_in keys rest)
     end
     end))


let rec append = (fun ( xs  :  Prims.list<'a> ) ( ys  :  Prims.list<'a> ) -> (match (xs) with
| [] -> begin
     ys
     end
| (x)::rest -> begin
     (x)::(append rest ys)
     end))


let update = (fun ( entry  :  pair<'k, 's> ) ( acc  :  accumulator<'k, 's> ) -> (

let uu___ = entry
in (match (uu___) with
| Pair (sub_id, start) -> begin
     (

let uu___1 = acc
in (match (uu___1) with
| Calc (dupes, new_keys, new_subs) -> begin
      
if (mem sub_id new_keys) then begin
     Calc ((sub_id)::dupes, new_keys, new_subs)
     end else begin
     Calc (dupes, (sub_id)::new_keys, (Pair (sub_id, start))::new_subs)
     end
     end))
     end)))


let rec calculate = (fun ( subs  :  Prims.list<pair<'k, 's>> ) -> (match (subs) with
| [] -> begin
     Calc ([], [], [])
     end
| (entry)::rest -> begin
     (update entry (calculate rest))
     end))


let diff = (fun ( active  :  Prims.list<pair<'k, 'h>> ) ( sub  :  Prims.list<pair<'k, 's>> ) -> (

let keys = (keys_of active)
in (

let uu___ = (calculate sub)
in (match (uu___) with
| Calc (dupes, new_keys, new_subs) -> begin
      
if (set_equal keys new_keys) then begin
     Diff (dupes, [], active, [])
     end else begin
     (

let to_keep = (with_key_in new_keys active)
in (

let to_stop = (with_key_not_in new_keys active)
in (

let to_start = (with_key_not_in keys new_subs)
in Diff (dupes, to_stop, to_keep, to_start))))
     end
     end))))


let rec started = (fun ( start  :  'k  ->  's  ->  opt<'h> ) ( xs  :  Prims.list<pair<'k, 's>> ) -> (match (xs) with
| [] -> begin
     []
     end
| (Pair (key, subscribe))::rest -> begin
     (match ((start key subscribe)) with
| OSome (handle) -> begin
     (Pair (key, handle))::(started start rest)
     end
| ONone -> begin
     (started start rest)
     end)
     end))


let change = (fun ( start  :  'k  ->  's  ->  opt<'h> ) ( d  :  diff_result<'k, 'h, 's> ) -> (

let uu___ = d
in (match (uu___) with
| Diff (uu___1, uu___2, to_keep, to_start) -> begin
     (append to_keep (started start to_start))
     end)))




