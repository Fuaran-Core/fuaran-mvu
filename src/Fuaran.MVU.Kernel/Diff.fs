// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Eugene Tolmachev and Fable.Elmish contributors
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)
// Copyright 2026 Diametrical Ltd

namespace Fuaran.MVU.Kernel

/// The keyed diff: the resource plan for one step. The runtime holds the instances that are running, each
/// under the key and the description it was started with, and the keys whose start threw, each with the
/// description that threw (held). A step ends with the descriptions the model wants. The plan is, for each
/// key in key order: STOP a running instance whose key is no longer wanted or whose description changed;
/// KEEP a running instance whose description is unchanged; START a wanted key that is not running, or
/// whose running description differs, unless it is held with that same description. A key that is both
/// stopped and started is a RESTART. The plan is applied as every stop, then every start, each in key
/// order, so the result is the same on every host. A start that throws is held, and is not tried again
/// until its description changes or the key leaves and returns.
///
/// Descended from the subscription diff of Fable.Elmish and the ToolUp Analytics open-source SDK
/// (toolup-forge, Apache-2.0): the shape "stop what vanished, keep what stayed, start what appeared" is
/// theirs. New here: a description per key, so a changed description is a restart; the held set; the walk
/// over two sorted maps rather than a set comparison; and the fixed order of application.
///
/// Its model is `proofs/KeyedDiff.fst`: `start_spec`, `stop_spec` and `keep_spec` say what each list holds,
/// exactly; `keys_after_are_wanted` says the keys running or held afterwards are exactly the keys wanted;
/// `held_unchanged_not_started` says a held key with an unchanged description is not started;
/// `plan_twice_empty` says the plan computed again after applying it is empty; `sequence_shape` says every
/// stop precedes every start, each in key order; `identity_skip_agrees` says skipping the diff when the
/// wanted descriptions are those of the last diff agrees with the general path. The differential test runs
/// the extracted model beside this module on .NET and under Fable.
module ResourceDiff =

    /// The plan: the keys to stop, the keys to keep and the descriptions to start, each in key order. A key
    /// in both `Stops` and `Starts` is a restart.
    type Plan<'k, 'd> = {
        Stops: 'k list
        Keeps: 'k list
        Starts: ('k * 'd) list
    }

    /// Whether the plan changes nothing: nothing to stop and nothing to start.
    let isEmpty (plan: Plan<'k, 'd>) =
        plan.Stops.IsEmpty && plan.Starts.IsEmpty

    /// The plan from what is running (key to its description and handle), what is held (key to the
    /// description whose start threw) and what the model wants (key to description). One walk over the two
    /// maps, which enumerate in key order.
    let plan
        (running: Map<'k, 'd * 'h>)
        (held: Map<'k, 'd>)
        (wanted: Map<'k, 'd>)
        : Plan<'k, 'd> when 'k: comparison and 'd: equality =
        let startUnlessHeld key description starts =
            match Map.tryFind key held with
            | Some heldDescription when heldDescription = description -> starts
            | _ -> (key, description) :: starts

        let rec walk (r: ('k * ('d * 'h)) list) (w: ('k * 'd) list) stops keeps starts =
            match r, w with
            | [], [] -> {
                Stops = List.rev stops
                Keeps = List.rev keeps
                Starts = List.rev starts
              }
            | (key, _) :: r', [] -> walk r' [] (key :: stops) keeps starts
            | [], (key, description) :: w' -> walk [] w' stops keeps (startUnlessHeld key description starts)
            | (rk, (rd, _)) :: r', (wk, wd) :: w' ->
                let c = compare rk wk

                if c < 0 then
                    walk r' w (rk :: stops) keeps starts
                elif c > 0 then
                    walk r w' stops keeps (startUnlessHeld wk wd starts)
                elif rd = wd then
                    walk r' w' stops (rk :: keeps) starts
                else
                    walk r' w' (rk :: stops) keeps (startUnlessHeld wk wd starts)

        walk (Map.toList running) (Map.toList wanted) [] [] []

    /// Apply a plan: every stop, then every start, each in key order. `stop` disposes an instance; `start`
    /// returns the handle of a started instance, or `None` when the start threw (reported by the host),
    /// which holds the key under that description. The result is what is running and what is held
    /// afterwards. A hold on a key that is no longer wanted, or wanted under another description, is
    /// dropped.
    let apply
        (stop: 'k -> 'h -> unit)
        (start: 'k -> 'd -> 'h option)
        (running: Map<'k, 'd * 'h>)
        (held: Map<'k, 'd>)
        (wanted: Map<'k, 'd>)
        (plan: Plan<'k, 'd>)
        : Map<'k, 'd * 'h> * Map<'k, 'd> when 'k: comparison and 'd: equality =
        let afterStops =
            plan.Stops
            |> List.fold
                (fun (r: Map<'k, 'd * 'h>) key ->
                    match Map.tryFind key r with
                    | Some(_, handle) -> stop key handle
                    | None -> ()

                    Map.remove key r)
                running

        let heldKept =
            held
            |> Map.filter (fun key description -> Map.tryFind key wanted = Some description)

        plan.Starts
        |> List.fold
            (fun (r: Map<'k, 'd * 'h>, h: Map<'k, 'd>) (key, description) ->
                match start key description with
                | Some handle -> Map.add key (description, handle) r, h
                | None -> r, Map.add key description h)
            (afterStops, heldKept)

    /// Whether two models are the same instance: the one condition under which a host may skip the diff.
    /// `plan_twice_empty` in the model is what makes the skip sound: the plan against the same wanted
    /// descriptions, computed again, is empty.
    let sameInstance (last: 'model) (current: 'model) = obj.ReferenceEquals(last, current)
