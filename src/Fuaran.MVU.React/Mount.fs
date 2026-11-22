// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Diametrical Ltd

namespace Fuaran.MVU.React

open System
open Fable.React
open Fuaran.MVU

/// What a mount reaches outside the runtime: React, and the browser's timing.
type Binding = { React: ReactApi; Timing: Timing }

/// How to mount (DECISIONS.md D29.1). `Host` is the runtime host the application builds (its clock, seed, decision,
/// fuel, queue, reports and record sink); the mount replaces its `Render` and `Schedule`, which are the adapter's.
/// `Author` is the principal a dispatch from the view is attributed to: a control the host drew is the user's.
/// `Store` is the store the mount publishes to: one per mount, created before it when the view reads slices of it
/// (forge's sliced shell, `ModelStore.useSelector`), and closed by its unmount. `HotKey` names the instance for hot
/// reload, and `Shape` is the model type's shape (`HotReload.shapeOf`); both are read only in a build that carries
/// hot reload.
type MountOptions<'model, 'msg, 'effect, 'resource, 'view> = {
    Container: obj
    Attach: Attach
    ToElement: 'view -> ('msg -> unit) -> ReactElement
    Author: Principal
    Host: Host<'model, 'msg, 'effect, 'resource, 'view>
    Store: ModelStore<'model, 'view>
    HotKey: string option
    Shape: string
}

