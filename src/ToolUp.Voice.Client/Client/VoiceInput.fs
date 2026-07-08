// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Voice.Client.VoiceInput

open System
open Fable.Core
open Fable.Core.JsInterop
open Feliz
open ToolUp.Voice
open ToolUp.AI.Client

// ─── Phase 518 — the VoiceInput mic affordance ───────────────────
//
// A mic-toggle button attachable to any text input, with two capture
// modes:
//
//   • local  — the browser Web Speech API (`SpeechRecognition`) when
//              available. Interim results stream into the input; the
//              final result commits. Zero server round-trip, no key.
//   • remote — `MediaRecorder` captures a clip; on stop it is handed to a
//              consumer-supplied transcriber (backed server-side by a
//              composed `ITranscriptionProvider`) and the returned text
//              commits.
//
// Mode resolution (`VoiceCaptureMode`): `Auto` picks local when the
// browser supports it, else remote; `ForceLocal` / `ForceRemote` pin it.
//
// The component reads/drives the target input through a small callback
// surface (`get` / `setText` / `submit`) so it never owns the input's
// React state — this is what lets one mic attach to the AI chat prompt
// box, a Forms field, or any other input unchanged. MVU discipline:
// interim recognised text is transient display state pushed via `setText`
// (the same `React.useState`-backed path a text input uses); nothing
// dispatches until the input's own submit fires.

/// How a `VoiceInput` resolves its capture backend.
[<RequireQualifiedAccess>]
type VoiceCaptureMode =
    /// Local Web Speech API if the browser supports it, else remote.
    | Auto
    /// Always local; the mic is disabled if the browser lacks the API.
    | ForceLocal
    /// Always remote (`MediaRecorder` → the supplied transcriber).
    | ForceRemote

/// A consumer-supplied remote transcriber: captured audio bytes + their
/// MIME content type → recognised text (or an error string). A consumer
/// backs this with a Remoting call to a server endpoint that runs the
/// composed `ITranscriptionProvider` and projects `Transcript.plainText`.
/// `None` means no remote path is wired — the mic then runs local-only.
type RemoteTranscribe = byte[] -> string -> Async<Result<string, string>>

// ─── Browser interop (Web Speech + MediaRecorder) ────────────────

/// Whether the browser exposes the Web Speech `SpeechRecognition` API
/// (standard or `webkit`-prefixed). Guarded so the button can fall back
/// to remote / disable itself where it is absent (Firefox, older
/// browsers).
[<Emit("(typeof window !== 'undefined') && (('SpeechRecognition' in window) || ('webkitSpeechRecognition' in window))")>]
let localAvailable () : bool = jsNative

[<Emit("new (window.SpeechRecognition || window.webkitSpeechRecognition)()")>]
let private newRecognition () : obj = jsNative

/// Concatenate the *final* recognised spans in a `SpeechRecognition`
/// result event.
[<Emit("(function(ev){var s='';for(var i=0;i<ev.results.length;i++){if(ev.results[i].isFinal){s+=ev.results[i][0].transcript;}}return s;})($0)")>]
let private finalText (ev: obj) : string = jsNative

/// Concatenate the *interim* (unstable) recognised spans in a result event.
[<Emit("(function(ev){var s='';for(var i=0;i<ev.results.length;i++){if(!ev.results[i].isFinal){s+=ev.results[i][0].transcript;}}return s;})($0)")>]
let private interimText (ev: obj) : string = jsNative

/// Start a `MediaRecorder` capture. `onBytes` receives the recorded audio
/// + its MIME type on stop; `onError` receives a mic-permission / capture
/// failure. Returns a stop function that ends the capture (which triggers
/// `onBytes`). Delegates are uncurried, so the emitted JS calls them
/// positionally.
[<Emit("""(function(onBytes, onError){
  var ref = { rec: null, stream: null };
  (navigator.mediaDevices ? navigator.mediaDevices.getUserMedia({ audio: true }) : Promise.reject('mediaDevices unavailable'))
    .then(function(stream){
      ref.stream = stream;
      var rec = new MediaRecorder(stream);
      ref.rec = rec;
      var chunks = [];
      rec.ondataavailable = function(e){ if (e.data && e.data.size > 0) chunks.push(e.data); };
      rec.onstop = function(){
        var type = rec.mimeType || 'audio/webm';
        var blob = new Blob(chunks, { type: type });
        blob.arrayBuffer().then(function(buf){ onBytes(new Uint8Array(buf), type); });
        if (ref.stream) ref.stream.getTracks().forEach(function(t){ t.stop(); });
      };
      rec.start();
    })
    .catch(function(err){ onError(String(err)); });
  return function(){ if (ref.rec && ref.rec.state !== 'inactive') ref.rec.stop(); };
})($0, $1)""")>]
let private startRecording (onBytes: Action<byte[], string>) (onError: Action<string>) : (unit -> unit) = jsNative

// ─── Text helpers ─────────────────────────────────────────────────

/// Append recognised text to an existing draft with exactly one
/// separating space (no leading/trailing/double whitespace), so streaming
/// interim text into a draft the user already started reads cleanly.
let appendText (existing: string) (addition: string) : string =
    let baseText = if isNull existing then "" else existing.TrimEnd()
    let add = if isNull addition then "" else addition.Trim()

    match baseText, add with
    | "", a -> a
    | b, "" -> b
    | b, a -> b + " " + a

