// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Diametrical Ltd

/// The kernel's laws, as checks over the shipped code alone: a reference queue beside each buffer, and
/// the diff's statements over small maps. Host-neutral: the Expecto suite and the Node runner both run
/// them, so "the laws pass on .NET and under Fable" is one list run twice.
module Fuaran.MVU.Tests.Laws

open Fuaran.MVU.Kernel
open Fuaran.MVU.Tests.Check
open Fuaran.MVU.Tests.Rand

/// A generated sequence of pushes (`Some item`) and pops (`None`).
let private script (seed: int) (steps: int) (pushBias: int) =
    let rand = Rand seed

    List.init steps (fun i -> if rand.Chance(pushBias, 10) then Some(i + 1) else None)

let private drainCases = [
    case "drain: a buffer answers as a FIFO queue over generated sequences" (fun expect ->
        for seed in 1..40 do
            let buffer = DrainBuffer<int>(1 + seed % 5)
            let mutable reference: int list = []

            for step in script seed 300 (4 + seed % 5) do
                match step with
                | Some item ->
                    buffer.Push item
                    reference <- reference @ [ item ]
                | None ->
                    let expected, rest =
                        match reference with
                        | [] -> None, []
                        | x :: rest -> Some x, rest

                    reference <- rest
                    expect (sprintf "seed %d: pop" seed) (buffer.Pop() = expected)

                expect (sprintf "seed %d: count" seed) (buffer.Count = List.length reference)
                expect (sprintf "seed %d: empty" seed) (buffer.IsEmpty = List.isEmpty reference))
    case "drain: a backlog survives a yield in order" (fun expect ->
        let buffer = DrainBuffer<int>(2)

        for item in 1..5 do
            buffer.Push item

        expect "first" (buffer.Pop() = Some 1)
        // The yield: nothing happens to the buffer between two drains.
        for item in 6..8 do
            buffer.Push item

        let drained = List.init buffer.Count (fun _ -> buffer.Take())
        expect "the rest in order" (drained = [ 2; 3; 4; 5; 6; 7; 8 ])
        expect "empty after" buffer.IsEmpty)
    case "drain: Take on an empty buffer is refused" (fun expect ->
        let buffer = DrainBuffer<int>(4)

        let refused =
            try
                buffer.Take() |> ignore
                false
            with _ ->
                true

        expect "refused" refused)
    case "drain: the capacity floor" (fun expect ->
        expect "two slots" (DrainBuffer<int>(0).Capacity = 2)
        expect "grows" (DrainBuffer<int>(-7).Capacity = 2))
]

let private ringCases = [
    case "ring: a ring answers as a bounded FIFO queue over generated sequences" (fun expect ->
        for seed in 1..40 do
            let capacity = 1 + seed % 7
            let ring = FixedRing<int>(capacity)
            let mutable reference: int list = []
            expect (sprintf "seed %d: capacity" seed) (ring.Capacity = capacity)

            for step in script seed 300 (4 + seed % 5) do
                match step with
                | Some item ->
                    let accepted = ring.Push item
                    let full = List.length reference = capacity
                    expect (sprintf "seed %d: refused exactly when full" seed) (accepted = not full)

                    if accepted then
                        reference <- reference @ [ item ]
                | None ->
                    let expected, rest =
                        match reference with
                        | [] -> None, []
                        | x :: rest -> Some x, rest

                    reference <- rest
                    expect (sprintf "seed %d: pop" seed) (ring.Pop() = expected)

                expect (sprintf "seed %d: count" seed) (ring.Count = List.length reference)
                expect (sprintf "seed %d: full flag" seed) (ring.IsFull = (List.length reference = capacity))
                expect (sprintf "seed %d: empty" seed) (ring.IsEmpty = List.isEmpty reference))
    case "ring: a refused push changes nothing" (fun expect ->
        let ring = FixedRing<int>(2)
        expect "first accepted" (ring.Push 1)
        expect "second accepted" (ring.Push 2)
        expect "third refused" (not (ring.Push 3))
        expect "count unchanged" (ring.Count = 2)
        expect "still full" ring.IsFull
        expect "pops the first" (ring.Pop() = Some 1)
        expect "pops the second" (ring.Pop() = Some 2)
        expect "then empty" (ring.Pop() = None))
    case "ring: the capacity floor" (fun expect ->
        expect "one item" (FixedRing<int>(0).Capacity = 1)
        let ring = FixedRing<int>(-3)
        expect "holds one" (ring.Push 7)
        expect "refuses the second" (not (ring.Push 8))
        expect "pops it" (ring.Pop() = Some 7))
]

/// A deterministic start: a key whose description is odd throws (is held), every other key starts.
let private startOddHeld (key: string) (description: int) =
    if description % 2 = 1 then
        None
    else
        Some(sprintf "%s/%d" key description)

