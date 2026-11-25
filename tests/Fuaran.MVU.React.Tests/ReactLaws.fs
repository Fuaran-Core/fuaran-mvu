// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Diametrical Ltd

/// The React adapter's laws (Phase 720, t7; DECISIONS.md D29.8), against a stand-in for React and the DOM: render
/// counts per drain, a yielded drain's continuation, hydration without a second initial render, unmount revocation,
/// the after-the-next-paint kind's one answer, hot reload, the element-view bridge and the slice hook. Host-neutral:
/// the same file runs on .NET and under Fable on Node, and its last lines are the runner. What the stand-in does not
/// model, and so what no law here shows of a real browser, is recorded in D29.8.
module Fuaran.MVU.React.Tests.ReactLaws

open System
open System.Collections.Generic
open Fable.React
open Fuaran.MVU
open Fuaran.MVU.React
open Fuaran.MVU.Tests.Check

// ---------------------------------------------------------------------------------------------------------------
// The stand-in. Elements are text, tags with children and an optional click, and components; a root renders in a
// task of its own, as React's concurrent root does; `useSyncExternalStore` subscribes once per component and renders
// again only when what `getSnapshot` answers is no longer the value the component last read; a hydrating root reads
// `getServerSnapshot` and compares what it draws with the markup it adopts.
// ---------------------------------------------------------------------------------------------------------------

/// A text node.
type Text(text: string) =
    member _.Value = text
    interface ReactElement

/// A host element: a tag, its children, and what a click on it does.
type Tag(name: string, children: ReactElement list, click: (unit -> unit) option) =
    member _.Name = name
    member _.Children = children
    member _.Click = click
    interface ReactElement

/// A component element.
type Composite(kind: Func<obj, ReactElement>, props: obj) =
    member _.Kind = kind
    member _.Props = props
    interface ReactElement

/// The stand-in DOM's container: the markup in it, and what the root did to it.
type Container(markup: string) =
    member val Markup = markup with get, set
    member val Hydrated = false with get, set
    member val Discarded = false with get, set
    member val Mismatches = 0 with get, set
    member val Unmounted = false with get, set

/// A subscription a component holds: how it reads, and what it read last.
type Subscription = { Get: Func<obj>; mutable Last: obj }

