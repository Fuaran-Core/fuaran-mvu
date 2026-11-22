// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Diametrical Ltd

/// The gate's laws (Phase 648, t1): authority follows authorship, a restricted principal never gets more than the
/// host, a step with one refused effect performs nothing, a parked step commits with its grant and with nothing
/// else, no message can approve, the author cannot approve, a kind's classes are the registered ones, the mount
/// check, the grant's expiry and generation, the principal-to-actor mapping, and the decision under two ceilings.
/// Host-neutral: one list, run on .NET and under Fable.
module Fuaran.MVU.Tests.GateLaws

open Fuaran.Core
open Fuaran.MVU
open Fuaran.MVU.Testing
open Fuaran.MVU.Tests.Check

type Msg =
    /// Ask the model: a model call whose reply is `Answered`.
    | Ask of int
    /// The model's reply, authored by the agent, which the component turns into a save.
    | Answered of int
    /// Save directly.
    | Write of int
    /// Send to a remote origin.
    | Post of origin: string * int
    /// Nothing.
    | Nop

type Eff =
    | Call of int
    | Save of int
    | Send of origin: string * int

let private writes = "writes"
let private egress = "egress"

let private kind (id: string) (classes: string list) (answers: Trust) : EffectKind = {
    Id = id
    Shape = Shape.Call
    Classes = Set.ofList classes
    Determinism = Set.empty
    Answers = answers
    Replies = []
}

/// The three kinds: a model call whose answers are the agent's, a save of class `writes`, a send of class `egress`.
let private modelCall = kind "model-call" [] (FromModel("gpt", "5", "assistant"))
let private save = kind "save" [ writes ] FirstParty
let private send = kind "send" [ egress ] FirstParty
let private kinds = [ modelCall; save; send ]

let private agent = Agent("gpt", "5", "assistant")
let private user = User "alice"
let private approver = User "bob"

let private describe (effect: Eff) : Invocation =
    match effect with
    | Call n -> {
        Kind = "model-call"
        Args = Some(string n)
        Destination = Local
      }
    | Save n -> {
        Kind = "save"
        Args = Some(string n)
        Destination = Local
      }
    | Send(origin, n) -> {
        Kind = "send"
        Args = Some(string n)
        Destination = Remote origin
      }

/// The component: a list of saved numbers; `Ask` calls the model, the reply saves, `Write` saves, `Post` sends.
let private subject: Component<unit, int list, Msg, Eff, NoResource, int list> =
    Component.plain
        (fun () -> [], [])
        (fun msg model ->
            match msg with
            | Ask n -> model, [ Do(Call n) ]
            | Answered n -> model @ [ n ], [ Do(Save n) ]
            | Write n -> model @ [ n ], [ Do(Save n) ]
            | Post(origin, n) -> model, [ Do(Send(origin, n)) ]
            | Nop -> model, [])
        (fun _ -> Map.empty)
        id

/// The host's own gate: egress only to the one trusted origin, for every principal.
let private egressAllowList: PolicyGate<DecisionContext, Capability, (string * string) list> = {
    Policy = "egress-allow-list"
    Decide =
        fun _ _ args ->
            match args |> List.tryFind (fun (addr, _) -> addr = Gate.DestinationAddress) with
            | Some(_, "remote:trusted.example")
            | Some(_, "local") -> PolicyDecision.Allow
            | _ -> PolicyDecision.deny "the destination is not on the host's allow-list"
}

/// Agents need approval to write and may never send; a foreign source may do nothing; users and the host act
/// through the host's registry.
let private restrictions (principal: Principal) : Restriction list =
    match principal with
    | Agent _ -> [ ApprovalFor(Set.singleton writes); Refusing(Set.singleton egress) ]
    | Foreign _ -> [ Only Set.empty ]
    | User _
    | Host -> []

let private gate: Gate<Eff, NoResource> =
    match Gate.create kinds [ egressAllowList ] restrictions Gate.allowAll with
    | Ok g -> g
    | Error why -> failwith why

/// A harness over the gate: the model call is answered at once, so the reply is the agent's step in the same drain.
let private start (decide: Decide<Eff, NoResource>) =
    Harness.startWith
        {
            HarnessOptions.defaults with
                Kinds = kinds
                Describe = describe
                Answers =
                    fun effect ->
                        match effect with
                        | Call n -> Immediate(Answered n)
                        | _ -> NoAnswer
                Decide = decide
        }
        subject
        ()

