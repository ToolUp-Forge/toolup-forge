module TaintFlow
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


let rec mem_of = (fun ( x  :  'a ) ( xs  :  Prims.list<'a> ) -> (match (xs) with
| [] -> begin
     false
     end
| (y)::rest -> begin
     ((Prims.op_Equals y x) || (mem_of x rest))
     end))


type label = Prims.list<Prims.string>


let mem : Prims.string  ->  label  ->  Prims.bool = (fun ( r  :  Prims.string ) ( l  :  label ) -> (mem_of r l))


let bottom : label = []


let is_bottom : label  ->  Prims.bool = (fun ( l  :  label ) -> (match (l) with
| [] -> begin
     true
     end
| uu___ -> begin
     false
     end))


let of_policy_ref : Prims.string  ->  label = (fun ( policy_ref  :  Prims.string ) -> (policy_ref)::[])


let rec app : label  ->  label  ->  label = (fun ( a  :  label ) ( b  :  label ) -> (match (a) with
| [] -> begin
     b
     end
| (r)::rest -> begin
     (r)::(app rest b)
     end))


let join : label  ->  label  ->  label = (fun ( a  :  label ) ( b  :  label ) -> (app a b))


let rec join_all : Prims.list<label>  ->  label = (fun ( ls  :  Prims.list<label> ) -> (match (ls) with
| [] -> begin
     bottom
     end
| (l)::rest -> begin
     (join l (join_all rest))
     end))


let rec sub : label  ->  label  ->  Prims.bool = (fun ( a  :  label ) ( b  :  label ) -> (match (a) with
| [] -> begin
     true
     end
| (r)::rest -> begin
     ((mem r b) && (sub rest b))
     end))


let eq_label : label  ->  label  ->  Prims.bool = (fun ( a  :  label ) ( b  :  label ) -> ((sub a b) && (sub b a)))


let below : label  ->  label  ->  Prims.bool = (fun ( a  :  label ) ( b  :  label ) -> (eq_label (join a b) b))

type routine = {accepting_scopes : Prims.list<Prims.string>}


let __proj__Mkroutine__item__accepting_scopes : routine  ->  Prims.list<Prims.string> = (fun ( projectee  :  routine ) -> (match (projectee) with
| {accepting_scopes = accepting_scopes} -> begin
     accepting_scopes
     end))


let routine_clears : (Prims.string  ->  opt<Prims.string>)  ->  routine  ->  Prims.string  ->  Prims.bool = (fun ( scope_of  :  Prims.string  ->  opt<Prims.string> ) ( rt  :  routine ) ( policy_ref  :  Prims.string ) -> (match ((scope_of policy_ref)) with
| ONone -> begin
     true
     end
| OSome (party) -> begin
     (mem_of party rt.accepting_scopes)
     end))


let rec narrows_to : (Prims.string  ->  Prims.bool)  ->  label  ->  label = (fun ( clears  :  Prims.string  ->  Prims.bool ) ( l  :  label ) -> (match (l) with
| [] -> begin
     []
     end
| (r)::rest -> begin
      
if (clears r) then begin
     (narrows_to clears rest)
     end else begin
     (r)::(narrows_to clears rest)
     end
     end))


let rec cleared_by : (Prims.string  ->  Prims.bool)  ->  label  ->  label = (fun ( clears  :  Prims.string  ->  Prims.bool ) ( l  :  label ) -> (match (l) with
| [] -> begin
     []
     end
| (r)::rest -> begin
      
if (clears r) then begin
     (r)::(cleared_by clears rest)
     end else begin
     (cleared_by clears rest)
     end
     end))

type transform<'src> =
| TJoin of 'src
| TResample
| TLag
| TWindow
| TFilter


