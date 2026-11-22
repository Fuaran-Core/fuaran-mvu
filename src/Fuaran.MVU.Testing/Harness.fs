// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Diametrical Ltd

namespace Fuaran.MVU.Testing

open System
open Fuaran.MVU

/// How the scripted interpreter answers an effect: at once, with one message; later, when the test calls
/// `Answer`; or not at all, for an effect that is a signal.
type Answer<'msg> =
    | Immediate of 'msg
    | Outstanding
    | NoAnswer

/// What a harness is built with. Every member has a default (`HarnessOptions.defaults`): one registered
/// kind `test` whose answers are first-party, every effect and resource described under it, every effect
/// outstanding until the test answers it, the allow-all decision, a clock that advances by one on each read
/// from 1000, seed one, the default fuel.
type HarnessOptions<'msg, 'effect, 'resource> = {
    Kinds: EffectKind list
    Describe: 'effect -> Invocation
    DescribeResource: ResourceKey -> 'resource -> Invocation
    Answers: 'effect -> Answer<'msg>
    Decide: Decide<'effect, 'resource>
    Clock: unit -> int64
    Seed: uint64
    Fuel: Fuel
}

module HarnessOptions =

    /// The kind every effect and resource is described under unless the test says otherwise.
    let testKind: EffectKind = {
        Id = "test"
        Shape = Call
        Classes = Set.empty
        Determinism = Set.empty
        Answers = FirstParty
        Replies = []
    }

    let private testInvocation: Invocation = {
        Kind = "test"
        Args = None
        Destination = Local
    }

    /// A clock that reads 1001, 1002, ... on each read.
    let countingClock () : unit -> int64 =
        let mutable now = 1000L

        fun () ->
            now <- now + 1L
            now

    let defaults<'msg, 'effect, 'resource> : HarnessOptions<'msg, 'effect, 'resource> = {
        Kinds = [ testKind ]
        Describe = fun _ -> testInvocation
        DescribeResource = fun _ _ -> testInvocation
        Answers = fun _ -> Outstanding
        Decide = Decide.allowAll
        Clock = countingClock ()
        Seed = 1UL
        Fuel = Fuel.defaults
    }

