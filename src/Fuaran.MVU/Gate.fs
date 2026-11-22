// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Diametrical Ltd

namespace Fuaran.MVU

open Fuaran.Core

/// The decision context this runtime passes to Core's gates (DECISIONS.md D27.3): who wrote the step, the grant
/// presented if a parked step is being approved, the host's clock as data, and the registry generation the gate
/// stands at. Every field is a value, so a gate that reads it stays total and free of side effects.
type DecisionContext = {
    /// The envelope's author: the principal whose registry decides the step.
    Actor: Principal
    /// The grant presented, if any. Its expiry and generation are read here, by the gate every registry carries.
    Grant: Fuaran.MVU.Grant option
    /// The host's clock as the decision reads it.
    At: int64
    /// The gate's registry generation.
    Generation: int
}

/// How a restricted principal's registry is tightened from the host's (DECISIONS.md D27.2). Each restriction is
/// one of Core's two tightening verbs — `restrict` (a subset of the kinds) or `withGate` (a gate added, its decision
/// joined) — so a restricted registry refuses everything the host's refuses, and possibly more.
type Restriction =
    /// Only these kinds: every other kind is unregistered for the principal, and refused.
    | Only of kinds: Set<string>
    /// Every invocation of a kind registered with one of these classes needs approval.
    | ApprovalFor of classes: Set<string>
    /// Every invocation of a kind registered with one of these classes is refused.
    | Refusing of classes: Set<string>
    /// A gate of the host's own, over Core's declaration (the kind as a capability) and its arguments under the
    /// decision context.
    | Gated of PolicyGate<DecisionContext, Capability, (string * string) list>

/// The gate (DECISIONS.md D27): the host's kinds, registered once in Core's capability registry — a kind's classes
/// are fixed there and read from there, never from a call — with the host's own gates; a tightening per principal,
/// from which each restricted principal's registry is derived; the domain's policy over a whole step; and the
/// registry generation. `Gate.decide` is the runtime's `Decide` through Core: the step's invocations through the
/// author's registry, joined with the policy, by Core's guarded decision; a grant presented through Core's guarded
/// approval. The runtime keeps no gate of its own.
type Gate<'effect, 'resource> = {
    /// The kinds the host registered, in registration order.
    Kinds: EffectKind list
    /// The host's registry: every kind as a capability, the grant gate and the host's own gates.
    Host: CapabilityRegistry<DecisionContext>
    /// The tightening of the host's registry for a principal; `[]` for one that acts as the host.
    Restrictions: Principal -> Restriction list
    /// The domain's policy over a whole step, for the principal acting: the author as a step is first decided, the
    /// approver when a grant is presented. Joined with the registries' decisions.
    Policy: Principal -> Proposal<'effect, 'resource> -> PolicyDecision
    /// The registry generation: raised by every rebuild, and a grant minted under another is refused.
    Generation: int
}

