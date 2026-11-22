// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)
// Copyright 2026 Diametrical Ltd

namespace Fuaran.MVU.React

open System
open Fable.React
open Fuaran.MVU.Kernel

/// A React ref cell as `useRef` answers it: one mutable `current`.
type Ref = { mutable current: obj }

/// A root React renders into: `createRoot`'s answer, or `hydrateRoot`'s.
type Root = {
    Render: ReactElement -> unit
    Unmount: unit -> unit
}

/// The slice of React the adapter calls (DECISIONS.md D29.2). The mount takes it as a value, so the laws run it
/// against a stand-in and an application passes `Live.react ()`, the binding to the `react` and `react-dom/client`
/// modules. A component is a `Func<obj, ReactElement>` from its props.
type ReactApi = {
    CreateElement: obj -> obj -> ReactElement
    /// `useSyncExternalStore(subscribe, getSnapshot, getServerSnapshot)`.
    UseSyncExternalStore: Func<Action, Action> -> Func<obj> -> Func<obj> -> obj
    UseRef: obj -> Ref
    /// `createRoot(container)`.
    CreateRoot: obj -> Root
    /// `hydrateRoot(container, element)`.
    HydrateRoot: obj -> ReactElement -> Root
}

/// One published value: the model the runtime rendered, the view it built from it, and the publish's number. A new
/// snapshot is minted per publish and never between, so a reader comparing snapshots by reference sees a change
/// exactly when the store published one, which is what `useSyncExternalStore` asks of `getSnapshot`.
type StoreSnapshot<'model, 'view> = {
    Model: 'model
    View: 'view
    Version: int
}

/// The model store React subscribes to (DECISIONS.md D29.3). The runtime renders once per drain; the mount's render
/// hook publishes that render here, and React reads it through `useSyncExternalStore`. A render whose model is the
/// same instance as the last one published is not published, so React is not asked to draw a model it has drawn.
/// The first snapshot is kept as the server snapshot: a hydrating root reads the model the server rendered even if
/// the boot step moved it before React hydrated. Closed at unmount: nothing is published after, and subscribers are
/// dropped. The store decides nothing the runtime decides, so nothing under `proofs/` models it. The design follows
/// forge's store binding (toolup-forge, Apache-2.0), with the view built by the runtime and the dispatch the mount's.
[<Sealed>]
type ModelStore<'model, 'view>() =
    let listeners = ResizeArray<Action>()
    let mutable snapshot: StoreSnapshot<'model, 'view> option = None
    let mutable first: StoreSnapshot<'model, 'view> option = None
    let mutable published = 0
    let mutable closed = false

    // Handed to React as they are, so each is created once: a `subscribe` whose identity changed per render would
    // make React subscribe again on every render.
    let subscribe =
        Func<Action, Action>(fun listener ->
            if not closed then
                listeners.Add listener

            Action(fun () -> listeners.Remove listener |> ignore))

    let read (which: StoreSnapshot<'model, 'view> option) : obj =
        match which with
        | Some s -> box s
        | None -> null

    let getSnapshot = Func<obj>(fun () -> read snapshot)
    let getServerSnapshot = Func<obj>(fun () -> read first)

    /// The last snapshot published; `None` before the first.
    member _.Snapshot = snapshot

    /// The first snapshot published: what a hydrating root reads as the server's.
    member _.First = first

    /// How many snapshots were published.
    member _.Published = published

    /// How many subscribers React holds now.
    member _.Subscribers = listeners.Count

    /// Whether the store is closed.
    member _.IsClosed = closed

    /// `useSyncExternalStore`'s `subscribe`, stable for the store's life.
    member _.Subscribe = subscribe

    /// `useSyncExternalStore`'s `getSnapshot`, stable for the store's life.
    member _.GetSnapshot = getSnapshot

    /// `useSyncExternalStore`'s `getServerSnapshot`: the first snapshot, stable for the store's life.
    member _.GetServerSnapshot = getServerSnapshot

    /// Publish the runtime's render and notify every subscriber, synchronously. Answers whether it published: not
    /// after `Close`, and not when `model` is the same instance as the last snapshot's.
    member _.Publish(model: 'model, view: 'view) : bool =
        let unchanged =
            match snapshot with
            | Some s -> ResourceDiff.sameInstance s.Model model
            | None -> false

        if closed || unchanged then
            false
        else
            published <- published + 1

            let s = {
                Model = model
                View = view
                Version = published
            }

            snapshot <- Some s

            if first.IsNone then
                first <- Some s

            for listener in listeners.ToArray() do
                listener.Invoke()

            true

    /// Close the store: no publish after, and every subscriber dropped.
    member _.Close() =
        closed <- true
        listeners.Clear()

/// The root component's props. React copies a props object field by field, so these are plain fields; `Render`
/// closes over the mount's types.
type RootProps = {
    Api: ReactApi
    Subscribe: Func<Action, Action>
    GetSnapshot: Func<obj>
    GetServerSnapshot: Func<obj>
    Render: obj -> ReactElement
}

module StoreRoot =

    /// The root component: reads the store's snapshot and renders it. Created once, so React sees one component.
    let root: Func<obj, ReactElement> =
        Func<obj, ReactElement>(fun props ->
            let p = unbox<RootProps> props
            p.Render(p.Api.UseSyncExternalStore p.Subscribe p.GetSnapshot p.GetServerSnapshot))

/// What a slice reader remembers between reads: the snapshot and selector it read with, and what it answered.
type SliceCache = {
    Snapshot: obj
    Selector: obj
    Selection: obj
}

module ModelStore =

    /// Reference identity: the comparison a slice of an immutable model wants.
    let refEquals (a: 'slice) (b: 'slice) : bool = obj.ReferenceEquals(a, b)

    /// A React hook: the slice `selector` picks out of the store's model (forge's store binding, the design's §22). The
    /// calling component renders again when a publish produces a slice `equals` calls different, and never otherwise;
    /// when `equals` calls it the same, the previous value is answered, so a selector that allocates still reads as
    /// unchanged. A hydrating read takes the slice of the first snapshot. Call it only under the mount's root.
    let useSelector
        (api: ReactApi)
        (store: ModelStore<'model, 'view>)
        (selector: 'model -> 'slice)
        (equals: 'slice -> 'slice -> bool)
        : 'slice =
        let reader (cell: Ref) (pick: unit -> StoreSnapshot<'model, 'view> option) =
            Func<obj>(fun () ->
                let current =
                    match pick () with
                    | Some s -> s
                    | None -> invalidOp "ModelStore.useSelector: the store has published nothing yet."

                let last =
                    if isNull cell.current then
                        None
                    else
                        Some(unbox<SliceCache> cell.current)

                match last with
                | Some l when
                    obj.ReferenceEquals(l.Snapshot, current)
                    && obj.ReferenceEquals(l.Selector, selector)
                    ->
                    l.Selection
                | _ ->
                    let next = selector current.Model

                    let selection =
                        match last with
                        | Some l when equals (unbox<'slice> l.Selection) next -> l.Selection
                        | _ -> box next

                    cell.current <-
                        box {
                            Snapshot = box current
                            Selector = box selector
                            Selection = selection
                        }

                    selection)

        let cache = api.UseRef null
        let serverCache = api.UseRef null

        unbox<'slice> (
            api.UseSyncExternalStore
                store.Subscribe
                (reader cache (fun () -> store.Snapshot))
                (reader serverCache (fun () -> store.First))
        )