let uu___is_TJoin = (fun ( projectee  :  transform<'src> ) -> (match (projectee) with
| TJoin (right) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__TJoin__item__right = (fun ( projectee  :  transform<'src> ) -> (match (projectee) with
| TJoin (right) -> begin
     right
     end))


let uu___is_TResample = (fun ( projectee  :  transform<'src> ) -> (match (projectee) with
| TResample -> begin
     true
     end
| uu___ -> begin
     false
     end))


let uu___is_TLag = (fun ( projectee  :  transform<'src> ) -> (match (projectee) with
| TLag -> begin
     true
     end
| uu___ -> begin
     false
     end))


let uu___is_TWindow = (fun ( projectee  :  transform<'src> ) -> (match (projectee) with
| TWindow -> begin
     true
     end
| uu___ -> begin
     false
     end))


let uu___is_TFilter = (fun ( projectee  :  transform<'src> ) -> (match (projectee) with
| TFilter -> begin
     true
     end
| uu___ -> begin
     false
     end))


type labelling<'src> = 'src  ->  label


let contributed_by = (fun ( label_of_source  :  labelling<'src> ) ( t  :  transform<'src> ) -> (match (t) with
| TJoin (right) -> begin
     (label_of_source right)
     end
| TResample -> begin
     bottom
     end
| TLag -> begin
     bottom
     end
| TWindow -> begin
     bottom
     end
| TFilter -> begin
     bottom
     end))

type labelled_transform<'src> = {lt_transform : transform<'src>; lt_label : label; lt_contributed : label}


let __proj__Mklabelled_transform__item__lt_transform = (fun ( projectee  :  labelled_transform<'src> ) -> (match (projectee) with
| {lt_transform = lt_transform; lt_label = lt_label; lt_contributed = lt_contributed} -> begin
     lt_transform
     end))


let __proj__Mklabelled_transform__item__lt_label = (fun ( projectee  :  labelled_transform<'src> ) -> (match (projectee) with
| {lt_transform = lt_transform; lt_label = lt_label; lt_contributed = lt_contributed} -> begin
     lt_label
     end))


let __proj__Mklabelled_transform__item__lt_contributed = (fun ( projectee  :  labelled_transform<'src> ) -> (match (projectee) with
| {lt_transform = lt_transform; lt_label = lt_label; lt_contributed = lt_contributed} -> begin
     lt_contributed
     end))

type labelled_assembly<'src> = {la_base_label : label; la_nodes : Prims.list<labelled_transform<'src>>; la_output_label : label}


let __proj__Mklabelled_assembly__item__la_base_label = (fun ( projectee  :  labelled_assembly<'src> ) -> (match (projectee) with
| {la_base_label = la_base_label; la_nodes = la_nodes; la_output_label = la_output_label} -> begin
     la_base_label
     end))


let __proj__Mklabelled_assembly__item__la_nodes = (fun ( projectee  :  labelled_assembly<'src> ) -> (match (projectee) with
| {la_base_label = la_base_label; la_nodes = la_nodes; la_output_label = la_output_label} -> begin
     la_nodes
     end))


let __proj__Mklabelled_assembly__item__la_output_label = (fun ( projectee  :  labelled_assembly<'src> ) -> (match (projectee) with
| {la_base_label = la_base_label; la_nodes = la_nodes; la_output_label = la_output_label} -> begin
     la_output_label
     end))


let rec label_nodes = (fun ( los  :  labelling<'src> ) ( incoming  :  label ) ( ts  :  Prims.list<transform<'src>> ) -> (match (ts) with
| [] -> begin
     Pair ([], incoming)
     end
| (t)::rest -> begin
     (

let contributed = (contributed_by los t)
in (

let node_label = (join incoming contributed)
in (match ((label_nodes los node_label rest)) with
| Pair (nodes, out) -> begin
     Pair (({lt_transform = t; lt_label = node_label; lt_contributed = contributed})::nodes, out)
     end)))
     end))


let rec fold_label = (fun ( los  :  labelling<'src> ) ( incoming  :  label ) ( ts  :  Prims.list<transform<'src>> ) -> (match (ts) with
| [] -> begin
     incoming
     end
| (t)::rest -> begin
     (fold_label los (join incoming (contributed_by los t)) rest)
     end))


