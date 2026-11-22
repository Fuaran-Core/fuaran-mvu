// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Diametrical Ltd

/// The browser package's laws (Phase 719, t5; DECISIONS.md D30.9). Under the scripted interpreter of
/// `Fuaran.MVU.Testing`, each standard effect is a value compared without a browser; against a stand-in for the DOM,
/// each performer does what its kind says, an unregistered or refused kind performs nothing, and the browser host
/// queues, yields and reports as D30.7 says. Host-neutral: the same file runs on .NET and under Fable on Node, and its
/// last lines are the runner.
module Fuaran.MVU.Browser.Tests.BrowserLaws

open System
open System.Collections.Generic
open Fuaran.ClientEffects
open Fuaran.MVU
open Fuaran.MVU.Browser
open Fuaran.MVU.Testing
open Fuaran.MVU.Tests.Check

// ---------------------------------------------------------------------------------------------------------------
// The stand-in. Every DOM call is written to `Calls` as a sentence; a delay and a file read wait in a task queue the
// law runs when it chooses; an interval fires when the law ticks it; elements are addressed by id.
// ---------------------------------------------------------------------------------------------------------------

/// An element of the stand-in's document.
type Element(id: string) =
    member _.Id = id

/// The stand-in for the browser.
type StandIn() =
    let calls = ResizeArray<string>()
    let errors = ResizeArray<string>()
    let tasks = Queue<unit -> unit>()
    let intervals = Dictionary<int, unit -> unit>()
    let storage = Dictionary<string, string>()
    let elements = HashSet<string>()
    let files = Dictionary<string, string>()
    let mutable nextInterval = 0

    let areaKey (area: StorageArea) (key: string) =
        (match area with
         | StorageArea.Local -> "local:"
         | StorageArea.Session -> "session:")
        + key

    /// What the reader answers a confirmation.
    member val ConfirmAnswer = true with get, set

    /// Every DOM call, in order.
    member _.Calls: string list = List.ofSeq calls

    /// Every sentence written to the error console.
    member _.Errors: string list = List.ofSeq errors

    /// How many intervals are running.
    member _.Intervals = intervals.Count

    /// How many tasks wait.
    member _.Pending = tasks.Count

    /// Add an element; with `file`, it holds a selected file with that body.
    member _.AddElement(id: string, file: string option) =
        elements.Add id |> ignore

        match file with
        | Some body -> files[id] <- body
        | None -> ()

    /// Run every waiting task, including those the tasks queue.
    member _.RunTasks() =
        while tasks.Count > 0 do
            tasks.Dequeue () ()

    /// Fire every running interval once.
    member _.Tick() =
        for callback in List.ofSeq intervals.Values do
            callback ()

    /// The browser, as the performers and the host use it.
    member this.Dom: Dom = {
        WriteClipboard = fun text -> calls.Add("clipboard " + text)
        Assign = fun url -> calls.Add("assign " + url)
        OpenBlank = fun url -> calls.Add("open " + url)
        PushState = fun route -> calls.Add("push " + route)
        Resolve =
            fun nodeId ->
                if elements.Contains nodeId then
                    Some(box (Element nodeId))
                else
                    None
        Focus = fun element -> calls.Add("focus " + (unbox<Element> element).Id)
        Download = fun url name -> calls.Add("download " + url + " " + name)
        ReadFile =
            fun element encoding deliver ->
                let id = (unbox<Element> element).Id
                calls.Add("read " + id + " " + encoding)

                tasks.Enqueue(fun () ->
                    match files.TryGetValue id with
                    | true, body -> deliver (Ok(encoding + ":" + body))
                    | _ -> deliver (Error NoFile))
        Print = fun () -> calls.Add "print"
        Confirm =
            fun prompt ->
                calls.Add("confirm " + prompt)
                this.ConfirmAnswer
        StorageGet =
            fun area key ->
                match storage.TryGetValue(areaKey area key) with
                | true, value -> Some value
                | _ -> None
        StorageSet = fun area key value -> storage[areaKey area key] <- value
        StorageRemove = fun area key -> storage.Remove(areaKey area key) |> ignore
        Delay =
            fun milliseconds callback ->
                calls.Add(sprintf "delay %d" milliseconds)
                tasks.Enqueue callback
        Repeat =
            fun milliseconds callback ->
                let id = nextInterval
                nextInterval <- nextInterval + 1
                calls.Add(sprintf "repeat %d" milliseconds)
                intervals[id] <- callback

                { new IDisposable with
                    member _.Dispose() =
                        calls.Add(sprintf "clear %d" milliseconds)
                        intervals.Remove id |> ignore
                }
        Error = fun message -> errors.Add message
    }

    /// The DOM calls that are not the host's own scheduling.
    member this.Performed: string list =
        this.Calls |> List.filter (fun call -> call <> "delay 0")