let private parkedAuthor (run: Harness<_, _, _, _, _, _>) =
    run.Runtime.Parked |> List.map (fun p -> p.Envelope.Author)

/// A proposal as the gate reads one, for a decision law that needs no runtime.
let private proposal (author: Principal) (effects: Eff list) : Proposal<Eff, NoResource> = {
    Author = author
    Sequence = 1L
    At = 1001L
    Effects = effects |> List.map (fun e -> describe e, e)
    Starts = []
    Stops = []
    Grant = None
}

let private rank (d: Decision) =
    match d with
    | Allow -> 0
    | NeedsApproval -> 1
    | Deny _ -> 2

let private proposals = [
    for author in [ Host; user; agent; Foreign "feed" ] do
        for effects in
            [
                []
                [ Call 1 ]
                [ Save 2 ]
                [ Send("trusted.example", 3) ]
                [ Send("evil.example", 4) ]
                [ Call 5; Save 6 ]
                [ Save 7; Send("trusted.example", 8) ]
            ] do
            yield proposal author effects
]

let cases: Case list = [
    case
        "t1: a reply to a model call is the agent's, and the step it causes is decided under the agent's registry though a user asked"
        (fun expect ->
            let run = start (Gate.decide gate)
            run.Send(Write 1) |> ignore
            expect "the user's own save commits" (run.Model = [ 1 ] && run.Performed = [ Save 1 ])
            run.SendAs(Ask 9, user) |> ignore
            expect "the model call was performed" (run.Performed = [ Save 1; Call 9 ])
            expect "the reply is the agent's, parked under the agent's registry" (parkedAuthor run = [ agent ])
            expect "the model is untouched by the parked step" (run.Model = [ 1 ]))
    case "t1: an agent's registry never allows what the host's denies (every generated step, by rank)" (fun expect ->
        for p in proposals do
            let host = Gate.decide gate { p with Author = Host }
            let restricted = Gate.decide gate { p with Author = agent }
            let foreign = Gate.decide gate { p with Author = Foreign "feed" }

            expect
                (sprintf "agent >= host on %A: host %A, agent %A" (List.map snd p.Effects) host restricted)
                (rank restricted >= rank host)

            expect
                (sprintf "foreign >= host on %A: host %A, foreign %A" (List.map snd p.Effects) host foreign)
                (rank foreign >= rank host)

        let run = start (Gate.decide gate)
        run.SendAs(Post("evil.example", 1), agent) |> ignore
        run.SendAs(Post("evil.example", 2), user) |> ignore
        expect "the host's allow-list refuses the user's send as it refuses the agent's" (run.Performed = [])
        expect "two refusals" (run.Runtime.Refusals.Length = 2))
    case "t1: a step with one refused effect performs nothing" (fun expect ->
        let decide = Gate.decide gate

        let run =
            Harness.startWith
                {
                    HarnessOptions.defaults with
                        Kinds = kinds
                        Describe = describe
                        Answers = fun _ -> NoAnswer
                        Decide = decide
                }
                (Component.plain
                    (fun () -> [], [])
                    (fun msg (model: int list) ->
                        match msg with
                        | Write n -> model @ [ n ], [ Do(Save n); Do(Send("evil.example", n)) ]
                        | _ -> model, [])
                    (fun _ -> Map.empty)
                    id)
                ()

        run.Send(Write 1) |> ignore
        expect "nothing performed" (run.Performed = [])
        expect "the model untouched" (run.Model = [])

        expect
            "refused as denied"
            (match run.Runtime.Refusals with
             | [ Denied _ ] -> true
             | _ -> false))
    case
        "t1: a parked step commits with a matching grant, is refused with a grant for another step, and is refused as stale when the model moved"
        (fun expect ->
            let run = start (Gate.decide gate)
            run.SendAs(Write 1, agent) |> ignore
            run.SendAs(Write 2, agent) |> ignore
            expect "two parked" (parkedAuthor run = [ agent; agent ])
            let first, second = run.Runtime.Parked.[0], run.Runtime.Parked.[1]
            let grantFirst = Gate.grant gate first approver 5000L
            let grantSecond = Gate.grant gate second approver 5000L
            // A grant for another step: the first grant names the first parked step; presenting a forgery that
            // names the second with the first's request is refused as stale, and nothing commits.
            let forged = {
                grantSecond with
                    Request = grantFirst.Request
            }

            expect "a grant for another step resolves that step without committing it" (run.Runtime.Approve forged)
            expect "refused as stale" (run.Runtime.Refusals = [ Stale ] && run.Model = [] && run.Performed = [])
            expect "the matching grant commits" (run.Runtime.Approve grantFirst)
            expect "committed" (run.Model = [ 1 ] && run.Performed = [ Save 1 ] && run.Runtime.Parked = [])
            expect "a grant already presented is refused" (not (run.Runtime.Approve grantFirst))
            // Stale: the model moves under a parked step so that its chain now asks something else.
            let moving =
                Harness.startWith
                    {
                        HarnessOptions.defaults with
                            Kinds = kinds
                            Describe = describe
                            Answers = fun _ -> NoAnswer
                            Decide = Gate.decide gate
                    }
                    (Component.plain
                        (fun () -> [], [])
                        (fun msg (model: int list) ->
                            match msg with
                            | Write n -> model @ [ n ], [ Do(Save(n + List.length model)) ]
                            | _ -> model, [])
                        (fun _ -> Map.empty)
                        id)
                    ()

            moving.SendAs(Write 1, agent) |> ignore
            let parked = moving.Runtime.Parked.Head
            let grant = Gate.grant gate parked approver 5000L
            moving.Send(Write 10) |> ignore
            expect "the user's save committed meanwhile" (moving.Model = [ 10 ])
            expect "the grant names a step still parked" (moving.Runtime.Approve grant)

            expect
                "refused as stale, nothing of it performed"
                (moving.Runtime.Refusals = [ Stale ] && moving.Performed = [ Save 10 ]))
    case
        "t1: no message can approve: every dispatched step is decided without a grant, and a parked step waits for one"
        (fun expect ->
            let seen = ResizeArray<Proposal<Eff, NoResource>>()

            let decide: Decide<Eff, NoResource> =
                fun p ->
                    seen.Add p
                    Gate.decide gate p

            let run = start decide
            run.SendAs(Write 1, agent) |> ignore
            expect "parked" (parkedAuthor run = [ agent ])
            let id = run.Runtime.Parked.Head.Id

            for msg in [ Ask 2; Answered 3; Write 4; Post("trusted.example", 5); Nop ] do
                run.SendAs(msg, user) |> ignore
                run.SendAs(msg, Host) |> ignore

            // Replies to the asks are the agent's and park too; the first parked step is still there, uncommitted.
            expect
                "the parked step is still parked, whatever was dispatched"
                (run.Runtime.Parked |> List.exists (fun p -> p.Id = id)
                 && not (List.contains 1 run.Model))

            expect "no dispatched step carried a grant" (seen |> Seq.forall (fun p -> p.Grant.IsNone))
            let grant = Gate.grant gate run.Runtime.Parked.Head approver 5000L
            run.Runtime.Approve grant |> ignore

            expect
                "the one proposal with a grant is the approval's"
                (seen |> Seq.filter (fun p -> p.Grant.IsSome) |> Seq.length = 1))
    case "t1: the author of a step cannot approve it; another user can" (fun expect ->
        let run = start (Gate.decide gate)
        run.SendAs(Write 1, agent) |> ignore
        let own = Gate.grant gate run.Runtime.Parked.Head agent 5000L
        expect "presented" (run.Runtime.Approve own)

        expect
            "refused: the author cannot approve"
            (run.Runtime.Refusals = [ Denied "the author of a step cannot approve it" ]
             && run.Model = [])

        run.SendAs(Write 2, agent) |> ignore
        let other = Gate.grant gate run.Runtime.Parked.Head approver 5000L
        expect "another user's grant commits" (run.Runtime.Approve other && run.Model = [ 2 ]))
    case "t3: a kind's classes are fixed at registration and read from there, never from the call" (fun expect ->
        // The same canonical arguments under two kinds: the class gate reads the kind's registered classes.
        let asSave = proposal agent [ Save 1 ]

        let asCall = {
            asSave with
                Effects = [ (describe (Call 1)), Save 1 ]
        }

        expect "a save needs the agent's approval" (Gate.decide gate asSave = NeedsApproval)
        expect "the same arguments under the model-call kind are allowed" (Gate.decide gate asCall = Allow)
        let capability = Gate.capabilityOf save

        expect
            "the capability declares arguments and a destination, and no class"
            (capability.Signature.Holes |> List.map (fun h -> h.Addr) = [ Gate.ArgsAddress; Gate.DestinationAddress ])

        expect "the classes are the registration's" (Gate.classesOf kinds "save" = Set.singleton writes))
    case "t4: the mount check refuses an interpreter whose kinds the host does not cover, naming them" (fun expect ->
        let extra = kind "shell" [] FirstParty

        let covered =
            Interpreter.create kinds (fun _ -> failwith "unused") (fun _ _ -> failwith "unused")

        let uncovered =
            Interpreter.create (kinds @ [ extra ]) (fun _ -> failwith "unused") (fun _ _ -> failwith "unused")

        expect "covered mounts" ((Gate.mount gate covered).IsOk)

        expect
            "uncovered is refused, naming the kind"
            (match Gate.mount gate uncovered with
             | Error missing -> missing = [ "shell" ]
             | Ok _ -> false))
    case "t4: an unregistered kind is refused before anything is performed" (fun expect ->
        let narrow =
            match Gate.create [ save ] [] restrictions Gate.allowAll with
            | Ok g -> g
            | Error why -> failwith why

        expect
            "a step naming a kind the gate does not hold is denied"
            (match Gate.decide narrow (proposal user [ Call 1 ]) with
             | Deny _ -> true
             | _ -> false)

        expect "a step within the gate's kinds is allowed" (Gate.decide narrow (proposal user [ Save 1 ]) = Allow))
    case
        "t5: a grant expired by the host's clock is refused, and so is one minted under an earlier generation"
        (fun expect ->
            let run = start (Gate.decide gate)
            run.SendAs(Write 1, agent) |> ignore
            let expired = Gate.grant gate run.Runtime.Parked.Head approver 1L
            expect "presented" (run.Runtime.Approve expired)
            expect "refused as expired" (run.Runtime.Refusals = [ Denied "the grant has expired" ] && run.Model = [])
            run.SendAs(Write 2, agent) |> ignore
            let early = Gate.grant gate run.Runtime.Parked.Head approver 5000L

            let rebuilt =
                match Gate.rebuild gate kinds [ egressAllowList ] restrictions Gate.allowAll with
                | Ok g -> g
                | Error why -> failwith why

            expect "the generation moved" (rebuilt.Generation = gate.Generation + 1)
            let later = start (Gate.decide rebuilt)
            later.SendAs(Write 2, agent) |> ignore

            let stale = {
                early with
                    Parked = later.Runtime.Parked.Head.Id
            }

            expect "presented" (later.Runtime.Approve stale)

            expect
                "refused: minted under an earlier generation"
                (later.Runtime.Refusals = [ Denied "the grant was minted under an earlier registry generation" ]
                 && later.Model = []))
    case "t2: the principal-to-actor mapping is injective, with a prefix per case" (fun expect ->
        let principals = [
            User "a"
            User "host"
            User "agent:x"
            Host
            Agent("m", "1", "a")
            Agent("m", "1", "b")
            Agent("app", "1", "host")
            Agent("feed", "", "feed")
            Foreign "feed"
            Foreign "m"
            Foreign "user:a"
        ]

        let actors = principals |> List.map (Principal.toActor "app" "1")

        expect
            "distinct principals map to distinct actors"
            (List.length (List.distinct actors) = List.length principals)

        expect "a user is a human" (Principal.toActor "app" "1" (User "a") = Fuaran.Core.Actor.Human "user:a")

        expect
            "the host is the application as an agent"
            (Principal.toActor "app" "1" Host = Fuaran.Core.Actor.Agent("app", "1", "host"))

        expect
            "a foreign source is an agent with no version"
            (Principal.toActor "app" "1" (Foreign "feed") = Fuaran.Core.Actor.Agent("feed", "", "foreign:feed"))

        let ids = principals |> List.map Principal.actorId
        expect "the actor ids are distinct too" (List.length (List.distinct ids) = List.length principals))
    case
        "t6: a step decided under two ceilings is never looser than under either (the program author's and the message's)"
        (fun expect ->
            for p in proposals do
                for ceiling in [ Host; user; agent; Foreign "feed" ] do
                    let own = Gate.decide gate p
                    let other = Gate.decide gate { p with Author = ceiling }
                    let both = Gate.decideUnder gate [ ceiling ] p

                    expect
                        (sprintf
                            "under %A and %A on %A: own %A, other %A, both %A"
                            p.Author
                            ceiling
                            (List.map snd p.Effects)
                            own
                            other
                            both)
                        (rank both >= rank own && rank both >= rank other))
]
