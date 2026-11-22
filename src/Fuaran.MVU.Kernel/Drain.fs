// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Diametrical Ltd

namespace Fuaran.MVU.Kernel

/// The drain buffer: the queue for a single-threaded host, and for .NET under the runtime's lock. An
/// array, a read cursor and a write cursor. `Push` writes at the write cursor; `Pop` and `Take` read at
/// the read cursor and advance it; both cursors return to zero only when the buffer is empty, so a backlog
/// left at a yield is simply still there, in order. When the write cursor reaches the end of the array
/// the live items move to the front (when at most half the array is live) or into an array twice the
/// size. A slot that has been read is cleared, so the buffer holds no reference to an item it handed over.
///
/// Its model is `proofs/Drain.fst`, clause for clause: `drain_is_queue` says that over any sequence of
/// pushes and pops the buffer answers exactly as a first-in first-out queue does and holds exactly what
/// that queue holds; `reset_agrees_with_empty` says the cursors are both zero exactly when the queue is
/// empty; `run_append` says a run that stops and resumes is the same run, so nothing is dropped across a
/// yield; `cleared_outside_window` says every slot outside the unread window is clear. The differential
/// test runs the extracted model beside this class on .NET and under Fable.
type DrainBuffer<'item>(capacity: int) =
    let mutable items: 'item array =
        Array.zeroCreate (max capacity DrainBuffer<'item>.MinimumCapacity)

    let mutable read = 0
    let mutable write = 0

    let advance () =
        read <- read + 1

        if read = write then
            read <- 0
            write <- 0

    /// The capacity floor, the one number the constructor reads: two slots, so the first compaction has
    /// room to decide between moving and growing.
    static member MinimumCapacity: int = 2

    /// The items pushed and not yet popped.
    member _.Count = write - read

    /// Whether nothing is waiting.
    member _.IsEmpty = read = write

    /// The slots the backing array holds now; it grows and never shrinks.
    member _.Capacity = items.Length

    /// Append an item. Never refused: the buffer grows.
    member _.Push(item: 'item) =
        if write = items.Length then
            let live = write - read

            let target =
                if live * 2 <= items.Length then
                    items
                else
                    Array.zeroCreate (items.Length * 2)

            Array.blit items read target 0 live

            if obj.ReferenceEquals(target, items) then
                Array.fill items live (items.Length - live) Unchecked.defaultof<'item>

            items <- target
            read <- 0
            write <- live

        items.[write] <- item
        write <- write + 1

    /// The oldest unread item, or `None` when the buffer is empty.
    member _.Pop() : 'item option =
        if read = write then
            None
        else
            let item = items.[read]
            items.[read] <- Unchecked.defaultof<'item>
            advance ()
            Some item

    /// The oldest unread item, for a caller that has checked `IsEmpty`. Taking from an empty buffer is a
    /// caller's defect and is refused with an exception rather than answered with a default.
    member _.Take() : 'item =
        if read = write then
            invalidOp "DrainBuffer.Take on an empty buffer"

        let item = items.[read]
        items.[read] <- Unchecked.defaultof<'item>
        advance ()
        item