/// The test harness: a runtime over a scripted interpreter. Effects are recorded as values and answered
/// from the options' table or by the test; resources are recorded as started and stopped, and the test
/// emits through a running instance; every rendered view is kept. A whole session runs on .NET or under
/// Fable with no host and no waiting. The stamp a direct `Update` call reads is the test's (`Stamp.forTest`);
/// the stamps the runtime fixes come from the options' clock and seed.
[<Sealed>]
type Harness<'flags, 'model, 'msg, 'effect, 'resource, 'view when 'effect: equality and 'resource: equality>
    (
        comp: Component<'flags, 'model, 'msg, 'effect, 'resource, 'view>,
        flags: 'flags,
        options: HarnessOptions<'msg, 'effect, 'resource>
    ) =
    let performed = ResizeArray<'effect>()
    let outstanding = ResizeArray<'effect * ('msg -> unit)>()
    let started = ResizeArray<'resource>()
    let stopped = ResizeArray<'resource>()
    let emitters = ResizeArray<ResourceKey * 'resource * ('msg -> unit)>()
    let views = ResizeArray<'view>()
    let reports = ResizeArray<Report<'msg, 'effect, 'resource>>()
    let mutable clockReads = 0

    let interpreter: Interpreter<'msg, 'effect, 'resource> = {
        Kinds = options.Kinds
        Effect =
            fun effect -> {
                Invocation = options.Describe effect
                Perform =
                    fun () ->
                        performed.Add effect

                        match options.Answers effect with
                        | Immediate msg -> Reply(fun deliver -> deliver msg)
                        | Outstanding -> Reply(fun deliver -> outstanding.Add((effect, deliver)))
                        | NoAnswer -> Signal ignore
            }
        Resource =
            fun key resource -> {
                Invocation = options.DescribeResource key resource
                Start =
                    fun emit ->
                        started.Add resource
                        emitters.Add((key, resource, emit))

                        { new IDisposable with
                            member _.Dispose() =
                                stopped.Add resource
                                emitters.RemoveAll(fun (k, r, _) -> k = key && r = resource) |> ignore
                        }
            }
        Refused = fun _ -> None
        Lost = fun _ _ _ -> None
        DrainEnded = ignore
    }

    let host: Host<'model, 'msg, 'effect, 'resource, 'view> = {
        Clock =
            fun () ->
                clockReads <- clockReads + 1
                options.Clock()
        Seed = options.Seed
        Render = views.Add
        Schedule = fun continuation -> continuation ()
        Report = reports.Add
        Record = ignore
        Decide = options.Decide
        Fuel = options.Fuel
        Queue = Queue.growing 16
        OnTerminated = ignore
    }

    let runtime = Runtime(comp, interpreter, host, flags)

    do runtime.Start()

    /// The runtime under test.
    member _.Runtime = runtime

    /// The current model.
    member _.Model = runtime.Model

    /// Dispatch a message as the test user.
    member _.Send(msg: 'msg) : Result<int64, NotAccepted> = runtime.Dispatch(msg, User "test")

    /// Dispatch a message as `author`.
    member _.SendAs(msg: 'msg, author: Principal) : Result<int64, NotAccepted> = runtime.Dispatch(msg, author)

    /// Every effect handed to the interpreter, in order.
    member _.Performed: 'effect list = List.ofSeq performed

    /// Forget the effects performed so far.
    member _.ClearPerformed() = performed.Clear()

    /// The effects answered neither at once nor yet by the test, oldest first.
    member _.Outstanding: 'effect list = [ for effect, _ in outstanding -> effect ]

    /// Answer the oldest outstanding effect with `msg`: the reply is accepted as a new envelope.
    member _.Answer(msg: 'msg) =
        if outstanding.Count = 0 then
            invalidOp "Harness.Answer: no effect is outstanding"

        let _, deliver = outstanding.[0]
        outstanding.RemoveAt 0
        deliver msg

    /// Answer the oldest outstanding effect that `matches`.
    member _.AnswerWhere(matches: 'effect -> bool, msg: 'msg) =
        match Seq.tryFindIndex (fun (effect, _) -> matches effect) outstanding with
        | None -> invalidOp "Harness.AnswerWhere: no outstanding effect matches"
        | Some index ->
            let _, deliver = outstanding.[index]
            outstanding.RemoveAt index
            deliver msg

    /// Every resource started, in order.
    member _.Started: 'resource list = List.ofSeq started

    /// Every resource stopped, in order.
    member _.Stopped: 'resource list = List.ofSeq stopped

    /// The emitter of the running instance with this resource value: what a late event through it does.
    member _.EmitterOf(resource: 'resource) : 'msg -> unit =
        match Seq.tryFind (fun (_, r, _) -> r = resource) emitters with
        | Some(_, _, emit) -> emit
        | None -> invalidOp "Harness.EmitterOf: no running instance has that resource"

    /// Emit a message from the running instance with this resource value.
    member this.Emit(resource: 'resource, msg: 'msg) = this.EmitterOf resource msg

    /// Every view rendered, oldest first.
    member _.Views: 'view list = List.ofSeq views

    /// Every report the runtime made, in order.
    member _.Reports: Report<'msg, 'effect, 'resource> list = List.ofSeq reports

    /// How many times the runtime read the clock.
    member _.ClockReads = clockReads

    /// What `msg` would do on the current model under `stamp`, performed nowhere.
    member _.Preview(stamp: Stamp, msg: 'msg, author: Principal) = runtime.Preview(stamp, msg, author)

module Harness =

    /// A harness with the default options.
    let start
        (comp: Component<'flags, 'model, 'msg, 'effect, 'resource, 'view>)
        (flags: 'flags)
        : Harness<'flags, 'model, 'msg, 'effect, 'resource, 'view> =
        Harness(comp, flags, HarnessOptions.defaults)

    /// A harness with the options given.
    let startWith
        (options: HarnessOptions<'msg, 'effect, 'resource>)
        (comp: Component<'flags, 'model, 'msg, 'effect, 'resource, 'view>)
        (flags: 'flags)
        : Harness<'flags, 'model, 'msg, 'effect, 'resource, 'view> =
        Harness(comp, flags, options)
