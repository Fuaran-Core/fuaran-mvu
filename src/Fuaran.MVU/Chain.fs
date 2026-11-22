// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Diametrical Ltd

namespace Fuaran.MVU

/// What a chain computed: the final model and the effects, in emission order, with the number of messages
/// the chain ran through `update`.
type Chain<'model, 'effect> = {
    Model: 'model
    Effects: 'effect list
    Messages: int
}

/// How a chain ended.
type ChainOutcome<'model, 'effect> =
    | Computed of Chain<'model, 'effect>
    /// More messages than the fuel allows: a follow-up loop.
    | TooLong of limit: int
    /// `update` threw on some message of the chain.
    | Threw of exn

/// The chain of an envelope (rules 4 to 6): its message and every follow-up, first-in first-out — run
/// `update`, append the `Then` messages in order, record the `Do` effects — until no follow-up is left, or
/// the fuel is spent. Every message reads the envelope's time and sequence; a follow-up reads a seed of its
/// own, derived from the emitting message's seed and the follow-up's position in that message's steps
/// (DECISIONS.md D24). Computing a chain performs nothing and installs nothing: it is a pure function of
/// the component's `update`, the model and the envelope, which is what lets a preview, an approval and a
/// replay compute exactly the step the runtime commits.
module Chain =

    /// The follow-ups a step list emits, each with the seed it will read.
    let private followUps (seed: uint64) (steps: Step<'msg, 'effect> list) : ('msg * uint64) list =
        steps
        |> List.indexed
        |> List.choose (fun (position, step) ->
            match step with
            | Then msg -> Some(msg, Seed.forFollowUp seed position)
            | Do _ -> None)

    /// Continue a chain from a model and the steps already emitted (by `Init` at boot, or by the first
    /// `update`): process the follow-ups first-in first-out, counting every message against `fuel`,
    /// `already` of which have been run.
    let continueFrom
        (update: Stamp -> 'msg -> 'model -> 'model * Step<'msg, 'effect> list)
        (stamp: Stamp)
        (fuel: int)
        (already: int)
        (model: 'model)
        (steps: Step<'msg, 'effect> list)
        : ChainOutcome<'model, 'effect> =
        // The pending follow-ups, oldest first, and the effects so far, newest first.
        let rec go pending (effects: 'effect list) (count: int) (model: 'model) =
            match pending with
            | [] ->
                Computed {
                    Model = model
                    Effects = List.rev effects
                    Messages = count
                }
            | (msg, seed) :: rest ->
                if count >= fuel then
                    TooLong fuel
                else
                    match
                        (try
                            Choice1Of2(update { stamp with Seed = seed } msg model)
                         with ex ->
                             Choice2Of2 ex)
                    with
                    | Choice2Of2 ex -> Threw ex
                    | Choice1Of2(model', steps') ->
                        let effects' = List.fold (fun acc e -> e :: acc) effects (Step.effects steps')
                        go (rest @ followUps seed steps') effects' (count + 1) model'

        if already > fuel then
            TooLong fuel
        else
            go (followUps stamp.Seed steps) (List.rev (Step.effects steps)) already model

    /// The chain of a message: `update` on it, then its follow-ups.
    let compute
        (update: Stamp -> 'msg -> 'model -> 'model * Step<'msg, 'effect> list)
        (stamp: Stamp)
        (fuel: int)
        (model: 'model)
        (msg: 'msg)
        : ChainOutcome<'model, 'effect> =
        if fuel < 1 then
            TooLong fuel
        else
            match
                (try
                    Choice1Of2(update stamp msg model)
                 with ex ->
                     Choice2Of2 ex)
            with
            | Choice2Of2 ex -> Threw ex
            | Choice1Of2(model', steps) -> continueFrom update stamp fuel 1 model' steps
