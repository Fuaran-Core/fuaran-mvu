// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Diametrical Ltd

/// The step machine's differential (Phase 647, t6 and t8): the extracted `StepMachine` model
/// (proofs/oracle/, compiled from what the prover writes) run beside the shipped `Runtime` over generated
/// envelope sequences — dispatches, events from instances live and revoked, terminations — and required to
/// agree on the model installed, the effects handed over and their order, the refusals, the parked steps,
/// the running instances and the render count. Host-neutral: the Expecto suite runs it on .NET under the
/// proof leg's `Proofs.Oracle` filter, and the Node runner runs it under Fable.
module Fuaran.MVU.Tests.StepDifferential

open Fuaran.MVU
open Fuaran.MVU.Tests.Check
open Fuaran.MVU.Tests.Rand

/// The test component, in integers: a deterministic update with a few effects and follow-ups per message,
/// one message that loops (and so runs over the chain's fuel), resources read off the model's bits, and a
/// decision read off the proposal's sums. Both sides compute from these same functions.
module private Subject =

    let chainFuel = 8

    /// `update`: the next model and the steps, as plain integers.
    let next (m: int) (x: int) : int * (Choice<int, int> list) =
        let m' = (m * 31 + x + 7) % 1000
        let h = (m' * 17 + x) % 10
        let effects = [ for i in 0 .. (h % 3) - 1 -> Choice1Of2(m' + i) ]

        let follows =
            if x = 42 then
                [ Choice2Of2 42 ]
            elif h = 4 then
                [ Choice2Of2((x + 1) % 50) ]
            elif h = 9 && x < 25 then
                [ Choice2Of2(x + 1); Choice2Of2(x + 2) ]
            else
                []

        m', effects @ follows

    /// The wanted resources of a model: keys 0 to 2 by the bits of `m / 7`, each under a description.
    let wanted (m: int) : (int * int) list = [
        for k in 0..2 do
            if ((m / 7) >>> k) &&& 1 = 1 then
                yield k, (m / 50) % 2
    ]

    /// The decision, from the effects' sum and the plan's size.
    let decide (effects: int list) (starts: int) (stops: int) : int =
        (List.sum effects + starts + stops + 3) % 11

/// The shipped side.
module private Shipped =

    let subject: Component<unit, int, int, int, int, int> = {
        Init = fun () -> 0, []
        Update =
            fun _ x m ->
                let m', steps = Subject.next m x

                m',
                steps
                |> List.map (fun s ->
                    match s with
                    | Choice1Of2 e -> Do e
                    | Choice2Of2 f -> Then f)
        Resources =
            fun m ->
                Subject.wanted m
                |> List.map (fun (k, d) -> ResourceKey.constant (string k), d)
                |> Map.ofList
        View = id
    }

    let decide: Decide<int, int> =
        fun proposal ->
            match
                Subject.decide
                    (List.map snd proposal.Effects)
                    (List.length proposal.Starts)
                    (List.length proposal.Stops)
            with
            | 0 -> Deny "scripted"
            | 1 -> NeedsApproval
            | _ -> Allow

/// The model side, through the extraction.
module private Modelled =

    let update (m: bigint) (x: bigint) : StepChain.pair<bigint, StepChain.step<bigint, bigint> list> =
        let m', steps = Subject.next (int m) (int x)

        {
            StepChain.pair.first = bigint m'
            StepChain.pair.second =
                steps
                |> List.map (fun s ->
                    match s with
                    | Choice1Of2 e -> StepChain.Do(bigint e)
                    | Choice2Of2 f -> StepChain.Then(bigint f))
        }

    let resources (m: bigint) : StepMachine.want list =
        Subject.wanted (int m)
        |> List.map (fun (k, d) -> {
            StepMachine.want.wkey = bigint k
            StepMachine.want.wdesc = bigint d
        })

    let decide (p: StepMachine.proposal<bigint>) : StepMachine.decision =
        match Subject.decide (List.map int p.effects) (List.length p.starts) (List.length p.stops) with
        | 0 -> StepMachine.Deny
        | 1 -> StepMachine.Park
        | _ -> StepMachine.Allow

/// One operation of a generated script.
type private Op =
    | Dispatch of msg: int
    | EmitToken of token: int * msg: int
    | Terminate

let private script (seed: int) (steps: int) : Op list =
    let rand = Rand seed

    List.init steps (fun _ ->
        let r = rand.Next 100

        if r < 70 then Dispatch(rand.Next 50)
        elif r < 96 then EmitToken(1 + rand.Next 6, rand.Next 50)
        else Terminate)

/// Drive the shipped runtime and the model over one script, comparing after every operation.
let private run (expect: string -> bool -> unit) (seed: int) =
    // The shipped runtime over a scripted interpreter whose effects are signals (so only resource starts
    // issue tokens) and whose resource emitters are kept by the token the runtime issued.
    let handed = ResizeArray<int>()
    let emitters = System.Collections.Generic.Dictionary<int, int -> unit>()
    let pendingEmitters = ResizeArray<ResourceKey * (int -> unit)>()

    let kind = {
        Fuaran.MVU.Testing.HarnessOptions.testKind with
            Id = "test"
    }

    let interpreter: Interpreter<int, int, int> = {
        Kinds = [ kind ]
        Effect =
            fun e -> {
                Invocation = {
                    Kind = "test"
                    Args = None
                    Destination = Local
                }
                Perform = fun () -> Signal(fun () -> handed.Add e)
            }
        Resource =
            fun key _ -> {
                Invocation = {
                    Kind = "test"
                    Args = None
                    Destination = Local
                }
                Start =
                    fun emit ->
                        pendingEmitters.Add((key, emit))

                        { new System.IDisposable with
                            member _.Dispose() = ()
                        }
            }
        Refused = fun _ -> None
        Lost = fun _ _ _ -> None
        DrainEnded = ignore
    }

    let mutable renders = 0

    let host: Host<int, int, int, int, int> = {
        Host.create (fun () -> 0L) 1UL (fun _ -> renders <- renders + 1) with
            Decide = Shipped.decide
            Fuel = {
                Fuel.defaults with
                    Chain = Subject.chainFuel
                    Drain = 100000
            }
    }

    let runtime = Runtime(Shipped.subject, interpreter, host, ())
    runtime.Start()
    let bootRenders = renders

    // Bind every emitter issued during the last operation to the token the runtime gave its instance.
    let bindEmitters () =
        for key, emit in pendingEmitters do
            match runtime.InstanceOf key with
            | Some token -> emitters.[token] <- emit
            | None -> ()

        pendingEmitters.Clear()

    // The model.
    let mutable st: StepMachine.st<bigint, bigint, bigint> = StepMachine.initial 0I
    let mutable modelRefusedAtAcceptance = 0

    let modelDispatch (msg: bigint) (src: StepMachine.source) =
        let r =
            StepMachine.dispatch
                false
                0I
                100000I
                (bigint Subject.chainFuel)
                Modelled.update
                Modelled.resources
                Modelled.decide
                st
                msg
                src

        st <- r.first

        if not r.second then
            modelRefusedAtAcceptance <- modelRefusedAtAcceptance + 1

    let compare (i: int) (op: string) =
        let where = sprintf "seed %d op %d (%s)" seed i op
        expect (where + ": the same model") (bigint runtime.Model = st.model)
        expect (where + ": the same effects handed over, in order") (List.ofSeq handed |> List.map bigint = st.handed)
        expect (where + ": the same render count") (bigint (renders - bootRenders) = st.renders)
        expect (where + ": the same acceptances") (runtime.Sequence = int64 (st.next_seq - 1I))
        expect (where + ": the same acceptance refusals") (runtime.RefusedAtAcceptance = modelRefusedAtAcceptance)

        let runtimeRefused =
            runtime.Log
            |> List.choose (fun r ->
                match r.Of, r.Outcome with
                | Step envelope, Refused _ -> Some(bigint envelope.Sequence)
                | _ -> None)

        expect (where + ": the same refused steps") (runtimeRefused = st.refused)

        expect
            (where + ": the same parked steps")
            (runtime.Parked |> List.map (fun p -> bigint p.Envelope.Sequence) = (st.parked |> List.map (fun e -> e.seq)))

        let runtimeKeys =
            runtime.Running
            |> Map.toList
            |> List.map (fun (k, _) -> bigint (int (List.head (ResourceKey.segments k))))

        // The model appends a started instance to its list; the runtime's map is in key order: compare as sets.
        let modelKeys = st.running |> List.map (fun r -> r.ikey) |> List.sort

        expect
            (sprintf "%s: the same running keys (runtime %A, model %A)" where runtimeKeys modelKeys)
            (runtimeKeys = modelKeys)

        expect (where + ": the same termination") (runtime.IsTerminated = st.terminated)

    script seed 60
    |> List.iteri (fun i op ->
        match op with
        | Dispatch msg ->
            runtime.Dispatch(msg, User "u") |> ignore
            bindEmitters ()
            modelDispatch (bigint msg) StepMachine.External
            compare i (sprintf "dispatch %d" msg)
        | EmitToken(token, msg) ->
            // A token the runtime never issued cannot be emitted through on either side: skip it.
            if emitters.ContainsKey token then
                emitters.[token] msg
                bindEmitters ()
                modelDispatch (bigint msg) (StepMachine.FromInstance(bigint token))
                compare i (sprintf "emit through token %d" token)
        | Terminate ->
            runtime.Terminate()
            st <- StepMachine.terminate st
            compare i "terminate")

/// Every differential case over the step machine, each named for the proof leg's `Proofs.Oracle` filter.
let cases: Case list = [
    case
        "Proofs.Oracle step machine: the extracted model and Runtime answer alike over generated envelope sequences"
        (fun expect ->
            for seed in 1..40 do
                run expect seed)
    case
        "Proofs.Oracle step machine: a revoked instance is refused on both sides (go-red control: a live one is not)"
        (fun expect ->
            let st0 = StepMachine.initial 0I
            // Dispatch a message whose model lists a resource and whose step is allowed, so a token is issued.
            let r1 =
                StepMachine.dispatch
                    false
                    0I
                    100I
                    8I
                    Modelled.update
                    Modelled.resources
                    Modelled.decide
                    st0
                    3I
                    StepMachine.External

            let st1 = r1.first

            match st1.running with
            | inst :: _ ->
                let live = StepMachine.accept false 0I st1 1I (StepMachine.FromInstance inst.tok)
                expect "a live token is accepted" live.second

                let revoked =
                    StepMachine.accept false 0I (StepMachine.terminate st1) 1I (StepMachine.FromInstance inst.tok)

                expect "a revoked token is refused" (not revoked.second)
            | [] -> expect "the first dispatch started an instance (the script relies on it)" false)
    case "Proofs.Oracle step machine: a chain over its fuel is refused whole on both sides" (fun expect ->
        let st0 = StepMachine.initial 0I

        let r =
            StepMachine.dispatch
                false
                0I
                100I
                8I
                Modelled.update
                Modelled.resources
                Modelled.decide
                st0
                42I
                StepMachine.External

        expect "refused step 1" (r.first.refused = [ 1I ])
        expect "the model is untouched" (r.first.model = 0I)
        expect "nothing handed" (r.first.handed = [])
        expect "one render" (r.first.renders = 1I))
]
