// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Eugene Tolmachev and Fable.Elmish contributors
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)
// Copyright 2026 Diametrical Ltd

namespace Fuaran.MVU.Kernel

/// The ring of fixed capacity: the queue for a host that must refuse rather than grow, such as a server
/// connection or a device with a single producer. It holds at most `Capacity` items; a push onto a full
/// ring is refused and changes nothing.
///
/// Adapted from the ring buffer of the ToolUp Analytics open-source SDK (toolup-forge, Apache-2.0) and the
/// array ring proved there to refine it. Taken from them: the backing array with one spare slot, the write
/// and read heads, the readable flag that tells one unread slot from none, and the cyclic index step.
/// Removed: the growth step, and with it every placeholder slot a grow manufactures. New: the refusal at
/// the configured capacity, and a read slot is cleared so the ring keeps no reference to an item it has
/// handed over.
///
/// Its model is `proofs/FixedRing.fst`: `ring_is_bounded_queue` says that over any sequence of pushes and
/// pops the ring answers exactly as a first-in first-out queue of capacity `Capacity` does, refusing a push
/// exactly when it holds `Capacity` items, and holds exactly what that queue holds; `refused_push_unchanged`
/// says a refused push leaves the ring as it was. The differential test runs the extracted model beside
/// this class on .NET and under Fable.
type FixedRing<'item>(capacity: int) =
    let cap = max capacity FixedRing<'item>.MinimumCapacity
    // One spare slot: in the two-head representation the write head never meets the read head while
    // items are unread, so `cap` items need `cap + 1` slots.
    let items: 'item array = Array.zeroCreate (cap + 1)
    let mutable wix = 0
    let mutable rix = 0
    let mutable readable = false

    let succ i =
        if i + 1 >= items.Length then 0 else i + 1

    /// The capacity floor: a ring holds at least one item.
    static member MinimumCapacity: int = 1

    /// The most items the ring holds; a push beyond it is refused.
    member _.Capacity = cap

    /// The items pushed and not yet popped.
    member _.Count =
        if not readable then 0
        elif wix >= rix then wix - rix
        else wix + items.Length - rix

    /// Whether nothing is waiting.
    member _.IsEmpty = not readable

    /// Whether the next push is refused.
    member _.IsFull = readable && succ wix = rix

    /// Append an item: `true`, or `false` when the ring is full and nothing changed.
    member _.Push(item: 'item) : bool =
        if not readable then
            items.[wix] <- item
            rix <- wix
            wix <- succ wix
            readable <- true
            true
        else
            let wix' = succ wix

            if wix' = rix then
                false
            else
                items.[wix] <- item
                wix <- wix'
                true

    /// The oldest unread item, or `None` when the ring is empty.
    member _.Pop() : 'item option =
        if not readable then
            None
        else
            let item = items.[rix]
            items.[rix] <- Unchecked.defaultof<'item>
            let rix' = succ rix

            if rix' = wix then readable <- false else rix <- rix'

            Some item
