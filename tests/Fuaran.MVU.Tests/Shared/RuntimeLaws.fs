// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Diametrical Ltd

/// The runtime's laws, one per numbered rule of the design's runtime semantics (Phase 647, t5): acceptance
/// (rules 1 to 3), the step (4 to 11), the drain (12 to 15), resources (16 to 22) and termination (23 to
/// 25), each named for the rule it is about. Over the shipped code alone, with a probing interpreter that
/// records what the runtime asked of it, in order. Host-neutral: the Expecto suite and the Node runner
/// both run them.
module Fuaran.MVU.Tests.RuntimeLaws

#nowarn "3391"

open System
open Fuaran.MVU
open Fuaran.MVU.Testing
open Fuaran.MVU.Tests.Check

/// The probe's component: integer messages, effects and resources, so each case writes its own `update`.
type Msg =
    | M of int
    | Reply of int
    | Loop
    | Refusal of string
    | Lost of int

type Model = {
    Seen: int list
    Wants: (int * int) list
    Same: bool
}

type Eff =
    | E of int
    | Boom

type Res = R of int

let private key (k: int) = ResourceKey.constant (string k)

let private registered: EffectKind list = [
    {
        Id = "first-party"
        Shape = Call
        Classes = Set.empty
        Determinism = Set.empty
        Answers = FirstParty
        Replies = []
    }
    {
        Id = "model-call"
        Shape = Call
        Classes = Set.empty
        Determinism = Set.empty
        Answers = Untrusted "model"
        Replies = []
    }
]

let private invocation (kind: string) : Invocation = {
    Kind = kind
    Args = None
    Destination = Local
}

/// A probe: the interpreter records every call in `Log`, performs effects as the test says, and lets the
/// test emit through started instances or throw from a start.
type Probe(?answer: int -> Performance<Msg>, ?startThrows: int -> bool, ?kindOf: int -> string) =
    let log = ResizeArray<string>()
    let emitters = ResizeArray<int * (Msg -> unit)>()
    let mutable drainEnded: (unit -> unit) = ignore

    member _.Log: string list = List.ofSeq log

    member _.Clear() = log.Clear()

    member _.Emit(k: int, msg: Msg) =
        match Seq.tryFindBack (fun (rk, _) -> rk = k) emitters with
        | Some(_, emit) -> emit msg
        | None -> invalidOp "no instance for that key was started"

    member _.LastEmitter(k: int) : Msg -> unit =
        match Seq.tryFindBack (fun (rk, _) -> rk = k) emitters with
        | Some(_, emit) -> emit
        | None -> invalidOp "no instance for that key was started"

    member _.OnDrainEnded(f: unit -> unit) = drainEnded <- f

    member _.Interpreter: Interpreter<Msg, Eff, Res> = {
        Kinds = registered
        Effect =
            fun e -> {
                Invocation =
                    invocation (
                        match e with
                        | E n -> (defaultArg kindOf (fun _ -> "first-party")) n
                        | Boom -> "first-party"
                    )
                Perform =
                    fun () ->
                        match e with
                        | Boom -> failwith "the performer threw"
                        | E n ->
                            log.Add(sprintf "perform %d" n)

                            match answer with
                            | Some f -> f n
                            | None -> Signal ignore
            }
        Resource =
            fun k (R d) -> {
                Invocation = invocation "first-party"
                Start =
                    fun emit ->
                        let kn = int (List.head (ResourceKey.segments k))

                        if (defaultArg startThrows (fun _ -> false)) kn then
                            failwith "the start threw"

                        log.Add(sprintf "start %d/%d" kn d)
                        emitters.Add((kn, emit))

                        { new IDisposable with
                            member _.Dispose() = log.Add(sprintf "stop %d/%d" kn d)
                        }
            }
        Refused =
            fun refusal ->
                log.Add "refused"

                match refusal with
                | Denied reason -> Some(Refusal reason)
                | Refusal.PerformerThrew _ -> Some(Refusal "performer")
                | _ -> None
        Lost = fun k _ _ -> Some(Lost(int (List.head (ResourceKey.segments k))))
        DrainEnded = fun () -> drainEnded ()
    }

let private empty = { Seen = []; Wants = []; Same = false }

/// A component whose `update` appends the message code to `Seen` and emits the steps `steps` names, and
/// whose `Resources` are the model's `Wants`.
let private subject (steps: Msg -> Model -> Step<Msg, Eff> list) (want: Msg -> Model -> (int * int) list) = {
    Init = fun () -> empty, []
    Update =
        fun _ msg m ->
            let code =
                match msg with
                | M n -> n
                | Reply n -> 1000 + n
                | Loop -> -1
                | Refusal _ -> -2
                | Lost k -> -100 - k

            let m' = { m with Seen = m.Seen @ [ code ] }
            { m' with Wants = want msg m' }, steps msg m'
    Resources = fun m -> m.Wants |> List.map (fun (k, d) -> key k, R d) |> Map.ofList
    View = fun m -> m.Seen
}

let private noWants _ (m: Model) = m.Wants

