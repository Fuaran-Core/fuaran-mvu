// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Diametrical Ltd

/// The differential: the extracted models (proofs/oracle/, compiled from what the prover writes) run beside
/// the shipped kernel over generated sequences, and must answer the same. This is the only evidence that
/// the proved models are about the code that ships. Host-neutral: the Expecto suite runs it on .NET under
/// the proof leg's `Proofs.Oracle` filter, and the Node runner runs it under Fable.
module Fuaran.MVU.Tests.Differential

open Fuaran.MVU.Kernel
open Fuaran.MVU.Tests.Check
open Fuaran.MVU.Tests.Rand

/// A generated sequence of pushes (`Some item`) and pops (`None`).
let private script (seed: int) (steps: int) (pushBias: int) =
    let rand = Rand seed
    List.init steps (fun i -> if rand.Chance(pushBias, 10) then Some(i + 1) else None)

/// The model's slot read back as the kernel's answer: a written slot is the item, a placeholder is a
/// disagreement the comparison will catch.
let private drainOut (o: Drain.opt<Drain.slot<int>>) =
    match o with
    | Drain.ONone -> None
    | Drain.OSome(Drain.Written v) -> Some v
    | Drain.OSome Drain.Placeholder -> Some -1

let private ringOut (o: FixedRing.out<int>) =
    match o with
    | FixedRing.Popped FixedRing.ONone -> Choice1Of2 None
    | FixedRing.Popped(FixedRing.OSome(FixedRing.Written v)) -> Choice1Of2(Some v)
    | FixedRing.Popped(FixedRing.OSome FixedRing.Placeholder) -> Choice1Of2(Some -1)
    | FixedRing.Pushed accepted -> Choice2Of2 accepted

let private drainCases = [
    case "Proofs.Oracle drain: the extracted model and DrainBuffer answer alike over generated sequences" (fun expect ->
        for seed in 1..60 do
            let capacity = seed % 6
            let steps = script seed 250 (3 + seed % 6)
            // The model, driven by its own extracted `run`.
            let ops =
                steps
                |> List.map (fun s ->
                    match s with
                    | Some v -> Drain.Push v
                    | None -> Drain.Pop)

            let result = Drain.run (Drain.create (bigint capacity)) ops
            let modelOuts = result.second |> List.map drainOut
            // The shipped class, driven step by step.
            let buffer = DrainBuffer<int>(capacity)

            let kernelOuts =
                steps
                |> List.choose (fun s ->
                    match s with
                    | Some v ->
                        buffer.Push v
                        None
                    | None -> Some(buffer.Pop()))

            expect (sprintf "seed %d: the same pops in the same order" seed) (modelOuts = kernelOuts)

            expect
                (sprintf "seed %d: the same backlog" seed)
                (int (result.first.write - result.first.read) = buffer.Count)

            expect
                (sprintf "seed %d: the same cursors at rest" seed)
                ((result.first.read = 0I && result.first.write = 0I) = buffer.IsEmpty))
    case "Proofs.Oracle drain: the model is a queue (go-red control: a wrong reference disagrees)" (fun expect ->
        let ops = [ Drain.Push 1; Drain.Push 2; Drain.Pop; Drain.Pop; Drain.Pop ]
        let result = Drain.run (Drain.create 2I) ops
        expect "FIFO" (List.map drainOut result.second = [ Some 1; Some 2; None ])
        expect "a LIFO reference would disagree" (List.map drainOut result.second <> [ Some 2; Some 1; None ]))
]

let private ringCases = [
    case "Proofs.Oracle ring: the extracted model and FixedRing answer alike over generated sequences" (fun expect ->
        for seed in 1..60 do
            let capacity = seed % 7
            let steps = script seed 250 (3 + seed % 6)

            let ops =
                steps
                |> List.map (fun s ->
                    match s with
                    | Some v -> FixedRing.Push v
                    | None -> FixedRing.Pop)

            let result = FixedRing.run (FixedRing.create (bigint capacity)) ops
            let modelOuts = result.second |> List.map ringOut
            let ring = FixedRing<int>(capacity)

            let kernelOuts =
                steps
                |> List.map (fun s ->
                    match s with
                    | Some v -> Choice2Of2(ring.Push v)
                    | None -> Choice1Of2(ring.Pop()))

            expect (sprintf "seed %d: the same answers in the same order" seed) (modelOuts = kernelOuts)
            expect (sprintf "seed %d: the same capacity" seed) (int (FixedRing.capacity result.first) = ring.Capacity)

            let modelEmpty =
                match result.first with
                | FixedRing.Writable _ -> true
                | FixedRing.ReadWritable _ -> false

            expect (sprintf "seed %d: the same emptiness" seed) (modelEmpty = ring.IsEmpty))
    case "Proofs.Oracle ring: a refused push changes nothing in the model" (fun expect ->
        let full =
            (FixedRing.run (FixedRing.create 2I) [ FixedRing.Push 1; FixedRing.Push 2 ]).first

        let refused = FixedRing.push 3 full
        expect "refused" (not refused.second)
        expect "unchanged" (refused.first = full))
]