/// The stand-in for React and the browser's timing.
type StandIn() =
    let tasks = Queue<unit -> unit>()
    let paints = Queue<unit -> unit>()
    let slots = Dictionary<string, ResizeArray<obj>>()
    let renders = Dictionary<string, int>()
    let clicks = Dictionary<string, unit -> unit>()
    let unsubscribes = ResizeArray<Action>()
    let mutable path = ""
    let mutable index = 0
    let mutable hydrating = false
    let mutable live = false
    let mutable root: ReactElement = null
    let mutable container = Container ""

    let slot () =
        let held =
            match slots.TryGetValue path with
            | true, held -> held
            | _ ->
                let held = ResizeArray<obj>()
                slots[path] <- held
                held

        let i = index
        index <- index + 1
        held, i

    let rec draw (here: string) (element: ReactElement) : string =
        match box element with
        | :? Text as t -> t.Value
        | :? Tag as t ->
            match t.Click with
            | Some click -> clicks[t.Name] <- click
            | None -> ()

            let inner =
                t.Children
                |> List.mapi (fun i c -> draw (here + "/" + string i) c)
                |> String.concat ""

            "<" + t.Name + ">" + inner + "</" + t.Name + ">"
        | :? Composite as c ->
            let savedPath, savedIndex = path, index
            path <- here
            index <- 0

            renders[here] <-
                (match renders.TryGetValue here with
                 | true, n -> n
                 | _ -> 0)
                + 1

            let out = c.Kind.Invoke c.Props
            path <- savedPath
            index <- savedIndex
            draw (here + "/c") out
        | _ -> failwith "stand-in: an element it does not know"

    let drawRoot () =
        if live then
            container.Markup <- draw "root" root

    let unmountFrom (target: Container) =
        live <- false

        for u in unsubscribes do
            u.Invoke()

        unsubscribes.Clear()
        slots.Clear()
        target.Markup <- ""
        target.Unmounted <- true


    /// The timing the adapter is handed: tasks and paints wait until the law runs them.
    member _.Timing: Timing = {
        Task = fun f -> tasks.Enqueue f
        AfterPaint = fun f -> paints.Enqueue f
    }

    /// The slice of React the adapter is handed.
    member _.Api: ReactApi = {
        CreateElement = fun kind props -> Composite(unbox<Func<obj, ReactElement>> kind, props) :> ReactElement
        UseSyncExternalStore =
            fun subscribe getSnapshot getServerSnapshot ->
                let held, i = slot ()

                let value =
                    if hydrating then
                        getServerSnapshot.Invoke()
                    else
                        getSnapshot.Invoke()

                if i >= held.Count then
                    let sub = { Get = getSnapshot; Last = value }
                    held.Add(box sub)

                    unsubscribes.Add(
                        subscribe.Invoke(
                            Action(fun () ->
                                if live && not hydrating then
                                    let now = sub.Get.Invoke()

                                    if not (obj.ReferenceEquals(now, sub.Last)) then
                                        drawRoot ())
                        )
                    )
                else
                    (unbox<Subscription> held[i]).Last <- value

                value
        UseRef =
            fun initial ->
                let held, i = slot ()

                if i >= held.Count then
                    held.Add(box { current = initial })

                unbox<Ref> held[i]
        CreateRoot =
            fun target ->
                let target = unbox<Container> target

                {
                    Render =
                        fun element ->
                            tasks.Enqueue(fun () ->
                                if target.Markup <> "" then
                                    target.Discarded <- true

                                container <- target
                                root <- element
                                live <- true
                                drawRoot ())
                    Unmount = fun () -> unmountFrom target
                }
        HydrateRoot =
            fun target element ->
                let target = unbox<Container> target

                tasks.Enqueue(fun () ->
                    container <- target
                    root <- element
                    live <- true
                    hydrating <- true
                    let drawn = draw "root" element
                    hydrating <- false

                    if drawn <> target.Markup then
                        target.Mismatches <- target.Mismatches + 1

                    target.Hydrated <- true
                    target.Markup <- drawn

                    // As React does after hydrating: render again if a store moved past the server snapshot.
                    let moved =
                        slots.Values
                        |> Seq.collect id
                        |> Seq.exists (fun o ->
                            match o with
                            | :? Subscription as s -> not (obj.ReferenceEquals(s.Get.Invoke(), s.Last))
                            | _ -> false)

                    if moved then
                        drawRoot ())

                {
                    Render = fun _ -> failwith "stand-in: a hydrated root was rendered again"
                    Unmount = fun () -> unmountFrom target
                }
    }

    /// Run every waiting task, and the tasks they queue.
    member _.RunTasks() =
        while tasks.Count > 0 do
            (tasks.Dequeue()) ()

    /// Tasks waiting.
    member _.TasksWaiting = tasks.Count

    /// Paint a frame: run the callbacks waiting for it (not those they queue).
    member _.Paint() =
        let n = paints.Count

        for _ in 1..n do
            (paints.Dequeue()) ()

    /// How many times the component at `at` rendered (the root is "root").
    member _.Renders(at: string) =
        match renders.TryGetValue at with
        | true, n -> n
        | _ -> 0

    /// Click the tag named `name` in the last drawn tree.
    member _.Click(name: string) = clicks[name]()

// ---------------------------------------------------------------------------------------------------------------
// The component the laws mount: a counter whose view is a React element (the bridge).
// ---------------------------------------------------------------------------------------------------------------

type Msg =
    | Inc
    | Same
    | Other
    | Burst of int
    | Wait
    | Painted
    | Boot

type Model = { Count: int; Other: int; Painted: int }

/// A model of another shape, for hot reload.
type Reshaped = {
    Count: int
    Other: int
    Painted: int
    Added: string
}

type Eff =
    | Spawn of int
    | After of Msg

let mutable private inits = 0