let label_assembly = (fun ( los  :  labelling<'src> ) ( base_source  :  'src ) ( ts  :  Prims.list<transform<'src>> ) -> (

let base_label = (los base_source)
in (match ((label_nodes los base_label ts)) with
| Pair (nodes, out) -> begin
     {la_base_label = base_label; la_nodes = nodes; la_output_label = out}
     end)))


let label_of = (fun ( node  :  labelled_transform<'src> ) -> node.lt_label)


let output_label = (fun ( los  :  labelling<'src> ) ( base_source  :  'src ) ( ts  :  Prims.list<transform<'src>> ) -> (label_assembly los base_source ts).la_output_label)

type semantics<'src, 'frame> = {step_fold : transform<'src>  ->  'frame  ->  'frame; step_join : 'src  ->  'frame  ->  'frame  ->  'frame}


let __proj__Mksemantics__item__step_fold = (fun ( projectee  :  semantics<'src, 'frame> ) -> (match (projectee) with
| {step_fold = step_fold; step_join = step_join} -> begin
     step_fold
     end))


let __proj__Mksemantics__item__step_join = (fun ( projectee  :  semantics<'src, 'frame> ) -> (match (projectee) with
| {step_fold = step_fold; step_join = step_join} -> begin
     step_join
     end))


type assignment<'src, 'frame> = 'src  ->  'frame


let rec eval = (fun ( sem  :  semantics<'src, 'frame> ) ( assign  :  assignment<'src, 'frame> ) ( working  :  'frame ) ( ts  :  Prims.list<transform<'src>> ) -> (match (ts) with
| [] -> begin
     working
     end
| (t)::rest -> begin
     (

let next = (match (t) with
| TJoin (right) -> begin
     (sem.step_join right working (assign right))
     end
| TResample -> begin
     (sem.step_fold t working)
     end
| TLag -> begin
     (sem.step_fold t working)
     end
| TWindow -> begin
     (sem.step_fold t working)
     end
| TFilter -> begin
     (sem.step_fold t working)
     end)
in (eval sem assign next rest))
     end))


let run = (fun ( sem  :  semantics<'src, 'frame> ) ( assign  :  assignment<'src, 'frame> ) ( base_source  :  'src ) ( ts  :  Prims.list<transform<'src>> ) -> (eval sem assign (assign base_source) ts))

type derivation =
| DNode of opt<Prims.string> * opt<routine> * Prims.list<derivation>


let uu___is_DNode : derivation  ->  Prims.bool = (fun ( projectee  :  derivation ) -> true)


let __proj__DNode__item__own : derivation  ->  opt<Prims.string> = (fun ( projectee  :  derivation ) -> (match (projectee) with
| DNode (own, routine_of, upstream) -> begin
     own
     end))


let __proj__DNode__item__routine_of : derivation  ->  opt<routine> = (fun ( projectee  :  derivation ) -> (match (projectee) with
| DNode (own, routine_of, upstream) -> begin
     routine_of
     end))


let __proj__DNode__item__upstream : derivation  ->  Prims.list<derivation> = (fun ( projectee  :  derivation ) -> (match (projectee) with
| DNode (own, routine_of, upstream) -> begin
     upstream
     end))


let rec taint_of : (Prims.string  ->  opt<Prims.string>)  ->  derivation  ->  label = (fun ( scope_of  :  Prims.string  ->  opt<Prims.string> ) ( d  :  derivation ) -> (match (d) with
| DNode (own, rt, ups) -> begin
     (

let inputs = (inputs_taint scope_of ups)
in (match (rt) with
| OSome (r) -> begin
     (narrows_to (routine_clears scope_of r) inputs)
     end
| ONone -> begin
     (match (own) with
| OSome (s) -> begin
     (s)::inputs
     end
| ONone -> begin
     inputs
     end)
     end))
     end))
and inputs_taint : (Prims.string  ->  opt<Prims.string>)  ->  Prims.list<derivation>  ->  label = (fun ( scope_of  :  Prims.string  ->  opt<Prims.string> ) ( ds  :  Prims.list<derivation> ) -> (match (ds) with
| [] -> begin
     bottom
     end
| (d)::rest -> begin
     (join (taint_of scope_of d) (inputs_taint scope_of rest))
     end))


let mints : Prims.string  ->  derivation  ->  Prims.bool = (fun ( r  :  Prims.string ) ( d  :  derivation ) -> (match (d) with
| DNode (own, rt, uu___) -> begin
     (match (((rt), (own))) with
| (ONone, OSome (s)) -> begin
     (Prims.op_Equals s r)
     end
| (uu___1, uu___2) -> begin
     false
     end)
     end))


let rec reaches : Prims.string  ->  derivation  ->  Prims.bool = (fun ( r  :  Prims.string ) ( d  :  derivation ) -> (match (d) with
| DNode (uu___, uu___1, ups) -> begin
     ((mints r d) || (reaches_list r ups))
     end))
and reaches_list : Prims.string  ->  Prims.list<derivation>  ->  Prims.bool = (fun ( r  :  Prims.string ) ( ds  :  Prims.list<derivation> ) -> (match (ds) with
| [] -> begin
     false
     end
| (d)::rest -> begin
     ((reaches r d) || (reaches_list r rest))
     end))


let rec drops_are_entitled : (Prims.string  ->  opt<Prims.string>)  ->  Prims.string  ->  derivation  ->  Prims.bool = (fun ( scope_of  :  Prims.string  ->  opt<Prims.string> ) ( r  :  Prims.string ) ( d  :  derivation ) -> (match (d) with
| DNode (uu___, rt, ups) -> begin
     (( 
if ((mem r (inputs_taint scope_of ups)) && (not ((mem r (taint_of scope_of d))))) then begin
     (match (rt) with
| OSome (rt') -> begin
     (routine_clears scope_of rt' r)
     end
| ONone -> begin
     false
     end)
     end else begin
     true
     end) && (drops_are_entitled_list scope_of r ups))
     end))
and drops_are_entitled_list : (Prims.string  ->  opt<Prims.string>)  ->  Prims.string  ->  Prims.list<derivation>  ->  Prims.bool = (fun ( scope_of  :  Prims.string  ->  opt<Prims.string> ) ( r  :  Prims.string ) ( ds  :  Prims.list<derivation> ) -> (match (ds) with
| [] -> begin
     true
     end
| (d)::rest -> begin
     ((drops_are_entitled scope_of r d) && (drops_are_entitled_list scope_of r rest))
     end))


let rec drop_occurred : (Prims.string  ->  opt<Prims.string>)  ->  Prims.string  ->  derivation  ->  Prims.bool = (fun ( scope_of  :  Prims.string  ->  opt<Prims.string> ) ( r  :  Prims.string ) ( d  :  derivation ) -> (match (d) with
| DNode (uu___, uu___1, ups) -> begin
     (((mem r (inputs_taint scope_of ups)) && (not ((mem r (taint_of scope_of d))))) || (drop_occurred_list scope_of r ups))
     end))
and drop_occurred_list : (Prims.string  ->  opt<Prims.string>)  ->  Prims.string  ->  Prims.list<derivation>  ->  Prims.bool = (fun ( scope_of  :  Prims.string  ->  opt<Prims.string> ) ( r  :  Prims.string ) ( ds  :  Prims.list<derivation> ) -> (match (ds) with
| [] -> begin
     false
     end
| (d)::rest -> begin
     ((drop_occurred scope_of r d) || (drop_occurred_list scope_of r rest))
     end))

type release<'src> = {rel_base : 'src; rel_transforms : Prims.list<transform<'src>>; rel_routine : opt<routine>; rel_observers : Prims.list<Prims.string>}


let __proj__Mkrelease__item__rel_base = (fun ( projectee  :  release<'src> ) -> (match (projectee) with
| {rel_base = rel_base; rel_transforms = rel_transforms; rel_routine = rel_routine; rel_observers = rel_observers} -> begin
     rel_base
     end))


let __proj__Mkrelease__item__rel_transforms = (fun ( projectee  :  release<'src> ) -> (match (projectee) with
| {rel_base = rel_base; rel_transforms = rel_transforms; rel_routine = rel_routine; rel_observers = rel_observers} -> begin
     rel_transforms
     end))


let __proj__Mkrelease__item__rel_routine = (fun ( projectee  :  release<'src> ) -> (match (projectee) with
| {rel_base = rel_base; rel_transforms = rel_transforms; rel_routine = rel_routine; rel_observers = rel_observers} -> begin
     rel_routine
     end))


let __proj__Mkrelease__item__rel_observers = (fun ( projectee  :  release<'src> ) -> (match (projectee) with
| {rel_base = rel_base; rel_transforms = rel_transforms; rel_routine = rel_routine; rel_observers = rel_observers} -> begin
     rel_observers
     end))


type room<'src> = Prims.list<release<'src>>


let release_label = (fun ( scope_of  :  Prims.string  ->  opt<Prims.string> ) ( los  :  labelling<'src> ) ( rl  :  release<'src> ) -> (

let computed = (output_label los rl.rel_base rl.rel_transforms)
in (match (rl.rel_routine) with
| OSome (rt) -> begin
     (narrows_to (routine_clears scope_of rt) computed)
     end
| ONone -> begin
     computed
     end)))

type emission<'frame> = {ev_label : label; ev_value : 'frame; ev_observers : Prims.list<Prims.string>}


let __proj__Mkemission__item__ev_label = (fun ( projectee  :  emission<'frame> ) -> (match (projectee) with
| {ev_label = ev_label; ev_value = ev_value; ev_observers = ev_observers} -> begin
     ev_label
     end))


let __proj__Mkemission__item__ev_value = (fun ( projectee  :  emission<'frame> ) -> (match (projectee) with
| {ev_label = ev_label; ev_value = ev_value; ev_observers = ev_observers} -> begin
     ev_value
     end))


let __proj__Mkemission__item__ev_observers = (fun ( projectee  :  emission<'frame> ) -> (match (projectee) with
| {ev_label = ev_label; ev_value = ev_value; ev_observers = ev_observers} -> begin
     ev_observers
     end))


let rec run_room = (fun ( sem  :  semantics<'src, 'frame> ) ( scope_of  :  Prims.string  ->  opt<Prims.string> ) ( los  :  labelling<'src> ) ( assign  :  assignment<'src, 'frame> ) ( rm  :  room<'src> ) -> (match (rm) with
| [] -> begin
     []
     end
| (rl)::rest -> begin
     ({ev_label = (release_label scope_of los rl); ev_value = (run sem assign rl.rel_base rl.rel_transforms); ev_observers = rl.rel_observers})::(run_room sem scope_of los assign rest)
     end))


let rec slice_tr = (fun ( q  :  Prims.string ) ( tr  :  Prims.list<emission<'frame>> ) -> (match (tr) with
| [] -> begin
     []
     end
| (e)::rest -> begin
      
if (mem_of q e.ev_observers) then begin
     (e.ev_value)::(slice_tr q rest)
     end else begin
     (slice_tr q rest)
     end
     end))


let rec q_free_of = (fun ( los  :  labelling<'src> ) ( p  :  Prims.string ) ( q  :  Prims.string ) ( rm  :  room<'src> ) -> (match (rm) with
| [] -> begin
     true
     end
| (rl)::rest -> begin
     (((not ((mem_of q rl.rel_observers))) || (not ((mem p (output_label los rl.rel_base rl.rel_transforms))))) && (q_free_of los p q rest))
     end))