/// A deterministic start outcome both sides read: the description's parity decides, salted by the seed.
let private startOutcome (seed: int) (key: int) (description: int) = (key + description + seed) % 3 <> 0

/// The kernel's map, as the model's sorted entry list.
let private entriesOf (m: Map<int, int>) : KeyedDiff.entry<bigint, bigint> list =
    m
    |> Map.toList
    |> List.map (fun (k, d) -> {
        KeyedDiff.entry.key = bigint k
        KeyedDiff.entry.description = bigint d
    })

let private keysOf (m: Map<int, 'v>) =
    m |> Map.toList |> List.map (fun (k, _) -> bigint k)

let private bigints (keys: int list) = keys |> List.map (fun k -> bigint k)

let private diffCases = [
    case "Proofs.Oracle diff: the extracted plan and ResourceDiff.plan agree over generated maps" (fun expect ->
        for seed in 1..80 do
            let rand = Rand seed

            let randomMap () =
                [ 0..7 ]
                |> List.filter (fun _ -> rand.Chance(1, 2))
                |> List.map (fun k -> k, rand.Next 3)
                |> Map.ofList

            let runningDescriptions = randomMap ()
            let running = runningDescriptions |> Map.map (fun k d -> d, sprintf "%d/%d" k d)
            // Held keys are never running: the invariant the runtime keeps.
            let held = randomMap () |> Map.filter (fun k _ -> not (Map.containsKey k running))
            let wanted = randomMap ()
            let plan = ResourceDiff.plan running held wanted

            let modelPlan =
                KeyedDiff.plan_of (entriesOf runningDescriptions) (entriesOf held) (entriesOf wanted)

            expect (sprintf "seed %d: stops" seed) (modelPlan.stops = bigints plan.Stops)
            expect (sprintf "seed %d: keeps" seed) (modelPlan.keeps = bigints plan.Keeps)

            expect
                (sprintf "seed %d: starts" seed)
                (modelPlan.starts = (plan.Starts
                                     |> List.map (fun (k, d) -> {
                                         KeyedDiff.entry.key = bigint k
                                         KeyedDiff.entry.description = bigint d
                                     })))

            // The plan applied: what runs and what is held afterwards.
            let start k d =
                if startOutcome seed k d then
                    Some(sprintf "%d/%d" k d)
                else
                    None

            let running', held' =
                ResourceDiff.apply (fun _ _ -> ()) start running held wanted plan

            let modelAfter =
                KeyedDiff.apply
                    (fun k d -> startOutcome seed (int k) (int d))
                    (entriesOf runningDescriptions)
                    (entriesOf held)
                    (entriesOf wanted)

            expect
                (sprintf "seed %d: running afterwards" seed)
                (modelAfter.first = entriesOf (running' |> Map.map (fun _ (d, _) -> d)))

            expect (sprintf "seed %d: held afterwards" seed) (modelAfter.second = entriesOf held')

            expect
                (sprintf "seed %d: keys afterwards are the keys wanted" seed)
                (List.sort (keysOf running' @ keysOf held') = keysOf wanted))
    case "Proofs.Oracle diff: the sequence is every stop then every start (go-red control)" (fun expect ->
        let running = entriesOf (Map.ofList [ 1, 1; 2, 1; 4, 1 ])
        let wanted = entriesOf (Map.ofList [ 2, 2; 3, 1; 4, 1 ])
        let plan = KeyedDiff.plan_of running [] wanted
        let sequence = KeyedDiff.sequence plan

        expect
            "stops first, in key order, then starts"
            (sequence = [
                KeyedDiff.Stop 1I
                KeyedDiff.Stop 2I
                KeyedDiff.Start(2I, 2I)
                KeyedDiff.Start(3I, 1I)
            ])

        expect "a start-first order would disagree" (sequence <> [ KeyedDiff.Start(2I, 2I); KeyedDiff.Stop 1I ]))
]

let private fingerprintCase =
    case "Proofs.Oracle fingerprint: the models fingerprint alike on .NET and under Fable" (fun expect ->
        let actual = OracleFingerprint.fingerprint ()

        expect
            (sprintf "the fingerprint is %A here; OracleFingerprint.expected says %A" actual OracleFingerprint.expected)
            (actual = OracleFingerprint.expected))

/// Every differential case, in one list. Each name starts with `Proofs.Oracle`, the filter the proof leg's
/// host step runs.
let cases: Case list = drainCases @ ringCases @ diffCases @ [ fingerprintCase ]