/// A runtime over the probe with the host tweaked.
let private start
    (comp: Component<unit, Model, Msg, Eff, Res, int list>)
    (probe: Probe)
    (tweak: Host<Model, Msg, Eff, Res, int list> -> Host<Model, Msg, Eff, Res, int list>)
    =
    let views = ResizeArray<int list>()
    let reports = ResizeArray<Report<Msg, Eff, Res>>()

    let host =
        tweak {
            Host.create (fun () -> 1L) 1UL views.Add with
                Report = reports.Add
        }

    let runtime = Runtime(comp, probe.Interpreter, host, ())
    runtime.Start()
    runtime, views, reports

let private user = User "u"

/// An expectation that names what it saw.
let private same (expect: string -> bool -> unit) (label: string) (actual: 'a) (expected: 'a) =
    expect (sprintf "%s: got %A" label actual) (actual = expected)

/// The helpers of the public surface the ported checks and the rules do not reach, each exercised once.
let private surface: Case list = [
    case "surface: Step.map, mapAll and followUps project a step list" (fun expect ->
        let steps: Step<int, string> list = [ Do "a"; Then 1; Do "b"; Then 2 ]
        let mapped = Step.mapAll (fun n -> n * 10) (fun (s: string) -> s + "!") steps
        expect "mapped" (mapped = [ Do "a!"; Then 10; Do "b!"; Then 20 ])

        expect
            "map is mapAll on one"
            (Step.map (fun n -> n * 10) (fun (s: string) -> s + "!") (List.head steps) = List.head mapped)

        expect "effects" (Step.effects steps = [ "a"; "b" ])
        expect "followUps" (Step.followUps steps = [ 1; 2 ]))
    case "surface: Interpreter.create's hooks do nothing, and tryKind finds a kind by id" (fun expect ->
        let interp: Interpreter<Msg, Eff, Res> =
            Interpreter.create registered (fun _ -> failwith "unused") (fun _ _ -> failwith "unused")

        expect "Refused gives nothing" ((interp.Refused Stale).IsNone)
        expect "Lost gives nothing" ((interp.Lost (key 1) (R 0) (exn "x")).IsNone)
        interp.DrainEnded()

        expect
            "tryKind"
            ((Interpreter.tryKind "model-call" interp |> Option.map (fun k -> k.Answers)) = Some(Untrusted "model")
             && (Interpreter.tryKind "none" interp).IsNone))
    case
        "surface: Handled.mapMsg and Started.mapMsg lift a child's performers into the parent's message type"
        (fun expect ->
            let handled: Handled<int> = {
                Invocation = invocation "first-party"
                Perform = fun () -> Fuaran.MVU.Reply(fun deliver -> deliver 7)
            }

            let delivered = ResizeArray<string>()

            match (Handled.mapMsg string handled).Perform() with
            | Fuaran.MVU.Reply run -> run delivered.Add
            | Signal _ -> ()

            expect "the reply is mapped" (List.ofSeq delivered = [ "7" ])

            let started: Started<int> = {
                Invocation = invocation "first-party"
                Start =
                    fun emit ->
                        emit 8

                        { new IDisposable with
                            member _.Dispose() = ()
                        }
            }

            let emitted = ResizeArray<string>()
            (Started.mapMsg string started).Start emitted.Add |> ignore
            expect "the event is mapped" (List.ofSeq emitted = [ "8" ])
            expect "the invocation is kept" ((Handled.mapMsg string handled).Invocation = handled.Invocation))
    case
        "surface: ResourceKey.named and prefix build paths; Principal.actorId is injective with a prefix per case"
        (fun expect ->
            expect "named" (ResourceKey.segments (ResourceKey.named "search" "q7") = [ "search"; "q7" ])

            expect
                "prefixed"
                (ResourceKey.segments (ResourceKey.prefix "child" (ResourceKey.named "search" "q7")) = [
                    "child"
                    "search"
                    "q7"
                ])

            let ids =
                [
                    User "x"
                    Host
                    Agent("m", "1", "x")
                    Foreign "x"
                    Foreign "host"
                    User "host"
                ]
                |> List.map Principal.actorId

            expect "six principals, six ids" (List.length (List.distinct ids) = 6)

            expect
                "a prefix per case"
                (ids = [ "user:x"; "host"; "agent:m/1/x"; "foreign:x"; "foreign:host"; "user:host" ]))
    case
        "surface: Law.replayReproduces answers nothing for a faithful log and one sentence for a wrong model"
        (fun expect ->
            let probe = Probe()
            let comp = subject (fun _ _ -> []) noWants
            let runtime, _, _ = start comp probe id
            runtime.Dispatch(M 1, user) |> ignore
            runtime.Dispatch(M 2, user) |> ignore
            expect "faithful" ((Law.replayReproduces comp () runtime.Log runtime.Model).IsEmpty)
            expect "a wrong model is named" (List.length (Law.replayReproduces comp () runtime.Log empty) = 1))
]