/// A mounted component: its runtime, its store, its scheduler, the dispatch its view is handed, and its unmount.
[<Sealed>]
type Mounted<'flags, 'model, 'msg, 'effect, 'resource, 'view when 'effect: equality and 'resource: equality>
    internal
    (
        runtime: Runtime<'flags, 'model, 'msg, 'effect, 'resource, 'view>,
        store: ModelStore<'model, 'view>,
        scheduler: Scheduler,
        author: Principal,
        unmount: unit -> unit,
        resumed: bool
    ) =

    /// The runtime.
    member _.Runtime = runtime

    /// The model store React reads.
    member _.Store = store

    /// The continuation scheduler.
    member _.Scheduler = scheduler

    /// Whether this mount resumed from a model a hot reload carried, rather than running `Init`.
    member _.Resumed = resumed

    /// Dispatch `msg` as the view's author would: refused once the mount is unmounted.
    member _.Dispatch(msg: 'msg) : Result<int64, NotAccepted> = runtime.Dispatch(msg, author)

    /// Unmount (DECISIONS.md D29.3): terminate the runtime, which revokes every resource instance and in-flight reply
    /// so nothing from this instance is accepted after; close the scheduler and the store; unmount React's root.
    /// Idempotent.
    member _.Unmount() = unmount ()

module Mount =

    /// Options with the defaults: a fresh root, dispatches attributed to `User "local"`, a store of its own, no hot
    /// reload.
    let options
        (container: obj)
        (toElement: 'view -> ('msg -> unit) -> ReactElement)
        (host: Host<'model, 'msg, 'effect, 'resource, 'view>)
        : MountOptions<'model, 'msg, 'effect, 'resource, 'view> =
        {
            Container = container
            Attach = Fresh
            ToElement = toElement
            Author = User "local"
            Host = host
            Store = ModelStore()
            HotKey = None
            Shape = ""
        }

    /// Mount `comp` (DECISIONS.md D29.1, D29.3). The runtime renders once per drain; each render is published to the
    /// store, and the first publish attaches React's root (fresh, or over server-rendered markup), so the root's first
    /// read is the boot paint, the `Init` model. `Init` runs once, in the runtime's constructor; a mount that resumes
    /// a hot-reloaded model runs it not at all. A yielded drain's continuation is scheduled in a task of its own and
    /// skipped if the queue is empty by then.
    let mount
        (binding: Binding)
        (options: MountOptions<'model, 'msg, 'effect, 'resource, 'view>)
        (comp: Component<'flags, 'model, 'msg, 'effect, 'resource, 'view>)
        (interpreter: Interpreter<'msg, 'effect, 'resource>)
        (flags: 'flags)
        : Mounted<'flags, 'model, 'msg, 'effect, 'resource, 'view> =
        let store = options.Store
        let scheduler = Scheduler binding.Timing
        let queue = options.Host.Queue

        let carried =
            match options.HotKey with
            | Some key -> HotReload.take key
            | None -> None

        let kept = carried |> Option.filter (fun c -> c.Shape = options.Shape)
        let attach = Hydration.choose options.Attach carried.IsSome

        let mutable running: Runtime<'flags, 'model, 'msg, 'effect, 'resource, 'view> option =
            None

        let mutable root: Root option = None
        let mutable unmounted = false

        let dispatch (msg: 'msg) =
            match running with
            | Some runtime -> runtime.Dispatch(msg, options.Author) |> ignore
            | None -> ()

        let renderElement (snapshot: obj) : ReactElement =
            options.ToElement (unbox<StoreSnapshot<'model, 'view>> snapshot).View dispatch

        let render (view: 'view) =
            match running with
            | Some runtime when store.Publish(runtime.Model, view) && root.IsNone && not unmounted ->
                let element =
                    binding.React.CreateElement
                        (box StoreRoot.root)
                        (box {
                            Api = binding.React
                            Subscribe = store.Subscribe
                            GetSnapshot = store.GetSnapshot
                            GetServerSnapshot = store.GetServerSnapshot
                            Render = renderElement
                        })

                root <- Some(Hydration.attach binding.React options.Container attach element)
            | _ -> ()

        let host = {
            options.Host with
                Render = render
                Schedule = scheduler.Schedule(fun () -> not (Queue.isEmpty queue))
        }

        let runtime =
            match kept with
            | Some c -> Runtime.Resume(comp, interpreter, host, unbox<'model> c.Model, [], c.Sequence)
            | None -> Runtime(comp, interpreter, host, flags)

        running <- Some runtime

        let unmount () =
            if not unmounted then
                unmounted <- true
                scheduler.Close()
                runtime.Terminate()
                store.Close()

                match root with
                | Some r -> r.Unmount()
                | None -> ()

        let dispose () =
            let carry = {
                Model = box runtime.Model
                Sequence = runtime.Sequence
                Shape = options.Shape
            }

            unmount ()
            carry

        let unmountAndForget () =
            match options.HotKey with
            | Some key -> HotReload.forget key dispose
            | None -> ()

            unmount ()

        match options.HotKey with
        | Some key -> HotReload.register key dispose
        | None -> ()

        runtime.Start()
        Mounted(runtime, store, scheduler, options.Author, unmountAndForget, kept.IsSome)

/// The live binding to React and the browser (DECISIONS.md D29.2). Every function is inline: the imports of `react`
/// and `react-dom/client` are emitted in the module that calls them, so this package's own modules import nothing,
/// and the laws run under Node with no React installed.
module Live =

    open Fable.Core.JsInterop

    /// React, from the `react` and `react-dom/client` modules.
    let inline react () : ReactApi =
        let createElement: Func<obj, obj, ReactElement> = import "createElement" "react"

        let useSyncExternalStore: Func<obj, obj, obj, obj> =
            import "useSyncExternalStore" "react"

        let useRef: Func<obj, obj> = import "useRef" "react"
        let createRoot: Func<obj, obj> = import "createRoot" "react-dom/client"
        let hydrateRoot: Func<obj, obj, obj> = import "hydrateRoot" "react-dom/client"

        let rootOf (root: obj) : Root = {
            Render = fun element -> root?render (element) |> ignore
            Unmount = fun () -> root?unmount () |> ignore
        }

        {
            CreateElement = fun kind props -> createElement.Invoke(kind, props)
            UseSyncExternalStore =
                fun subscribe getSnapshot getServerSnapshot ->
                    useSyncExternalStore.Invoke(box subscribe, box getSnapshot, box getServerSnapshot)
            UseRef = fun initial -> unbox<Ref> (useRef.Invoke initial)
            CreateRoot = fun container -> rootOf (createRoot.Invoke container)
            HydrateRoot = fun container element -> rootOf (hydrateRoot.Invoke(container, box element))
        }

    /// The browser's timing: a task is a zero-delay timeout; after the next paint is a timeout queued from the next
    /// animation frame's callback, which runs before that frame is painted, so the timeout runs after it.
    let inline timing () : Timing = {
        Task = fun callback -> emitJsStatement callback "setTimeout($0, 0)"
        AfterPaint =
            fun callback -> emitJsStatement callback "requestAnimationFrame(function () { setTimeout($0, 0); })"
    }

    /// React and the browser's timing.
    let inline binding () : Binding = { React = react (); Timing = timing () }

    /// The document's element with `id`.
    let inline container (id: string) : obj =
        emitJsExpr id "document.getElementById($0)"

    /// The shape of `'model`'s type, for hot reload; empty in a build without it.
    let inline shapeOf<'model> () : string = HotReload.shapeOf typeof<'model>

    /// Mount a component whose view is a React element (the bridge) at the element with id `id`: fresh, or over
    /// server-rendered markup with `Hydrate`, and hot-reloaded under `id` in a development build.
    let inline elements
        (id: string)
        (attach: Attach)
        (host: Host<'model, 'msg, 'effect, 'resource, ElementView<'msg>>)
        (comp: Component<'flags, 'model, 'msg, 'effect, 'resource, ElementView<'msg>>)
        (interpreter: Interpreter<'msg, 'effect, 'resource>)
        (flags: 'flags)
        : Mounted<'flags, 'model, 'msg, 'effect, 'resource, ElementView<'msg>> =
        Mount.mount
            (binding ())
            {
                Mount.options (container id) ElementView.toElement host with
                    Attach = attach
                    HotKey = Some id
                    Shape = shapeOf<'model> ()
            }
            comp
            interpreter
            flags