// ---------------------------------------------------------------------------------------------------------------
// A component that runs whatever standard effects it is handed and keeps every answer.
// ---------------------------------------------------------------------------------------------------------------

type Msg =
    | Run of StandardEffect list
    | Answered of StandardAnswer
    | Ticking of bool
    | Noop

type Model = {
    Answers: StandardAnswer list
    Ticking: bool
}

let private runner: Component<unit, Model, Msg, StandardEffect, StandardResource, Model> =
    Component.plain
        (fun () -> { Answers = []; Ticking = false }, [])
        (fun msg model ->
            match msg with
            | Run effects -> model, List.map Do effects
            | Answered answer ->
                {
                    model with
                        Answers = model.Answers @ [ answer ]
                },
                []
            | Ticking on -> { model with Ticking = on }, []
            | Noop -> model, [])
        (fun model ->
            if model.Ticking then
                Map.ofList [ ResourceKey.constant "tick", Every("tick", 1000) ]
            else
                Map.empty)
        id

/// The runner mounted on the browser host over the stand-in, with `decide` and `fuel`.
let private mountWith
    (s: StandIn)
    (fuel: Fuel)
    (decide: Interpreter<Msg, StandardEffect, StandardResource> -> Decide<StandardEffect, StandardResource>)
    (interpreter: Interpreter<Msg, StandardEffect, StandardResource>)
    =
    let host = {
        BrowserHost.create s.Dom (HarnessOptions.countingClock ()) 7UL ignore with
            Fuel = fuel
            Decide = decide interpreter
    }

    BrowserHost.mount host runner interpreter ()

let private mountOn (s: StandIn) =
    mountWith s Fuel.defaults (fun _ -> Decide.allowAll) (StandardInterpreter.create s.Dom Answered)

let private run (effects: StandardEffect list) =
    let s = StandIn()
    let r = mountOn s
    r.Dispatch(Run effects, User "test") |> ignore
    s, r

/// The author of the envelope that carried `answer`.
let private authorOf (r: Runtime<_, Model, Msg, _, _, _>) (answer: StandardAnswer) : Principal option =
    r.Log
    |> List.tryPick (fun record ->
        match record.Of with
        | Step envelope when envelope.Msg = Answered answer -> Some envelope.Author
        | _ -> None)

let private every: StandardEffect list = [
    Client(WriteToClipboard "copied")
    Client(Navigate("/here", NavigateTarget.Self))
    Client(PushState "/state")
    Client(Focus "name")
    Client(Download("/report.csv", "report.csv"))
    Client(ReadFileBody("upload", "Text"))
    Client Print
    Client(Confirm("Sure?", "t1"))
    ReadStorage(StorageArea.Local, "k")
    WriteStorage(StorageArea.Session, "k", "v")
    RemoveStorage(StorageArea.Local, "k")
    After("soon", 50)
]

let private kindNamed (name: string) =
    Standard.kinds |> List.find (fun k -> k.Id = Standard.Prefix + name)

// ---------------------------------------------------------------------------------------------------------------
// The laws.
// ---------------------------------------------------------------------------------------------------------------

