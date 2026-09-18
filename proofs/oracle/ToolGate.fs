module ToolGate
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


let rec mem = (fun ( x  :  'a ) ( xs  :  Prims.list<'a> ) -> (match (xs) with
| [] -> begin
     false
     end
| (y)::rest -> begin
     ((Prims.op_Equals x y) || (mem x rest))
     end))

type tool_effect =
| ReadFacts
| ComputeFacts
| ReadContent
| WriteState of Prims.string
| Egress of Prims.string
| Spend of Prims.string
| External of Prims.string
| EmitsActions


let uu___is_ReadFacts : tool_effect  ->  Prims.bool = (fun ( projectee  :  tool_effect ) -> (match (projectee) with
| ReadFacts -> begin
     true
     end
| uu___ -> begin
     false
     end))


let uu___is_ComputeFacts : tool_effect  ->  Prims.bool = (fun ( projectee  :  tool_effect ) -> (match (projectee) with
| ComputeFacts -> begin
     true
     end
| uu___ -> begin
     false
     end))


let uu___is_ReadContent : tool_effect  ->  Prims.bool = (fun ( projectee  :  tool_effect ) -> (match (projectee) with
| ReadContent -> begin
     true
     end
| uu___ -> begin
     false
     end))


let uu___is_WriteState : tool_effect  ->  Prims.bool = (fun ( projectee  :  tool_effect ) -> (match (projectee) with
| WriteState (scope) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__WriteState__item__scope : tool_effect  ->  Prims.string = (fun ( projectee  :  tool_effect ) -> (match (projectee) with
| WriteState (scope) -> begin
     scope
     end))


let uu___is_Egress : tool_effect  ->  Prims.bool = (fun ( projectee  :  tool_effect ) -> (match (projectee) with
| Egress (destination) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__Egress__item__destination : tool_effect  ->  Prims.string = (fun ( projectee  :  tool_effect ) -> (match (projectee) with
| Egress (destination) -> begin
     destination
     end))


let uu___is_Spend : tool_effect  ->  Prims.bool = (fun ( projectee  :  tool_effect ) -> (match (projectee) with
| Spend (budget_class) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__Spend__item__budget_class : tool_effect  ->  Prims.string = (fun ( projectee  :  tool_effect ) -> (match (projectee) with
| Spend (budget_class) -> begin
     budget_class
     end))


let uu___is_External : tool_effect  ->  Prims.bool = (fun ( projectee  :  tool_effect ) -> (match (projectee) with
| External (capability_id) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__External__item__capability_id : tool_effect  ->  Prims.string = (fun ( projectee  :  tool_effect ) -> (match (projectee) with
| External (capability_id) -> begin
     capability_id
     end))


let uu___is_EmitsActions : tool_effect  ->  Prims.bool = (fun ( projectee  :  tool_effect ) -> (match (projectee) with
| EmitsActions -> begin
     true
     end
| uu___ -> begin
     false
     end))

type effect_class =
| CReadFacts
| CComputeFacts
| CReadContent
| CWriteState
| CEgress
| CSpend
| CExternal
| CEmitsActions


let uu___is_CReadFacts : effect_class  ->  Prims.bool = (fun ( projectee  :  effect_class ) -> (match (projectee) with
| CReadFacts -> begin
     true
     end
| uu___ -> begin
     false
     end))


let uu___is_CComputeFacts : effect_class  ->  Prims.bool = (fun ( projectee  :  effect_class ) -> (match (projectee) with
| CComputeFacts -> begin
     true
     end
| uu___ -> begin
     false
     end))


let uu___is_CReadContent : effect_class  ->  Prims.bool = (fun ( projectee  :  effect_class ) -> (match (projectee) with
| CReadContent -> begin
     true
     end
| uu___ -> begin
     false
     end))


let uu___is_CWriteState : effect_class  ->  Prims.bool = (fun ( projectee  :  effect_class ) -> (match (projectee) with
| CWriteState -> begin
     true
     end
| uu___ -> begin
     false
     end))


let uu___is_CEgress : effect_class  ->  Prims.bool = (fun ( projectee  :  effect_class ) -> (match (projectee) with
| CEgress -> begin
     true
     end
| uu___ -> begin
     false
     end))


let uu___is_CSpend : effect_class  ->  Prims.bool = (fun ( projectee  :  effect_class ) -> (match (projectee) with
| CSpend -> begin
     true
     end
| uu___ -> begin
     false
     end))


let uu___is_CExternal : effect_class  ->  Prims.bool = (fun ( projectee  :  effect_class ) -> (match (projectee) with
| CExternal -> begin
     true
     end
| uu___ -> begin
     false
     end))


let uu___is_CEmitsActions : effect_class  ->  Prims.bool = (fun ( projectee  :  effect_class ) -> (match (projectee) with
| CEmitsActions -> begin
     true
     end
| uu___ -> begin
     false
     end))


let class_of : tool_effect  ->  effect_class = (fun ( e  :  tool_effect ) -> (match (e) with
| ReadFacts -> begin
     CReadFacts
     end
| ComputeFacts -> begin
     CComputeFacts
     end
| ReadContent -> begin
     CReadContent
     end
| WriteState (uu___) -> begin
     CWriteState
     end
| Egress (uu___) -> begin
     CEgress
     end
| Spend (uu___) -> begin
     CSpend
     end
| External (uu___) -> begin
     CExternal
     end
| EmitsActions -> begin
     CEmitsActions
     end))

type declaration =
| Undeclared
| Declared of Prims.list<tool_effect>


let uu___is_Undeclared : declaration  ->  Prims.bool = (fun ( projectee  :  declaration ) -> (match (projectee) with
| Undeclared -> begin
     true
     end
| uu___ -> begin
     false
     end))


let uu___is_Declared : declaration  ->  Prims.bool = (fun ( projectee  :  declaration ) -> (match (projectee) with
| Declared (effects) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__Declared__item__effects : declaration  ->  Prims.list<tool_effect> = (fun ( projectee  :  declaration ) -> (match (projectee) with
| Declared (effects) -> begin
     effects
     end))

type ceiling =
| Unbounded
| Bounded of Prims.list<effect_class>


let uu___is_Unbounded : ceiling  ->  Prims.bool = (fun ( projectee  :  ceiling ) -> (match (projectee) with
| Unbounded -> begin
     true
     end
| uu___ -> begin
     false
     end))


let uu___is_Bounded : ceiling  ->  Prims.bool = (fun ( projectee  :  ceiling ) -> (match (projectee) with
| Bounded (classes) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__Bounded__item__classes : ceiling  ->  Prims.list<effect_class> = (fun ( projectee  :  ceiling ) -> (match (projectee) with
| Bounded (classes) -> begin
     classes
     end))

type policy = {ceiling : ceiling; permit_undeclared : Prims.bool}


let __proj__Mkpolicy__item__ceiling : policy  ->  ceiling = (fun ( projectee  :  policy ) -> (match (projectee) with
| {ceiling = ceiling1; permit_undeclared = permit_undeclared} -> begin
     ceiling1
     end))


let __proj__Mkpolicy__item__permit_undeclared : policy  ->  Prims.bool = (fun ( projectee  :  policy ) -> (match (projectee) with
| {ceiling = ceiling1; permit_undeclared = permit_undeclared} -> begin
     permit_undeclared
     end))

type verdict =
| Admitted
| RefusedSource of Prims.string
| RefusedGrant of Prims.string
| RefusedUndeclared
| RefusedEffects of Prims.list<tool_effect>


let uu___is_Admitted : verdict  ->  Prims.bool = (fun ( projectee  :  verdict ) -> (match (projectee) with
| Admitted -> begin
     true
     end
| uu___ -> begin
     false
     end))


let uu___is_RefusedSource : verdict  ->  Prims.bool = (fun ( projectee  :  verdict ) -> (match (projectee) with
| RefusedSource (source_module) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__RefusedSource__item__source_module : verdict  ->  Prims.string = (fun ( projectee  :  verdict ) -> (match (projectee) with
| RefusedSource (source_module) -> begin
     source_module
     end))


let uu___is_RefusedGrant : verdict  ->  Prims.bool = (fun ( projectee  :  verdict ) -> (match (projectee) with
| RefusedGrant (source_module) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__RefusedGrant__item__source_module : verdict  ->  Prims.string = (fun ( projectee  :  verdict ) -> (match (projectee) with
| RefusedGrant (source_module) -> begin
     source_module
     end))


let uu___is_RefusedUndeclared : verdict  ->  Prims.bool = (fun ( projectee  :  verdict ) -> (match (projectee) with
| RefusedUndeclared -> begin
     true
     end
| uu___ -> begin
     false
     end))


let uu___is_RefusedEffects : verdict  ->  Prims.bool = (fun ( projectee  :  verdict ) -> (match (projectee) with
| RefusedEffects (exceeding) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__RefusedEffects__item__exceeding : verdict  ->  Prims.list<tool_effect> = (fun ( projectee  :  verdict ) -> (match (projectee) with
| RefusedEffects (exceeding) -> begin
     exceeding
     end))


let rec exceeding_in : Prims.list<effect_class>  ->  Prims.list<tool_effect>  ->  Prims.list<tool_effect> = (fun ( classes  :  Prims.list<effect_class> ) ( es  :  Prims.list<tool_effect> ) -> (match (es) with
| [] -> begin
     []
     end
| (e)::rest -> begin
      
if (mem (class_of e) classes) then begin
     (exceeding_in classes rest)
     end else begin
     (e)::(exceeding_in classes rest)
     end
     end))


let exceeding : ceiling  ->  Prims.list<tool_effect>  ->  Prims.list<tool_effect> = (fun ( c  :  ceiling ) ( es  :  Prims.list<tool_effect> ) -> (match (c) with
| Unbounded -> begin
     []
     end
| Bounded (classes) -> begin
     (exceeding_in classes es)
     end))


let decide : (Prims.string  ->  Prims.bool)  ->  (Prims.string  ->  Prims.bool)  ->  policy  ->  Prims.string  ->  declaration  ->  verdict = (fun ( source_permitted  :  Prims.string  ->  Prims.bool ) ( grant_live  :  Prims.string  ->  Prims.bool ) ( p  :  policy ) ( source_module  :  Prims.string ) ( d  :  declaration ) ->  
if (not ((source_permitted source_module))) then begin
     RefusedSource (source_module)
     end else begin
      
if (not ((grant_live source_module))) then begin
     RefusedGrant (source_module)
     end else begin
     (match (d) with
| Undeclared -> begin
      
if p.permit_undeclared then begin
     Admitted
     end else begin
     RefusedUndeclared
     end
     end
| Declared (es) -> begin
     (match ((exceeding p.ceiling es)) with
| [] -> begin
     Admitted
     end
| over -> begin
     RefusedEffects (over)
     end)
     end)
     end
     end)


let admits : verdict  ->  Prims.bool = (fun ( v  :  verdict ) -> (match (v) with
| Admitted -> begin
     true
     end
| uu___ -> begin
     false
     end))

type tool = {name : Prims.string; alias : Prims.string; source_module : Prims.string; effects : declaration}


let __proj__Mktool__item__name : tool  ->  Prims.string = (fun ( projectee  :  tool ) -> (match (projectee) with
| {name = name; alias = alias; source_module = source_module; effects = effects} -> begin
     name
     end))


let __proj__Mktool__item__alias : tool  ->  Prims.string = (fun ( projectee  :  tool ) -> (match (projectee) with
| {name = name; alias = alias; source_module = source_module; effects = effects} -> begin
     alias
     end))


let __proj__Mktool__item__source_module : tool  ->  Prims.string = (fun ( projectee  :  tool ) -> (match (projectee) with
| {name = name; alias = alias; source_module = source_module; effects = effects} -> begin
     source_module
     end))


let __proj__Mktool__item__effects : tool  ->  declaration = (fun ( projectee  :  tool ) -> (match (projectee) with
| {name = name; alias = alias; source_module = source_module; effects = effects} -> begin
     effects
     end))


let rec list_accessible : (Prims.string  ->  Prims.bool)  ->  (Prims.string  ->  Prims.bool)  ->  policy  ->  Prims.list<tool>  ->  Prims.list<tool> = (fun ( source_permitted  :  Prims.string  ->  Prims.bool ) ( grant_live  :  Prims.string  ->  Prims.bool ) ( p  :  policy ) ( ts  :  Prims.list<tool> ) -> (match (ts) with
| [] -> begin
     []
     end
| (t)::rest -> begin
      
if (admits (decide source_permitted grant_live p t.source_module t.effects)) then begin
     (t)::(list_accessible source_permitted grant_live p rest)
     end else begin
     (list_accessible source_permitted grant_live p rest)
     end
     end))


let rec find_by_name : Prims.list<tool>  ->  Prims.string  ->  opt<tool> = (fun ( ts  :  Prims.list<tool> ) ( n  :  Prims.string ) -> (match (ts) with
| [] -> begin
     ONone
     end
| (t)::rest -> begin
      
if ((Prims.op_Equals t.name n) || (Prims.op_Equals t.alias n)) then begin
     OSome (t)
     end else begin
     (find_by_name rest n)
     end
     end))


let dispatch_admits : (Prims.string  ->  Prims.bool)  ->  (Prims.string  ->  Prims.bool)  ->  policy  ->  Prims.list<tool>  ->  Prims.string  ->  Prims.bool = (fun ( source_permitted  :  Prims.string  ->  Prims.bool ) ( grant_live  :  Prims.string  ->  Prims.bool ) ( p  :  policy ) ( ts  :  Prims.list<tool> ) ( n  :  Prims.string ) -> (match ((find_by_name ts n)) with
| ONone -> begin
     false
     end
| OSome (t) -> begin
     (admits (decide source_permitted grant_live p t.source_module t.effects))
     end))




