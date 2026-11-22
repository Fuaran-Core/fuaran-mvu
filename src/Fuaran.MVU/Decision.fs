// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Diametrical Ltd

namespace Fuaran.MVU

open Fuaran.Core

/// What a decision answers about a whole step.
type Decision =
    | Allow
    | NeedsApproval
    | Deny of reason: string

/// An approval (DECISIONS.md D27.4): a value the host mints when a person acts on the host's own controls, bound
/// to one parked step as it was previewed. It names the parked step, the identity of the request the approver was
/// shown (`Proposal.request`: the injective canonical rendering of the step's invocations and resource plan — never
/// a hash), the approving user, the host's clock reading after which it is spent unused, and the registry
/// generation it was minted under. Single use: the runtime resolves the parked step the moment the grant is
/// presented, whatever the outcome. No message carries one: only `Runtime.Approve` presents it, and the
/// `Proposal` the decision reads says so in `Grant`.
type Grant = {
    /// The parked step the approval is for.
    Parked: int
    /// The identity of the step as the approver saw it. Compared by equality, never parsed.
    Request: string
    /// Who approved: the user who acted on the host's controls.
    Approver: Principal
    /// The host's clock reading after which the grant is spent unused.
    ExpiresAt: int64
    /// The registry generation the preview was decided under; a generation moved since refuses the grant.
    Generation: int
}

/// A step as the decision sees it: who wrote the message, its sequence number, the host's clock as data, every
/// effect the chain emitted with its invocation in emission order, the resource plan — every start with its
/// invocation and every stop — and the grant presented, if a parked step is being approved. Nothing in it has
/// been performed or installed.
type Proposal<'effect, 'resource> = {
    Author: Principal
    Sequence: int64
    /// The host's clock as the decision reads it: the envelope's time of acceptance for a step as it is first
    /// decided, the instant of the approval when a grant is presented. Data, so the decision stays a function.
    At: int64
    Effects: (Invocation * 'effect) list
    Starts: (ResourceKey * Invocation * 'resource) list
    Stops: ResourceKey list
    /// The grant presented with a parked step: `None` for a step as it is first decided. Only the runtime's
    /// `Approve` sets it; a message cannot.
    Grant: Grant option
}

module Proposal =

    /// The invocations of a step, as the gate sees them: every effect's, in emission order, then every start's,
    /// in key order.
    let invocations (proposal: Proposal<'effect, 'resource>) : Invocation list =
        (proposal.Effects |> List.map fst)
        @ (proposal.Starts |> List.map (fun (_, invocation, _) -> invocation))

    let private destination (destination: Destination) : string list =
        match destination with
        | Local -> [ "local"; "" ]
        | Remote origin -> [ "remote"; origin ]

    /// The fields of one invocation: the kind, whether it has arguments and their text, and the destination.
    let private invocationFields (invocation: Invocation) : string list =
        [
            invocation.Kind
            match invocation.Args with
            | Some args -> "1"
            | None -> "0"
            Option.defaultValue "" invocation.Args
        ]
        @ destination invocation.Destination

    /// The identity of the request a step makes (DECISIONS.md D27.4): the canonical, INJECTIVE rendering of its
    /// effects' invocations, its starts (key and invocation) and its stops, through Core's field encoding
    /// (`Hash.canonicalFields`, which Core proves injective and binds its own grant to, D249). No hash is taken: two
    /// steps have one request exactly when they ask the same kinds the same arguments at the same destinations
    /// and plan the same starts and stops. The author, the sequence and the effect values are not part of it: the
    /// identity is what the gate decides on and what the approver is shown.
    let request (proposal: Proposal<'effect, 'resource>) : string =
        let key (ResourceKey path) = Hash.canonicalFields path

        Hash.canonicalFields [
            for invocation, _ in proposal.Effects do
                yield Hash.canonicalFields ("e" :: invocationFields invocation)
            for k, invocation, _ in proposal.Starts do
                yield Hash.canonicalFields ("s" :: key k :: invocationFields invocation)
            for k in proposal.Stops do
                yield Hash.canonicalFields [ "x"; key k ]
        ]

module Grant =

    /// The grant for the parked step `parked` whose proposal is `proposal`: minted by the host when `approver`
    /// acts on the host's controls, spent unused after the host's clock reads past `expiresAt`, and bound to the
    /// registry generation `generation` (`Gate.grant` supplies the gate's own).
    let mint
        (parked: int)
        (proposal: Proposal<'effect, 'resource>)
        (approver: Principal)
        (expiresAt: int64)
        (generation: int)
        : Grant =
        {
            Parked = parked
            Request = Proposal.request proposal
            Approver = approver
            ExpiresAt = expiresAt
            Generation = generation
        }

/// The decision seam: the host supplies the function that decides a proposal as a unit. `Decide.allowAll` is the
/// default, for a host without a registry; `Gate.decide` is the decision through Core's registry (Gate.fs). A
/// grant presented with the proposal is part of what the function reads.
type Decide<'effect, 'resource> = Proposal<'effect, 'resource> -> Decision

module Decide =

    /// Every step is allowed, approved or not.
    let allowAll: Decide<'effect, 'resource> = fun _ -> Allow