/// The step log is a ring the host sizes (Phase 716, DECISIONS.md D28.12): `Fuel.Log` bounds what `Runtime.Log`
/// keeps, the oldest record is dropped first and counted in `LogDropped`, and the sink receives every record.
let private stepLog: Case list =
    // A runtime whose log holds `capacity` records, with a sink attached, after `messages` dispatches: the boot
    // step and one committed step per message, so `messages + 1` records are made.
    let run (capacity: int) (messages: int) (each: Runtime<unit, Model, Msg, Eff, Res, int list> -> unit) =
        let sink = ResizeArray<StepRecord<Msg>>()

        let runtime, _, _ =
            start (subject (fun _ _ -> []) noWants) (Probe()) (fun h -> {
                h with
                    Fuel = { Fuel.defaults with Log = capacity }
                    Record = sink.Add
            })

        each runtime

        for n in 1..messages do
            runtime.Dispatch(M n, user) |> ignore
            each runtime

        runtime, List.ofSeq sink

    let sequences (records: StepRecord<Msg> list) =
        records |> List.map (fun r -> StepOf.sequence r.Of)

    [
        case "t2: the log never holds more records than its bound, over a long run" (fun expect ->
            let mutable most = 0
            let runtime, _ = run 8 500 (fun r -> most <- max most (List.length r.Log))
            same expect "the most records ever held" most 8
            same expect "held at the end" (List.length runtime.Log) 8)
        case "t2: the log keeps the most recent records, oldest first" (fun expect ->
            let runtime, made = run 8 100 ignore
            same expect "the last eight records made, in order" runtime.Log (List.skip (List.length made - 8) made)
            same expect "sequences ascend" (sequences runtime.Log) [ 93L .. 100L ])
        case "t2: the dropped count is every record made and not held" (fun expect ->
            let mutable counts = []

            let runtime, made =
                run 8 100 (fun r -> counts <- (List.length r.Log, r.LogDropped) :: counts)

            same expect "dropped at the end" runtime.LogDropped (int64 (List.length made - 8))

            expect
                (sprintf "held + dropped = made, after every step: %A" (List.rev counts))
                (List.rev counts
                 |> List.mapi (fun i (held, dropped) -> int64 held + dropped = int64 (i + 1))
                 |> List.forall id)

            let short, _ = run 8 5 ignore
            same expect "a short history drops nothing" (short.LogDropped, List.length short.Log) (0L, 6))
        case "t2: the sink receives every record as it is made, whatever the log drops" (fun expect ->
            let runtime, made = run 4 200 ignore
            same expect "every record reached the sink" (sequences made) [ 0L .. 200L ]
            expect "the log dropped some" (runtime.LogDropped > 0L))
        case
            "t3: a log bound of zero keeps no log, counts every record dropped, and the sink still receives them"
            (fun expect ->
                for capacity in [ 0; -1 ] do
                    let runtime, made = run capacity 20 (fun r -> expect "nothing held" r.Log.IsEmpty)
                    same expect "every record dropped" runtime.LogDropped 21L
                    same expect "the sink missed nothing" (sequences made) [ 0L .. 20L ]
                    same expect "the runtime still runs" (List.length runtime.Model.Seen) 20)
        case "t2: the default bound is stated: the last 1024 records" (fun expect ->
            same expect "Fuel.defaults.Log" Fuel.defaults.Log 1024)
    ]

