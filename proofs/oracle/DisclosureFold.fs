module DisclosureFold
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


let rec length = (fun ( xs  :  Prims.list<'a> ) -> (match (xs) with
| [] -> begin
     (Prims.parse_int "0")
     end
| (uu___)::rest -> begin
     ((Prims.parse_int "1") + (length rest))
     end))

type disclosure =
| Surfaceable
| Internal
| Restricted of Prims.string


let uu___is_Surfaceable : disclosure  ->  Prims.bool = (fun ( projectee  :  disclosure ) -> (match (projectee) with
| Surfaceable -> begin
     true
     end
| uu___ -> begin
     false
     end))


let uu___is_Internal : disclosure  ->  Prims.bool = (fun ( projectee  :  disclosure ) -> (match (projectee) with
| Internal -> begin
     true
     end
| uu___ -> begin
     false
     end))


let uu___is_Restricted : disclosure  ->  Prims.bool = (fun ( projectee  :  disclosure ) -> (match (projectee) with
| Restricted (policy_ref) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__Restricted__item__policy_ref : disclosure  ->  Prims.string = (fun ( projectee  :  disclosure ) -> (match (projectee) with
| Restricted (policy_ref) -> begin
     policy_ref
     end))

type verdict =
| Disclosable
| NotDisclosable of Prims.string


let uu___is_Disclosable : verdict  ->  Prims.bool = (fun ( projectee  :  verdict ) -> (match (projectee) with
| Disclosable -> begin
     true
     end
| uu___ -> begin
     false
     end))


let uu___is_NotDisclosable : verdict  ->  Prims.bool = (fun ( projectee  :  verdict ) -> (match (projectee) with
| NotDisclosable (policy_ref) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__NotDisclosable__item__policy_ref : verdict  ->  Prims.string = (fun ( projectee  :  verdict ) -> (match (projectee) with
| NotDisclosable (policy_ref) -> begin
     policy_ref
     end))


type resolver<'surface> = Prims.string  ->  'surface  ->  opt<Prims.bool>


let deny_unknown = (fun ( uu___  :  Prims.string ) ( uu___1  :  'surface ) -> ONone)


let evaluate = (fun ( resolve_policy  :  resolver<'surface> ) ( at_surface  :  'surface ) ( d  :  disclosure ) -> (match (d) with
| Surfaceable -> begin
     Disclosable
     end
| Internal -> begin
     NotDisclosable ("Internal")
     end
| Restricted (policy_ref) -> begin
     (match ((resolve_policy policy_ref at_surface)) with
| OSome (true) -> begin
     Disclosable
     end
| OSome (false) -> begin
     NotDisclosable (policy_ref)
     end
| ONone -> begin
     NotDisclosable (policy_ref)
     end)
     end))

type fact<'p> = {fact_id : Prims.string; payload : 'p}


let __proj__Mkfact__item__fact_id = (fun ( projectee  :  fact<'p> ) -> (match (projectee) with
| {fact_id = fact_id; payload = payload} -> begin
     fact_id
     end))


let __proj__Mkfact__item__payload = (fun ( projectee  :  fact<'p> ) -> (match (projectee) with
| {fact_id = fact_id; payload = payload} -> begin
     payload
     end))

type population_disclosure<'p> = {disclosable : Prims.list<pair<Prims.nat, fact<'p>>>; withheld_count : Prims.nat; withheld_by_policy : Prims.list<pair<Prims.string, Prims.nat>>}


let __proj__Mkpopulation_disclosure__item__disclosable = (fun ( projectee  :  population_disclosure<'p> ) -> (match (projectee) with
| {disclosable = disclosable; withheld_count = withheld_count; withheld_by_policy = withheld_by_policy} -> begin
     disclosable
     end))


let __proj__Mkpopulation_disclosure__item__withheld_count = (fun ( projectee  :  population_disclosure<'p> ) -> (match (projectee) with
| {disclosable = disclosable; withheld_count = withheld_count; withheld_by_policy = withheld_by_policy} -> begin
     withheld_count
     end))


let __proj__Mkpopulation_disclosure__item__withheld_by_policy = (fun ( projectee  :  population_disclosure<'p> ) -> (match (projectee) with
| {disclosable = disclosable; withheld_count = withheld_count; withheld_by_policy = withheld_by_policy} -> begin
     withheld_by_policy
     end))

type judged<'p> = {rank : Prims.nat; judged_fact : fact<'p>; judged_verdict : verdict}


let __proj__Mkjudged__item__rank = (fun ( projectee  :  judged<'p> ) -> (match (projectee) with
| {rank = rank; judged_fact = judged_fact; judged_verdict = judged_verdict} -> begin
     rank
     end))


let __proj__Mkjudged__item__judged_fact = (fun ( projectee  :  judged<'p> ) -> (match (projectee) with
| {rank = rank; judged_fact = judged_fact; judged_verdict = judged_verdict} -> begin
     judged_fact
     end))


let __proj__Mkjudged__item__judged_verdict = (fun ( projectee  :  judged<'p> ) -> (match (projectee) with
| {rank = rank; judged_fact = judged_fact; judged_verdict = judged_verdict} -> begin
     judged_verdict
     end))


let rec judge = (fun ( verdict_for  :  Prims.string  ->  verdict ) ( position  :  Prims.nat ) ( ranked  :  Prims.list<fact<'p>> ) -> (match (ranked) with
| [] -> begin
     []
     end
| (f)::rest -> begin
     ({rank = (position + (Prims.parse_int "1")); judged_fact = f; judged_verdict = (verdict_for f.fact_id)})::(judge verdict_for (position + (Prims.parse_int "1")) rest)
     end))


let rec disclosable_of = (fun ( js  :  Prims.list<judged<'p>> ) -> (match (js) with
| [] -> begin
     []
     end
| (j)::rest -> begin
      
if (match (j.judged_verdict) with
| Disclosable -> begin
     true
     end
| uu___ -> begin
     false
     end) then begin
     (Pair (j.rank, j.judged_fact))::(disclosable_of rest)
     end else begin
     (disclosable_of rest)
     end
     end))


let rec withheld_refs = (fun ( js  :  Prims.list<judged<'p>> ) -> (match (js) with
| [] -> begin
     []
     end
| (j)::rest -> begin
     (match (j.judged_verdict) with
| Disclosable -> begin
     (withheld_refs rest)
     end
| NotDisclosable (policy_ref) -> begin
     (policy_ref)::(withheld_refs rest)
     end)
     end))


let rec count_into : Prims.string  ->  Prims.list<pair<Prims.string, Prims.nat>>  ->  Prims.list<pair<Prims.string, Prims.nat>> = (fun ( key  :  Prims.string ) ( counts  :  Prims.list<pair<Prims.string, Prims.nat>> ) -> (match (counts) with
| [] -> begin
     (Pair (key, (Prims.parse_int "1")))::[]
     end
| (Pair (k, n))::rest -> begin
      
if (Prims.op_Equals k key) then begin
     (Pair (k, (n + (Prims.parse_int "1"))))::rest
     end else begin
     (Pair (k, n))::(count_into key rest)
     end
     end))


let rec count_by : Prims.list<Prims.string>  ->  Prims.list<pair<Prims.string, Prims.nat>>  ->  Prims.list<pair<Prims.string, Prims.nat>> = (fun ( keys  :  Prims.list<Prims.string> ) ( counts  :  Prims.list<pair<Prims.string, Prims.nat>> ) -> (match (keys) with
| [] -> begin
     counts
     end
| (key)::rest -> begin
     (count_by rest (count_into key counts))
     end))


let rec insert_by : (Prims.string  ->  Prims.string  ->  Prims.bool)  ->  pair<Prims.string, Prims.nat>  ->  Prims.list<pair<Prims.string, Prims.nat>>  ->  Prims.list<pair<Prims.string, Prims.nat>> = (fun ( before  :  Prims.string  ->  Prims.string  ->  Prims.bool ) ( entry  :  pair<Prims.string, Prims.nat> ) ( sorted  :  Prims.list<pair<Prims.string, Prims.nat>> ) -> (match (sorted) with
| [] -> begin
     (entry)::[]
     end
| (head)::rest -> begin
     (match (((entry), (head))) with
| (Pair (key, uu___), Pair (head_key, uu___1)) -> begin
      
if (before head_key key) then begin
     (head)::(insert_by before entry rest)
     end else begin
     (entry)::sorted
     end
     end)
     end))


let rec sort_by : (Prims.string  ->  Prims.string  ->  Prims.bool)  ->  Prims.list<pair<Prims.string, Prims.nat>>  ->  Prims.list<pair<Prims.string, Prims.nat>> = (fun ( before  :  Prims.string  ->  Prims.string  ->  Prims.bool ) ( entries  :  Prims.list<pair<Prims.string, Prims.nat>> ) -> (match (entries) with
| [] -> begin
     []
     end
| (entry)::rest -> begin
     (insert_by before entry (sort_by before rest))
     end))


let withheld_projection : (Prims.string  ->  Prims.string  ->  Prims.bool)  ->  Prims.list<Prims.string>  ->  Prims.list<pair<Prims.string, Prims.nat>> = (fun ( before  :  Prims.string  ->  Prims.string  ->  Prims.bool ) ( refs  :  Prims.list<Prims.string> ) -> (sort_by before (count_by refs [])))


let fold = (fun ( before  :  Prims.string  ->  Prims.string  ->  Prims.bool ) ( verdict_for  :  Prims.string  ->  verdict ) ( ranked  :  Prims.list<fact<'p>> ) -> (

let judged1 = (judge verdict_for (Prims.parse_int "0") ranked)
in (

let withheld_policies = (withheld_refs judged1)
in {disclosable = (disclosable_of judged1); withheld_count = (length withheld_policies); withheld_by_policy = (withheld_projection before withheld_policies)})))


let values_withheld = (fun ( d  :  population_disclosure<'p> ) -> (d.withheld_count > (Prims.parse_int "0")))

type stats<'m, 'r> = {minimum : opt<'m>; maximum : opt<'m>; mean : opt<'m>; existence : 'r}


let __proj__Mkstats__item__minimum = (fun ( projectee  :  stats<'m, 'r> ) -> (match (projectee) with
| {minimum = minimum; maximum = maximum; mean = mean; existence = existence} -> begin
     minimum
     end))


let __proj__Mkstats__item__maximum = (fun ( projectee  :  stats<'m, 'r> ) -> (match (projectee) with
| {minimum = minimum; maximum = maximum; mean = mean; existence = existence} -> begin
     maximum
     end))


let __proj__Mkstats__item__mean = (fun ( projectee  :  stats<'m, 'r> ) -> (match (projectee) with
| {minimum = minimum; maximum = maximum; mean = mean; existence = existence} -> begin
     mean
     end))


let __proj__Mkstats__item__existence = (fun ( projectee  :  stats<'m, 'r> ) -> (match (projectee) with
| {minimum = minimum; maximum = maximum; mean = mean; existence = existence} -> begin
     existence
     end))


let disclosed_stats = (fun ( d  :  population_disclosure<'p> ) ( s  :  stats<'m, 'r> ) ->  
if (values_withheld d) then begin
     {minimum = ONone; maximum = ONone; mean = ONone; existence = s.existence}
     end else begin
     s
     end)