// ─── The mic button component ─────────────────────────────────────

/// The mic toggle. `get` reads the input's current draft (snapshotted when
/// the mic starts), `setText` streams recognised text in, `submit` is
/// available for callers that want a spoken utterance to send immediately
/// (unused by the default prompt-box wiring, which commits into the draft
/// and lets the user press Send). `disabled` reflects the host's busy
/// state.
[<ReactComponent>]
let MicButton
    (mode: VoiceCaptureMode)
    (remote: RemoteTranscribe option)
    (getText: unit -> string)
    (setText: string -> unit)
    (disabled: bool)
    =
    let listening, setListening = React.useState false
    // The draft text captured when the mic started, so streaming interim
    // results replace only the spoken portion, not the whole input.
    let baseTextRef = React.useRef ""
    // The live SpeechRecognition instance / the MediaRecorder stop thunk,
    // held so the toggle-off click can stop an in-flight capture.
    let recognitionRef = React.useRef<obj option> None
    let stopRecordingRef = React.useRef<(unit -> unit) option> None

    // Resolve the effective backend once per render from the mode + browser
    // capability + whether a remote transcriber is wired.
    let useLocal =
        match mode with
        | VoiceCaptureMode.ForceLocal -> true
        | VoiceCaptureMode.ForceRemote -> false
        | VoiceCaptureMode.Auto -> localAvailable ()

    // The mic is unusable when local is required but unavailable, or remote
    // is required but no transcriber is wired.
    let unavailable =
        (useLocal && not (localAvailable ())) || (not useLocal && remote.IsNone)

    let stopLocal () =
        match recognitionRef.current with
        | Some r ->
            try
                r?stop () |> ignore
            with _ ->
                ()

            recognitionRef.current <- None
        | None -> ()

    let startLocal () =
        baseTextRef.current <- getText ()
        let recognition = newRecognition ()
        recognition?continuous <- false
        recognition?interimResults <- true

        recognition?onresult <-
            (fun (ev: obj) ->
                let fin = finalText ev
                let interim = interimText ev
                let spoken = if fin <> "" then fin else interim
                setText (appendText baseTextRef.current spoken))

        recognition?onerror <- (fun (_: obj) -> setListening false)
        recognition?onend <- (fun (_: obj) -> setListening false)
        recognitionRef.current <- Some recognition
        recognition?start () |> ignore
        setListening true

    let startRemote (transcribe: RemoteTranscribe) =
        baseTextRef.current <- getText ()

        let onBytes =
            Action<byte[], string>(fun (bytes: byte[]) (contentType: string) ->
                setListening false

                async {
                    let! result = transcribe bytes contentType

                    match result with
                    | Ok text when text.Trim() <> "" -> setText (appendText baseTextRef.current text)
                    | _ -> ()
                }
                |> Async.StartImmediate)

        let onError = Action<string>(fun (_: string) -> setListening false)
        stopRecordingRef.current <- Some(startRecording onBytes onError)
        setListening true

    let stopRemote () =
        match stopRecordingRef.current with
        | Some stop ->
            (try
                stop ()
             with _ ->
                 ())

            stopRecordingRef.current <- None
        | None -> ()

    let toggle () =
        if listening then
            if useLocal then stopLocal () else stopRemote ()
            setListening false
        else if useLocal then
            startLocal ()
        else
            match remote with
            | Some transcribe -> startRemote transcribe
            | None -> ()

    Html.button [
        prop.type' "button"
        prop.title (
            if unavailable then
                "Voice input is not available in this browser"
            elif listening then
                "Stop dictation"
            else
                "Dictate with your voice"
        )
        prop.disabled (disabled || unavailable)
        prop.className [
            "px-3 py-2 rounded-lg text-sm font-medium transition-colors disabled:opacity-40"
            if listening then
                "bg-red-500 hover:bg-red-600 text-white animate-pulse"
            else
                "bg-gray-100 hover:bg-gray-200 text-gray-600"
        ]
        prop.onClick (fun _ -> toggle ())
        prop.text (if listening then "■" else "🎤") // ■ while recording, 🎤 otherwise
    ]

// ─── AI chat prompt-box opt-in ────────────────────────────────────

/// Opt in to a mic affordance on the AI chat prompt box. Default off —
/// call this once at client boot to register the mic into the
/// `ToolUp.AI.Client` prompt-accessory seam. Without this call the prompt
/// box is byte-for-byte unchanged (GP 13). `mode` picks the capture
/// backend; the local-only overload needs no server round-trip.
let registerPromptMicWithRemote (mode: VoiceCaptureMode) (remote: RemoteTranscribe option) : unit =
    PromptAccessoryBridge.setAccessory (
        Some(fun (ctx: PromptAccessoryBridge.PromptAccessoryContext) ->
            MicButton mode remote (fun () -> ctx.CurrentText) ctx.SetText ctx.IsBusy)
    )

/// Opt in to a **local-only** mic on the AI chat prompt box — the browser
/// Web Speech API, no server round-trip, no key. The mic disables itself
/// where the browser lacks the API.
let registerPromptMic (mode: VoiceCaptureMode) : unit = registerPromptMicWithRemote mode None