let cases: Case list =
    surface
    @ stepLog
    @ [
        // ---- 8.1 Envelopes and acceptance -----------------------------------------------------------------
        case "rule 1-2: a reply is authored from its kind's Answers, never by the performer" (fun expect ->
            let authors = ResizeArray<Principal>()

            let probe =
                Probe(
                    answer = (fun n -> Fuaran.MVU.Reply(fun deliver -> deliver (Reply n))),
                    kindOf = (fun n -> if n = 1 then "model-call" else "first-party")
                )

            let comp =
                subject
                    (fun msg _ ->
                        match msg with
                        | M n -> [ Do(E n) ]
                        | _ -> [])
                    noWants

            let runtime, _, _ =
                start comp probe (fun h -> {
                    h with
                        Decide =
                            fun p ->
                                authors.Add p.Author
                                Allow
                })

            runtime.Dispatch(M 1, user) |> ignore
            runtime.Dispatch(M 2, user) |> ignore

            expect
                "the user's step, the model's reply, the user's step, the host's reply"
                (List.ofSeq authors = [ Host; user; Foreign "model"; user; Host ]))
        case
            "rule 3: an envelope is refused when terminated, from a revoked instance, or by a full bounded queue - and the producer is told"
            (fun expect ->
                let probe = Probe()

                let comp =
                    subject (fun _ _ -> []) (fun msg m ->
                        match msg with
                        | M 1 -> [ 1, 0 ]
                        | M 2 -> []
                        | _ -> m.Wants)

                let runtime, _, reports = start comp probe id
                runtime.Dispatch(M 1, user) |> ignore
                let emitter = probe.LastEmitter 1
                runtime.Dispatch(M 2, user) |> ignore
                emitter (M 3)

                expect
                    "revoked"
                    (reports
                     |> Seq.exists (fun r ->
                         match r with
                         | EnvelopeNotAccepted(Revoked, _, _) -> true
                         | _ -> false))

                runtime.Terminate()
                expect "terminated is told" (runtime.Dispatch(M 4, user) = Error Terminated)
                // A bounded queue of one: two dispatches from inside a step overflow it.
                let probe2 = Probe(answer = (fun _ -> Signal ignore))
                let mutable inner: Runtime<unit, Model, Msg, Eff, Res, int list> option = None

                let probe2 =
                    Probe(
                        answer =
                            (fun n ->
                                Signal(fun () ->
                                    inner.Value.Dispatch(M(n + 10), user) |> ignore
                                    inner.Value.Dispatch(M(n + 20), user) |> ignore))
                    )

                let comp2 =
                    subject
                        (fun msg _ ->
                            match msg with
                            | M 1 -> [ Do(E 1) ]
                            | _ -> [])
                        noWants

                let runtime2, _, reports2 =
                    start comp2 probe2 (fun h -> { h with Queue = Queue.bounded 1 })

                inner <- Some runtime2
                runtime2.Dispatch(M 1, user) |> ignore

                expect
                    "one refused as full"
                    (reports2
                     |> Seq.filter (fun r ->
                         match r with
                         | EnvelopeNotAccepted(QueueFull, _, _) -> true
                         | _ -> false)
                     |> Seq.length = 1)

                expect "the other landed" (runtime2.Model.Seen = [ 1; 11 ]))
        // ---- 8.2 A step --------------------------------------------------------------------------------------
        case
            "rule 4: a chain is first-in first-out: a message, then its follow-ups, each follow-up's own behind them"
            (fun expect ->
                let probe = Probe()

                let comp =
                    subject
                        (fun msg _ ->
                            match msg with
                            | M 1 -> [ Then(M 2); Then(M 3) ]
                            | M 2 -> [ Then(M 4) ]
                            | _ -> [])
                        noWants

                let runtime, _, _ = start comp probe id
                runtime.Dispatch(M 1, user) |> ignore
                expect "1, 2, 3, then 2's follow-up 4" (runtime.Model.Seen = [ 1; 2; 3; 4 ])

                expect
                    "one step"
                    (runtime.Log
                     |> List.filter (fun r ->
                         r.Outcome = Committed
                         && (match r.Of with
                             | Step _ -> true
                             | _ -> false))
                     |> List.length = 1))
        case
            "rule 5: a chain over its fuel is refused whole and reported; a follow-up loop cannot freeze the host"
            (fun expect ->
                let probe = Probe()

                let comp =
                    subject
                        (fun msg _ ->
                            match msg with
                            | Loop -> [ Do(E 9); Then Loop ]
                            | _ -> [])
                        noWants

                let runtime, _, reports =
                    start comp probe (fun h -> {
                        h with
                            Fuel = {
                                Fuel.defaults with
                                    Chain = 5
                                    Drain = 100
                            }
                    })

                runtime.Dispatch(Loop, user) |> ignore
                expect "refused as too long" (runtime.Refusals = [ ChainTooLong 5 ])
                expect "the model is untouched" (runtime.Model = empty)
                expect "nothing performed" (not (List.exists (fun (l: string) -> l.StartsWith "perform") probe.Log))

                expect
                    "reported"
                    (reports
                     |> Seq.exists (fun r ->
                         match r with
                         | StepRefused(_, ChainTooLong 5) -> true
                         | _ -> false)))
        case
            "rule 6: computing a chain performs and installs nothing: a preview leaves the runtime as it was"
            (fun expect ->
                let probe = Probe()

                let comp =
                    subject
                        (fun msg _ ->
                            match msg with
                            | M 1 -> [ Do(E 1); Then(M 2) ]
                            | M 2 -> [ Do(E 2) ]
                            | _ -> [])
                        (fun _ _ -> [ 1, 0 ])

                let runtime, _, _ = start comp probe id
                let before = runtime.Model
                let preview = runtime.Preview(Stamp.forTest 1L, M 1, user)

                expect
                    "the chain was computed"
                    (match preview.Outcome with
                     | Computed c -> c.Effects = [ E 1; E 2 ] && c.Messages = 2
                     | _ -> false)

                same expect "nothing performed" probe.Log []
                expect "nothing installed" (obj.ReferenceEquals(before, runtime.Model) && runtime.Running.IsEmpty))
        case
            "rule 7: the step is decided as a unit: the proposal holds every effect of the chain and the whole plan"
            (fun expect ->
                let proposals = ResizeArray<Proposal<Eff, Res>>()
                let probe = Probe()

                let comp =
                    subject
                        (fun msg _ ->
                            match msg with
                            | M 1 -> [ Do(E 1); Then(M 2) ]
                            | M 2 -> [ Do(E 2) ]
                            | _ -> [])
                        (fun msg m ->
                            match msg with
                            | M 1 -> [ 1, 0 ]
                            | M 2 -> [ 2, 0 ]
                            | _ -> m.Wants)

                let runtime, _, _ =
                    start comp probe (fun h -> {
                        h with
                            Decide =
                                fun p ->
                                    proposals.Add p
                                    Allow
                    })

                runtime.Dispatch(M 1, user) |> ignore
                let p = proposals.[proposals.Count - 1]
                expect "both effects, in order" (List.map snd p.Effects = [ E 1; E 2 ])

                expect
                    "the plan of the FINAL model: key 2 starts, key 1 never did"
                    (List.map (fun (k, _, _) -> k) p.Starts = [ key 2 ] && p.Stops = []))
        case
            "rule 8: allow commits: the model, then every stop, then every start, then each effect once in emission order"
            (fun expect ->
                let probe = Probe()

                let comp =
                    subject
                        (fun msg _ ->
                            match msg with
                            | M 1 -> [ Do(E 1); Then(M 2) ]
                            | M 2 -> [ Do(E 2) ]
                            | M 3 -> [ Do(E 3) ]
                            | _ -> [])
                        (fun msg m ->
                            match msg with
                            | M 1 -> [ 1, 0; 2, 0 ]
                            | M 2 -> [ 2, 1; 3, 0 ]
                            | M 3 -> [ 1, 0; 2, 0 ]
                            | _ -> m.Wants)

                let runtime, _, _ = start comp probe id
                runtime.Dispatch(M 1, user) |> ignore

                same expect "starts (of the FINAL model), then performs" probe.Log [
                    "start 2/1"
                    "start 3/0"
                    "perform 1"
                    "perform 2"
                ]

                probe.Clear()
                runtime.Dispatch(M 3, user) |> ignore

                same expect "every stop (a restart's first), then every start, in key order, then the effect" probe.Log [
                    "stop 2/1"
                    "stop 3/0"
                    "start 1/0"
                    "start 2/0"
                    "perform 3"
                ])
        case
            "rule 9: needs approval parks: the model is untouched and the step waits; approval commits it on its own stamp"
            (fun expect ->
                let probe = Probe()

                let comp =
                    subject
                        (fun msg _ ->
                            match msg with
                            | M n -> [ Do(E n) ]
                            | _ -> [])
                        noWants

                let mutable gate = NeedsApproval

                let runtime, _, _ =
                    start comp probe (fun h -> {
                        h with
                            Decide = fun p -> if p.Effects = [] then Allow else gate
                    })

                runtime.Dispatch(M 1, user) |> ignore
                expect "parked" (List.length runtime.Parked = 1 && runtime.Model = empty && probe.Log = [])
                gate <- Allow
                let parked = runtime.Parked.Head
                expect "approved" (runtime.Approve(Grant.mint parked.Id parked.Proposal (User "approver") 10L 0))
                expect "committed" (runtime.Model.Seen = [ 1 ] && probe.Log = [ "perform 1" ] && runtime.Parked = []))
        case "rule 9: a rejected parked step is refused and the model untouched" (fun expect ->
            let probe = Probe()

            let comp =
                subject
                    (fun msg _ ->
                        match msg with
                        | M n -> [ Do(E n) ]
                        | _ -> [])
                    noWants

            let runtime, _, _ =
                start comp probe (fun h -> {
                    h with
                        Decide = fun p -> if p.Effects = [] then Allow else NeedsApproval
                })

            runtime.Dispatch(M 1, user) |> ignore
            expect "rejected" (runtime.Reject runtime.Parked.Head.Id)
            expect "refused" (runtime.Refusals = [ Rejected ] && runtime.Model = empty && runtime.Parked = []))
        case
            "rule 10: deny refuses: the model is untouched, and Refused may give the component a message authored by the host"
            (fun expect ->
                let probe = Probe()
                let authors = ResizeArray<Principal * Source>()

                let comp =
                    subject
                        (fun msg _ ->
                            match msg with
                            | M n -> [ Do(E n) ]
                            | _ -> [])
                        noWants

                let runtime, _, _ =
                    start comp probe (fun h -> {
                        h with
                            Decide = fun p -> if p.Effects = [] then Allow else Deny "no"
                            Record =
                                fun r ->
                                    match r.Of with
                                    | Step e -> authors.Add((e.Author, e.Source))
                                    | _ -> ()
                    })

                runtime.Dispatch(M 1, user) |> ignore
                expect "the step was refused" (runtime.Refusals = [ Denied "no" ])

                expect
                    "the component got the refusal, authored by the host from the runtime"
                    (runtime.Model.Seen = [ -2 ]
                     && List.ofSeq authors = [ user, External; Host, FromRuntime ])

                same expect "nothing performed" probe.Log [ "refused" ])
        case "rule 11: a throw in update refuses the step, reports it and records it with a marker" (fun expect ->
            let probe = Probe()

            let comp = {
                subject (fun _ _ -> []) noWants with
                    Update =
                        fun _ msg m ->
                            match msg with
                            | M 7 -> failwith "seven"
                            | _ -> { m with Seen = m.Seen @ [ 1 ] }, []
            }

            let runtime, _, reports = start comp probe id
            runtime.Dispatch(M 7, user) |> ignore

            expect
                "refused"
                (match runtime.Refusals with
                 | [ UpdateThrew _ ] -> true
                 | _ -> false)

            expect
                "reported"
                (reports
                 |> Seq.exists (fun r ->
                     match r with
                     | StepRefused(_, UpdateThrew _) -> true
                     | _ -> false))

            expect
                "recorded with a marker"
                (runtime.Log
                 |> List.exists (fun r ->
                     match r.Outcome with
                     | Refused reason -> reason.StartsWith "update threw"
                     | _ -> false))

            runtime.Dispatch(M 8, user) |> ignore
            expect "the next step proceeds" (runtime.Model.Seen = [ 1 ]))
        case
            "rule 2 and 3: an effect of an unregistered kind refuses the step, because its reply could not be authored"
            (fun expect ->
                let probe = Probe(kindOf = (fun _ -> "nobody-registered-this"))

                let comp =
                    subject
                        (fun msg _ ->
                            match msg with
                            | M n -> [ Do(E n) ]
                            | _ -> [])
                        noWants

                let runtime, _, _ = start comp probe id
                runtime.Dispatch(M 1, user) |> ignore

                expect
                    "refused"
                    (runtime.Refusals = [ Unregistered "nobody-registered-this" ]
                     && runtime.Model = empty))
        case
            "rule 8: a performer that throws is reported and the component may be told; the step stays committed"
            (fun expect ->
                let probe = Probe()

                let comp =
                    subject
                        (fun msg _ ->
                            match msg with
                            | M 1 -> [ Do Boom; Do(E 2) ]
                            | _ -> [])
                        noWants

                let runtime, _, reports = start comp probe id
                runtime.Dispatch(M 1, user) |> ignore

                expect
                    "committed, the later effect still performed"
                    (runtime.Model.Seen = [ 1; -2 ] && List.contains "perform 2" probe.Log)

                expect
                    "reported"
                    (reports
                     |> Seq.exists (fun r ->
                         match r with
                         | Report.PerformerThrew(1L, Boom, _) -> true
                         | _ -> false)))
        // ---- 8.3 A drain -------------------------------------------------------------------------------------
        case
            "rule 12: a drain processes until the queue is empty; DrainEnded runs then and may accept more, which join the drain"
            (fun expect ->
                let probe = Probe()
                let comp = subject (fun _ _ -> []) noWants
                let mutable runtimeRef: Runtime<unit, Model, Msg, Eff, Res, int list> option = None
                let mutable once = true

                probe.OnDrainEnded(fun () ->
                    match runtimeRef with
                    | Some r when once ->
                        once <- false
                        r.Dispatch(M 99, user) |> ignore
                    | _ -> ())

                let runtime, views, _ = start comp probe id
                runtimeRef <- Some runtime
                let renders = views.Count
                runtime.Dispatch(M 1, user) |> ignore
                expect "99 joined the drain" (runtime.Model.Seen = [ 1; 99 ])
                expect "one render for the drain" (views.Count = renders + 1))
        case "rule 13: render once at the end of a drain; a render that dispatches opens a new drain" (fun expect ->
            let probe = Probe()
            let comp = subject (fun _ _ -> []) noWants
            let mutable runtimeRef: Runtime<unit, Model, Msg, Eff, Res, int list> option = None
            let mutable fired = false
            let views = ResizeArray<int list>()

            let render (v: int list) =
                views.Add v

                if not fired && v = [ 1 ] then
                    fired <- true
                    runtimeRef.Value.Dispatch(M 2, user) |> ignore

            let host = {
                Host.create (fun () -> 1L) 1UL render with
                    Report = ignore
            }

            let runtime = Runtime(comp, probe.Interpreter, host, ())
            runtime.Start()
            runtimeRef <- Some runtime
            runtime.Dispatch(M 1, user) |> ignore

            expect
                "the boot paint, the drain's render, then the new drain's render"
                (List.ofSeq views = [ []; [ 1 ]; [ 1; 2 ] ]))
        case
            "rule 14: a drain has fuel in steps; at the bound it renders and continues, dropping nothing and keeping order"
            (fun expect ->
                let probe = Probe()
                let mutable runtimeRef: Runtime<unit, Model, Msg, Eff, Res, int list> option = None

                let probe =
                    Probe(
                        answer =
                            (fun n ->
                                Signal(fun () ->
                                    for i in 1..6 do
                                        runtimeRef.Value.Dispatch(M(n * 10 + i), user) |> ignore))
                    )

                let comp =
                    subject
                        (fun msg _ ->
                            match msg with
                            | M 1 -> [ Do(E 1) ]
                            | _ -> [])
                        noWants

                let runtime, views, reports =
                    start comp probe (fun h -> {
                        h with
                            Fuel = {
                                Fuel.defaults with
                                    Chain = 8
                                    Drain = 2
                            }
                    })

                runtimeRef <- Some runtime
                let renders = views.Count
                runtime.Dispatch(M 1, user) |> ignore
                same expect "every message, in order" runtime.Model.Seen [ 1; 11; 12; 13; 14; 15; 16 ]
                same expect "three yields" runtime.Yields 3
                same expect "one render per drain: the first, then one per continuation" views.Count (renders + 4)

                expect
                    "each yield reported"
                    (reports
                     |> Seq.filter (fun r ->
                         match r with
                         | Yielded _ -> true
                         | _ -> false)
                     |> Seq.length = 3))
        case
            "rule 15: boot paints the Init model, then performs Init's steps authored by the host, then starts the resources"
            (fun expect ->
                let probe = Probe()
                let authors = ResizeArray<Principal>()

                let comp = {
                    subject (fun _ _ -> []) noWants with
                        Init = fun () -> { empty with Wants = [ 1, 0 ] }, [ Do(E 1); Then(M 2) ]
                }

                let runtime, views, _ =
                    start comp probe (fun h -> {
                        h with
                            Decide =
                                fun p ->
                                    authors.Add p.Author
                                    Allow
                    })

                expect "the Init model was painted first" (views.[0] = [])

                expect
                    "Init's follow-up ran and the model moved, then it was painted again"
                    (runtime.Model.Seen = [ 2 ] && views.Count = 2 && views.[1] = [ 2 ])

                expect "the boot step is the host's" (List.ofSeq authors = [ Host ])

                same expect "the resources started at boot, after the paint and the effect" probe.Log [
                    "start 1/0"
                    "perform 1"
                ])
        // ---- 8.4 Resources -----------------------------------------------------------------------------------
        case
            "rule 16-17: the plan is start, stop, keep or restart per key; every stop precedes every start, in key order"
            (fun expect ->
                let probe = Probe()

                let comp =
                    subject (fun _ _ -> []) (fun msg m ->
                        match msg with
                        | M 1 -> [ 1, 0; 2, 0; 3, 0 ]
                        | M 2 -> [ 2, 1; 3, 0; 4, 0 ]
                        | _ -> m.Wants)

                let runtime, _, _ = start comp probe id
                runtime.Dispatch(M 1, user) |> ignore
                probe.Clear()
                runtime.Dispatch(M 2, user) |> ignore

                same expect "stop 1 (gone), stop 2 (changed), start 2, start 4; 3 kept" probe.Log [
                    "stop 1/0"
                    "stop 2/0"
                    "start 2/1"
                    "start 4/0"
                ])
        case
            "rule 18: a start is issued its token before it is called, so a message it emits at once is accepted; a stop revokes before disposing"
            (fun expect ->
                let probe = Probe()

                let comp =
                    subject (fun _ _ -> []) (fun msg m ->
                        match msg with
                        | M 1 -> [ 1, 0 ]
                        | M 2 -> []
                        | _ -> m.Wants)

                let eager: Interpreter<Msg, Eff, Res> = {
                    probe.Interpreter with
                        Resource =
                            fun k r ->
                                let started = probe.Interpreter.Resource k r

                                {
                                    started with
                                        Start =
                                            fun emit ->
                                                let handle = started.Start emit
                                                emit (M 50)
                                                handle
                                }
                }

                let views = ResizeArray<int list>()

                let runtime =
                    Runtime(
                        comp,
                        eager,
                        {
                            Host.create (fun () -> 1L) 1UL views.Add with
                                Report = ignore
                        },
                        ()
                    )

                runtime.Start()
                runtime.Dispatch(M 1, user) |> ignore
                expect "the eager event was accepted and processed in the same drain" (runtime.Model.Seen = [ 1; 50 ])
                let emitter = probe.LastEmitter 1
                runtime.Dispatch(M 2, user) |> ignore
                let before = runtime.RefusedAtAcceptance
                emitter (M 51)

                expect
                    "a late event through the stopped instance is refused"
                    (runtime.RefusedAtAcceptance = before + 1 && runtime.Model.Seen = [ 1; 50; 2 ]))
        case
            "rule 19: a start that throws is held, reported through Lost, and not tried again until its description changes"
            (fun expect ->
                let probe = Probe(startThrows = (fun k -> k = 1))

                let comp =
                    subject (fun _ _ -> []) (fun msg m ->
                        match msg with
                        | M 1 -> [ 1, 0 ]
                        | M 2 -> [ 1, 0 ]
                        | M 3 -> [ 1, 1 ]
                        | _ -> m.Wants)

                let runtime, _, reports = start comp probe id
                runtime.Dispatch(M 1, user) |> ignore
                expect "held" (runtime.Held = Map.ofList [ key 1, R 0 ] && runtime.Running.IsEmpty)
                expect "the component was told" (runtime.Model.Seen = [ 1; -101 ])

                expect
                    "reported"
                    (reports
                     |> Seq.exists (fun r ->
                         match r with
                         | ResourceLost _ -> true
                         | _ -> false))

                let lost =
                    reports
                    |> Seq.filter (fun r ->
                        match r with
                        | ResourceLost _ -> true
                        | _ -> false)
                    |> Seq.length

                runtime.Dispatch(M 2, user) |> ignore

                expect
                    "not tried again under the same description"
                    (reports
                     |> Seq.filter (fun r ->
                         match r with
                         | ResourceLost _ -> true
                         | _ -> false)
                     |> Seq.length = lost)

                runtime.Dispatch(M 3, user) |> ignore

                expect
                    "tried again when the description changed"
                    (reports
                     |> Seq.filter (fun r ->
                         match r with
                         | ResourceLost _ -> true
                         | _ -> false)
                     |> Seq.length = lost + 1))
        case
            "rule 20: if Resources throws nothing changes and it is tried again at the next step; a throw is never read as an empty map"
            (fun expect ->
                let probe = Probe()
                let mutable throwOnce = true

                let comp = {
                    subject (fun _ _ -> []) (fun _ _ -> [ 1, 0 ]) with
                        Resources =
                            fun m ->
                                if throwOnce then
                                    throwOnce <- false
                                    failwith "resources threw"

                                m.Wants |> List.map (fun (k, d) -> key k, R d) |> Map.ofList
                }

                let runtime, _, reports = start comp probe id
                // The boot threw: nothing started, and the step still committed.
                same
                    expect
                    "reported, nothing started, nothing stopped"
                    (reports
                     |> Seq.exists (fun r ->
                         match r with
                         | ResourcesThrew _ -> true
                         | _ -> false),
                     probe.Log)
                    (true, [])

                runtime.Dispatch(M 1, user) |> ignore
                same expect "tried again at the next step" (probe.Log, runtime.Model.Seen) ([ "start 1/0" ], [ 1 ]))
        case
            "rule 21: resources are level-triggered: a key present only in the middle of a chain is never started"
            (fun expect ->
                let probe = Probe()

                let comp =
                    subject
                        (fun msg _ ->
                            match msg with
                            | M 1 -> [ Then(M 2) ]
                            | _ -> [])
                        (fun msg m ->
                            match msg with
                            | M 1 -> [ 1, 0 ]
                            | M 2 -> []
                            | _ -> m.Wants)

                let runtime, _, _ = start comp probe id
                runtime.Dispatch(M 1, user) |> ignore
                same expect "never started" (probe.Log, runtime.Running.IsEmpty) ([], true))
        case "rule 22: the diff is skipped when the model is the same instance as at the last diff" (fun expect ->
            let probe = Probe()
            let mutable calls = 0

            let comp = {
                subject (fun _ _ -> []) noWants with
                    Update =
                        fun _ msg m ->
                            (match msg with
                             | M 1 -> m
                             | _ -> { m with Seen = m.Seen @ [ 2 ] }),
                            []
                    Resources =
                        fun m ->
                            calls <- calls + 1
                            Map.empty
            }

            let runtime, _, _ = start comp probe id
            let afterBoot = calls
            runtime.Dispatch(M 1, user) |> ignore
            expect "the same instance: no diff" (calls = afterBoot)
            runtime.Dispatch(M 2, user) |> ignore
            expect "a new instance: diffed" (calls = afterBoot + 1))
        // ---- 8.5 Termination ----------------------------------------------------------------------------------
        case "rule 23: a termination requested during a step takes effect when the step ends" (fun expect ->
            let mutable runtimeRef: Runtime<unit, Model, Msg, Eff, Res, int list> option = None

            let probe =
                Probe(
                    answer =
                        (fun n ->
                            Signal(fun () ->
                                if n = 1 then
                                    runtimeRef.Value.Terminate()))
                )

            let comp =
                subject
                    (fun msg _ ->
                        match msg with
                        | M 1 -> [ Do(E 1); Do(E 2); Then(M 9) ]
                        | _ -> [])
                    noWants

            let runtime, views, _ = start comp probe id
            runtimeRef <- Some runtime
            let renders = views.Count
            runtime.Dispatch(M 1, user) |> ignore
            runtime.Dispatch(M 2, user) |> ignore

            same
                expect
                "the step completed: both effects handed over, the whole chain installed"
                (probe.Log, runtime.Model.Seen)
                ([ "perform 1"; "perform 2" ], [ 1; 9 ])

            expect "then terminated: no render, nothing more" (runtime.IsTerminated && views.Count = renders))
        case
            "rule 24: teardown stops every resource, cancels every parked step, refuses what was in flight, and hands the final model to the host"
            (fun expect ->
                let probe =
                    Probe(answer = (fun n -> Fuaran.MVU.Reply(fun deliver -> if n = 3 then () else deliver (Reply n))))

                let mutable final: Model option = None
                let mutable gate = Allow

                let comp =
                    subject
                        (fun msg _ ->
                            match msg with
                            | M 3 -> [ Do(E 3) ]
                            | M n -> [ Do(E n) ]
                            | _ -> [])
                        (fun msg m ->
                            match msg with
                            | M 1 -> [ 1, 0; 2, 0 ]
                            | _ -> m.Wants)

                let runtime, _, _ =
                    start comp probe (fun h -> {
                        h with
                            Decide = fun p -> if p.Effects = [] then Allow else gate
                            OnTerminated = fun m -> final <- Some m
                    })

                runtime.Dispatch(M 1, user) |> ignore
                gate <- NeedsApproval
                runtime.Dispatch(M 2, user) |> ignore
                expect "one parked" (List.length runtime.Parked = 1)
                probe.Clear()
                runtime.Terminate()

                expect
                    "every running instance stopped"
                    (probe.Log = [ "stop 1/0"; "stop 2/0" ] && runtime.Running.IsEmpty)

                expect
                    "the parked step resolved as cancelled"
                    (runtime.Parked = [] && List.contains Cancelled runtime.Refusals)

                expect "the final model handed to the host" (final = Some runtime.Model))
        case "rule 25: after termination nothing is accepted, performed, started or rendered" (fun expect ->
            let probe =
                Probe(answer = (fun n -> Fuaran.MVU.Reply(fun deliver -> if n = 2 then () else deliver (Reply n))))

            let mutable late: (Msg -> unit) option = None

            let probe =
                Probe(answer = (fun n -> Fuaran.MVU.Reply(fun deliver -> late <- Some deliver)))

            let comp =
                subject
                    (fun msg _ ->
                        match msg with
                        | M n -> [ Do(E n) ]
                        | _ -> [])
                    (fun msg m ->
                        match msg with
                        | M 1 -> [ 1, 0 ]
                        | _ -> m.Wants)

            let runtime, views, _ = start comp probe id
            runtime.Dispatch(M 1, user) |> ignore
            let emitter = probe.LastEmitter 1
            runtime.Terminate()
            let renders = views.Count
            probe.Clear()
            expect "a dispatch is refused" (runtime.Dispatch(M 2, user) = Error Terminated)
            let before = runtime.RefusedAtAcceptance
            late.Value(Reply 1)
            emitter (M 3)
            expect "an in-flight reply and a late event are refused" (runtime.RefusedAtAcceptance = before + 2)

            expect
                "nothing rendered, performed or started"
                (views.Count = renders && probe.Log = [] && runtime.Running.IsEmpty)

            runtime.Terminate()
            expect "idempotent" (runtime.IsTerminated && views.Count = renders))
    ]