module Gate =

    /// The address of a kind's canonical arguments in its capability's signature.
    [<Literal>]
    let ArgsAddress = "args"

    /// The address of an invocation's destination in its capability's signature.
    [<Literal>]
    let DestinationAddress = "destination"

    let private factorOf (source: Fuaran.MVU.DeterminismSource) : DeterminismFactor =
        match source with
        | Clock -> ClockFactor
        | Random -> RandomFactor
        | Network -> NetworkFactor

    /// The capability a kind is registered as (DECISIONS.md D27.2): the kind's id; one optional hole for the
    /// canonical arguments and one required hole for the destination, both any string; the determinism the kind
    /// declares, carried on Core's axis, and the widest host effect, because a kind states its authority in its
    /// classes and Core's host axis is read by nothing here; no result; a placement Core carries and never reads.
    let capabilityOf (kind: EffectKind) : Capability =
        Capability.create
            kind.Id
            {
                Name = kind.Id
                Holes = [
                    {
                        Addr = ArgsAddress
                        Name = ArgsAddress
                        Kind = ValueHole AnyString
                        Required = false
                    }
                    {
                        Addr = DestinationAddress
                        Name = DestinationAddress
                        Kind = ValueHole AnyString
                        Required = true
                    }
                ]
                Effect = {
                    Host = WritesHost
                    Determinism = kind.Determinism |> Set.map factorOf
                }
            }
            Placement.Server
            Returns.Nothing

    /// An invocation's arguments as Core's bindings: its canonical arguments when it has any, and its destination.
    let bindings (invocation: Invocation) : (string * string) list = [
        match invocation.Args with
        | Some args -> yield ArgsAddress, args
        | None -> ()
        yield
            DestinationAddress,
            (match invocation.Destination with
             | Local -> "local"
             | Remote origin -> "remote:" + origin)
    ]

    /// The grant verdict, joined into every decision (DECISIONS.md D27.4): a grant presented is read as data — spent
    /// when the clock has passed its expiry, refused when the registry generation has moved since it was minted.
    /// Neither refusal is lifted by the grant itself: a grant lifts a needs-approval and nothing else. Read once per
    /// step rather than per invocation, so a step with no invocation is held to it too.
    let grantVerdict (ctx: DecisionContext) : PolicyDecision =
        match ctx.Grant with
        | None -> PolicyDecision.Allow
        | Some grant ->
            if grant.ExpiresAt < ctx.At then
                PolicyDecision.deny "the grant has expired"
            elif grant.Generation <> ctx.Generation then
                PolicyDecision.deny "the grant was minted under an earlier registry generation"
            else
                PolicyDecision.Allow

    /// The classes a kind was registered with; none for a kind the gate does not hold.
    let classesOf (kinds: EffectKind list) (kind: string) : Set<string> =
        kinds
        |> List.tryFind (fun k -> k.Id = kind)
        |> Option.map (fun k -> k.Classes)
        |> Option.defaultValue Set.empty

    /// A gate over a kind's REGISTERED classes: `verdict` when the capability's kind carries one of `classes`,
    /// allow otherwise. The classes are read from the registration, never from the invocation.
    let private classGate
        (kinds: EffectKind list)
        (name: string)
        (verdict: PolicyDecision)
        (classes: Set<string>)
        : PolicyGate<DecisionContext, Capability, (string * string) list> =
        {
            Policy = name
            Decide =
                fun _ capability _ ->
                    if Set.isEmpty (Set.intersect (classesOf kinds capability.Id) classes) then
                        PolicyDecision.Allow
                    else
                        verdict
        }

    let private build
        (generation: int)
        (kinds: EffectKind list)
        (hostGates: PolicyGate<DecisionContext, Capability, (string * string) list> list)
        (restrictions: Principal -> Restriction list)
        (policy: Principal -> Proposal<'effect, 'resource> -> PolicyDecision)
        : Result<Gate<'effect, 'resource>, string> =
        let registered =
            kinds
            |> List.fold
                (fun registry kind ->
                    registry
                    |> Result.bind (fun r -> CapabilityRegistry.register (capabilityOf kind) r))
                (Ok CapabilityRegistry.empty<DecisionContext>)

        match registered with
        | Error fault -> Error(InvokeError.describe fault)
        | Ok registry ->
            Ok {
                Kinds = kinds
                Host =
                    hostGates
                    |> List.fold (fun r gate -> CapabilityRegistry.withGate gate r) registry
                Restrictions = restrictions
                Policy = policy
                Generation = generation
            }

    /// A gate at generation one: the host's kinds registered (a repeated id is refused, naming it), the grant gate
    /// and the host's own gates on the host's registry, the tightening per principal and the domain's policy.
    let create
        (kinds: EffectKind list)
        (hostGates: PolicyGate<DecisionContext, Capability, (string * string) list> list)
        (restrictions: Principal -> Restriction list)
        (policy: Principal -> Proposal<'effect, 'resource> -> PolicyDecision)
        : Result<Gate<'effect, 'resource>, string> =
        build 1 kinds hostGates restrictions policy

    /// The gate rebuilt — a kind added or removed, a gate or a restriction changed, a hot reload — at the next
    /// generation (DECISIONS.md D27.5), so every grant minted under the previous one is refused.
    let rebuild
        (previous: Gate<'effect, 'resource>)
        (kinds: EffectKind list)
        (hostGates: PolicyGate<DecisionContext, Capability, (string * string) list> list)
        (restrictions: Principal -> Restriction list)
        (policy: Principal -> Proposal<'effect, 'resource> -> PolicyDecision)
        : Result<Gate<'effect, 'resource>, string> =
        build (previous.Generation + 1) kinds hostGates restrictions policy

    /// A policy that allows every step: the registries alone decide.
    let allowAll (_: Principal) (_: Proposal<'effect, 'resource>) : PolicyDecision = PolicyDecision.Allow

    /// The registry a principal acts through (DECISIONS.md D27.2): the host's, tightened by each of the principal's
    /// restrictions in order — `restrict` for `Only`, `withGate` for the rest. Core proves a gate added never lowers
    /// a decision (`with_gate_tightens_in_context`), so no restriction can give a principal what the host refuses.
    let registryOf (gate: Gate<'effect, 'resource>) (principal: Principal) : CapabilityRegistry<DecisionContext> =
        gate.Restrictions principal
        |> List.fold
            (fun registry restriction ->
                match restriction with
                | Only kinds -> CapabilityRegistry.restrict kinds registry
                | ApprovalFor classes ->
                    CapabilityRegistry.withGate
                        (classGate gate.Kinds "approval-for-class" PolicyDecision.NeedsApproval classes)
                        registry
                | Refusing classes ->
                    CapabilityRegistry.withGate
                        (classGate
                            gate.Kinds
                            "refusing-class"
                            (PolicyDecision.deny "the kind's class is refused for this principal")
                            classes)
                        registry
                | Gated g -> CapabilityRegistry.withGate g registry)
            gate.Host

    /// The kinds of `kinds` the gate's host registry does not hold: what the mount check refuses on.
    let uncovered (gate: Gate<'effect, 'resource>) (kinds: EffectKind list) : string list =
        kinds
        |> List.choose (fun kind ->
            match CapabilityRegistry.tryFind kind.Id gate.Host with
            | Some _ -> None
            | None -> Some kind.Id)

    /// Who is acting on a proposal: its author as it is first decided, the approver when a grant is presented.
    let private acting (proposal: Proposal<'effect, 'resource>) : Principal =
        match proposal.Grant with
        | Some grant -> grant.Approver
        | None -> proposal.Author

    /// A step as Core's state-aware guarded witness sees it (DECISIONS.md D27.3): the operation is the proposal,
    /// whose chain the runtime has already computed and whose plan it has already drawn, so the dry run passes and
    /// applies nothing (the runtime commits), and the effects-of are the step's invocations — each effect's and each
    /// start's kind with its bindings. The surface's decision is the domain's policy for the principal acting.
    let private witness
        (gate: Gate<'effect, 'resource>)
        : StateAwareGuardedWitness<unit, Proposal<'effect, 'resource>, string> =
        {
            Surface = {
                ReadTools = []
                OpKinds = []
                KindOfOp = fun _ -> "step"
                Patterns = []
                Decide = fun _ proposal -> gate.Policy (acting proposal) proposal
                Apply = fun _ () -> Ok()
                Explain = fun reason -> { Message = reason; Alternatives = [] }
            }
            DryRun = fun _ () -> Ok()
            EffectsOf =
                fun proposal () ->
                    Proposal.invocations proposal
                    |> List.map (fun invocation -> invocation.Kind, bindings invocation)
        }

    let private contextOf (gate: Gate<'effect, 'resource>) (proposal: Proposal<'effect, 'resource>) : DecisionContext = {
        Actor = proposal.Author
        Grant = proposal.Grant
        At = proposal.At
        Generation = gate.Generation
    }

    /// Core's decision for a step through `principal`'s registry (DECISIONS.md D27.3). Without a grant it is
    /// `Proposals.decideGuardedIn`: the policy for the author joined with the registry's decision, under the
    /// context, for every invocation of the step — an unregistered kind is a denial, a class gate answers as
    /// registered. With a grant it is `Proposals.approveGuardedIn` over the parked step: Core refuses an approver
    /// who is the author, reads the policy for the approver, presents the grant for exactly each invocation the
    /// step makes — so the registry's needs-approval is lifted for those and nothing else, and a denial is lifted by
    /// none — and the grant gate reads the expiry and the generation from the context.
    let decideFor
        (gate: Gate<'effect, 'resource>)
        (principal: Principal)
        (proposal: Proposal<'effect, 'resource>)
        : PolicyDecision =
        let registry = registryOf gate principal
        let ctx = contextOf gate proposal
        let at = string proposal.At
        let author = Principal.actorId proposal.Author

        // The grant verdict is read once per step, whatever the step invokes, and joined with Core's decision.
        PolicyDecision.join (grantVerdict ctx)
        <| match proposal.Grant with
           | None -> Proposals.decideGuardedIn (witness gate) registry ctx author proposal ()
           | Some grant ->
               let staged: Proposals.StagedOp<Proposal<'effect, 'resource>> = {
                   Op = proposal
                   Invocations = (witness gate).EffectsOf proposal ()
               }

               match Proposals.proposeWithId author at grant.Parked None [ staged ] Proposals.Queue.empty with
               | Error _ -> PolicyDecision.deny "the parked step could not be staged for approval"
               | Ok queue ->
                   match
                       Proposals.approveGuardedIn
                           (witness gate)
                           registry
                           ctx
                           (Principal.actorId grant.Approver)
                           at
                           grant.Parked
                           queue
                           ()
                   with
                   | Ok _ -> PolicyDecision.Allow
                   | Error(Proposals.StagedApprovalFailure.Refused(Proposals.SelfApproval _)) ->
                       PolicyDecision.deny "the author of a step cannot approve it"
                   | Error(Proposals.StagedApprovalFailure.Refused(Proposals.ApprovalDenied(_, guidance))) ->
                       PolicyDecision.Deny guidance
                   | Error(Proposals.StagedApprovalFailure.Refused(Proposals.OpNoLongerApplies(_, reason))) ->
                       PolicyDecision.deny reason
                   | Error(Proposals.StagedApprovalFailure.Refused(Proposals.UnknownProposal _))
                   | Error(Proposals.StagedApprovalFailure.Refused(Proposals.NotPending _)) ->
                       PolicyDecision.deny "the parked step is not pending"
                   | Error(Proposals.StagedApprovalFailure.StaleEffects _) ->
                       PolicyDecision.deny "the step's invocations moved since it was parked"

    let private toDecision (decision: PolicyDecision) : Decision =
        match decision with
        | PolicyDecision.Allow -> Allow
        | PolicyDecision.NeedsApproval -> NeedsApproval
        | PolicyDecision.Deny guidance -> Deny guidance.Message

    /// The decision for a step under several ceilings (DECISIONS.md D27.6): the author's registry decides, joined
    /// with each further principal's — the stricter of them all, which is how an effect a hosted program lowers to
    /// is decided under the program's author as well as the message's. With no further principal it is `decide`.
    let decideUnder (gate: Gate<'effect, 'resource>) (ceilings: Principal list) : Decide<'effect, 'resource> =
        fun proposal ->
            proposal.Author :: ceilings
            |> List.map (fun principal -> decideFor gate principal proposal)
            |> PolicyDecision.all
            |> toDecision

    /// The runtime's `Decide` through Core: `decideFor` through the author's registry.
    let decide (gate: Gate<'effect, 'resource>) : Decide<'effect, 'resource> = decideUnder gate []

    /// The mount check (DECISIONS.md D27.7): the host covers every kind the component can ask for — every kind
    /// the interpreter registers is in the gate's host registry — or the mount is refused, naming the kinds it does
    /// not cover. The decision function is handed over only through this check.
    let mount
        (gate: Gate<'effect, 'resource>)
        (interpreter: Interpreter<'msg, 'effect, 'resource>)
        : Result<Decide<'effect, 'resource>, string list> =
        match uncovered gate interpreter.Kinds with
        | [] -> Ok(decide gate)
        | missing -> Error missing

    /// The grant a host mints when `approver` acts on its controls for the parked step `parked`: bound to the
    /// step's request as previewed, spent after `expiresAt`, and to this gate's generation.
    let grant
        (gate: Gate<'effect, 'resource>)
        (parked: ParkedStep<'model, 'msg, 'effect, 'resource>)
        (approver: Principal)
        (expiresAt: int64)
        : Fuaran.MVU.Grant =
        Grant.mint parked.Id parked.Proposal approver expiresAt gate.Generation
