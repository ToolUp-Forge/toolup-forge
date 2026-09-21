module ElmishLoop
type ev<'m> =
| Msg of 'm
| Term


let uu___is_Msg = (fun ( projectee  :  ev<'m> ) -> (match (projectee) with
| Msg (msg) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__Msg__item__msg = (fun ( projectee  :  ev<'m> ) -> (match (projectee) with
| Msg (msg) -> begin
     msg
     end))


let uu___is_Term = (fun ( projectee  :  ev<'m> ) -> (match (projectee) with
| Term -> begin
     true
     end
| uu___ -> begin
     false
     end))

type ext<'m> =
| XDispatch of 'm
| XTerminate


let uu___is_XDispatch = (fun ( projectee  :  ext<'m> ) -> (match (projectee) with
| XDispatch (msg) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__XDispatch__item__msg = (fun ( projectee  :  ext<'m> ) -> (match (projectee) with
| XDispatch (msg) -> begin
     msg
     end))


let uu___is_XTerminate = (fun ( projectee  :  ext<'m> ) -> (match (projectee) with
| XTerminate -> begin
     true
     end
| uu___ -> begin
     false
     end))

type st<'m, 'md> = {ring : ElmishRing.ring<'m>; reentered : Prims.bool; terminated : Prims.bool; active : Prims.bool; model : 'md; trace : Prims.list<'m>; log : Prims.list<'m>}


let __proj__Mkst__item__ring = (fun ( projectee  :  st<'m, 'md> ) -> (match (projectee) with
| {ring = ring; reentered = reentered; terminated = terminated; active = active; model = model; trace = trace; log = log} -> begin
     ring
     end))


let __proj__Mkst__item__reentered = (fun ( projectee  :  st<'m, 'md> ) -> (match (projectee) with
| {ring = ring; reentered = reentered; terminated = terminated; active = active; model = model; trace = trace; log = log} -> begin
     reentered
     end))


let __proj__Mkst__item__terminated = (fun ( projectee  :  st<'m, 'md> ) -> (match (projectee) with
| {ring = ring; reentered = reentered; terminated = terminated; active = active; model = model; trace = trace; log = log} -> begin
     terminated
     end))


let __proj__Mkst__item__active = (fun ( projectee  :  st<'m, 'md> ) -> (match (projectee) with
| {ring = ring; reentered = reentered; terminated = terminated; active = active; model = model; trace = trace; log = log} -> begin
     active
     end))


let __proj__Mkst__item__model = (fun ( projectee  :  st<'m, 'md> ) -> (match (projectee) with
| {ring = ring; reentered = reentered; terminated = terminated; active = active; model = model; trace = trace; log = log} -> begin
     model
     end))


let __proj__Mkst__item__trace = (fun ( projectee  :  st<'m, 'md> ) -> (match (projectee) with
| {ring = ring; reentered = reentered; terminated = terminated; active = active; model = model; trace = trace; log = log} -> begin
     trace
     end))


let __proj__Mkst__item__log = (fun ( projectee  :  st<'m, 'md> ) -> (match (projectee) with
| {ring = ring; reentered = reentered; terminated = terminated; active = active; model = model; trace = trace; log = log} -> begin
     log
     end))


let initial = (fun ( capacity  :  Prims.int ) ( model  :  'md ) -> {ring = (ElmishRing.create capacity); reentered = false; terminated = false; active = true; model = model; trace = []; log = []})


let enqueue = (fun ( s  :  st<'m, 'md> ) ( msg  :  'm ) ->  
if s.terminated then begin
     s
     end else begin
     {ring = (ElmishRing.push msg s.ring); reentered = s.reentered; terminated = s.terminated; active = s.active; model = s.model; trace = s.trace; log = (ElmishRing.append s.log ((msg)::[]))}
     end)


let terminate = (fun ( s  :  st<'m, 'md> ) ->  
if s.terminated then begin
     s
     end else begin
     {ring = s.ring; reentered = s.reentered; terminated = true; active = false; model = s.model; trace = s.trace; log = s.log}
     end)


let apply_ev = (fun ( s  :  st<'m, 'md> ) ( e  :  ev<'m> ) -> (match (e) with
| Msg (msg) -> begin
     (enqueue s msg)
     end
| Term -> begin
     (terminate s)
     end))


let rec apply_evs = (fun ( s  :  st<'m, 'md> ) ( evs  :  Prims.list<ev<'m>> ) -> (match (evs) with
| [] -> begin
     s
     end
| (e)::rest -> begin
     (apply_evs (apply_ev s e) rest)
     end))


let step = (fun ( update  :  'm  ->  'md  ->  ElmishRing.pair<'md, Prims.list<ev<'m>>> ) ( to_terminate  :  'm  ->  Prims.bool ) ( msg  :  'm ) ( s  :  st<'m, 'md> ) ->  
if (to_terminate msg) then begin
     (terminate s)
     end else begin
     (

let uu___ = (update msg s.model)
in (match (uu___) with
| ElmishRing.Pair (model', evs) -> begin
     (

let s1 = {ring = s.ring; reentered = s.reentered; terminated = s.terminated; active = s.active; model = s.model; trace = (ElmishRing.append s.trace ((msg)::[])); log = s.log}
in (

let s2 = (apply_evs s1 evs)
in {ring = s2.ring; reentered = s2.reentered; terminated = s2.terminated; active = s2.active; model = model'; trace = s2.trace; log = s2.log}))
     end))
     end)


let rec loop = (fun ( fuel  :  Prims.nat ) ( update  :  'm  ->  'md  ->  ElmishRing.pair<'md, Prims.list<ev<'m>>> ) ( to_terminate  :  'm  ->  Prims.bool ) ( s  :  st<'m, 'md> ) ( next  :  ElmishRing.opt<ElmishRing.slot<'m>> ) ->  
if s.terminated then begin
     ElmishRing.Pair (s, true)
     end else begin
     (match (next) with
| ElmishRing.ONone -> begin
     ElmishRing.Pair (s, true)
     end
| ElmishRing.OSome (ElmishRing.Placeholder) -> begin
     ElmishRing.Pair (s, true)
     end
| ElmishRing.OSome (ElmishRing.Written (msg)) -> begin
     (

let s' = (step update to_terminate msg s)
in  
if (Prims.op_Equals fuel (Prims.parse_int "0")) then begin
     ElmishRing.Pair (s', false)
     end else begin
     (

let uu___ = (ElmishRing.pop s'.ring)
in (match (uu___) with
| ElmishRing.Pair (r', next') -> begin
     (loop (fuel - (Prims.parse_int "1")) update to_terminate {ring = r'; reentered = s'.reentered; terminated = s'.terminated; active = s'.active; model = s'.model; trace = s'.trace; log = s'.log} next')
     end))
     end)
     end)
     end)


let process_msgs = (fun ( fuel  :  Prims.nat ) ( update  :  'm  ->  'md  ->  ElmishRing.pair<'md, Prims.list<ev<'m>>> ) ( to_terminate  :  'm  ->  Prims.bool ) ( s  :  st<'m, 'md> ) -> (

let uu___ = (ElmishRing.pop s.ring)
in (match (uu___) with
| ElmishRing.Pair (r, next) -> begin
     (loop fuel update to_terminate {ring = r; reentered = s.reentered; terminated = s.terminated; active = s.active; model = s.model; trace = s.trace; log = s.log} next)
     end)))


let critical = (fun ( fuel  :  Prims.nat ) ( update  :  'm  ->  'md  ->  ElmishRing.pair<'md, Prims.list<ev<'m>>> ) ( to_terminate  :  'm  ->  Prims.bool ) ( s  :  st<'m, 'md> ) -> (

let uu___ = (process_msgs fuel update to_terminate {ring = s.ring; reentered = true; terminated = s.terminated; active = s.active; model = s.model; trace = s.trace; log = s.log})
in (match (uu___) with
| ElmishRing.Pair (s', finished) -> begin
      
if finished then begin
     {ring = s'.ring; reentered = false; terminated = s'.terminated; active = s'.active; model = s'.model; trace = s'.trace; log = s'.log}
     end else begin
     s'
     end
     end)))


let dispatch = (fun ( fuel  :  Prims.nat ) ( update  :  'm  ->  'md  ->  ElmishRing.pair<'md, Prims.list<ev<'m>>> ) ( to_terminate  :  'm  ->  Prims.bool ) ( s  :  st<'m, 'md> ) ( msg  :  'm ) ->  
if s.terminated then begin
     s
     end else begin
     (

let s1 = {ring = (ElmishRing.push msg s.ring); reentered = s.reentered; terminated = s.terminated; active = s.active; model = s.model; trace = s.trace; log = (ElmishRing.append s.log ((msg)::[]))}
in  
if s1.reentered then begin
     s1
     end else begin
     (critical fuel update to_terminate s1)
     end)
     end)


let boot_ev = (fun ( fuel  :  Prims.nat ) ( update  :  'm  ->  'md  ->  ElmishRing.pair<'md, Prims.list<ev<'m>>> ) ( to_terminate  :  'm  ->  Prims.bool ) ( s  :  st<'m, 'md> ) ( e  :  ev<'m> ) -> (match (e) with
| Msg (msg) -> begin
     (dispatch fuel update to_terminate s msg)
     end
| Term -> begin
     (terminate s)
     end))


let rec boot_evs = (fun ( fuel  :  Prims.nat ) ( update  :  'm  ->  'md  ->  ElmishRing.pair<'md, Prims.list<ev<'m>>> ) ( to_terminate  :  'm  ->  Prims.bool ) ( s  :  st<'m, 'md> ) ( evs  :  Prims.list<ev<'m>> ) -> (match (evs) with
| [] -> begin
     s
     end
| (e)::rest -> begin
     (boot_evs fuel update to_terminate (boot_ev fuel update to_terminate s e) rest)
     end))


let boot = (fun ( fuel  :  Prims.nat ) ( update  :  'm  ->  'md  ->  ElmishRing.pair<'md, Prims.list<ev<'m>>> ) ( to_terminate  :  'm  ->  Prims.bool ) ( s  :  st<'m, 'md> ) ( evs  :  Prims.list<ev<'m>> ) -> (

let s1 = {ring = s.ring; reentered = true; terminated = s.terminated; active = s.active; model = s.model; trace = s.trace; log = s.log}
in (

let s2 = (boot_evs fuel update to_terminate s1 evs)
in (

let uu___ = (process_msgs fuel update to_terminate s2)
in (match (uu___) with
| ElmishRing.Pair (s3, finished) -> begin
      
if finished then begin
     {ring = s3.ring; reentered = false; terminated = s3.terminated; active = s3.active; model = s3.model; trace = s3.trace; log = s3.log}
     end else begin
     s3
     end
     end)))))


let rec run = (fun ( fuel  :  Prims.nat ) ( update  :  'm  ->  'md  ->  ElmishRing.pair<'md, Prims.list<ev<'m>>> ) ( to_terminate  :  'm  ->  Prims.bool ) ( s  :  st<'m, 'md> ) ( exts  :  Prims.list<ext<'m>> ) ->  
if s.reentered then begin
     s
     end else begin
     (match (exts) with
| [] -> begin
     s
     end
| (XDispatch (msg))::rest -> begin
     (run fuel update to_terminate (dispatch fuel update to_terminate s msg) rest)
     end
| (XTerminate)::rest -> begin
     (run fuel update to_terminate (terminate s) rest)
     end)
     end)


let program = (fun ( fuel  :  Prims.nat ) ( update  :  'm  ->  'md  ->  ElmishRing.pair<'md, Prims.list<ev<'m>>> ) ( to_terminate  :  'm  ->  Prims.bool ) ( capacity  :  Prims.int ) ( model  :  'md ) ( init_evs  :  Prims.list<ev<'m>> ) ( exts  :  Prims.list<ext<'m>> ) -> (run fuel update to_terminate (boot fuel update to_terminate (initial capacity model) init_evs) exts))


let fallback_terminate = (fun ( s  :  st<'m, 'md> ) -> {ring = s.ring; reentered = s.reentered; terminated = s.terminated; active = false; model = s.model; trace = s.trace; log = s.log})




