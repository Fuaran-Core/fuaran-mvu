// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Eugene Tolmachev and Fable.Elmish contributors
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)
// Copyright 2026 Diametrical Ltd

namespace Fuaran.MVU

open System
open Fuaran.MVU.Kernel

/// The bounds the host sets (DECISIONS.md D26.3, D28.12): the most messages a chain may run through `update`
/// before it is refused whole; the most steps a drain processes before it yields — renders, and schedules its own
/// continuation, dropping nothing; and the most step records `Runtime.Log` keeps, the oldest dropped first and
/// counted in `Runtime.LogDropped`. A log bound of zero (or less) keeps no log; the sink still receives every record.
type Fuel = { Chain: int; Drain: int; Log: int }

module Fuel =

    /// 256 messages per chain; 1024 steps per drain; the last 1024 step records.
    let defaults: Fuel = {
        Chain = 256
        Drain = 1024
        Log = 1024
    }

/// The queue a host chooses once (DECISIONS.md D25.2): growing, for a single-threaded host and for .NET
/// under the host's lock; bounded, for a server connection or a device, where a push onto a full queue is
/// refused and the producer is told.
type Queue<'item> =
    | Growing of DrainBuffer<'item>
    | Bounded of FixedRing<'item>

module Queue =

    /// A growing queue with the given initial capacity.
    let growing (capacity: int) : Queue<'item> = Growing(DrainBuffer<'item> capacity)

    /// A bounded queue that refuses beyond `capacity`.
    let bounded (capacity: int) : Queue<'item> = Bounded(FixedRing<'item> capacity)

    let push (item: 'item) (queue: Queue<'item>) : bool =
        match queue with
        | Growing buffer ->
            buffer.Push item
            true
        | Bounded ring -> ring.Push item

    let pop (queue: Queue<'item>) : 'item option =
        match queue with
        | Growing buffer -> buffer.Pop()
        | Bounded ring -> ring.Pop()

    let isEmpty (queue: Queue<'item>) : bool =
        match queue with
        | Growing buffer -> buffer.IsEmpty
        | Bounded ring -> ring.IsEmpty

/// What a step was: the boot step carrying `Init`'s steps under the boot stamp, or an accepted envelope.
type StepOf<'msg> =
    | BootStep of Stamp
    | Step of Envelope<'msg>

module StepOf =

    /// The step's sequence number: the boot stamp's (zero for a fresh boot, one past the restored log's last for a
    /// resumed one), or the envelope's.
    let sequence (step: StepOf<'msg>) : int64 =
        match step with
        | BootStep stamp -> stamp.Sequence
        | Step envelope -> envelope.Sequence

    let author (step: StepOf<'msg>) : Principal =
        match step with
        | BootStep _ -> Host
        | Step envelope -> envelope.Author

    /// The time the step was accepted, as the host's clock read it.
    let at (step: StepOf<'msg>) : int64 =
        match step with
        | BootStep stamp -> stamp.At
        | Step envelope -> envelope.Stamp.At

/// How a step ended.
type StepOutcome =
    | Committed
    | Parked of id: int
    | Refused of reason: string

/// The record of one step, in the order steps were decided: the replay's input. `Grant` is the grant the step was
/// decided with: `Some` for a parked step decided again by `Runtime.Approve` (committed, stale or refused), `None`
/// for every step as it is first decided and for a rejection (DECISIONS.md D28).
type StepRecord<'msg> = {
    Of: StepOf<'msg>
    Outcome: StepOutcome
    Grant: Grant option
}

/// What the runtime tells its host as it goes.
type Report<'msg, 'effect, 'resource> =
    | StepRefused of StepOf<'msg> * Refusal<'effect>
    /// A performer threw when a committed step's effect was handed over; the step's sequence number.
    | PerformerThrew of cause: int64 * 'effect * exn
    | ResourceLost of ResourceKey * 'resource * exn
    | StopThrew of ResourceKey * exn
    | ResourcesThrew of exn
    | EnvelopeNotAccepted of NotAccepted * Principal * Source
    | Yielded of steps: int

/// What the host supplies: the clock and the root seed the stamps are made from, the render hook, how to
/// schedule a drain's continuation after a yield, where reports and step records go, the decision
/// function, the fuel, the queue and what to do with the final model at termination. `Record` is the session
/// seam: the runtime hands it every step as it is decided; a host that attaches no sink leaves it `ignore`, and
/// the runtime then writes nothing anywhere (DECISIONS.md D28).
type Host<'model, 'msg, 'effect, 'resource, 'view> = {
    Clock: unit -> int64
    Seed: uint64
    Render: 'view -> unit
    Schedule: (unit -> unit) -> unit
    Report: Report<'msg, 'effect, 'resource> -> unit
    Record: StepRecord<'msg> -> unit
    Decide: Decide<'effect, 'resource>
    Fuel: Fuel
    Queue: Queue<Envelope<'msg>>
    OnTerminated: 'model -> unit
}

module Host =

    /// A host from its clock, root seed and render hook: a growing queue, the allow-all decision, the
    /// default fuel, a continuation run at once, and every report and record dropped.
    let create
        (clock: unit -> int64)
        (seed: uint64)
        (render: 'view -> unit)
        : Host<'model, 'msg, 'effect, 'resource, 'view> =
        {
            Clock = clock
            Seed = seed
            Render = render
            Schedule = fun continuation -> continuation ()
            Report = ignore
            Record = ignore
            Decide = Decide.allowAll
            Fuel = Fuel.defaults
            Queue = Queue.growing 16
            OnTerminated = ignore
        }

/// A liveness token: one per resource instance and per in-flight reply. Revoked before the instance is
/// disposed, so a message from a revoked instance is refused at acceptance.
[<Sealed; AllowNullLiteral>]
type Token(id: int) =
    let mutable live = true
    member _.Id = id
    member _.Live = live
    member _.Revoke() = live <- false

/// A running resource instance: its token and what stops it.
type Instance = { Token: Token; Handle: IDisposable }

/// A step parked for approval: the envelope, the proposal the approver reads, and the chain it would commit.
type ParkedStep<'model, 'msg, 'effect, 'resource> = {
    Id: int
    Envelope: Envelope<'msg>
    Proposal: Proposal<'effect, 'resource>
    Chain: Chain<'model, 'effect>
}

/// A preview: what a message would do on the current model, decided but neither performed nor installed.
type Preview<'model, 'msg, 'effect, 'resource> = {
    Outcome: ChainOutcome<'model, 'effect>
    Proposal: Proposal<'effect, 'resource> option
    Decision: Decision option
}

/// The step machine (the design's runtime semantics, rules 1 to 25; its model is `proofs/StepMachine.fst`
/// over `proofs/Chain.fst`). It owns the queue, the re-entrancy latch, the termination flag, the model,
/// the running resources with their tokens, the in-flight replies and the parked steps. The host owns
/// everything the interpreter reaches.
///
/// The dispatch loop's shape — the latch, the drain, the render once at the end, the absorbing
/// termination and the boot that paints first — descends from Fable.Elmish's program loop as proved in
/// the ToolUp Analytics open-source SDK (toolup-forge, Apache-2.0). New here: envelopes with authors,
/// sources and stamps; a chain computed whole and decided as a unit before anything is installed;
/// parked steps; liveness tokens per instance and per reply; fuel on the chain and on the drain.
[<Sealed>]
type Runtime<'flags, 'model, 'msg, 'effect, 'resource, 'view when 'effect: equality and 'resource: equality>
    private
    (
        comp: Component<'flags, 'model, 'msg, 'effect, 'resource, 'view>,
        interpreter: Interpreter<'msg, 'effect, 'resource>,
        host: Host<'model, 'msg, 'effect, 'resource, 'view>,
        boot: 'model * Step<'msg, 'effect> list,
        bootSequence: int64
    ) =
    let initModel, initSteps = boot
    let mutable model = initModel
    let mutable sequence = bootSequence
    let mutable nextToken = 0
    let mutable nextParked = 1
    let mutable started = false
    let mutable terminated = false
    let mutable terminationRequested = false
    let mutable draining = false
    let mutable renders = 0
    let mutable yields = 0
    let mutable refusedAtAcceptance = 0
    // The model the last resource plan was computed against: the identity skip (rule 22).
    let mutable lastDiffed: 'model option = None
    let mutable running: Map<ResourceKey, 'resource * Instance> = Map.empty
    let mutable held: Map<ResourceKey, 'resource> = Map.empty
    let mutable parked: ParkedStep<'model, 'msg, 'effect, 'resource> list = []
    let inflight = ResizeArray<Token>()
    // The step log (DECISIONS.md D28.12): the kernel's fixed ring at the host's `Fuel.Log`, none at a bound of zero.
    // When it is full the oldest record is dropped and counted; the sink receives every record all the same.
    let log: FixedRing<StepRecord<'msg>> option =
        if host.Fuel.Log > 0 then
            Some(FixedRing host.Fuel.Log)
        else
            None

    let mutable logDropped = 0L
    let refusals = ResizeArray<Refusal<'effect>>()
    // Open a drain when an acceptance from outside one (a resource event, a late reply) queued an envelope.
    // Bound once the drain is defined, below.
    let mutable openDrain: unit -> unit = ignore

    let emptyPlan: ResourceDiff.Plan<ResourceKey, 'resource> = { Stops = []; Keeps = []; Starts = [] }

    let newToken () =
        nextToken <- nextToken + 1
        Token nextToken

    let recordWith (grant: Grant option) (step: StepOf<'msg>) (outcome: StepOutcome) =
        let entry = {
            Of = step
            Outcome = outcome
            Grant = grant
        }

        match log with
        | Some ring when ring.Push entry -> ()
        | Some ring ->
            ring.Pop() |> ignore
            ring.Push entry |> ignore
            logDropped <- logDropped + 1L
        | None -> logDropped <- logDropped + 1L

        host.Record entry

    let record (step: StepOf<'msg>) (outcome: StepOutcome) = recordWith None step outcome

    /// The author of a kind's answers (rule 2).
    let authorOf (kind: string) =
        match Interpreter.tryKind kind interpreter with
        | Some k ->
            match k.Answers with
            | FirstParty -> Host
            | Untrusted who -> Foreign who
            | FromModel(model, version, id) -> Agent(model, version, id)
        | None -> Host

    /// Acceptance (rule 3): not terminated, the source's token live, room in the queue. The clock is read
    /// once, here, and the stamp is fixed. The producer is told the outcome.
    let accept
        (token: Token)
        (msg: 'msg)
        (author: Principal)
        (source: Source)
        (cause: int64 option)
        : Result<int64, NotAccepted> =
        let refuse why =
            refusedAtAcceptance <- refusedAtAcceptance + 1
            host.Report(EnvelopeNotAccepted(why, author, source))
            Error why

        if terminated then
            refuse Terminated
        elif not (isNull token) && not token.Live then
            refuse Revoked
        else
            let seq = sequence + 1L

            let envelope = {
                Sequence = seq
                Msg = msg
                Author = author
                Source = source
                Cause = cause
                Stamp = {
                    At = host.Clock()
                    Sequence = seq
                    Seed = Seed.ofSequence host.Seed seq
                }
            }

            if Queue.push envelope host.Queue then
                sequence <- seq
                Ok seq
            else
                refuse QueueFull

    let render () =
        renders <- renders + 1
        host.Render(comp.View model)

    let refuseStepWith (grant: Grant option) (step: StepOf<'msg>) (refusal: Refusal<'effect>) =
        refusals.Add refusal
        host.Report(StepRefused(step, refusal))

        let reason =
            match refusal with
            | Denied r -> "denied: " + r
            | UpdateThrew ex -> "update threw: " + ex.Message
            | ChainTooLong limit -> sprintf "chain longer than %d" limit
            | Unregistered kind -> "unregistered kind: " + kind
            | Refusal.PerformerThrew(_, ex) -> "performer threw: " + ex.Message
            | Stale -> "stale"
            | Rejected -> "rejected"
            | Cancelled -> "cancelled"

        recordWith grant step (Refused reason)

        match interpreter.Refused refusal with
        | Some msg -> accept null msg Host FromRuntime (Some(StepOf.sequence step)) |> ignore
        | None -> ()

    let refuseStep (step: StepOf<'msg>) (refusal: Refusal<'effect>) = refuseStepWith None step refusal

    /// Deliver a message from a resource instance: accepted only while its token is live.
    let deliverFrom (key: ResourceKey) (token: Token) (kind: string) (msg: 'msg) =
        accept token msg (authorOf kind) (FromResource(key, token.Id)) None |> ignore
        openDrain ()

    /// Stop an instance: revoke its token, then dispose it under its own guard (rule 18).
    let stop (key: ResourceKey) (instance: Instance) =
        instance.Token.Revoke()

        try
            instance.Handle.Dispose()
        with ex ->
            host.Report(StopThrew(key, ex))

    /// Start an instance: its token is issued before the start is called, so a message it emits at once is
    /// accepted; a start that throws is held and reported (rule 19).
    let start (cause: int64) (key: ResourceKey) (resource: 'resource) : Instance option =
        let token = newToken ()

        try
            let started = interpreter.Resource key resource

            let handle =
                started.Start(fun msg -> deliverFrom key token started.Invocation.Kind msg)

            Some { Token = token; Handle = handle }
        with ex ->
            token.Revoke()
            host.Report(ResourceLost(key, resource, ex))

            match interpreter.Lost key resource ex with
            | Some msg -> accept null msg Host FromRuntime (Some cause) |> ignore
            | None -> ()

            None

    /// The resource plan for a chain's final model (rules 16, 20 to 22): skipped when the model is the
    /// instance the last plan was computed against; empty, and tried again next step, when `Resources`
    /// throws.
    let planFor (chainModel: 'model) : Map<ResourceKey, 'resource> option * ResourceDiff.Plan<ResourceKey, 'resource> =
        let skip =
            match lastDiffed with
            | Some last -> ResourceDiff.sameInstance last chainModel
            | None -> false

        if skip then
            None, emptyPlan
        else
            match
                (try
                    Some(comp.Resources chainModel)
                 with ex ->
                     host.Report(ResourcesThrew ex)
                     None)
            with
            | Some wanted -> Some wanted, ResourceDiff.plan running held wanted
            | None -> None, emptyPlan

    /// Hand one committed effect to its performer, once (rule 8). A reply is accepted as a new envelope,
    /// authored from its kind, through a one-shot token, and names the effect's position in the step's emission
    /// order; a performer that throws is reported.
    let perform (cause: int64) (position: int) (handled: Handled<'msg>) (effect: 'effect) =
        let kind = handled.Invocation.Kind

        try
            match handled.Perform() with
            | Signal run -> run ()
            | Reply run ->
                let token = newToken ()
                inflight.Add token

                run (fun msg ->
                    accept token msg (authorOf kind) (Answer(kind, position)) (Some cause) |> ignore
                    token.Revoke()
                    inflight.Remove token |> ignore
                    openDrain ())
        with ex ->
            let refusal = Refusal.PerformerThrew(effect, ex)
            refusals.Add refusal
            host.Report(Report.PerformerThrew(cause, effect, ex))

            match interpreter.Refused refusal with
            | Some msg -> accept null msg Host FromRuntime (Some cause) |> ignore
            | None -> ()

    /// Commit (rule 8): install the model, record the step, carry out the plan — every stop, then every
    /// start — and hand each effect to its performer, once, in emission order.
    let commit
        (grant: Grant option)
        (step: StepOf<'msg>)
        (chain: Chain<'model, 'effect>)
        (wanted: Map<ResourceKey, 'resource> option)
        (plan: ResourceDiff.Plan<ResourceKey, 'resource>)
        (effects: (Handled<'msg> * 'effect) list)
        =
        model <- chain.Model
        recordWith grant step Committed
        let cause = StepOf.sequence step

        match wanted with
        | Some wanted ->
            let running', held' = ResourceDiff.apply stop (start cause) running held wanted plan

            running <- running'
            held <- held'
            lastDiffed <- Some chain.Model
        | None -> ()

        effects
        |> List.iteri (fun position (handled, effect) -> perform cause position handled effect)

    /// Describe a chain's effects and the plan's starts through the interpreter; refuse the step on a kind
    /// the interpreter has not registered, whose reply could not be authored (rule 2).
    let describe
        (chain: Chain<'model, 'effect>)
        (plan: ResourceDiff.Plan<ResourceKey, 'resource>)
        : Result<(Handled<'msg> * 'effect) list * (ResourceKey * Invocation * 'resource) list, string> =
        let handled = chain.Effects |> List.map (fun e -> interpreter.Effect e, e)

        let starts =
            plan.Starts
            |> List.map (fun (key, r) -> key, (interpreter.Resource key r).Invocation, r)

        let kinds =
            (handled |> List.map (fun (h, _) -> h.Invocation.Kind))
            @ (starts |> List.map (fun (_, inv, _) -> inv.Kind))

        match kinds |> List.tryFind (fun k -> (Interpreter.tryKind k interpreter).IsNone) with
        | Some kind -> Error kind
        | None -> Ok(handled, starts)

    /// Decide a computed chain as a unit (rules 7 to 10): allow commits, needs-approval parks, deny refuses.
    let decide (step: StepOf<'msg>) (chain: Chain<'model, 'effect>) =
        let wanted, plan = planFor chain.Model

        match describe chain plan with
        | Error kind -> refuseStep step (Unregistered kind)
        | Ok(handled, starts) ->
            let proposal = {
                Author = StepOf.author step
                Sequence = StepOf.sequence step
                At = StepOf.at step
                Effects = handled |> List.map (fun (h, e) -> h.Invocation, e)
                Starts = starts
                Stops = plan.Stops
                Grant = None
            }

            match host.Decide proposal with
            | Allow -> commit None step chain wanted plan handled
            | Deny reason -> refuseStep step (Denied reason)
            | NeedsApproval ->
                match step with
                | Step envelope ->
                    let id = nextParked
                    nextParked <- nextParked + 1

                    parked <-
                        parked
                        @ [
                            {
                                Id = id
                                Envelope = envelope
                                Proposal = proposal
                                Chain = chain
                            }
                        ]

                    record step (Parked id)
                | BootStep _ ->
                    // Nobody can approve the boot: a boot that needs approval is refused.
                    refuseStep step (Denied "the boot step needs approval, which nothing can give")

    /// One step (rules 4 to 11): compute the chain, then decide it.
    let processStep (envelope: Envelope<'msg>) =
        match Chain.compute comp.Update envelope.Stamp host.Fuel.Chain model envelope.Msg with
        | Threw ex -> refuseStep (Step envelope) (UpdateThrew ex)
        | TooLong limit -> refuseStep (Step envelope) (ChainTooLong limit)
        | Computed chain -> decide (Step envelope) chain

    /// Teardown (rule 24), in order: the flag; every token revoked; every running resource stopped under its
    /// own guard; what was collected and not started discarded; every parked step resolved as cancelled; the
    /// final model handed to the host.
    let teardown () =
        if not terminated then
            terminated <- true

            for token in inflight do
                token.Revoke()

            inflight.Clear()

            for KeyValue(key, (_, instance)) in running do
                stop key instance

            running <- Map.empty
            held <- Map.empty

            while (Queue.pop host.Queue).IsSome do
                ()

            for p in parked do
                refusals.Add Cancelled
                host.Report(StepRefused(Step p.Envelope, Cancelled))
                record (Step p.Envelope) (Refused "cancelled")

            parked <- []
            host.OnTerminated model

    /// A drain (rules 12 to 14): `first` runs under the latch, then queued envelopes are processed until the
    /// queue is empty after `DrainEnded`, or the drain's fuel is spent; then one render. A render that
    /// dispatches opens a new drain; a yield schedules a continuation. The boot drain renders at its end only
    /// if a step committed after the boot paint.
    let rec drain (isBoot: bool) (first: unit -> unit) =
        draining <- true
        let mutable continueLater = false

        try
            let modelAtStart = model
            let mutable firstPass = isBoot
            first ()
            let mutable again = true

            while again && not terminated do
                again <- false
                let mutable processed = 0
                let mutable idle = false
                let mutable yielded = false

                while not idle && not terminated do
                    if terminationRequested then
                        teardown ()
                    elif Queue.isEmpty host.Queue then
                        interpreter.DrainEnded()

                        if Queue.isEmpty host.Queue then
                            idle <- true
                    elif processed >= host.Fuel.Drain then
                        idle <- true
                        yielded <- true
                    else
                        match Queue.pop host.Queue with
                        | Some envelope ->
                            processStep envelope
                            processed <- processed + 1
                        | None -> idle <- true

                if not terminated then
                    // One render per drain. The boot drain painted the `Init` model before anything ran, and
                    // renders again only if a step moved the model since that paint.
                    if not firstPass || not (ResourceDiff.sameInstance modelAtStart model) then
                        render ()

                    firstPass <- false

                    if yielded then
                        yields <- yields + 1
                        host.Report(Yielded processed)
                        continueLater <- true
                    elif not (Queue.isEmpty host.Queue) then
                        again <- true

            // A termination requested during the render takes effect now that the drain has ended.
            if terminationRequested then
                teardown ()
        finally
            draining <- false

        // The yield's continuation is scheduled once the latch is released, so a host that runs it at once
        // opens the new drain rather than finding the latch set.
        if continueLater then
            host.Schedule(fun () ->
                if not terminated && not draining then
                    drain false ignore)

    do
        openDrain <-
            fun () ->
                if not draining && not terminated && not (Queue.isEmpty host.Queue) then
                    drain false ignore

    /// A runtime that boots from `Init flags` at sequence zero.
    new
        (
            comp: Component<'flags, 'model, 'msg, 'effect, 'resource, 'view>,
            interpreter: Interpreter<'msg, 'effect, 'resource>,
            host: Host<'model, 'msg, 'effect, 'resource, 'view>,
            flags: 'flags
        ) =
        Runtime(comp, interpreter, host, comp.Init flags, 0L)

    /// A runtime resumed from a restored session (DECISIONS.md D28): it boots from `model` with `steps` as its boot
    /// steps (what a restore asks for: effects sent again, messages for the component) instead of `Init`, and its
    /// boot is stamped at sequence `after + 1`, where `after` is the last sequence the restored log recorded, so
    /// every stamp and every fresh id after the restore is derived from a sequence no earlier step used.
    static member Resume
        (
            comp: Component<'flags, 'model, 'msg, 'effect, 'resource, 'view>,
            interpreter: Interpreter<'msg, 'effect, 'resource>,
            host: Host<'model, 'msg, 'effect, 'resource, 'view>,
            model: 'model,
            steps: Step<'msg, 'effect> list,
            after: int64
        ) : Runtime<'flags, 'model, 'msg, 'effect, 'resource, 'view> =
        Runtime(comp, interpreter, host, (model, steps), after + 1L)

    /// The current model.
    member _.Model = model

    /// How many times the view was rendered: one at boot, then one per drain.
    member _.Renders = renders

    /// How many drains yielded at their fuel bound.
    member _.Yields = yields

    /// How many envelopes were refused at acceptance.
    member _.RefusedAtAcceptance = refusedAtAcceptance

    /// Every step refusal, in order.
    member _.Refusals: Refusal<'effect> list = List.ofSeq refusals

    /// Whether the runtime has terminated.
    member _.IsTerminated = terminated

    /// The running resources, by key.
    member _.Running: Map<ResourceKey, 'resource> = running |> Map.map (fun _ (r, _) -> r)

    /// The token id of a running instance, for a host that correlates events.
    member _.InstanceOf(key: ResourceKey) : int option =
        running
        |> Map.tryFind key
        |> Option.map (fun (_, instance) -> instance.Token.Id)

    /// The held keys: each start threw under this description.
    member _.Held: Map<ResourceKey, 'resource> = held

    /// The parked steps, oldest first.
    member _.Parked: ParkedStep<'model, 'msg, 'effect, 'resource> list = parked

    /// The step records the log holds, in the order decided: the most recent `Fuel.Log` of them (DECISIONS.md D28.12).
    /// A history the log truncated says so in `LogDropped`; the whole history is the sink's (`Host.Record`).
    member _.Log: StepRecord<'msg> list =
        match log with
        | None -> []
        | Some ring ->
            // Read by turning the ring once round: each record popped is pushed back behind the rest, so the ring
            // ends as it began.
            let held = ResizeArray<StepRecord<'msg>>(ring.Count)

            for _ in 1 .. ring.Count do
                match ring.Pop() with
                | Some entry ->
                    held.Add entry
                    ring.Push entry |> ignore
                | None -> ()

            List.ofSeq held

    /// How many step records the log dropped, oldest first, to stay within `Fuel.Log`: every record made and not
    /// held. Zero means `Log` is the whole history.
    member _.LogDropped: int64 = logDropped

    /// The sequence number of the last accepted envelope.
    member _.Sequence = sequence

    /// Boot (rule 15): paint the `Init` model; then the boot step — `Init`'s steps under the boot stamp,
    /// authored by the host, decided as a unit and committed with the resource plan; then the boot drain.
    member _.Start() =
        if started then
            invalidOp "Runtime.Start was called twice"

        started <- true

        drain true (fun () ->
            render ()

            let bootStamp = {
                At = host.Clock()
                Sequence = bootSequence
                Seed = Seed.ofSequence host.Seed bootSequence
            }

            match Chain.continueFrom comp.Update bootStamp host.Fuel.Chain 0 model initSteps with
            | Threw ex -> refuseStep (BootStep bootStamp) (UpdateThrew ex)
            | TooLong limit -> refuseStep (BootStep bootStamp) (ChainTooLong limit)
            | Computed chain -> decide (BootStep bootStamp) chain)

    /// Dispatch a message from outside, written by `author`. The envelope is accepted or the producer is
    /// told why not; an accepted envelope is processed in the drain that is open, or in a new one.
    member _.Dispatch(msg: 'msg, author: Principal) : Result<int64, NotAccepted> =
        let result = accept null msg author External None

        if not draining && not terminated && not (Queue.isEmpty host.Queue) then
            drain false ignore

        result

    /// What `msg` would do on the current model under `stamp`: the chain, the proposal and the decision,
    /// nothing performed or installed. Reads no clock.
    member _.Preview(stamp: Stamp, msg: 'msg, author: Principal) : Preview<'model, 'msg, 'effect, 'resource> =
        match Chain.compute comp.Update stamp host.Fuel.Chain model msg with
        | Computed chain ->
            let _, plan = planFor chain.Model

            match describe chain plan with
            | Error kind -> {
                Outcome = Computed chain
                Proposal = None
                Decision = Some(Deny("unregistered kind: " + kind))
              }
            | Ok(handled, starts) ->
                let proposal = {
                    Author = author
                    Sequence = stamp.Sequence
                    At = stamp.At
                    Effects = handled |> List.map (fun (h, e) -> h.Invocation, e)
                    Starts = starts
                    Stops = plan.Stops
                    Grant = None
                }

                {
                    Outcome = Computed chain
                    Proposal = Some proposal
                    Decision = Some(host.Decide proposal)
                }
        | outcome -> {
            Outcome = outcome
            Proposal = None
            Decision = None
          }

    /// Present a grant for a parked step (DECISIONS.md D27.4). The parked step the grant names is resolved at
    /// once, whatever follows, so a grant is single use. The chain is computed again on the CURRENT model with the
    /// envelope's own stamp; if the request it now makes is not the one the grant was minted for (its
    /// invocations, starts or stops moved since the preview) the step is refused as stale. Otherwise the step is
    /// decided again with the grant presented — through the host's decision, which is where the registry's
    /// needs-approval is lifted for exactly this request, the approver's own ceiling is read, and an author
    /// approving their own step is refused — and commits only on allow. Answers whether the grant named a step
    /// still parked: `false` for a grant for another step, or one already presented.
    member _.Approve(grant: Grant) : bool =
        match parked |> List.tryFind (fun p -> p.Id = grant.Parked) with
        | None -> false
        | Some p ->
            parked <- parked |> List.filter (fun q -> q.Id <> grant.Parked)

            let approve () =
                let step = Step p.Envelope

                let refuseStep = refuseStepWith (Some grant)

                match Chain.compute comp.Update p.Envelope.Stamp host.Fuel.Chain model p.Envelope.Msg with
                | Threw ex -> refuseStep step (UpdateThrew ex)
                | TooLong limit -> refuseStep step (ChainTooLong limit)
                | Computed chain ->
                    let wanted, plan = planFor chain.Model

                    match describe chain plan with
                    | Error kind -> refuseStep step (Unregistered kind)
                    | Ok(handled, starts) ->
                        let proposal = {
                            Author = p.Envelope.Author
                            Sequence = p.Envelope.Sequence
                            At = host.Clock()
                            Effects = handled |> List.map (fun (h, e) -> h.Invocation, e)
                            Starts = starts
                            Stops = plan.Stops
                            Grant = Some grant
                        }

                        if Proposal.request proposal <> grant.Request then
                            refuseStep step Stale
                        else
                            match host.Decide proposal with
                            | Allow -> commit (Some grant) step chain wanted plan handled
                            | Deny reason -> refuseStep step (Denied reason)
                            | NeedsApproval ->
                                // The grant was presented and the decision still waits on an approval: the
                                // parked step is spent, and nothing can be parked twice under one grant.
                                refuseStep step (Denied "the grant presented did not lift the decision")

            if draining then approve () else drain false approve
            true

    /// Reject a parked step: refused, and the component may be told.
    member _.Reject(id: int) : bool =
        match parked |> List.tryFind (fun p -> p.Id = id) with
        | None -> false
        | Some p ->
            parked <- parked |> List.filter (fun q -> q.Id <> id)
            let reject () = refuseStep (Step p.Envelope) Rejected
            if draining then reject () else drain false reject
            true

    /// Terminate (rules 23 to 25). A request made during a step takes effect when the step ends. After it,
    /// nothing is accepted, performed, started or rendered.
    member _.Terminate() =
        if draining then
            terminationRequested <- true
        else
            teardown ()

/// Replay: the model a log reproduces, with nothing performed. Each committed step's chain is computed
/// again from its recorded stamp, so ids and times included. A log from a fresh boot: a resumed runtime's boot ran
/// a restore's steps rather than `Init`'s, which the session log records and replays (DECISIONS.md D28). The whole
/// log: `Runtime.Log` is one only while `Runtime.LogDropped` is zero; past its bound the history is the sink's (D28.12).
module Replay =

    /// Fold the committed steps of `log` over the component from `Init flags`.
    let run
        (comp: Component<'flags, 'model, 'msg, 'effect, 'resource, 'view>)
        (flags: 'flags)
        (log: StepRecord<'msg> list)
        : 'model =
        let initModel, initSteps = comp.Init flags
        let unbounded = Int32.MaxValue

        log
        |> List.fold
            (fun model entry ->
                let outcome =
                    match entry.Outcome, entry.Of with
                    | Committed, BootStep stamp ->
                        Some(Chain.continueFrom comp.Update stamp unbounded 0 model initSteps)
                    | Committed, Step envelope ->
                        Some(Chain.compute comp.Update envelope.Stamp unbounded model envelope.Msg)
                    | _ -> None

                match outcome with
                | Some(Computed chain) -> chain.Model
                | _ -> model)
            initModel
