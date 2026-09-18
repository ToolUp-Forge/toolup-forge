module ModelInput
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

type outcome<'a> =
| Admitted of 'a
| Refused of Prims.string


let uu___is_Admitted = (fun ( projectee  :  outcome<'a> ) -> (match (projectee) with
| Admitted (item) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__Admitted__item__item = (fun ( projectee  :  outcome<'a> ) -> (match (projectee) with
| Admitted (item) -> begin
     item
     end))


let uu___is_Refused = (fun ( projectee  :  outcome<'a> ) -> (match (projectee) with
| Refused (reason) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__Refused__item__reason = (fun ( projectee  :  outcome<'a> ) -> (match (projectee) with
| Refused (reason) -> begin
     reason
     end))


let rec append = (fun ( xs  :  Prims.list<'a> ) ( ys  :  Prims.list<'a> ) -> (match (xs) with
| [] -> begin
     ys
     end
| (x)::rest -> begin
     (x)::(append rest ys)
     end))


let rec join : Prims.string  ->  Prims.list<Prims.string>  ->  Prims.string = (fun ( sep  :  Prims.string ) ( parts  :  Prims.list<Prims.string> ) -> (match (parts) with
| [] -> begin
     ""
     end
| (p)::[] -> begin
     p
     end
| (p)::rest -> begin
     (Prims.strcat p (Prims.strcat sep (join sep rest)))
     end))


let rec mem_id : Prims.string  ->  Prims.list<Prims.string>  ->  Prims.bool = (fun ( id  :  Prims.string ) ( ids  :  Prims.list<Prims.string> ) -> (match (ids) with
| [] -> begin
     false
     end
| (x)::rest -> begin
      
if (Prims.op_Equals x id) then begin
     true
     end else begin
     (mem_id id rest)
     end
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


let refusal_text : Prims.string  ->  Prims.string = (fun ( policy_ref  :  Prims.string ) -> (Prims.strcat "computed, but not disclosable under policy " policy_ref))

type disclosed_fact = {fact_id : Prims.string; fact_value : Prims.string; fact_verdict : verdict; fact_scope : Prims.string}


let __proj__Mkdisclosed_fact__item__fact_id : disclosed_fact  ->  Prims.string = (fun ( projectee  :  disclosed_fact ) -> (match (projectee) with
| {fact_id = fact_id; fact_value = fact_value; fact_verdict = fact_verdict; fact_scope = fact_scope} -> begin
     fact_id
     end))


let __proj__Mkdisclosed_fact__item__fact_value : disclosed_fact  ->  Prims.string = (fun ( projectee  :  disclosed_fact ) -> (match (projectee) with
| {fact_id = fact_id; fact_value = fact_value; fact_verdict = fact_verdict; fact_scope = fact_scope} -> begin
     fact_value
     end))


let __proj__Mkdisclosed_fact__item__fact_verdict : disclosed_fact  ->  verdict = (fun ( projectee  :  disclosed_fact ) -> (match (projectee) with
| {fact_id = fact_id; fact_value = fact_value; fact_verdict = fact_verdict; fact_scope = fact_scope} -> begin
     fact_verdict
     end))


let __proj__Mkdisclosed_fact__item__fact_scope : disclosed_fact  ->  Prims.string = (fun ( projectee  :  disclosed_fact ) -> (match (projectee) with
| {fact_id = fact_id; fact_value = fact_value; fact_verdict = fact_verdict; fact_scope = fact_scope} -> begin
     fact_scope
     end))

type gate_result =
| GatePassed
| GateFailed of Prims.string
| NotGated


let uu___is_GatePassed : gate_result  ->  Prims.bool = (fun ( projectee  :  gate_result ) -> (match (projectee) with
| GatePassed -> begin
     true
     end
| uu___ -> begin
     false
     end))


let uu___is_GateFailed : gate_result  ->  Prims.bool = (fun ( projectee  :  gate_result ) -> (match (projectee) with
| GateFailed (reason) -> begin
     true
     end
| uu___ -> begin
     false
     end))


let __proj__GateFailed__item__reason : gate_result  ->  Prims.string = (fun ( projectee  :  gate_result ) -> (match (projectee) with
| GateFailed (reason) -> begin
     reason
     end))


let uu___is_NotGated : gate_result  ->  Prims.bool = (fun ( projectee  :  gate_result ) -> (match (projectee) with
| NotGated -> begin
     true
     end
| uu___ -> begin
     false
     end))

type chunk = {chunk_id : Prims.string; chunk_text : Prims.string; chunk_gate : gate_result; chunk_source : opt<Prims.string>}


let __proj__Mkchunk__item__chunk_id : chunk  ->  Prims.string = (fun ( projectee  :  chunk ) -> (match (projectee) with
| {chunk_id = chunk_id; chunk_text = chunk_text; chunk_gate = chunk_gate; chunk_source = chunk_source} -> begin
     chunk_id
     end))


let __proj__Mkchunk__item__chunk_text : chunk  ->  Prims.string = (fun ( projectee  :  chunk ) -> (match (projectee) with
| {chunk_id = chunk_id; chunk_text = chunk_text; chunk_gate = chunk_gate; chunk_source = chunk_source} -> begin
     chunk_text
     end))


let __proj__Mkchunk__item__chunk_gate : chunk  ->  gate_result = (fun ( projectee  :  chunk ) -> (match (projectee) with
| {chunk_id = chunk_id; chunk_text = chunk_text; chunk_gate = chunk_gate; chunk_source = chunk_source} -> begin
     chunk_gate
     end))


let __proj__Mkchunk__item__chunk_source : chunk  ->  opt<Prims.string> = (fun ( projectee  :  chunk ) -> (match (projectee) with
| {chunk_id = chunk_id; chunk_text = chunk_text; chunk_gate = chunk_gate; chunk_source = chunk_source} -> begin
     chunk_source
     end))

type prompt_block = {block_builder_id : Prims.string; block_text : Prims.string}


let __proj__Mkprompt_block__item__block_builder_id : prompt_block  ->  Prims.string = (fun ( projectee  :  prompt_block ) -> (match (projectee) with
| {block_builder_id = block_builder_id; block_text = block_text} -> begin
     block_builder_id
     end))


let __proj__Mkprompt_block__item__block_text : prompt_block  ->  Prims.string = (fun ( projectee  :  prompt_block ) -> (match (projectee) with
| {block_builder_id = block_builder_id; block_text = block_text} -> begin
     block_text
     end))

type tool_result = {tool_name : Prims.string; tool_content : Prims.string}


let __proj__Mktool_result__item__tool_name : tool_result  ->  Prims.string = (fun ( projectee  :  tool_result ) -> (match (projectee) with
| {tool_name = tool_name; tool_content = tool_content} -> begin
     tool_name
     end))


let __proj__Mktool_result__item__tool_content : tool_result  ->  Prims.string = (fun ( projectee  :  tool_result ) -> (match (projectee) with
| {tool_name = tool_name; tool_content = tool_content} -> begin
     tool_content
     end))

type message = {message_role : Prims.string; message_content : Prims.string; message_tool_results : Prims.list<tool_result>}


let __proj__Mkmessage__item__message_role : message  ->  Prims.string = (fun ( projectee  :  message ) -> (match (projectee) with
| {message_role = message_role; message_content = message_content; message_tool_results = message_tool_results} -> begin
     message_role
     end))


let __proj__Mkmessage__item__message_content : message  ->  Prims.string = (fun ( projectee  :  message ) -> (match (projectee) with
| {message_role = message_role; message_content = message_content; message_tool_results = message_tool_results} -> begin
     message_content
     end))


let __proj__Mkmessage__item__message_tool_results : message  ->  Prims.list<tool_result> = (fun ( projectee  :  message ) -> (match (projectee) with
| {message_role = message_role; message_content = message_content; message_tool_results = message_tool_results} -> begin
     message_tool_results
     end))

type model_input = {input_facts : Prims.list<disclosed_fact>; input_chunks : Prims.list<chunk>; input_prompt_blocks : Prims.list<prompt_block>; input_tool_results : Prims.list<tool_result>; input_messages : Prims.list<message>}


let __proj__Mkmodel_input__item__input_facts : model_input  ->  Prims.list<disclosed_fact> = (fun ( projectee  :  model_input ) -> (match (projectee) with
| {input_facts = input_facts; input_chunks = input_chunks; input_prompt_blocks = input_prompt_blocks; input_tool_results = input_tool_results; input_messages = input_messages} -> begin
     input_facts
     end))


let __proj__Mkmodel_input__item__input_chunks : model_input  ->  Prims.list<chunk> = (fun ( projectee  :  model_input ) -> (match (projectee) with
| {input_facts = input_facts; input_chunks = input_chunks; input_prompt_blocks = input_prompt_blocks; input_tool_results = input_tool_results; input_messages = input_messages} -> begin
     input_chunks
     end))


let __proj__Mkmodel_input__item__input_prompt_blocks : model_input  ->  Prims.list<prompt_block> = (fun ( projectee  :  model_input ) -> (match (projectee) with
| {input_facts = input_facts; input_chunks = input_chunks; input_prompt_blocks = input_prompt_blocks; input_tool_results = input_tool_results; input_messages = input_messages} -> begin
     input_prompt_blocks
     end))


let __proj__Mkmodel_input__item__input_tool_results : model_input  ->  Prims.list<tool_result> = (fun ( projectee  :  model_input ) -> (match (projectee) with
| {input_facts = input_facts; input_chunks = input_chunks; input_prompt_blocks = input_prompt_blocks; input_tool_results = input_tool_results; input_messages = input_messages} -> begin
     input_tool_results
     end))


let __proj__Mkmodel_input__item__input_messages : model_input  ->  Prims.list<message> = (fun ( projectee  :  model_input ) -> (match (projectee) with
| {input_facts = input_facts; input_chunks = input_chunks; input_prompt_blocks = input_prompt_blocks; input_tool_results = input_tool_results; input_messages = input_messages} -> begin
     input_messages
     end))

type provider_messages = {rendered_system_prompt : opt<Prims.string>; rendered_messages : Prims.list<message>}


let __proj__Mkprovider_messages__item__rendered_system_prompt : provider_messages  ->  opt<Prims.string> = (fun ( projectee  :  provider_messages ) -> (match (projectee) with
| {rendered_system_prompt = rendered_system_prompt; rendered_messages = rendered_messages} -> begin
     rendered_system_prompt
     end))


let __proj__Mkprovider_messages__item__rendered_messages : provider_messages  ->  Prims.list<message> = (fun ( projectee  :  provider_messages ) -> (match (projectee) with
| {rendered_system_prompt = rendered_system_prompt; rendered_messages = rendered_messages} -> begin
     rendered_messages
     end))


let empty : model_input = {input_facts = []; input_chunks = []; input_prompt_blocks = []; input_tool_results = []; input_messages = []}


let try_add_fact : disclosed_fact  ->  outcome<disclosed_fact> = (fun ( f  :  disclosed_fact ) -> (match (f.fact_verdict) with
| Disclosable -> begin
     Admitted (f)
     end
| NotDisclosable (policy_ref) -> begin
     Refused ((Prims.strcat "Fact " (Prims.strcat f.fact_id (Prims.strcat " refused: " (refusal_text policy_ref)))))
     end))


let add_fact : disclosed_fact  ->  model_input  ->  outcome<model_input> = (fun ( f  :  disclosed_fact ) ( input  :  model_input ) -> (match ((try_add_fact f)) with
| Refused (reason) -> begin
     Refused (reason)
     end
| Admitted (admitted) -> begin
     Admitted ({input_facts = (append input.input_facts ((admitted)::[])); input_chunks = input.input_chunks; input_prompt_blocks = input.input_prompt_blocks; input_tool_results = input.input_tool_results; input_messages = input.input_messages})
     end))


let add_chunk : chunk  ->  model_input  ->  model_input = (fun ( c  :  chunk ) ( input  :  model_input ) -> {input_facts = input.input_facts; input_chunks = (append input.input_chunks ((c)::[])); input_prompt_blocks = input.input_prompt_blocks; input_tool_results = input.input_tool_results; input_messages = input.input_messages})


let add_block : prompt_block  ->  model_input  ->  model_input = (fun ( b  :  prompt_block ) ( input  :  model_input ) -> {input_facts = input.input_facts; input_chunks = input.input_chunks; input_prompt_blocks = (append input.input_prompt_blocks ((b)::[])); input_tool_results = input.input_tool_results; input_messages = input.input_messages})


let add_tool_result : tool_result  ->  model_input  ->  model_input = (fun ( t  :  tool_result ) ( input  :  model_input ) -> {input_facts = input.input_facts; input_chunks = input.input_chunks; input_prompt_blocks = input.input_prompt_blocks; input_tool_results = (append input.input_tool_results ((t)::[])); input_messages = input.input_messages})


let rec tool_results_of : Prims.list<message>  ->  Prims.list<tool_result> = (fun ( ms  :  Prims.list<message> ) -> (match (ms) with
| [] -> begin
     []
     end
| (m)::rest -> begin
     (append m.message_tool_results (tool_results_of rest))
     end))


let with_messages : Prims.list<message>  ->  model_input  ->  model_input = (fun ( ms  :  Prims.list<message> ) ( input  :  model_input ) -> {input_facts = input.input_facts; input_chunks = input.input_chunks; input_prompt_blocks = input.input_prompt_blocks; input_tool_results = (tool_results_of ms); input_messages = ms})


let of_system_prompt : Prims.string  ->  opt<Prims.string>  ->  Prims.list<message>  ->  model_input = (fun ( builder_id  :  Prims.string ) ( system_prompt  :  opt<Prims.string> ) ( ms  :  Prims.list<message> ) -> (

let with_blocks = (match (system_prompt) with
| ONone -> begin
     empty
     end
| OSome (text) -> begin
     (add_block {block_builder_id = builder_id; block_text = text} empty)
     end)
in (with_messages ms with_blocks)))


let rec block_texts : Prims.list<prompt_block>  ->  Prims.list<Prims.string> = (fun ( blocks  :  Prims.list<prompt_block> ) -> (match (blocks) with
| [] -> begin
     []
     end
| (b)::rest -> begin
     (b.block_text)::(block_texts rest)
     end))


let rec tool_contents : Prims.list<tool_result>  ->  Prims.list<Prims.string> = (fun ( ts  :  Prims.list<tool_result> ) -> (match (ts) with
| [] -> begin
     []
     end
| (t)::rest -> begin
     (t.tool_content)::(tool_contents rest)
     end))


let render : model_input  ->  provider_messages = (fun ( input  :  model_input ) -> {rendered_system_prompt = (match (input.input_prompt_blocks) with
| [] -> begin
     ONone
     end
| blocks -> begin
     OSome ((join "\n\n" (block_texts blocks)))
     end); rendered_messages = input.input_messages})


let rec message_texts : Prims.list<message>  ->  Prims.list<Prims.string> = (fun ( ms  :  Prims.list<message> ) -> (match (ms) with
| [] -> begin
     []
     end
| (m)::rest -> begin
     (m.message_content)::(append (tool_contents m.message_tool_results) (message_texts rest))
     end))


let rendered_text : model_input  ->  Prims.string = (fun ( input  :  model_input ) -> (

let r = (render input)
in (

let head = (match (r.rendered_system_prompt) with
| ONone -> begin
     ""
     end
| OSome (s) -> begin
     s
     end)
in (join "\n" ((head)::(message_texts r.rendered_messages))))))


let rec fact_ids : Prims.list<disclosed_fact>  ->  Prims.list<Prims.string> = (fun ( fs  :  Prims.list<disclosed_fact> ) -> (match (fs) with
| [] -> begin
     []
     end
| (f)::rest -> begin
     (f.fact_id)::(fact_ids rest)
     end))


let disclosed_fact_ids : model_input  ->  Prims.list<Prims.string> = (fun ( input  :  model_input ) -> (fact_ids input.input_facts))


let rec leaked_of : (Prims.string  ->  Prims.string  ->  Prims.bool)  ->  Prims.string  ->  Prims.list<Prims.string>  ->  Prims.list<Prims.string>  ->  Prims.list<Prims.string> = (fun ( contains  :  Prims.string  ->  Prims.string  ->  Prims.bool ) ( text  :  Prims.string ) ( declared  :  Prims.list<Prims.string> ) ( candidates  :  Prims.list<Prims.string> ) -> (match (candidates) with
| [] -> begin
     []
     end
| (id)::rest -> begin
      
if (contains text id) then begin
      
if (mem_id id declared) then begin
     (leaked_of contains text declared rest)
     end else begin
     (id)::(leaked_of contains text declared rest)
     end
     end else begin
     (leaked_of contains text declared rest)
     end
     end))


let leaked_fact_ids : (Prims.string  ->  Prims.string  ->  Prims.bool)  ->  Prims.list<Prims.string>  ->  model_input  ->  Prims.list<Prims.string> = (fun ( contains  :  Prims.string  ->  Prims.string  ->  Prims.bool ) ( candidates  :  Prims.list<Prims.string> ) ( input  :  model_input ) -> (leaked_of contains (rendered_text input) (disclosed_fact_ids input) candidates))


let rec any_failed : Prims.list<chunk>  ->  Prims.bool = (fun ( cs  :  Prims.list<chunk> ) -> (match (cs) with
| [] -> begin
     false
     end
| (c)::rest -> begin
      
if (match (c.chunk_gate) with
| GateFailed (reason) -> begin
     true
     end
| uu___ -> begin
     false
     end) then begin
     true
     end else begin
     (any_failed rest)
     end
     end))


let has_failed_gates : model_input  ->  Prims.bool = (fun ( input  :  model_input ) -> (any_failed input.input_chunks))


let disclosable_for : Prims.string  ->  disclosed_fact  ->  Prims.bool = (fun ( scope  :  Prims.string ) ( f  :  disclosed_fact ) ->  
if (Prims.op_Equals f.fact_scope scope) then begin
     (match (f.fact_verdict) with
| Disclosable -> begin
     true
     end
| uu___ -> begin
     false
     end)
     end else begin
     false
     end)


let rec visible : Prims.string  ->  Prims.list<disclosed_fact>  ->  Prims.list<disclosed_fact> = (fun ( scope  :  Prims.string ) ( store  :  Prims.list<disclosed_fact> ) -> (match (store) with
| [] -> begin
     []
     end
| (f)::rest -> begin
      
if (disclosable_for scope f) then begin
     (f)::(visible scope rest)
     end else begin
     (visible scope rest)
     end
     end))


let rec admit_all : Prims.string  ->  Prims.list<disclosed_fact>  ->  model_input  ->  model_input = (fun ( scope  :  Prims.string ) ( store  :  Prims.list<disclosed_fact> ) ( input  :  model_input ) -> (match (store) with
| [] -> begin
     input
     end
| (f)::rest -> begin
      
if (Prims.op_Equals f.fact_scope scope) then begin
     (match ((add_fact f input)) with
| Admitted (next) -> begin
     (admit_all scope rest next)
     end
| Refused (uu___) -> begin
     (admit_all scope rest input)
     end)
     end else begin
     (admit_all scope rest input)
     end
     end))


let build : (Prims.list<disclosed_fact>  ->  Prims.string)  ->  Prims.string  ->  Prims.string  ->  Prims.list<disclosed_fact>  ->  Prims.list<message>  ->  model_input = (fun ( render_facts  :  Prims.list<disclosed_fact>  ->  Prims.string ) ( builder_id  :  Prims.string ) ( scope  :  Prims.string ) ( store  :  Prims.list<disclosed_fact> ) ( ms  :  Prims.list<message> ) -> (

let admitted = (admit_all scope store empty)
in (

let blocked = (add_block {block_builder_id = builder_id; block_text = (render_facts admitted.input_facts)} admitted)
in (with_messages ms blocked))))