let cases: Case list = [
    case "every standard effect has a kind, registered once under the package's prefix" (fun expect ->
        let ids = Standard.kinds |> List.map (fun k -> k.Id)
        expect "thirteen kinds" (List.length ids = 13)
        expect "every id is distinct" (List.length (List.distinct ids) = 13)
        expect "every id is under the prefix" (ids |> List.forall (fun id -> id.StartsWith Standard.Prefix))

        for effect in every do
            expect (sprintf "%s is registered" (Standard.kindOf effect)) (List.contains (Standard.kindOf effect) ids)

        expect "the repeating timer is registered" (List.contains Standard.EveryKind ids)

        let registered: Result<Gate<StandardEffect, StandardResource>, string> =
            Gate.create Standard.kinds [] (fun _ -> []) Gate.allowAll

        expect "Core's registry takes every kind" registered.IsOk)

    case "the client kinds are named by the vocabulary's discriminator" (fun expect ->
        for effect in every do
            match effect with
            | Client clientEffect ->
                expect
                    (sprintf "%A" clientEffect)
                    (Standard.kindOf effect = Standard.Prefix + ClientEffect.kind clientEffect)
            | _ -> ())

    case "each kind registers with the classes D30.2 decides" (fun expect ->
        let classes name = (kindNamed name).Classes

        let is name (expected: string list) =
            expect name (classes name = Set.ofList expected)

        is "WriteToClipboard" [ Standard.Writes ]
        is "Navigate" [ Standard.Navigation ]
        is "PushState" [ Standard.Navigation ]
        is "Focus" [ Standard.UserVisible ]
        is "Download" [ Standard.Writes; Standard.UserVisible ]
        is "ReadFileBody" [ Standard.Reads ]
        is "Print" [ Standard.UserVisible ]
        is "Confirm" [ Standard.UserVisible ]
        is "ReadStorage" [ Standard.Reads ]
        is "WriteStorage" [ Standard.Writes ]
        is "RemoveStorage" [ Standard.Writes ]
        is "After" []
        is "Every" [])

    case "a kind that answers is a call, one that does not is a signal, the repeating timer is a stream" (fun expect ->
        for name in [ "ReadFileBody"; "Confirm"; "ReadStorage"; "After" ] do
            expect (name + " is a call") ((kindNamed name).Shape = Call)

        for name in
            [
                "WriteToClipboard"
                "Navigate"
                "PushState"
                "Focus"
                "Download"
                "Print"
                "WriteStorage"
                "RemoveStorage"
            ] do
            expect (name + " is a signal") ((kindNamed name).Shape = Shape.Signal)

        expect "Every is a stream" ((kindNamed "Every").Shape = Stream)
        expect "a file's body is foreign content" ((kindNamed "ReadFileBody").Answers = Untrusted "file")
        expect "storage is foreign content" ((kindNamed "ReadStorage").Answers = Untrusted "storage")
        expect "a confirmation is the host's" ((kindNamed "Confirm").Answers = FirstParty)
        expect "the timers read the clock" ((kindNamed "After").Determinism = Set.ofList [ Clock ]))

    case "an invocation carries the wire encoding and goes where its URL goes" (fun expect ->
        let copy = Client(WriteToClipboard "x")
        let i = Standard.invocation copy

        expect
            "a client effect's arguments are its encoding"
            (i.Args = Some(ClientEffect.encode (WriteToClipboard "x")))

        expect "the clipboard is local" (i.Destination = Local)

        let away =
            Standard.invocation (Client(Navigate("HTTPS://Example.com:8443/a?b#c", NavigateTarget.Blank)))

        expect "an absolute URL goes to its origin" (away.Destination = Remote "https://example.com:8443")

        let relative = Standard.invocation (Client(Navigate("/a/b", NavigateTarget.Self)))
        expect "a route goes nowhere" (relative.Destination = Local)

        let cdn = Standard.invocation (Client(Download("//cdn.example/f.csv", "f.csv")))
        expect "a scheme-relative URL goes to its host" (cdn.Destination = Remote "//cdn.example")

        let read = Standard.invocation (ReadStorage(StorageArea.Local, "k"))
        expect "storage's arguments are canonical" (read.Args = Some """{"area":"Local","key":"k"}""")

        let timer = Standard.invocation (After("t", 5))
        expect "the timer's arguments are canonical" (timer.Args = Some """{"label":"t","milliseconds":5}"""))

    case "under the scripted interpreter each standard effect is a value compared without a browser" (fun expect ->
        // An application's own effect type, with the standard vocabulary as one case.
        let comp: Component<unit, string list, Msg, Choice<StandardEffect, string>, NoResource, unit> =
            Component.plain
                (fun () -> [], [])
                (fun msg log ->
                    match msg with
                    | Run effects -> log, effects |> List.map (fun e -> Do(Choice1Of2 e))
                    | Answered(Confirmed(token, yes)) ->
                        log @ [ sprintf "%s %b" token yes ], [ Do(Choice2Of2 "save") ]
                    | _ -> log, [])
                (fun _ -> Map.empty)
                ignore

        let options = {
            HarnessOptions.defaults with
                Kinds = Standard.kinds @ [ HarnessOptions.testKind ]
                Describe =
                    function
                    | Choice1Of2 standard -> Standard.invocation standard
                    | Choice2Of2 _ -> {
                        Kind = "test"
                        Args = None
                        Destination = Local
                      }
                Answers =
                    function
                    | Choice1Of2(Client(Confirm _)) -> Outstanding
                    | _ -> NoAnswer
        }

        let h = Harness.startWith options comp ()

        h.Send(Run [ Client(WriteToClipboard "hello"); Client(Confirm("Delete?", "del")) ])
        |> ignore

        expect
            "the effects are the values the step asked for"
            (h.Performed = [
                Choice1Of2(Client(WriteToClipboard "hello"))
                Choice1Of2(Client(Confirm("Delete?", "del")))
            ])

        expect
            "the confirmation waits for its answer"
            (h.Outstanding = [ Choice1Of2(Client(Confirm("Delete?", "del"))) ])

        h.Answer(Answered(Confirmed("del", true)))
        expect "the answer is a new step" (h.Model = [ "del true" ])
        expect "the answer's own effect is the application's" (List.last h.Performed = Choice2Of2 "save"))

    case
        "the vocabulary embeds as one case: standard kinds first, the application's effects to its own interpreter"
        (fun expect ->
            let s = StandIn()
            let saved = ResizeArray<string>()

            let saveKind = {
                HarnessOptions.testKind with
                    Id = "app/save"
            }

            let own: Interpreter<Msg, Choice<StandardEffect, string>, NoResource> =
                Interpreter.create
                    [ saveKind ]
                    (fun effect -> {
                        Invocation = {
                            Kind = "app/save"
                            Args = None
                            Destination = Local
                        }
                        Perform =
                            fun () ->
                                Signal(fun () ->
                                    match effect with
                                    | Choice2Of2 what -> saved.Add what
                                    | Choice1Of2 _ -> ())
                    })
                    (fun _ _ -> invalidOp "no resource")

            let embedded =
                StandardInterpreter.embed
                    s.Dom
                    Answered
                    (function
                    | Choice1Of2 e -> Some e
                    | _ -> None)
                    (fun _ -> None)
                    own

            expect "the standard kinds come first" (embedded.Kinds = Standard.kinds @ [ saveKind ])

            let comp: Component<unit, unit, Msg, Choice<StandardEffect, string>, NoResource, unit> =
                Component.plain
                    (fun () -> (), [])
                    (fun msg () ->
                        match msg with
                        | Run effects ->
                            (), (effects |> List.map (fun e -> Do(Choice1Of2 e))) @ [ Do(Choice2Of2 "doc") ]
                        | _ -> (), [])
                    (fun _ -> Map.empty)
                    ignore

            let r =
                BrowserHost.mount
                    (BrowserHost.create s.Dom (HarnessOptions.countingClock ()) 1UL ignore)
                    comp
                    embedded
                    ()

            r.Dispatch(Run [ Client Print ], User "test") |> ignore
            expect "the standard effect reached the standard performer" (s.Performed = [ "print" ])
            expect "the application's effect reached its own" (List.ofSeq saved = [ "doc" ]))

    case "each signal performer does what its kind says" (fun expect ->
        let s = StandIn()
        s.AddElement("name", None)
        let r = mountOn s

        r.Dispatch(
            Run [
                Client(WriteToClipboard "copied")
                Client(Navigate("/here", NavigateTarget.Self))
                Client(Navigate("https://example.com/", NavigateTarget.Blank))
                Client(PushState "/state")
                Client(Focus "name")
                Client(Focus "missing")
                Client(Download("/report.csv", "report.csv"))
                Client Print
            ],
            User "test"
        )
        |> ignore

        expect
            "the DOM calls, in emission order"
            (s.Performed = [
                "clipboard copied"
                "assign /here"
                "open https://example.com/"
                "push /state"
                "focus name"
                "download /report.csv report.csv"
                "print"
            ])

        expect "nothing was answered" (r.Model.Answers = [])
        expect "nothing went wrong" (s.Errors = []))

    case "a confirmation asks once and answers once, with its token, authored by the host" (fun expect ->
        let s = StandIn()
        s.ConfirmAnswer <- false
        let r = mountOn s
        r.Dispatch(Run [ Client(Confirm("Sure?", "t1")) ], User "test") |> ignore
        expect "it asked" (s.Performed = [ "confirm Sure?" ])
        expect "it answered" (r.Model.Answers = [ Confirmed("t1", false) ])
        expect "the host wrote the answer" (authorOf r (Confirmed("t1", false)) = Some Host))

    case "a file read answers once with the body, or with why not, authored as foreign" (fun expect ->
        let s = StandIn()
        s.AddElement("upload", Some "body")
        s.AddElement("empty", None)
        let r = mountOn s

        r.Dispatch(
            Run [
                Client(ReadFileBody("upload", "Base64"))
                Client(ReadFileBody("missing", "Text"))
                Client(ReadFileBody("upload", "Hex"))
                Client(ReadFileBody("empty", "Text"))
            ],
            User "test"
        )
        |> ignore

        expect
            "an unresolved address and an unknown encoding answer at once"
            (r.Model.Answers = [
                FileBody("missing", "Text", Error NoNode)
                FileBody("upload", "Hex", Error UnknownEncoding)
            ])

        s.RunTasks()
        let body = FileBody("upload", "Base64", Ok "Base64:body")
        expect "the body arrives when the reader finishes" (List.contains body r.Model.Answers)

        expect
            "a node with no file answers so"
            (List.contains (FileBody("empty", "Text", Error NoFile)) r.Model.Answers)

        expect "four answers, one each" (List.length r.Model.Answers = 4)
        expect "the body is foreign" (authorOf r body = Some(Foreign "file")))

    case "storage reads what was written and answers as foreign content" (fun expect ->
        let s, r =
            run [
                WriteStorage(StorageArea.Local, "k", "v")
                ReadStorage(StorageArea.Local, "k")
                ReadStorage(StorageArea.Session, "k")
                RemoveStorage(StorageArea.Local, "k")
                ReadStorage(StorageArea.Local, "k")
            ]

        expect
            "the reads, in order"
            (r.Model.Answers = [
                Stored(StorageArea.Local, "k", Some "v")
                Stored(StorageArea.Session, "k", None)
                Stored(StorageArea.Local, "k", None)
            ])

        expect
            "the answer is foreign"
            (authorOf r (Stored(StorageArea.Local, "k", Some "v")) = Some(Foreign "storage"))

        expect "no DOM call beyond storage" (s.Performed = []))

    case "the one-shot timer answers once, after its delay" (fun expect ->
        let s, r = run [ After("soon", 50) ]
        expect "it waits" (s.Calls = [ "delay 50" ] && r.Model.Answers = [])
        s.RunTasks()
        expect "it answered" (r.Model.Answers = [ Elapsed "soon" ]))

    case
        "the repeating timer is a resource: it ticks while the model lists it, and stops when it does not"
        (fun expect ->
            let s = StandIn()
            let r = mountOn s
            r.Dispatch(Ticking true, User "test") |> ignore
            expect "started" (s.Intervals = 1 && s.Calls = [ "repeat 1000" ])
            s.Tick()
            s.Tick()
            expect "two ticks" (r.Model.Answers = [ Ticked "tick"; Ticked "tick" ])
            r.Dispatch(Ticking false, User "test") |> ignore
            expect "stopped" (s.Intervals = 0 && List.last s.Calls = "clear 1000")
            s.Tick()
            expect "no tick after the stop" (List.length r.Model.Answers = 2))

    case "a kind the gate refuses for the author performs nothing, and the step is refused whole" (fun expect ->
        let s = StandIn()

        let restrictions principal =
            match principal with
            | User _ -> [ Refusing(Set.ofList [ Standard.Navigation ]) ]
            | _ -> []

        let decide interpreter =
            match Gate.create Standard.kinds [] restrictions Gate.allowAll with
            | Error why -> failwith why
            | Ok gate ->
                match Gate.mount gate interpreter with
                | Ok d -> d
                | Error missing -> failwithf "uncovered: %A" missing

        let r = mountWith s Fuel.defaults decide (StandardInterpreter.create s.Dom Answered)

        r.Dispatch(Run [ Client(WriteToClipboard "a"); Client(Navigate("/x", NavigateTarget.Self)) ], User "u")
        |> ignore

        expect "nothing was performed" (s.Performed = [])
        expect "the step was refused" (List.length r.Refusals = 1)
        r.Dispatch(Run [ Client(WriteToClipboard "b") ], User "u") |> ignore
        expect "an allowed kind is performed" (s.Performed = [ "clipboard b" ])
        r.Dispatch(Run [ Client(PushState "/y") ], Host) |> ignore
        expect "the host is not restricted" (List.last s.Performed = "push /y"))

    case
        "a kind the gate does not hold for the author, or the interpreter does not register, performs nothing"
        (fun expect ->
            let s = StandIn()
            let clipboard = Standard.Prefix + "WriteToClipboard"

            let only principal =
                match principal with
                | User _ -> [
                    Only(
                        Standard.kinds
                        |> List.map (fun k -> k.Id)
                        |> List.filter ((<>) clipboard)
                        |> Set.ofList
                    )
                  ]
                | _ -> []

            let decide interpreter =
                match Gate.create Standard.kinds [] only Gate.allowAll with
                | Error why -> failwith why
                | Ok gate -> Gate.decide gate

            let r = mountWith s Fuel.defaults decide (StandardInterpreter.create s.Dom Answered)

            r.Dispatch(Run [ Client Print; Client(WriteToClipboard "a") ], User "u")
            |> ignore

            expect "outside the author's registry: nothing performed" (s.Performed = [])

            let unregistered = {
                StandardInterpreter.create s.Dom Answered with
                    Kinds = Standard.kinds |> List.filter (fun k -> k.Id <> clipboard)
            }

            let r2 = mountWith s Fuel.defaults (fun _ -> Decide.allowAll) unregistered

            r2.Dispatch(Run [ Client Print; Client(WriteToClipboard "a") ], User "u")
            |> ignore

            expect "unregistered: nothing performed" (s.Performed = [])

            expect
                "unregistered: refused as such"
                (r2.Refusals
                 |> List.exists (fun refusal ->
                     match refusal with
                     | Refusal.Unregistered kind -> kind = clipboard
                     | _ -> false)))

    case
        "the browser host queues on the kernel's growing queue, and an answer during a step runs in the same drain"
        (fun expect ->
            let s = StandIn()
            let host = BrowserHost.create s.Dom (HarnessOptions.countingClock ()) 1UL ignore

            expect
                "a growing queue"
                (match host.Queue with
                 | Growing _ -> true
                 | Bounded _ -> false)

            let r = BrowserHost.mount host runner (StandardInterpreter.create s.Dom Answered) ()
            let before = r.Renders

            r.Dispatch(Run [ Client(Confirm("a", "1")); Client(Confirm("b", "2")) ], User "test")
            |> ignore

            expect "both answers were processed" (r.Model.Answers = [ Confirmed("1", true); Confirmed("2", true) ])
            expect "in one drain, with one render" (r.Renders = before + 1)
            expect "nothing was scheduled" (s.Pending = 0))

    case "a yielded drain continues in a task of its own, after the latch is released" (fun expect ->
        let s = StandIn()
        let fuel = { Fuel.defaults with Drain = 2 }

        let r =
            mountWith s fuel (fun _ -> Decide.allowAll) (StandardInterpreter.create s.Dom Answered)

        let confirms = [ for i in 1..4 -> Client(Confirm("q", string i)) ]
        r.Dispatch(Run confirms, User "test") |> ignore
        expect "the drain yielded at its fuel" (r.Yields = 1)
        expect "one answer was processed before the yield" (List.length r.Model.Answers = 1)
        expect "one continuation waits" (s.Pending = 1)
        s.RunTasks()
        expect "the continuations took the rest" (List.length r.Model.Answers = 4)
        expect "in order" (r.Model.Answers = [ for i in 1..4 -> Confirmed(string i, true) ]))

    case "a continuation that finds the queue empty opens no drain and renders nothing" (fun expect ->
        let s = StandIn()
        let fuel = { Fuel.defaults with Drain = 3 }

        let r =
            mountWith s fuel (fun _ -> Decide.allowAll) (StandardInterpreter.create s.Dom Answered)

        r.Dispatch(Run [ for i in 1..4 -> Client(Confirm("q", string i)) ], User "test")
        |> ignore

        expect "the drain yielded" (r.Yields = 1 && s.Pending = 1)
        r.Dispatch(Noop, User "test") |> ignore
        expect "a dispatch took the rest" (List.length r.Model.Answers = 4)
        let renders = r.Renders
        s.RunTasks()
        expect "the continuation rendered nothing" (r.Renders = renders))

    case "a performer that throws is reported, and the step's other effects keep their order" (fun expect ->
        let s, r =
            run [
                Client(WriteToClipboard "a")
                Client(Navigate("  JavaScript:alert(1)", NavigateTarget.Self))
                Client(Download("javascript:void(0)", "x"))
                Client(WriteToClipboard "b")
                Client(Confirm("c", "t"))
            ]

        expect "the others ran, in order" (s.Performed = [ "clipboard a"; "clipboard b"; "confirm c" ])
        expect "the confirmation still answered" (r.Model.Answers = [ Confirmed("t", true) ])
        expect "two reports reached the console" (List.length s.Errors = 2)

        expect
            "each names the throw"
            (s.Errors
             |> List.forall (fun e -> e.Contains "threw" && e.Contains "javascript:"))

        expect
            "the runtime counts both"
            (r.Refusals
             |> List.filter (fun refusal ->
                 match refusal with
                 | Refusal.PerformerThrew _ -> true
                 | _ -> false)
             |> List.length = 2))

    case "the host words what went wrong, and nothing that is the runtime working as designed" (fun expect ->
        let yielded: Report<Msg, StandardEffect, StandardResource> = Yielded 3
        expect "a yield is not an error" (BrowserHost.describe yielded = None)

        let late: Report<Msg, StandardEffect, StandardResource> =
            EnvelopeNotAccepted(Revoked, Host, External)

        expect "a late answer is not an error" (BrowserHost.describe late = None)

        let lost: Report<Msg, StandardEffect, StandardResource> =
            ResourceLost(ResourceKey.constant "tick", Every("tick", 1), exn "no timers")

        expect "a lost resource is" ((BrowserHost.describe lost).IsSome))
]