let private diffCases = [
    case "diff: start, stop, keep and restart are exactly as specified" (fun expect ->
        let running = Map.ofList [ "a", (1, "a/1"); "b", (2, "b/2"); "c", (3, "c/3") ]
        let wanted = Map.ofList [ "b", 2; "c", 4; "d", 5 ]
        let plan = ResourceDiff.plan running Map.empty wanted
        expect "stops: a vanished, c changed" (plan.Stops = [ "a"; "c" ])
        expect "keeps: b unchanged" (plan.Keeps = [ "b" ])
        expect "starts: c restarted, d new" (plan.Starts = [ "c", 4; "d", 5 ])
        expect "not empty" (not (ResourceDiff.isEmpty plan)))
    case "diff: the keys afterwards are exactly the keys wanted" (fun expect ->
        for seed in 1..30 do
            let rand = Rand seed
            let keys = [ "k0"; "k1"; "k2"; "k3"; "k4"; "k5" ]

            let randomMap () =
                keys
                |> List.filter (fun _ -> rand.Chance(1, 2))
                |> List.map (fun k -> k, rand.Next 4)
                |> Map.ofList

            let wanted0 = randomMap ()
            let plan0 = ResourceDiff.plan Map.empty Map.empty wanted0

            let running1, held1 =
                ResourceDiff.apply (fun _ _ -> ()) startOddHeld Map.empty Map.empty wanted0 plan0

            let keysOf (m: Map<string, _>) = m |> Map.toList |> List.map fst

            expect
                (sprintf "seed %d: running + held = wanted" seed)
                (Set.ofList (keysOf running1 @ keysOf held1) = Set.ofList (keysOf wanted0))

            expect
                (sprintf "seed %d: disjoint" seed)
                (Set.intersect (Set.ofList (keysOf running1)) (Set.ofList (keysOf held1)) = Set.empty)

            let wanted1 = randomMap ()
            let plan1 = ResourceDiff.plan running1 held1 wanted1

            let running2, held2 =
                ResourceDiff.apply (fun _ _ -> ()) startOddHeld running1 held1 wanted1 plan1

            expect
                (sprintf "seed %d: second step too" seed)
                (Set.ofList (keysOf running2 @ keysOf held2) = Set.ofList (keysOf wanted1))

            for KeyValue(key, (description, _)) in running2 do
                expect
                    (sprintf "seed %d: running descriptions are the wanted ones" seed)
                    (Map.tryFind key wanted1 = Some description))
    case "diff: a held key with an unchanged description is not started" (fun expect ->
        let wanted = Map.ofList [ "a", 1 ]
        let plan0 = ResourceDiff.plan Map.empty Map.empty wanted
        expect "first attempt starts" (plan0.Starts = [ "a", 1 ])

        let running1, held1 =
            ResourceDiff.apply (fun _ _ -> ()) startOddHeld Map.empty Map.empty wanted plan0

        expect "held after the throw" (held1 = Map.ofList [ "a", 1 ] && running1.IsEmpty)
        let plan1 = ResourceDiff.plan running1 held1 wanted
        expect "not tried again" (ResourceDiff.isEmpty plan1)
        let changed = Map.ofList [ "a", 2 ]
        let plan2 = ResourceDiff.plan running1 held1 changed
        expect "tried once the description changes" (plan2.Starts = [ "a", 2 ])

        let running2, held2 =
            ResourceDiff.apply (fun _ _ -> ()) startOddHeld running1 held1 changed plan2

        expect "running now, hold dropped" (held2.IsEmpty && Map.containsKey "a" running2)

        let gone =
            ResourceDiff.apply
                (fun _ _ -> ())
                startOddHeld
                running1
                held1
                Map.empty
                (ResourceDiff.plan running1 held1 Map.empty)

        expect "a hold on a key no longer wanted is dropped" (snd gone).IsEmpty)
    case "diff: the plan applied twice is empty the second time" (fun expect ->
        for seed in 1..30 do
            let rand = Rand seed
            let keys = [ "k0"; "k1"; "k2"; "k3"; "k4" ]

            let randomMap () =
                keys
                |> List.filter (fun _ -> rand.Chance(2, 3))
                |> List.map (fun k -> k, rand.Next 4)
                |> Map.ofList

            let running0, held0 =
                let w = randomMap ()

                ResourceDiff.apply
                    (fun _ _ -> ())
                    startOddHeld
                    Map.empty
                    Map.empty
                    w
                    (ResourceDiff.plan Map.empty Map.empty w)

            let wanted = randomMap ()
            let plan = ResourceDiff.plan running0 held0 wanted

            let running1, held1 =
                ResourceDiff.apply (fun _ _ -> ()) startOddHeld running0 held0 wanted plan

            let again = ResourceDiff.plan running1 held1 wanted
            expect (sprintf "seed %d: empty" seed) (ResourceDiff.isEmpty again)

            expect
                (sprintf "seed %d: keeps everything running" seed)
                (again.Keeps = (running1 |> Map.toList |> List.map fst)))
    case "diff: every stop precedes every start, in key order" (fun expect ->
        let stopped = ResizeArray<string>()
        let started = ResizeArray<string>()
        let running = Map.ofList [ "d", (1, "d/1"); "b", (1, "b/1"); "a", (1, "a/1") ]
        let wanted = Map.ofList [ "c", 2; "b", 2; "a", 1; "e", 2 ]
        let plan = ResourceDiff.plan running Map.empty wanted

        let _ =
            ResourceDiff.apply
                (fun key _ -> stopped.Add key)
                (fun key description ->
                    started.Add key
                    Some(sprintf "%s/%d" key description))
                running
                Map.empty
                wanted
                plan

        expect "stops in key order" (List.ofSeq stopped = [ "b"; "d" ])
        expect "starts in key order" (List.ofSeq started = [ "b"; "c"; "e" ])
        expect "restart of b: stopped before started" (stopped.Count = 2 && started.Count = 3))
    case "diff: the identity skip agrees with the general path" (fun expect ->
        let model = [ "a", 1; "b", 2 ]
        expect "same instance" (ResourceDiff.sameInstance model model)

        expect
            "a structurally equal other instance is not the same instance"
            (not (ResourceDiff.sameInstance model (List.map id model))))
]

/// Every law, in one list.
let cases: Case list = drainCases @ ringCases @ diffCases
