// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Eugene Tolmachev and Fable.Elmish contributors
// Copyright 2026 Diametrical Ltd

namespace Fuaran.MVU

/// The central type (DECISIONS.md D6): a model, an update, the resources the model wants and a view, all
/// pure. `Init` takes the host's flags and no stamp; `Update` takes the stamp the runtime fixed when it
/// accepted the message. The shape follows the Elm Architecture as realised by Fable.Elmish; new here are
/// the stamp, the step type with its follow-ups, and resources as a keyed map the runtime diffs.
type Component<'flags, 'model, 'msg, 'effect, 'resource, 'view> = {
    Init: 'flags -> 'model * Step<'msg, 'effect> list
    Update: Stamp -> 'msg -> 'model -> 'model * Step<'msg, 'effect> list
    Resources: 'model -> Map<ResourceKey, 'resource>
    View: 'model -> 'view
}

/// A child as its parent hosts it: the child's `Init`, `Update` and `Resources` with their steps lifted
/// into the parent's message, effect and resource types, their keys prefixed and the stamp's seed split
/// by the child's key. The parent calls these from its own members, holding the child's model where it
/// chooses.
type Lifted<'flags, 'model, 'cmsg, 'msg, 'effect, 'resource> = {
    Init: 'flags -> 'model * Step<'msg, 'effect> list
    Update: Stamp -> 'cmsg -> 'model -> 'model * Step<'msg, 'effect> list
    Resources: 'model -> Map<ResourceKey, 'resource>
}

module Component =

    /// A component whose `update` reads no stamp.
    let plain
        (init: 'flags -> 'model * Step<'msg, 'effect> list)
        (update: 'msg -> 'model -> 'model * Step<'msg, 'effect> list)
        (resources: 'model -> Map<ResourceKey, 'resource>)
        (view: 'model -> 'view)
        : Component<'flags, 'model, 'msg, 'effect, 'resource, 'view> =
        {
            Init = init
            Update = fun _ msg model -> update msg model
            Resources = resources
            View = view
        }

    /// The child's resources under the parent's prefix, each wrapped as a parent resource.
    let prefixResources
        (key: string)
        (liftResource: 'cresource -> 'resource)
        (resources: Map<ResourceKey, 'cresource>)
        : Map<ResourceKey, 'resource> =
        resources
        |> Map.toSeq
        |> Seq.map (fun (k, r) -> ResourceKey.prefix key k, liftResource r)
        |> Map.ofSeq

    /// Lift a child with a seed split of your own and a step mapping that may turn a child effect into any
    /// parent step: `bind` an up-call into a parent `Then`, and every other effect into a parent `Do`.
    let liftWith
        (split: Stamp -> string -> Stamp)
        (key: string)
        (liftMsg: 'cmsg -> 'msg)
        (liftEffect: 'ceffect -> Step<'msg, 'effect>)
        (liftResource: 'cresource -> 'resource)
        (child: Component<'cflags, 'cmodel, 'cmsg, 'ceffect, 'cresource, 'cview>)
        : Lifted<'cflags, 'cmodel, 'cmsg, 'msg, 'effect, 'resource> =
        let liftSteps steps =
            List.map (Step.bind liftMsg liftEffect) steps

        {
            Init =
                fun flags ->
                    let model, steps = child.Init flags
                    model, liftSteps steps
            Update =
                fun stamp msg model ->
                    let model', steps = child.Update (split stamp key) msg model
                    model', liftSteps steps
            Resources = fun model -> prefixResources key liftResource (child.Resources model)
        }

    /// Lift a child under `key`: messages, effects and resources each wrapped by the parent's constructor,
    /// resource keys prefixed, and the child's seed split from the parent's by the key, so a parent and a
    /// child that mint ids in one step cannot mint the same one (DECISIONS.md D21, condition 2).
    let lift
        (key: string)
        (liftMsg: 'cmsg -> 'msg)
        (liftEffect: 'ceffect -> 'effect)
        (liftResource: 'cresource -> 'resource)
        (child: Component<'cflags, 'cmodel, 'cmsg, 'ceffect, 'cresource, 'cview>)
        : Lifted<'cflags, 'cmodel, 'cmsg, 'msg, 'effect, 'resource> =
        liftWith (fun stamp k -> Stamp.forChild k stamp) key liftMsg (fun e -> Do(liftEffect e)) liftResource child

/// The identity of a legacy effect: its site label. A non-generic base, so equality is one type test with
/// no type arguments, which both hosts honour (Fable ignores type arguments in a type test).
[<AllowNullLiteral>]
type LegacySite(site: string) =

    /// The label that identifies the site.
    member _.Site = site

    override _.Equals(other: obj) =
        match other with
        | :? LegacySite as that -> site = that.Site
        | _ -> false

    override _.GetHashCode() = hash site

/// An effect that wraps code the application has not yet ported: an opaque function that will dispatch
/// through the callback it is given. A function has no equality, which would strip equality from the
/// whole effect union; this wrapper compares by its site label alone, so a test can still build an
/// expected value. Two sites with the same label and different bodies compare equal — the label is the
/// identity, and the application keeps labels distinct.
[<Sealed>]
type LegacyEffect<'msg>(site: string, run: ('msg -> unit) -> unit) =
    inherit LegacySite(site)

    /// The opaque code: it dispatches through the callback it is handed.
    member _.Run: ('msg -> unit) -> unit = run