// ---------------------------------------------------------------------------------------------------------------
// Under Fable only: the live binding against stubbed globals. Node has no DOM, so the globals the binding names are
// stubbed here in JavaScript; the law shows the emitted calls reach them with the arguments in place, not that a real
// browser does what the stubs do (D30.9).
// ---------------------------------------------------------------------------------------------------------------

#if FABLE_COMPILER
open Fable.Core.JsInterop

let private liveCases: Case list = [
    case "under Node, the live binding calls the globals it names" (fun expect ->
        emitJsStatement
            ()
            """
                const calls = [];
                globalThis.__liveCalls = calls;
                const store = () => { const m = new Map(); return { getItem: k => m.has(k) ? m.get(k) : null, setItem: (k, v) => { m.set(k, String(v)); }, removeItem: k => { m.delete(k); } }; };
                globalThis.localStorage = store();
                globalThis.sessionStorage = store();
                Object.defineProperty(globalThis, 'navigator', { value: { clipboard: { writeText: t => { calls.push('clipboard ' + t); return Promise.resolve(); } } }, configurable: true, writable: true });
                globalThis.window = {
                    location: { assign: u => calls.push('assign ' + u) },
                    open: (u, t, f) => calls.push('open ' + u + ' ' + t + ' ' + f),
                    history: { pushState: (s, t, u) => calls.push('push ' + u) },
                    print: () => calls.push('print'),
                    confirm: q => { calls.push('confirm ' + q); return false; }
                };
                globalThis.document = {
                    getElementById: id => id === 'x' ? { focus: () => calls.push('focus x') } : null,
                    createElement: tag => { const a = { click: () => calls.push('download ' + a.href + ' ' + a.download + ' ' + a.rel), remove: () => {} }; return a; },
                    body: { appendChild: () => {} }
                };
                """

        let dom = Live.dom ()
        dom.WriteClipboard "t"
        dom.Assign "/a"
        dom.OpenBlank "/b"
        dom.PushState "/c"

        match dom.Resolve "x" with
        | Some element -> dom.Focus element
        | None -> expect "the element resolved" false

        expect "an unknown id resolves to nothing" (dom.Resolve "y" = None)
        dom.Print()
        expect "the reader's answer" (not (dom.Confirm "q"))
        dom.StorageSet StorageArea.Local "k" "v"
        expect "storage reads back" (dom.StorageGet StorageArea.Local "k" = Some "v")
        expect "the areas are apart" (dom.StorageGet StorageArea.Session "k" = None)
        dom.StorageRemove StorageArea.Local "k"
        expect "a removed key reads nothing" (dom.StorageGet StorageArea.Local "k" = None)
        dom.Download "/f.csv" "f.csv"
        let calls: string[] = emitJsExpr () "globalThis.__liveCalls"

        expect
            "the calls, in order"
            (List.ofArray calls = [
                "clipboard t"
                "assign /a"
                "open /b _blank noopener,noreferrer"
                "push /c"
                "focus x"
                "print"
                "confirm q"
                "download /f.csv f.csv noopener"
            ])

        let host: Host<Model, Msg, StandardEffect, StandardResource, Model> =
            Live.host ignore

        expect "the live host reads the browser's clock" (host.Clock() > 0L))
]
#else
let private liveCases: Case list = []
#endif

// ---------------------------------------------------------------------------------------------------------------
// The runner.
// ---------------------------------------------------------------------------------------------------------------

let private failed = runAll "browser-laws" (cases @ liveCases)

if failed > 0 then
    failwithf "%d browser law(s) failed" failed

printfn "browser: every law passed"
