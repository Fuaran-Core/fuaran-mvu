// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Diametrical Ltd

namespace Fuaran.MVU

/// Who wrote a message's content. Authority follows authorship: a reply from a model call is the agent's,
/// a reply from the application's own contract is the host's, content from outside the trust boundary is
/// foreign, and a click on a control the host drew is the user's. A follow-up keeps its author.
type Principal =
    /// A person, through controls the host owns.
    | User of id: string
    /// The application itself and its first-party services.
    | Host
    /// A model: what it asks for and what it writes.
    | Agent of model: string * version: string * id: string
    /// Content from outside the trust boundary.
    | Foreign of source: string

module Principal =

    /// The principal as one actor id, injective with a prefix per case, so a foreign source can never share
    /// an id with a user or the host. A host that records steps in an op-stream keyed by actor uses this.
    let actorId (principal: Principal) : string =
        match principal with
        | User id -> "user:" + id
        | Host -> "host"
        | Agent(model, version, id) -> "agent:" + model + "/" + version + "/" + id
        | Foreign source -> "foreign:" + source

    /// The principal as Core's `Actor`, for a host that records steps in Core's op-stream (DECISIONS.md D27.8).
    /// Core's `Actor` gains no case: a user is `Human`; the host, an agent and a foreign source are each `Agent`,
    /// which Core documents as an automated emitter — the host under the application's `name` and `version`, an
    /// agent under its own model and version, a foreign source under its name with no version. The id is
    /// prefixed per case (`user:`, `host`, `agent:`, `foreign:`), so the mapping is injective: no foreign source
    /// can share an actor with a user, the host or an agent.
    let toActor (name: string) (version: string) (principal: Principal) : Fuaran.Core.Actor =
        match principal with
        | User id -> Fuaran.Core.Actor.Human("user:" + id)
        | Host -> Fuaran.Core.Actor.Agent(name, version, "host")
        | Agent(model, agentVersion, id) -> Fuaran.Core.Actor.Agent(model, agentVersion, "agent:" + id)
        | Foreign source -> Fuaran.Core.Actor.Agent(source, "", "foreign:" + source)

/// The key a resource is listed under: a path, so a parent prefixes a child's keys with the child's key
/// and two children cannot collide.
type ResourceKey = ResourceKey of string list

module ResourceKey =

    /// A key with one segment.
    let constant (name: string) : ResourceKey = ResourceKey [ name ]

    /// A key with a name and an id: `named "search" "q7"`.
    let named (name: string) (id: string) : ResourceKey = ResourceKey [ name; id ]

    /// A child's key under its parent's prefix.
    let prefix (segment: string) (ResourceKey path) : ResourceKey = ResourceKey(segment :: path)

    /// The key's segments.
    let segments (ResourceKey path) : string list = path

/// Where an envelope came from.
type Source =
    /// Dispatched from outside: a control, a host event, a test.
    | External
    /// The one answer of a performed effect: its kind, and the effect's position in the emission order of the
    /// step that emitted it (the envelope's `Cause`), so a record can say which effect of a step was answered.
    | Answer of kind: string * effect: int
    /// An event from a resource instance: its key and the instance's liveness token.
    | FromResource of key: ResourceKey * instance: int
    /// A message the runtime authored for the component: a refusal, a loss.
    | FromRuntime
    /// The boot envelope that carries `Init`'s steps.
    | Boot

/// A message as the runtime accepted it: its sequence number, the message, who wrote it, where it came
/// from, the sequence number of the step that caused it, and its stamp. Every guarantee the runtime makes
/// is about accepted envelopes.
type Envelope<'msg> = {
    Sequence: int64
    Msg: 'msg
    Author: Principal
    Source: Source
    Cause: int64 option
    Stamp: Stamp
}
