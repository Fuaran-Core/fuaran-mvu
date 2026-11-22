// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Diametrical Ltd

namespace Fuaran.MVU

open System

/// What an effect kind is: one answer, none, or a resource.
type Shape =
    | Call
    | Signal
    | Stream

/// What a kind's performer reads that a replay must record.
type DeterminismSource =
    | Clock
    | Random
    | Network

/// Whose content a kind's answer is: the runtime authors the reply from it (rule 2), so nothing a
/// performer or a client sends can set the author. A reply to a model call is the agent's (`FromModel`: the
/// model, its version and the id it acts under), a reply from outside the trust boundary is foreign
/// (`Untrusted`), and a reply from the application's own contract is the host's (`FirstParty`).
type Trust =
    | FirstParty
    | Untrusted of who: string
    | FromModel of model: string * version: string * id: string

/// A kind, registered once by the host. Its classes are fixed here: an effect names its kind and cannot
/// state its own class per call.
type EffectKind = {
    /// Minted by the host, namespaced by module.
    Id: string
    Shape: Shape
    /// Authority classes, from the host's vocabulary.
    Classes: Set<string>
    Determinism: Set<DeterminismSource>
    /// Whose content an answer is.
    Answers: Trust
    /// The message kinds a reply may produce.
    Replies: string list
}

/// Where an invocation goes.
type Destination =
    | Local
    | Remote of origin: string

/// What a performer is asked to do, as the gate sees it: the kind, the arguments in their canonical text,
/// and the destination.
type Invocation = {
    Kind: string
    Args: string option
    Destination: Destination
}

/// How a performed effect answers: exactly one message, delivered through the callback it is handed, or
/// none. An answer is a new message, accepted and decided on its own; a performer never answers into the
/// step that emitted it.
type Performance<'msg> =
    | Reply of (('msg -> unit) -> unit)
    | Signal of (unit -> unit)

/// An effect, interpreted: its invocation and its performer, from one function so the two cannot disagree.
type Handled<'msg> = {
    Invocation: Invocation
    Perform: unit -> Performance<'msg>
}

/// A resource, interpreted: its invocation and how to start it. `Start` is handed the emitter for its
/// instance and returns what stops it.
type Started<'msg> = {
    Invocation: Invocation
    Start: ('msg -> unit) -> IDisposable
}

module Handled =

    /// A child's handled effect in the parent's message type.
    let mapMsg (f: 'a -> 'b) (handled: Handled<'a>) : Handled<'b> = {
        Invocation = handled.Invocation
        Perform =
            fun () ->
                match handled.Perform() with
                | Reply perform -> Reply(fun deliver -> perform (fun msg -> deliver (f msg)))
                | Signal perform -> Signal perform
    }

module Started =

    /// A child's started resource in the parent's message type.
    let mapMsg (f: 'a -> 'b) (started: Started<'a>) : Started<'b> = {
        Invocation = started.Invocation
        Start = fun emit -> started.Start(fun msg -> emit (f msg))
    }

/// Why a step was refused, handed to the interpreter's `Refused`, which may give the component a message.
type Refusal<'effect> =
    /// The decision was deny.
    | Denied of reason: string
    /// `update` threw while the chain was computed.
    | UpdateThrew of exn
    /// The chain reached its fuel bound: a follow-up loop, refused whole.
    | ChainTooLong of limit: int
    /// An effect of the step names a kind the interpreter has not registered, so its reply could not be
    /// authored.
    | Unregistered of kind: string
    /// A performer threw when the committed step's effect was handed over.
    | PerformerThrew of effect: 'effect * exn
    /// An approval arrived, the chain was computed again, and its effects or plan no longer matched.
    | Stale
    /// A parked step was rejected.
    | Rejected
    /// A parked step was resolved as cancelled by termination.
    | Cancelled

/// Why the runtime did not accept an envelope; the producer is told.
type NotAccepted =
    | Terminated
    /// The source's liveness token is revoked: a stopped resource instance, a one-shot effect answering
    /// twice, an in-flight reply after termination.
    | Revoked
    /// The bounded queue is full.
    | QueueFull

/// The host's interpreter: the kinds it registers, one function per effect and per resource returning the
/// invocation and the performer, and the three hooks.
type Interpreter<'msg, 'effect, 'resource> = {
    Kinds: EffectKind list
    Effect: 'effect -> Handled<'msg>
    Resource: ResourceKey -> 'resource -> Started<'msg>
    /// The step was refused: the gate said no, `update` or a performer threw, the chain was too long. The
    /// message returned is authored by the host.
    Refused: Refusal<'effect> -> 'msg option
    /// A resource's start threw; the key is held under that description until it changes.
    Lost: ResourceKey -> 'resource -> exn -> 'msg option
    /// The drain found the queue empty; a performer that batches flushes here, and may accept more.
    DrainEnded: unit -> unit
}

module Interpreter =

    /// An interpreter from its kinds and its two functions, with the hooks doing nothing.
    let create
        (kinds: EffectKind list)
        (effect: 'effect -> Handled<'msg>)
        (resource: ResourceKey -> 'resource -> Started<'msg>)
        : Interpreter<'msg, 'effect, 'resource> =
        {
            Kinds = kinds
            Effect = effect
            Resource = resource
            Refused = fun _ -> None
            Lost = fun _ _ _ -> None
            DrainEnded = ignore
        }

    /// A kind by its id.
    let tryKind (id: string) (interpreter: Interpreter<'msg, 'effect, 'resource>) : EffectKind option =
        interpreter.Kinds |> List.tryFind (fun k -> k.Id = id)