let private counter (initSteps: Step<Msg, Eff> list) (view: Model -> ElementView<Msg>) = {
    Init =
        fun () ->
            inits <- inits + 1

            { Count = 0; Other = 0; Painted = 0 }, initSteps
    Update =
        fun _ msg (model: Model) ->
            match msg with
            | Inc -> { model with Count = model.Count + 1 }, []
            | Same -> model, []
            | Other -> { model with Other = model.Other + 1 }, []
            | Burst n -> model, [ Do(Spawn n) ]
            | Wait -> model, [ Do(After Painted) ]
            | Painted ->
                {
                    model with
                        Painted = model.Painted + 1
                },
                []
            | Boot -> { model with Count = model.Count + 100 }, []
    Resources = fun _ -> Map.empty<ResourceKey, unit>
    View = view
}

let private button =
    ElementView.ofView (fun (model: Model) dispatch ->
        Tag("button", [ Text(string model.Count) ], Some(fun () -> dispatch Inc)) :> ReactElement)

let private spawnKind: EffectKind = {
    Id = "laws/spawn"
    Shape = Shape.Signal
    Classes = Set.empty
    Determinism = Set.empty
    Answers = FirstParty
    Replies = []
}

let private interpreter (timing: Timing) (send: (Msg -> unit) ref) : Interpreter<Msg, Eff, unit> =
    Interpreter.create
        [ spawnKind; AfterPaint.kind ]
        (fun effect ->
            match effect with
            | Spawn n -> {
                Invocation = {
                    Kind = spawnKind.Id
                    Args = None
                    Destination = Local
                }
                Perform =
                    fun () ->
                        Performance.Signal(fun () ->
                            for _ in 1..n do
                                send.Value Inc)
              }
            | After msg -> AfterPaint.handle timing msg)
        (fun _ _ -> failwith "the laws' component holds no resources")

type private Mounting = {
    Attach: Attach
    Drain: int
    InitSteps: Step<Msg, Eff> list
    View: Model -> ElementView<Msg>
    Hot: (string * string) option
    Timing: Timing option
    Store: ModelStore<Model, ElementView<Msg>> option
}

let private plain: Mounting = {
    Attach = Fresh
    Drain = Fuel.defaults.Drain
    InitSteps = []
    View = button
    Hot = None
    Timing = None
    Store = None
}

let private mountOn (s: StandIn) (container: Container) (how: Mounting) =
    let send = ref (fun (_: Msg) -> ())
    let timing = defaultArg how.Timing s.Timing

    let host: Host<Model, Msg, Eff, unit, ElementView<Msg>> = {
        Host.create (fun () -> 0L) 7UL ignore with
            Fuel = { Fuel.defaults with Drain = how.Drain }
    }

    let options = {
        Mount.options (box container) ElementView.toElement host with
            Attach = how.Attach
            HotKey = how.Hot |> Option.map fst
            Shape = how.Hot |> Option.map snd |> Option.defaultValue ""
            Store = how.Store |> Option.defaultWith ModelStore
    }

    let mounted =
        Mount.mount
            { React = s.Api; Timing = s.Timing }
            options
            (counter how.InitSteps how.View)
            (interpreter timing send)
            ()

    send.Value <- fun msg -> mounted.Dispatch msg |> ignore
    mounted

let private countOf (mounted: Mounted<unit, Model, Msg, Eff, unit, ElementView<Msg>>) = mounted.Runtime.Model.Count

// ---------------------------------------------------------------------------------------------------------------
// The laws.
// ---------------------------------------------------------------------------------------------------------------

