// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Diametrical Ltd

namespace Fuaran.MVU.Testing

open Fuaran.MVU

/// The laws a component's tests run. Each answers the failures it found, as sentences, so a test framework
/// on either host reports them without this library taking a dependency on one.
module Law =

    /// The determinism law: `Update` run twice on the same stamp, message and model gives the same model and
    /// the same steps. Replay and preview both depend on it, and a purity analyzer cannot establish it alone.
    let deterministic
        (comp: Component<'flags, 'model, 'msg, 'effect, 'resource, 'view>)
        (inputs: (Stamp * 'msg * 'model) list)
        : string list when 'model: equality and 'msg: equality and 'effect: equality =
        inputs
        |> List.indexed
        |> List.choose (fun (i, (stamp, msg, model)) ->
            let first = comp.Update stamp msg model
            let second = comp.Update stamp msg model

            if first = second then
                None
            else
                Some(sprintf "input %d: Update gave a different model or different steps the second time" i))

    /// The replay law: the committed steps of a log, replayed over the component from `Init flags` with
    /// nothing performed, reproduce the model given.
    let replayReproduces
        (comp: Component<'flags, 'model, 'msg, 'effect, 'resource, 'view>)
        (flags: 'flags)
        (log: StepRecord<'msg> list)
        (model: 'model)
        : string list when 'model: equality =
        if Replay.run comp flags log = model then
            []
        else
            [ "the replay of the log does not reproduce the model" ]
