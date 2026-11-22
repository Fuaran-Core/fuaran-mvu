// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Diametrical Ltd

namespace Fuaran.MVU.React

open Fuaran.MVU

/// The browser's timing, as the adapter uses it (DECISIONS.md D29.3, D29.6). `Task` runs a callback in a task of its
/// own, after the current one: where a yielded drain's continuation runs. `AfterPaint` runs a callback after the
/// browser has painted the next frame. The laws pass a stand-in that runs each when told; an application passes
/// `Live.timing ()`.
type Timing = {
    Task: (unit -> unit) -> unit
    AfterPaint: (unit -> unit) -> unit
}

/// Schedules a yielded drain's continuation (the runtime host's `Schedule`, DECISIONS.md D29.3). At most one
/// continuation is pending, in a task of its own so React renders the yielded drain's publish first. When it runs it
/// opens a drain only if the queue holds work: a drain opened since the yield may have taken it, and a continuation
/// that found nothing would have the runtime render the model it has just rendered. After `Close` (an unmount)
/// nothing pending runs.
[<Sealed>]
type Scheduler(timing: Timing) =
    let mutable pending = false
    let mutable closed = false
    let mutable ran = 0
    let mutable skipped = 0

    /// Continuations that opened a drain.
    member _.Ran = ran

    /// Continuations that found the queue empty and opened nothing.
    member _.Skipped = skipped

    /// Whether a continuation is waiting for its task.
    member _.Pending = pending

    /// Schedule `continuation`, to run in a task of its own if `hasWork` still holds then.
    member _.Schedule (hasWork: unit -> bool) (continuation: unit -> unit) : unit =
        if not closed && not pending then
            pending <- true

            timing.Task(fun () ->
                pending <- false

                if not closed then
                    if hasWork () then
                        ran <- ran + 1
                        continuation ()
                    else
                        skipped <- skipped + 1)

    /// Stop: nothing scheduled runs after this.
    member _.Close() = closed <- true

/// "After the next paint" (the design's §14, DECISIONS.md D29.6): a standard kind this adapter supplies, so an agent
/// loop can wait for the browser to draw what it just changed without the kernel knowing about painting. A call:
/// one answer, the message the effect carries, once the next frame is painted.
module AfterPaint =

    /// The kind's id.
    [<Literal>]
    let Kind = "fuaran.mvu.react/after-paint"

    /// The kind, for the host's interpreter to register: a call whose one answer is the application's own message,
    /// so the host authors it.
    let kind: EffectKind = {
        Id = Kind
        Shape = Call
        Classes = Set.empty
        Determinism = Set.empty
        Answers = FirstParty
        Replies = []
    }

    /// The interpreter's line for the kind: the invocation, and a performer that answers `answer` once, after the next
    /// paint. A timing that calls back twice still answers once.
    let handle (timing: Timing) (answer: 'msg) : Handled<'msg> = {
        Invocation = {
            Kind = Kind
            Args = None
            Destination = Local
        }
        Perform =
            fun () ->
                Reply(fun deliver ->
                    let mutable answered = false

                    timing.AfterPaint(fun () ->
                        if not answered then
                            answered <- true
                            deliver answer))
    }