let cases: Case list = [
    case "one render per drain, and none for a drain that left the model as it was" (fun expect ->
        let s = StandIn()
        let c = Container ""
        let m = mountOn s c plain
        expect "the root is not rendered before React's task runs" (s.Renders "root" = 0)
        s.RunTasks()
        expect "the boot paint renders once" (s.Renders "root" = 1)
        expect "the boot paint is drawn" (c.Markup = "<button>0</button>")
        expect "the root renders fresh into an empty container" (not c.Discarded)
        m.Dispatch Inc |> ignore
        expect "a drain that moved the model renders once" (s.Renders "root" = 2)
        m.Dispatch Same |> ignore
        expect "a drain that left the model as it was is not published" (s.Renders "root" = 2)
        s.Click "button"
        expect "a click dispatches through the bridge, and renders once" (s.Renders "root" = 3)
        expect "the clicked count is drawn" (c.Markup = "<button>2</button>")
        expect "the runtime rendered once at boot and once per drain" (m.Runtime.Renders = 4)
        expect "the store published once per drain that moved the model" (m.Store.Published = 3))

    case
        "a yielded drain renders once, and its continuation renders once or, finding no work, not at all"
        (fun expect ->
            let s = StandIn()
            let m = mountOn s (Container "") { plain with Drain = 3 }
            s.RunTasks()
            m.Dispatch(Burst 3) |> ignore
            expect "the drain yielded once" (m.Runtime.Yields = 1)
            expect "it rendered what it had done" (countOf m = 2 && s.Renders "root" = 2)
            expect "its continuation waits for a task" (m.Scheduler.Pending)
            s.RunTasks()
            expect "the continuation drained the rest and rendered once" (countOf m = 3 && s.Renders "root" = 3)
            expect "the continuation ran" (m.Scheduler.Ran = 1 && m.Scheduler.Skipped = 0)
            m.Dispatch(Burst 3) |> ignore
            expect "a second burst yielded" (m.Runtime.Yields = 2 && countOf m = 5)
            m.Dispatch Inc |> ignore
            expect "a dispatch before the continuation drained everything" (countOf m = 7)
            let runtimeRenders = m.Runtime.Renders
            let reactRenders = s.Renders "root"
            s.RunTasks()
            expect "the continuation found no work and was skipped" (m.Scheduler.Skipped = 1 && m.Scheduler.Ran = 1)
            expect "so the runtime did not render again" (m.Runtime.Renders = runtimeRenders)
            expect "and React did not render again" (s.Renders "root" = reactRenders))

    case "an unmount revokes the instance: nothing from it is accepted, performed or rendered after" (fun expect ->
        let s = StandIn()
        let c = Container ""
        let m = mountOn s c { plain with Drain = 2 }
        s.RunTasks()
        m.Dispatch Wait |> ignore
        m.Dispatch(Burst 3) |> ignore
        expect "a paint is awaited and a continuation is pending" (m.Scheduler.Pending)
        let published = m.Store.Published
        let rendered = s.Renders "root"
        let refusedBefore = m.Runtime.RefusedAtAcceptance
        m.Unmount()
        expect "React's root is unmounted" (c.Unmounted && c.Markup = "")
        expect "React holds no subscription" (m.Store.Subscribers = 0)
        expect "the runtime is terminated" (m.Runtime.IsTerminated)

        expect
            "a dispatch after the unmount is refused"
            (match m.Dispatch Inc with
             | Error Terminated -> true
             | _ -> false)

        s.Paint()
        expect "the paint's answer is refused" (m.Runtime.RefusedAtAcceptance = refusedBefore + 2)
        expect "and changed nothing" (m.Runtime.Model.Painted = 0)
        s.RunTasks()
        expect "the pending continuation never ran" (m.Scheduler.Ran = 0)
        expect "nothing was published or rendered after" (m.Store.Published = published && s.Renders "root" = rendered)
        m.Unmount()
        expect "a second unmount does nothing" (c.Unmounted))

    case "hydration adopts the server's markup without a second initial render, and Init runs once" (fun expect ->
        let s = StandIn()
        let c = Container "<button>0</button>"
        let before = inits
        let m = mountOn s c { plain with Attach = Hydrate }
        s.RunTasks()
        expect "Init ran once" (inits = before + 1)
        expect "the markup was hydrated" (c.Hydrated && not c.Discarded)
        expect "with no mismatch" (c.Mismatches = 0)
        expect "and one render" (s.Renders "root" = 1)
        m.Dispatch Inc |> ignore

        expect
            "a later drain renders over the adopted markup"
            (c.Markup = "<button>1</button>" && s.Renders "root" = 2))

    case "a boot step that moves the model is a later render, not a mismatch with the server's markup" (fun expect ->
        let s = StandIn()
        let c = Container "<button>0</button>"
        let before = inits

        let _ =
            mountOn s c {
                plain with
                    Attach = Hydrate
                    InitSteps = [ Then Boot ]
            }

        s.RunTasks()
        expect "Init ran once" (inits = before + 1)
        expect "the hydrating render read the server's snapshot" (c.Hydrated && c.Mismatches = 0)
        expect "then the moved model rendered once" (c.Markup = "<button>100</button>" && s.Renders "root" = 2))

    case "a fresh root over server markup discards it (the stand-in's check, held to be able to fail)" (fun expect ->
        let s = StandIn()
        let c = Container "<button>0</button>"
        let _ = mountOn s c plain
        s.RunTasks()
        expect "the stand-in reports the discarded markup" (c.Discarded))

    case "after the next paint is a kind with one answer" (fun expect ->
        let s = StandIn()
        let m = mountOn s (Container "") plain
        s.RunTasks()
        m.Dispatch Wait |> ignore
        expect "no answer before the paint" (m.Runtime.Model.Painted = 0)
        s.Paint()
        expect "one answer after it" (m.Runtime.Model.Painted = 1)
        s.Paint()
        expect "and no second" (m.Runtime.Model.Painted = 1)
        expect "nothing was refused" (m.Runtime.RefusedAtAcceptance = 0)

        let twice: Timing = {
            s.Timing with
                AfterPaint =
                    fun f ->
                        f ()
                        f ()
        }

        let s2 = StandIn()
        let m2 = mountOn s2 (Container "") { plain with Timing = Some twice }
        s2.RunTasks()
        m2.Dispatch Wait |> ignore
        expect "a timing that calls back twice still answers once" (m2.Runtime.Model.Painted = 1)
        expect "and the second call reached nothing" (m2.Runtime.RefusedAtAcceptance = 0)

        expect
            "the kind is a call, answered by the host"
            (AfterPaint.kind.Shape = Call && AfterPaint.kind.Answers = FirstParty))

    case "hot reload keeps a model of the same shape and starts fresh when the shape moved" (fun expect ->
        let shape = HotReload.shapeOf typeof<Model>
        let reshaped = HotReload.shapeOf typeof<Reshaped>
        let s = StandIn()
        let first = Container ""
        let m1 = mountOn s first { plain with Hot = Some("hot", shape) }
        s.RunTasks()
        m1.Dispatch Inc |> ignore
        m1.Dispatch Inc |> ignore
        let before = inits
        let second = Container ""
        let m2 = mountOn s second { plain with Hot = Some("hot", shape) }
        s.RunTasks()

        if HotReload.enabled then
            expect "the shape is read the same twice" (shape = HotReload.shapeOf typeof<Model>)
            expect "another shape reads differently" (shape <> reshaped)
            expect "the replaced instance was unmounted" (first.Unmounted && m1.Runtime.IsTerminated)
            expect "the model was carried" (m2.Resumed && countOf m2 = 2)
            expect "Init did not run again" (inits = before)
            expect "the resumed runtime stamps after the carried sequence" (m2.Runtime.Sequence > m1.Runtime.Sequence)
            expect "a reload renders fresh, never hydrating an emptied container" (not second.Hydrated)
            expect "and draws the carried model" (second.Markup = "<button>2</button>")
            let third = Container ""

            let m3 =
                mountOn s third {
                    plain with
                        Hot = Some("hot", reshaped)
                }

            s.RunTasks()
            expect "a model whose shape moved is not carried" (not m3.Resumed && countOf m3 = 0)
            expect "Init ran for it" (inits = before + 1)
            expect "and the instance it replaced was unmounted" (second.Unmounted)
            m3.Unmount()
            let m4 = mountOn s (Container "") { plain with Hot = Some("hot", shape) }
            expect "an instance the application unmounted is not carried" (not m4.Resumed)
        else
            expect "a build without hot reload reads no shape" (shape = "")
            expect "and carries nothing" (not m2.Resumed && countOf m2 = 0 && not first.Unmounted))

    case "a view that is a React element runs through the bridge" (fun expect ->
        let s = StandIn()
        let c = Container ""
        let m = mountOn s c plain
        s.RunTasks()
        s.Click "button"
        s.Click "button"
        expect "two clicks are two messages from the view" (countOf m = 2)

        expect
            "attributed to the view's author"
            (m.Runtime.Log
             |> List.forall (fun r ->
                 match r.Of with
                 | Step e -> e.Author = User "local"
                 | BootStep _ -> true))

        expect "and drawn" (c.Markup = "<button>2</button>"))

    // A view may key a memo, an effect's dependencies or a handler cache on the dispatch's identity, so the mount
    // hands it one function value for the mount's life (DECISIONS.md D32). On .NET and in a Debug transpile a local
    // function already was one; a Release transpile by Fable 5 inlined it and built a new one per render, which only
    // this law, run under Node from the Release transpile, can see.
    case "the view is handed one dispatch, the same function value on every render" (fun expect ->
        let s = StandIn()
        let handed = ResizeArray<obj>()

        let view =
            ElementView.ofView (fun (model: Model) dispatch ->
                handed.Add(box dispatch)
                Tag("button", [ Text(string model.Count) ], Some(fun () -> dispatch Inc)) :> ReactElement)

        let m = mountOn s (Container "") { plain with View = view }
        s.RunTasks()
        m.Dispatch Inc |> ignore
        s.Click "button"
        expect "the view was built on three renders" (handed.Count = 3 && s.Renders "root" = 3)

        expect
            "against the identical dispatch each time"
            (handed |> Seq.forall (fun d -> obj.ReferenceEquals(d, handed[0])))

        expect "which reaches the runtime" (countOf m = 2))

    case "the slice hook answers the previous slice while it is unchanged" (fun expect ->
        let s = StandIn()
        let reads = ResizeArray<obj>()
        // The store is made before the mount, so the view can read slices of it: forge's sliced shell.
        let store = ModelStore<Model, ElementView<Msg>>()

        let child =
            Func<obj, ReactElement>(fun _ ->
                let slice =
                    ModelStore.useSelector s.Api store (fun (m: Model) -> (m.Count, "count")) (=)

                reads.Add(box slice)
                Text(string (fst slice)) :> ReactElement)

        let view =
            fun (_: Model) -> ElementView(fun _ -> Composite(child, null) :> ReactElement)

        let m =
            mountOn s (Container "") {
                plain with
                    View = view
                    Store = Some store
            }

        expect "the mount published to the store it was handed" (obj.ReferenceEquals(m.Store, store))
        s.RunTasks()
        m.Dispatch Other |> ignore
        m.Dispatch Inc |> ignore
        expect "the child read three times" (reads.Count = 3)
        expect "an unrelated change answered the same slice" (obj.ReferenceEquals(reads[0], reads[1]))
        expect "a change to the slice answered a new one" (not (obj.ReferenceEquals(reads[1], reads[2]))))
]

// ---------------------------------------------------------------------------------------------------------------
// A governed surface refuses a component whose view is an element view: at compile time.
// ---------------------------------------------------------------------------------------------------------------

/// A view that carries the governed mark, as a tree will: it is admitted.
type TreeView = {
    Text: string
} with

    interface IGovernedView

let private admitted: Component<unit, Model, Msg, Eff, unit, TreeView> =
    let c = counter [] button

    Governed.admit {
        Init = c.Init
        Update = c.Update
        Resources = c.Resources
        View = fun m -> { Text = string m.Count }
    }

#if NEGATIVE_GOVERNED
// This line must not compile: `ElementView<Msg>` does not carry `IGovernedView`. The gate builds it and requires
// the refusal (DECISIONS.md D29.5).
let private refused = Governed.admit (counter [] button)
#endif

// ---------------------------------------------------------------------------------------------------------------
// The runner.
// ---------------------------------------------------------------------------------------------------------------

let private failed = runAll "react-laws" cases

if failed > 0 then
    failwithf "%d React law(s) failed" failed

printfn
    "react: every law passed (hot reload %s in this build)"
    (if HotReload.enabled then "carried" else "compiled out")
