# Decisions

The decisions this repository was cut under. Each is dated, and each says what was decided and why.
A later decision that changes an earlier one says so; an entry is never rewritten.

## D1 — The repository: private at the cut, public-destined, Apache-2.0 (2026-11-11)

The library is Apache-2.0 and is meant to be public. The repository is **private** at its cut, for
one reason: the prototype under `spike/` includes ports of existing application code, and at least
one of those applications is private. A public repository cannot carry that (D4).

Private to public is a deliberate later act. The reverse cannot be undone, which is why the
conservative choice was made first.

## D2 — The name is Fuaran.MVU, and it promises no compatibility (2026-11-11)

The library is named for what it is: a model-update-view runtime. It does not use the name of any
other MVU library, and it promises no API compatibility with one. Compatibility was considered and
set aside so that the design could be shaped around the proved kernel and around effects as values,
not around an inherited surface.

`MVU` is capitalised in every package id.

## D3 — The repository keeps a notice crediting the architecture's origin (2026-11-11)

The architecture follows the Elm Architecture as realised for F# by Fable.Elmish. `NOTICE` credits
it, and any file that descends from that code carries its copyright line beside this project's.

This was chosen over judging, file by file, what still derives from earlier code. A notice is
accurate about where the architecture came from, costs nothing, and does not weaken the position
that the design here is its own.

## D4 — The prototype is evidence, and it is not publishable as it stands (2026-11-11)

`spike/` exists to show that the proposed types hold: it type-checks them and runs ports of real
code against them. It is not the implementation and no package is built from it.

Two consequences for publication:

1. **The ports are replaced before this repository is made public.** Each port that derives from
   application code is replaced with a neutral example that exercises the same shapes.
2. **The public repository is a fresh cut.** Replacing files does not remove them from history. When
   the library is published, it is published from a new history that never contained the ports, and
   this repository's history is archived privately.

Until then, files at the top level of this repository are written to public standard: they name no
private application, product or repository.

## D5 — No implementation phase is started from the prototype alone (2026-11-11)

The prototype is single-threaded, has not been compiled to JavaScript, and its gate is a plain
function. It shows the types hold. It does not show the runtime is right. Four questions are
answered before implementation begins:

1. Does the implicit conversion from an effect to a step compile to JavaScript, is equality on a
   boxed union structural there, and how fast is the proposed queue against the present one?
2. What share of the first host's remote API uses only types the interface-definition language can
   express?
3. How many purity violations does an analyzer find in existing `update` functions?
4. How much of the renderer already reaches the runtime seam?

## D6 — The central type is `Component`, not `Program` (2026-11-11)

The record that holds `Init`, `Update`, `Resources` and `View` is named `Component`. An application
is a root component, and a parent is built from children of the same type.

The design first called it `Program`, the name the Elm Architecture's libraries use. Two things
decided against it:

1. **The name is taken where this library will be used first.** `Fuaran.Program` is the
   programs-as-data domain, and its UI adapter declares a type and a module named `Program`. A host
   that runs a bounded program inside its loop would write `Program.handleEvent` and `Program.run`
   in one file, meaning unrelated things.
2. **Children are first-class here.** In the libraries this design descends from, only the root is
   a `Program` and a child has no type. Here parent and child share one type, and "a component made
   of components" says so where "a program made of programs" does not.

The prototype under `spike/` predates this decision and still says `Program`. It is not renamed: it
is evidence for the design as first drawn (D4), and the implementation is written fresh.

## D7 — This library never evaluates behaviour carried as data (2026-11-11)

A component's `update` is compiled code that a developer wrote and a host trusts. This library runs
it. It does not, and will not, interpret behaviour that arrives as data: handlers in a tree, logic
in a document, or a plan a model wrote. That is `Fuaran.Program`'s job. A program there is bounded
and total, and it can be signed, priced and refused before it runs, which compiled code cannot.

The two meet in one place. `Fuaran.Program` has a step and no queue, and this library is a queue
with a proof behind it. So a program placement is hosted on the loop as a component: its step is
the component's `update`, its effects are registered kinds, and an answer returns as a message.
That component is a bridge that lives beside `Fuaran.Program`, on the pattern of the bridges for
remote calls and model calls. Neither library depends on the other.

Why it is written down: a runtime with an `update` function is one short step from growing a small
interpreter "for convenience", and two evaluators of the same behaviour diverge.

## D8 — What this library shares with Fuaran.Program belongs to Fuaran.Core (2026-11-11)

Both runtimes gate effects on their arguments and destinations, check before running that a host
covers everything that can be asked of it, bound their work by a budget, and can keep a journal.
`Fuaran.Program` already has each of these. They are not built a second time here. Each moves into
`Fuaran.Core` once, taken from `Fuaran.Program`, and both runtimes use it from there.

One case is not settled: the standard vocabulary of client effects (clipboard, navigation, focus,
download and the like). Both runtimes need it, and whether it is declared in `Fuaran.Core` is that
library's decision.

## D9 — Fuaran.Core stays generic; the standard client kinds are not declared there (2026-11-11)

This settles the case D8 left open, and it bounds D8.

`Fuaran.Core` holds mechanisms: a registry, a policy over arguments and destinations, a coverage
check, the shape of a budget. It never holds a domain's vocabulary. Clipboard, navigation, focus
and download are a vocabulary, so they are not declared in `Fuaran.Core`, and nothing D8 moves
there is one either. The kinds a registry holds, the classes a host names and the dimensions a
budget counts in are supplied by whoever owns them.

For this library that means `Fuaran.MVU.Browser` declares the standard client kinds and supplies
their performers, as its own. `Fuaran.Program` keeps the client effect vocabulary of its own
specification. How a hosted program's client effects reach a host's registered kinds is the
bridge's design and is recorded when it is decided.

## D10 — A correction to D9: the client effect vocabulary is not Fuaran.Program's (2026-11-11)

D9's last paragraph says `Fuaran.Program` keeps a client effect vocabulary of its own
specification. That is wrong. `Fuaran.Program`'s packages are generic in the effect type and hold
no client vocabulary. The type is declared once, in the UI language's server-driven package, and
`Fuaran.Program`'s client placement is instantiated with it by the UI language's adapters.

D9's decision is unaffected: `Fuaran.Core` stays generic, and the standard client kinds are not
declared there.

What is not decided is where they are declared next: in this library's browser package, or in a
small library of their own that this one and the UI language both reference. The second is
proposed. It keeps one declaration, and a server that only emits effects would not have to take
browser performers and a runtime in order to name them.

## D11 — The standard client kinds are declared in a library of their own (2026-11-11)

This decides what D10 left open. The client effect vocabulary, with its discriminator and its wire
codec, moves into a small library of its own. That library depends on neither this runtime nor the
UI language.

This library's browser package references it, supplies the performers and registers each kind with
its classes. The UI language references it for what it sends to a client and for its own adapters.
`Fuaran.Program`'s packages reference nothing new: they stay generic in the effect type.

The vocabulary is declared once, as it was. Adding a kind to it permits nothing: a host registers
the performers it chooses to, and its gate still decides.

Where the library is published from, and its name, are not decided.

## D12 — D8 is withdrawn: nothing moves into Fuaran.Core for this library (2026-11-11)

D8 said that what this library shares with `Fuaran.Program` moves into `Fuaran.Core`: a policy over
arguments and destinations, a coverage check, a budget and a journal. D9 listed the first three as
mechanisms. Both were written from the names of those things. Reading `Fuaran.Program`'s code shows
that each is a vocabulary, so D8 is withdrawn and D9's list is corrected here. D9's decision, that
`Fuaran.Core` stays generic, is what withdraws it.

| What D8 moved | What it is in `Fuaran.Program` |
|---|---|
| The policy over arguments | A clause language: bounds on a named integer argument, windowed limits, dwell |
| The policy over destinations | The network's vocabulary: hosts, host suffixes, schemes, local and remote |
| The coverage check | A document of what a program demands of its host, in that domain's terms |
| The budget | Counts of actions and of rendered nodes |

What the two libraries share is what `Fuaran.Core` already has, and it is enough:

- **The gate.** It is a function of a declaration and the arguments, and both are type parameters.
  A policy over arguments or destinations is a function a host installs. This library ships none.
- **The registry.** It can already say whether an id is registered, which is all this library's
  coverage check needs. This library makes that check itself.
- **The journal.** `Fuaran.Core`'s op-stream exists, and this library's optional session log is one.

Fuel is this library's own count of messages. It is not `Fuaran.Program`'s budget and is not shared.

The test, for the next time something looks shareable: a mechanism is generic in its type
parameters and names nothing from a domain. Anything that names things is a vocabulary, and stays
with its owner or gets a small library of its own.

## D13 — The libraries this one sits beside are named (2026-11-11)

D11 left the client kinds library unnamed. It is `Fuaran.ClientEffects`. This library's browser
package references it.

Two more are named the same day, and this library depends on neither:

- `Fuaran.Policy` is the effect policy and its evaluator: classes, clauses over arguments, rules
  over destinations and limits over a window. A host installs it as the gate. This library ships no
  policy and takes no reference to one (D12).
- `Fuaran.Transport` is the HTTP transport seam. The libraries for remote calls and for model calls
  use it. This library does not.

## D14 — What a model can reach is a program, in the strict form (2026-11-11)

A governed surface mounts only where what a model can reach is pure. Purity is what makes a preview
match what runs, which an approval and a replay rely on. It is not what keeps a model inside its
ceiling: a model sends messages and never an `update`, effects are performed by the host after the
gate, and limits on destinations and arguments apply to every principal.

The condition has two forms.

- **Strict.** Every message a restricted principal can author is handled by a component whose
  behaviour is a `Fuaran.Program` program, hosted through the bridge (D7). Such a program is bounded
  and total and reaches outside only through named effects, so it is pure by construction. No
  analyzer is involved.
- **Analysed.** The behaviour is a compiled `update` that passes the purity analyzer. This is
  weaker, because a scan cannot be complete, and a surface mounted this way says so.

F# cannot prove an `update` pure, and that was the weakest assumption in this design. The strict
form removes it in the place it matters.

Two things are not decided: which form the first consumer mounts in, and how the route from the
surface to a program-hosted component is kept out of application code.

## D15 — The first consumer mounts in the strict form (2026-11-11)

This decides the first of the two things D14 left open. The first application to mount a governed
surface does so in the strict form.

One consequence is not decided. An agent loop handles messages a model authors, namely its replies,
and D14's strict form is stated over every such message. Two readings are possible:

- The loop is library code, trusted with the runtime, and the strict form governs application
  behaviour.
- The loop is itself a program, and the strict form has no exception.

The first is proposed for the first release and the second as the destination. Which holds is
recorded when it is decided.

## D16 — An agent loop is the host of a placement, and neither reading in D15 (2026-11-11)

D15 offered two readings of an agent loop: library code trusted as an exception, or itself a
program. Both asked the wrong question. A model does not write the loop. It writes a program to a
schema, and the loop accepts it, checks it and runs it.

So an agent loop is the host of a placement, written once as library code. A model's output is
accepted only as data to a schema: a `Fuaran.Program` program, a tree or an op that carries them, a
read-only query, or a typed question. With a reply, the loop decodes it strictly, verifies it
before it loads, gates it under the model's ceiling, and runs it on the bounded interpreter. No
model output is interpreted by compiled application code.

The loop is not an exception to the strict form of D14, because what a model authors is never
compiled behaviour. It also does the waiting, so a program does not have to.

## D17 — A program carries its author (2026-11-11)

A program a model wrote can be triggered by someone else. The effects a program lowers to are
decided under the stricter of two ceilings: its author's, and that of whoever sent the message that
triggered it.

This is how the rule "a click on a control a model drew is the model's" is enforced, and it
extends that rule to any handler a model wrote or edited. It does not cover data: a value a model
wrote, which a program written by someone else later sends, is not decided under the model's
ceiling by this rule.

## D18 — Labels are keyed by node id and state key where behaviour is a tree (2026-11-11)

This covers the data case D17 left. A label is the set of restricted principals that have
influenced a thing, and an effect is decided under the strictest of them.

Where behaviour is a tree, what reads and writes what can be read off it, so the label does not
have to sit on a whole component:

- **Behaviour is labelled by node id.** A handler's author is the author of the node that carries
  it, taken from the attribution every op already has.
- **Data is labelled by state key.** A write gives the key the join of the triggering message's
  author, the writing node's author, and the labels of the keys that handler reads.
- **An effect is decided under the join** of the message's author, its handler's author, and the
  labels of the keys its arguments read.

It is built conservatively first, from the tree's own read and write sets, beside the store and
outside the proved fold. Following a label precisely through the fold comes later.

A label on a whole component stays the rule for compiled components, where nothing can see which
field flowed where. A value that leaves a tree loses its label.

How many keys a real tree ends up labelling is measured before either form is built.

## D19 — The four questions of D5 are answered (2026-11-11)

D5 set four questions to answer before implementation begins. Each was measured on 2026-11-11 and
the result checked.

1. **JavaScript.** The implicit conversion from an effect to a step compiles and runs correctly
   under Fable 5.0.0, at the cost of one call per converted element. One edge is a compile error on
   both hosts: a helper whose list was inferred as a list of effects. Equality on a boxed union is
   structural, with one divergence: Fable ignores type arguments when it compares values, so two
   values that differ only in a type argument are equal in JavaScript and unequal on .NET. The
   proposed queue is 2.3 to 4.1 times faster than the present one under node. The code and how to
   run it are in `spike/fable/`.
2. **The interface-definition language.** Of 350 methods in the first host's remote API, 57% can be
   declared as the language stands and 72% once it is widened. None is ruled out. The rest turn on
   date and time types the language does not yet name; if a date and time without an offset is
   admitted, the share is 94%.
3. **Purity.** In existing code, 13 of 44 `update` functions in the first host are impure, and 1 of
   4 in the browser application. The commonest cause is reading the clock and minting ids, which
   the stamp replaces. One class was not anticipated: an `update` that calls a function carried in
   its model or its configuration.
4. **The renderer.** Of nine things a governed surface needs from the renderer, one is met today,
   three partly and five not. Between 36 and 54 sites would be re-routed.

What follows for this library:

- Existing code does not need a `Do` prefix. Only follow-ups are written `Then`.
- The drain buffer stands as the queue for single-threaded hosts.
- A host that carries effects erased compares the module id with the value, always.
- The purity analyzer refuses a call to a function carried in the model or in configuration.

None of the four answers blocks an implementation phase. They size it.

## D20 — Principals map onto Core's two actors, and Core gains none (2026-11-11)

A message's author is a principal: a user, the host, an agent or a foreign source. Where a session
log is kept it is a Core op-stream, and each record carries Core's `Actor`, which has two cases: a
person, and an automated emitter with a name, a version and the id it acts under.

The principal stays this library's own type. It is recorded in the envelope, which the chain hashes,
and it maps onto the two cases Core has:

| Principal | Actor |
|---|---|
| a user | `Human` of the user's id |
| an agent | `Agent` of its model, version and id |
| the host | `Agent` of the application's name, its version and the host's id |
| a foreign source | `Agent` of the source's name, an empty version and the source's id |

Core is asked for nothing. Three facts decided it. Core documents its second case as an automated
emitter, and existing code already uses it for emitters that are not models. Nothing in Core
branches on the case: outside the type's own file no package matches on it, and the guarded
surface's policy takes the actor as an id string. And host against foreign is a distinction of
trust, which a foreign person shows is independent of person against automation. Trust belongs to
the registry and to this library, and Core holds no domain's vocabulary (D9).

One condition. The mapping from a principal to an actor id is injective, with a prefix per case, so
a foreign source can never share an id with a user or with the host. A law test holds it.

## D21 — The stamp is an argument to `update` (2026-11-11)

`update` takes the stamp as its first argument: a time, a sequence number and a seed for fresh ids.
The alternative was for a component to ask for a time by effect.

The effect form was measured in the prototype at one extra message for each timestamped item. It has
since become dearer. The measurement used a synchronous answer, which the design removed: an answer
is now always a step of its own, decided separately. The purity measurement points the same way.
Most of the impure sites found in existing code read the clock or mint an id, and with the argument
each becomes a read.

Three conditions.

1. **The stamp is fixed when the message is accepted** and travels in the envelope. A preview, the
   computation repeated when an approval arrives, and a replay all read the same one. A timestamp in
   an approved step is therefore the time of acceptance.
2. **The seed is split when a child is lifted.** The helper that lifts a child's steps derives the
   child's seed from the parent's and the child's key. Without that, a parent and a child that mint
   ids in one step mint the same id.
3. **`Init` takes no stamp.** A host that wants one passes it in the flags.

A component that needs no stamp is built with the constructor that ignores it.

**The argument form has never been compiled.** The prototype in `spike/` implements the effect form.
The first act of the implementation is to port one of the prototype's ports to the argument.

## D22 — The date and time question in D19 is settled outside this library (2026-11-11)

D19's second answer left open whether a date and time without an offset joins the interface-definition
language. It does not. A time in a contract is an instant, which the language already has. The
language gains a time of day, a duration and a set, and an enum the host platform defines is declared
by its case names. Every method counted in D19 can then be declared, once the first host's fields that
hold a date and time become instants. The ruling belongs to the contract family and is recorded there.
It changes nothing in this library.

## D23 — The stamp's argument form compiles, and a chain needs a seed rule too (2026-11-11)

D21 said the argument form had never been compiled. It has now. `spike/stamp-argument.fsx` ports the
notes module and the shell of port C to it: 25 checks, all passing, and `core.fsx` unchanged. The
prototype's runtime does not stamp, so the envelope carries the stamp and a small adapter hosts a
stamped component on that runtime.

**What it confirms.** Each condition of D21 holds in running code.

1. *Fixed at acceptance.* A timestamped item is in the model after one message, where port C's
   form takes two. A preview reads no clock. The same envelope commits after the clock has moved,
   and the item carries the time of acceptance. An envelope stamped afresh at approval no longer
   matches what was approved. A replay under a clock that fails when read reproduces the model, ids
   and times included.
2. *The seed is split for a child.* With the split, a parent and a lifted child that mint in one
   call get different ids. Without it they get the same one. The file shows both.
3. *`Init` takes no stamp.* What it needs comes in the flags.

**What it found that D21 did not say.**

- **A chain needs a seed rule as well.** A message's follow-ups belong to its step and read its
  stamp. Two follow-ups to one child therefore mint the same id, split or no split. The file shows
  it. The rule it uses is that the runtime gives each follow-up a seed derived from its step's and
  from its position in the message's steps. This is the runtime's to do, not the author's. It is a
  fourth condition and is put to the operator as one.
- **`Init`'s follow-ups need a stamp.** They are messages, and `update` will read one. The host fixes
  a stamp at boot, and `Init` itself still reads none.
- **A reply is stamped when it is accepted.** Its time is later than its cause's, which a component
  that compares the two will see.
- **The word `component` is reserved by F#.** A value with that name draws warning FS0046, which
  fails a build that treats warnings as errors. The type and module named `Component` are not affected. The
  library's samples and documents should not name a value `component`.

**What it does not show.** The seed derivation is a stand-in, a 64-bit mix: which function the
library uses, and how wide an id is, are not decided. Nothing here ran as JavaScript. The stamping
is done at dispatch by an adapter, not by a runtime that owns acceptance. The erased composition, a
host with a dynamic set of modules, was not ported.

## D24 — A follow-up has a seed of its own (2026-11-12)

D23 put a fourth condition to the operator, and it is ruled. It joins the three of D21.

4. **The runtime gives each follow-up a seed of its own.** A message's follow-ups belong to its step
   and read its time and its sequence number. Each reads a seed the runtime derives from two things:
   the seed of the message that emitted it, and the follow-up's position in that message's steps.

An author does nothing for this. The runtime owns the chain, so the rule lives there, as the child's
split lives in the lifting helper.

The alternative was one counter threaded through the whole chain. It was not taken. An id would
then depend on how many ids the earlier messages of the chain happened to mint, so a handler that
started minting one more id would change the ids every later handler in the chain produces.

`spike/stamp-argument.fsx` already runs under this rule and shows the collision without it.

## D25 — The kernel's first parts: two queues and a keyed diff (2026-11-16)

Phase 646 cut `Fuaran.MVU.Kernel` with three parts, each held to an F\* model by a differential test on
.NET and under Fable. Six decisions were taken in cutting them; each records the alternative that lost.

### D25.1 — The three parts' public surface

The two queues are classes with mutable state: `DrainBuffer<'item>` (`Push`, `Pop`, `Take`, `Count`,
`IsEmpty`, `Capacity`) and `FixedRing<'item>` (`Push: bool`, `Pop`, `Count`, `IsEmpty`, `IsFull`,
`Capacity`). The diff is a module of pure functions over F#'s `Map`: `ResourceDiff.plan`,
`ResourceDiff.apply`, `ResourceDiff.isEmpty`, the `Plan` record and `sameInstance`. The surface is in
`STABILITY.md`.

Two alternatives lost. **One queue interface over both queues** was rejected: the two differ in exactly
the thing a caller must handle, the refusal, and an interface would hide it; a host chooses its queue once
(D25.2) and never swaps it at runtime. **Persistent (immutable) queues** were rejected on the prototype's
measurement (D19): the present ring's per-operation allocation is what made it slow under Fable, and the
drain buffer's whole advantage is that a push and a pop allocate nothing.

### D25.2 — Which host uses which queue

The drain buffer is the queue for single-threaded hosts (the browser) and for .NET under the runtime's
lock. The fixed-capacity ring is the queue for a server connection and for a device with a single
producer: hosts that must refuse rather than grow.

The alternative, **one growing queue everywhere with a cap**, lost: a bounded host refuses at acceptance,
where the producer can be told, and a growing queue's refusal would come at memory exhaustion, where
nobody can.

### D25.3 — A refused push changes nothing

`FixedRing.Push` on a full ring answers `false` and leaves the ring exactly as it was: the same slots, the
same heads, the same state (`refused_push_unchanged`). There is no partial write and no side effect.
Telling the producer is the caller's, the step machine's.

Two alternatives lost. **Overwriting the oldest item** (a latest-wins ring) was rejected: it drops an
accepted envelope silently, and every guarantee the runtime makes is about accepted envelopes. **An
exception on a full ring** was rejected: on a bounded host a refusal is an expected outcome, not an error,
and an exception would make the ordinary path the exceptional one.

### D25.4 — The key order the diff walks

The diff walks the running and wanted maps in the host's key order — F#'s `compare` on the key type, the
order `Map` enumerates in — and the plan is applied as every stop, then every start, each in increasing
key order. A restart's stop therefore precedes its start, and the order of effects is the same on every
host (`sequence_shape`).

The alternative, **one merge pass performing stops and starts as it meets each key**, lost: a start met
between two stops could observe a resource not yet stopped, and two hosts whose maps enumerated
differently would perform in different orders.

### D25.5 — What "held" means

A key whose start threw is held under the description that threw. While the model wants that key under
that same description it is not started again (`held_unchanged_not_started`); it is reported once,
through the host's `Lost`. The hold is dropped when the key is no longer wanted, or is wanted under another
description, and the key is then tried again (`apply` and the differential test both show it).

Two alternatives lost. **A hold that persists across the key's absence until its description changes**
was rejected: it keeps state for keys the model no longer lists, which is unbounded, and a key that leaves
and returns is itself a change in what the model asks for. **Retrying a held start with a backoff** was
rejected: a transient failure is the performer's to retry, reporting its state as messages, and the
kernel has no clock.

### D25.6 — The models are pure, tied by the differential; the shipped code is hand-written

The three models are pure list models (`Drain.fst`, `FixedRing.fst`, `KeyedDiff.fst`), extracted through
the prover's F# backend and run beside the hand-written kernel by the differential test on both hosts. The
ring's model is adapted from the reference ring's list model and the refusal shape of the array ring
proved to refine it; it is not itself an array ring in Pulse.

The alternative, **a Pulse array ring extracted and shipped as the implementation**, lost for this cut:
the kernel ships one hand-written F# source to both hosts, and an extracted implementation would be a
second source with a second support library under Fable; the differential already ties the shipped ring
to the model's semantics over every sequence drawn. What the Pulse route would add is the proof that the
array code itself refines the model, closing the gap the differential only samples. That gap is named in
`proofs.json` (`extractor-and-compiler-trusted` and the `tested` rows), and taking the Pulse route is a
decision for a later phase, not a residue of this one.

### D25.7 — The proof kit is adopted by pin, not by copy

The phase's shard said "adopt the proof kit by copy". The kit's own rule since its 0.40.0 is that an
adopter copies nothing: the kit arrives as one release asset pinned by sha256 (`proofs/proofs-kit.json`),
its templates are copied once and are the adopter's from then on, and `copies.json` declares no copy of
any kit file. The kit's rule wins, because a copy is what drifts; the shard's phrase predates it.

## D26 — The runtime: the component, the step decided whole, and the step machine's model (2026-11-16)

Phase 647 cut `Fuaran.MVU` and `Fuaran.MVU.Testing` on the kernel, and the step machine's model beside the
kernel's three. Eleven decisions were taken; each records the alternative that lost. Where a premise of the
phase did not hold, the record (D26.12) says what was true instead.

### D26.1 — The public surface of `Fuaran.MVU`

Namespace `Fuaran.MVU`. The step type `Step<'msg, 'effect>` (`Do`, `Then`, the implicit conversion from an
effect, `Step.map`, `mapAll`, `bind`, `effects`, `followUps`) and the two placeholders `NoEffect`,
`NoResource`. The stamp (`Stamp`, `Stamp.freshId`, `forChild`, `forFollowUp`, `forTest`; the `Seed` module).
Envelopes: `Principal` (`User`, `Host`, `Agent`, `Foreign`; `Principal.actorId`), `ResourceKey` (a path;
`constant`, `named`, `prefix`, `segments`), `Source` (`External`, `Answer`, `FromResource`, `FromRuntime`,
`Boot`), `Envelope<'msg>`. The component: `Component<'flags, 'model, 'msg, 'effect, 'resource, 'view>`,
`Component.plain`, `Component.lift`, `Component.liftWith`, `Component.prefixResources`, `Lifted`, and the
legacy effect `LegacyEffect<'msg>` over `LegacySite`. Kinds and the interpreter: `EffectKind`, `Shape`,
`DeterminismSource`, `Trust`, `Invocation`, `Destination`, `Performance<'msg>` (`Reply`, `Signal`),
`Handled<'msg>`, `Started<'msg>`, `Refusal<'effect>`, `NotAccepted`, `Interpreter<'msg, 'effect, 'resource>`
(`Interpreter.create`, `tryKind`; `Handled.mapMsg`, `Started.mapMsg`). The decision seam: `Decision`,
`Proposal<'effect, 'resource>`, `Decide<'effect, 'resource>`, `Decide.allowAll`. The chain: `Chain<'model,
'effect>`, `ChainOutcome`, `Chain.compute`, `Chain.continueFrom`. The runtime: `Fuel` (`Fuel.defaults`),
`Queue<'item>` (`Queue.growing`, `bounded`), `StepOf`, `StepOutcome`, `StepRecord`, `Report`, `Host<…>`
(`Host.create`), `Token`, `Instance`, `ParkedStep`, `Preview`, `Runtime<…>` (`Start`, `Dispatch`, `Preview`,
`Approve`, `Reject`, `Terminate`, and the observables `Model`, `Renders`, `Yields`, `RefusedAtAcceptance`,
`Refusals`, `IsTerminated`, `Running`, `InstanceOf`, `Held`, `Parked`, `Log`, `Sequence`), and `Replay.run`.
The surface is in `STABILITY.md`.

The alternative, **a smaller surface with the runtime's cells private and no observables**, lost: the
differential must read the model installed, the effects handed over, the refusals, the parked steps, the
running keys and the render count after every operation, and an application's tests want the same things
through the harness. Every observable is a read; nothing on the surface mutates the machine but the five
verbs.

### D26.2 — The host premise each kind of host discharges

The runtime ships the re-entrancy latch: a dispatch made while a step runs is queued and processed in the
same drain. It ships no lock. What each host adds:

| Host | What it must do |
|---|---|
| Browser | Nothing beyond the latch, which the runtime owns |
| .NET | One lock around every call into the runtime (`Dispatch`, `Approve`, `Reject`, `Terminate`, and the emitters and reply callbacks the runtime hands out), and the latch as well because the lock is re-entrant. A performer must not block on work that needs the lock |
| A server connection | The lock, and `Queue.bounded`: a push onto a full queue is refused and the producer is told (`NotAccepted.QueueFull`) |
| A device | Mask interrupts around the push, or a single producer into `Queue.bounded` |

The alternative, **a lock inside the runtime**, lost: the browser host would pay for a lock it cannot
need, the kernel's own rule is that it ships no lock (D25), and a .NET host already owns the boundary at
which its threads meet. The premise is on the ladder as `host-serialises-the-queues`, which the step
machine's model inherits: every theorem is about sequences of operations and says nothing about two at once.

Amended 2026-11-20 (Phase 716, operator ruling): the .NET lock wrapper is designed with the remote host (stage V6 of
the runtime plan), the first host that runs the runtime on .NET threads; a browser host needs the latch alone.

### D26.3 — The fuel bounds, and what is reported when one is hit

`Fuel.defaults` is 256 messages per chain and 1,024 steps per drain; a host sets its own in `Host.Fuel`.
A chain over its bound is refused whole: nothing is installed or performed, the interpreter's `Refused`
receives `ChainTooLong limit` and may give the component a message authored by the host, the host's
`Report` receives `StepRefused`, and the step is recorded as refused with the reason. A drain over its bound
yields: it renders once, reports `Yielded steps`, and schedules its own continuation through `Host.Schedule`
after the latch is released; nothing is dropped and order is kept (`machine-yield-drops-nothing`).

Two alternatives lost. **No chain bound** was rejected: a follow-up loop would freeze the host, and a
page that cannot be frozen is the point of the rule. **An exception on either bound** was rejected: on the
drain a yield is the ordinary way a long backlog is processed, not an error; on the chain the author's bug is
reported to the component through the same `Refused` path every other refusal takes.

### D26.4 — The seed function and an id's width

The prototype left both as stand-ins. A seed is a 64-bit word and every derivation is one SplitMix64 mixing
step over a combination distinct per purpose: the message's seed from the root seed and its sequence number,
a child's from its parent's and a hash of the child's key (FNV-1a over the key's characters), a follow-up's
from the emitting message's and the follow-up's position, and the `n`-th fresh id from the seed and `n`. An
id is 64 bits, rendered as sixteen lowercase hexadecimal characters. The mixer is a bijection whose output
bits each depend on every input bit, and 64-bit arithmetic wraps the same way on both hosts (a JavaScript
BigInt under Fable; the stamp laws run under Node).

Two alternatives lost. **A 128-bit id (a UUID)** was rejected: nothing here needs collision resistance
across machines — an id is unique within a session's seed, which the derivation guarantees — and 128-bit
arithmetic has no native form under Fable. **One counter threaded through the chain** was rejected in D24,
and the file that showed the collision (`spike/stamp-argument.fsx`) is now `stamp 24` and `stamp 25` in the
suite.

### D26.5 — The shape of the decision seam, and its allow-all default

The host supplies `Decide: Proposal -> Decision`. A proposal is the step as the decision sees it: who wrote
the message, its sequence number, every effect of the chain with its invocation in emission order, every
start with its invocation, and every stop. The decision is `Allow`, `NeedsApproval` or `Deny reason`, and it
decides the step as a unit (rule 7). This phase ships `Decide.allowAll`; the gate over a registry of kinds,
classes and grants is Phase 648's, and it is one more `Decide`.

The alternative, **the gate built into the runtime with a registry parameter**, lost: the runtime would
then depend on the registry's vocabulary, and the step machine's model would have to carry it; as a function
of the proposal the decision is one parameter of the model (`decision-is-a-function`) and one member of the
host.

### D26.6 — A reply is one answer through a callback, not an `Async`

`Performance<'msg>` is `Reply of (('msg -> unit) -> unit)` or `Signal of (unit -> unit)`. A performer that
answers calls the callback once; the runtime accepts the answer as a new envelope through a one-shot token,
authored from the kind's `Answers`, and revokes the token, so a second call is refused at acceptance.

The alternative, the design's **`Reply of Async<'msg>`**, lost: an `Async` needs a scheduler to start it and
decides on which thread the answer lands, which on .NET is inside the host's lock premise; a callback lands
where the performer calls it, the scripted interpreter holds an unanswered one as a value, and a performer
written with `Async` wraps in one line (`Async.StartWithContinuations`).

### D26.7 — `Fuaran.MVU.Testing` takes no test framework

The harness and the laws answer failures as sentences (`string list`), which Expecto, xUnit, a Node runner or
a `main` report in their own way. The stamp a direct `Update` call reads is the test's (`Stamp.forTest at`);
the stamps the runtime fixes come from the harness's clock (1001, 1002, …) and seed, which the options set.
The scripted interpreter registers one kind, `test`, describes every effect and resource under it, and
answers each effect from a table: at once, when the test calls `Answer`, or not at all.

The alternative, **a dependency on Expecto for `test`-shaped helpers**, lost: an application's tests
choose their framework, and the two hosts run the same laws from one list.

### D26.8 — The boot drain paints first and renders again only if a step moved the model

Rule 15 paints the `Init` model before anything runs, so hydration sees it. The boot drain then runs the
boot step (`Init`'s steps under the boot stamp, authored by the host) and whatever it accepts, and renders at
its end only if the model is no longer the instance that was painted. Every later drain renders exactly once
at its end (rule 13; `machine-one-render-per-drain`). A component with no `Init` steps therefore renders
once at boot, as the prototype's first check expects.

The alternative, **the boot drain always rendering a second time**, lost: it paints the same model twice
for every application whose `Init` emits nothing, which is most of them.

### D26.9 — An effect of an unregistered kind refuses the step

The runtime authors a reply from its kind's `Answers` (rule 2), so an effect whose kind the interpreter has
not registered has no author for its reply. The step is refused (`Refusal.Unregistered kind`) before the
decision. This is the runtime's own invariant; the gate's refusal of an unregistered kind (Phase 648) is a
class decision on top of it.

The alternative, **authoring such a reply as the host's**, lost: it would silently grant first-party
authority to a kind nobody registered.

### D26.10 — What the step machine's model represents, and in two modules

The model is two F\* modules: `StepChain` (the chain with fuel) and `StepMachine` over it. The queue is a
list of envelopes, oldest first — `Drain.fst` and `FixedRing.fst` prove each kernel queue is its unread list
over any sequence of operations, so the machine stands on that list and a bounded host refuses at
`capacity`. Resources are keys with descriptions and instances with tokens; the plan is the diff's three
filters over lists (stop what is not wanted under the same description, start what is wanted and not
running), without the held set, which `KeyedDiff.fst` covers. The stamp and the seeds are inputs to `update`
the theorems do not read. Three invariants carry every theorem: accepted = updated ++ queue; every token
below the next to issue; a terminated machine holds nothing. The differential drives the extraction beside
`Runtime` over generated scripts and compares nine observables after every operation.

Two alternatives lost. **One module** was rejected because the chain's theorems (bounded, monotone, whole)
stand on their own and the machine's proofs read better over an abstract `chain`. **Opening `KeyedDiff`
for the plan** was rejected: the machine's theorems are about tokens and traces, not about which keys the
plan names, and the diff's own theorems already hold its plan to its specification; re-stating the plan
over lists kept each module's discharge under six seconds.

### D26.11 — The legacy effect is a class over a non-generic identity

`LegacyEffect<'msg>` is a sealed class inheriting `LegacySite`, whose equality is a type test on the
non-generic base and a comparison of the site labels. The prototype's record with a custom `Equals` needed a
type test naming the generic type, which Fable refuses (it ignores type arguments in a type test and names
the omission as an error under warnings-as-errors); an interface type test on a record it refuses as well.

The alternative, **a non-generic effect carrying `obj -> unit`**, lost: it loses the typed callback the
wrapped code dispatches through.

### D26.12 — Premises that did not hold, and what was built instead

- The shard said `proofs/` and `tests/Fuaran.MVU.Tests/` were new; Phase 646 had landed both, and this
  phase extended them (the opener said as much).
- The design's chain model was to be a module named `Chain`; the proof kit already carries a read-only
  model of that name, which an adopter may open and never shadow, so the module is `StepChain`.
- The prototype's `Stream` performance and synchronous `Now` answer are gone (the design removed them); the
  ported checks that measured them now assert what stands in their place (a reply's token is one-shot; a
  resource stops when delisted; an answer at once is still its own step in the same drain).
- The prototype let an exception in `update` escape to the dispatcher; rule 11 refuses the step, and the
  erased mis-addressed message is now that refusal (`port-c 19`).

## D27 — The gate: a principal's registry, a step decided through Core, and a grant (2026-11-16)

Phase 648 brought Core into the runtime: `Fuaran.MVU` takes its first `Fuaran.Core.*` references, and the decision
seam of D26.5 gains its one more `Decide` — the gate over Core's capability registry — while the runtime keeps no
gate of its own. Twelve decisions were taken; each records the alternative that lost. Where a premise of the phase
did not hold, the record (D27.12) says what was true instead.

### D27.1 — The principal type, and where it is stamped

`Principal` stays this runtime's type (D26.1; the design's P40): `User`, `Host`, `Agent of model * version * id`,
`Foreign of source`. It is stamped on the envelope at acceptance and nowhere else: a dispatch from outside carries
the author the host names; an answer carries the author of its kind's `Answers`, which gains a case — `Trust.FromModel
of model * version * id`, so a reply to a model call is the agent's (`Agent(model, version, id)`), beside
`FirstParty` (the host's) and `Untrusted of who` (foreign); a resource event likewise; a message the runtime authors
and the boot are the host's. A follow-up keeps its author (the chain is one step). So when a user asks and the model
answers, the answer's step is decided under the agent's registry: authority follows authorship, not the cause.

The alternative, **Core's `Actor` as the author type**, lost: Core gains no case (P40), and host against foreign
is a distinction of trust that is this runtime's and the registry's, not Core's. The mapping onto `Actor` is D27.6.

### D27.2 — How a host builds a restricted principal's registry

A host registers its kinds ONCE, in `Gate.create`, and each becomes a Core `Capability`: the kind's id; a signature
of one optional hole for the canonical arguments (`args`, any string) and one required hole for the destination
(`destination`, any string: `local`, or `remote:` and the origin); the kind's determinism carried on Core's axis; the
widest host effect, because a kind states its authority in its classes and nothing here reads Core's host axis; no
result; a placement Core carries and never reads. A kind's classes are fixed at that registration: the class gates
read them from the gate's own kinds by id, and an invocation has no field to state a class.

A principal's registry is the HOST's, tightened by the principal's `Restriction`s in order: `Only kinds` is Core's
`restrict`; `ApprovalFor classes`, `Refusing classes` and `Gated g` are Core's `withGate`. Nothing else builds a
registry, so a restricted registry refuses everything the host's refuses — Core proves a gate added never lowers a
decision (`with_gate_tightens_in_context`) — and no configuration can give an agent more than the user it acts for.
The host's own gates (`Gate.create`'s second argument: an egress allow-list, say) sit on the host's registry and so
on every principal's.

The alternative, **a registry per principal the host builds by hand** (as Core's own guidance suggested before
Phase 638), lost: nothing would hold it to the host's, and the theorem `restricted_never_looser` would be a property
of one host's configuration rather than of the construction.

### D27.3 — The decision context, and the decision through Core

The context the runtime passes to Core's gates is `DecisionContext { Actor; Grant; At; Generation }`: the envelope's
author, the grant presented if any, the host's clock as data, and the gate's registry generation. Every member is a
value, so a gate reading it is total and side-effect free, as Core requires; the proposal carries `At` so the clock
is read by the runtime, never by a gate.

The decision is Core's, over a `StateAwareGuardedWitness` whose operation is the proposal itself: the runtime has
already computed the chain and drawn the plan, so the witness's dry run passes and applies nothing (the step machine
commits), and its effects-of are the step's invocations — each effect's and each start's kind with its bindings. The
surface's `Decide` is the domain's policy over the whole step (`Gate.Policy`, for the principal acting). Without a
grant the step's decision is `Proposals.decideGuardedIn` through the AUTHOR's registry — the policy joined with the
registry's decision for every invocation: an unregistered kind is a denial, a class gate answers as registered. With
a grant it is `Proposals.approveGuardedIn` over a one-step queue built from the parked step: Core refuses an approver
who is the author (`SelfApproval`), reads the policy for the approver, and presents the grant for exactly each
invocation the step makes, so the registry's needs-approval is lifted for those and nothing else and a denial is
lifted by none. The grant verdict (D27.4) is joined once per step. `Gate.decideUnder` joins the author's decision
with each further principal's (D27.10).

Two alternatives lost. **`Proposals.submitGuardedIn` as the decision** was rejected: its park and apply halves are
the step machine's — proved in `StepMachine.fst` — and Core's proposal queue would be a second record of the parked
steps the runtime already holds; `decideGuardedIn` is the decision `submitGuardedIn` decides by, and nothing else of
it is lost (the dry run passes by construction). **A grant check of the runtime's own** was rejected: Core's
approval already refuses the author, reads the approver's ceiling and lifts per invocation; duplicating any of it
would be a second gate.

### D27.4 — The grant's fields and the digest; `Approve` takes a grant

`Grant { Parked; Request; Approver; ExpiresAt; Generation }`. `Request` is `Proposal.request`: the canonical,
INJECTIVE rendering of the step's effect invocations (kind, whether it has arguments and their text, destination),
its starts (key and invocation) and its stops, through Core's field encoding `Hash.canonicalFields` — the encoding
Core proves injective and binds its own grant to (Core's D249). No hash is taken: two steps have one request exactly
when they ask the same kinds the same arguments at the same destinations and plan the same starts and stops. The
author, the sequence and the effect values are not part of it: the identity is what the gate decides on and what the
approver is shown. `Gate.grant gate parked approver expiresAt` mints one for a parked step under the gate's
generation; `Grant.mint` is the general form.

`Runtime.Approve` now takes the grant. The parked step the grant names is resolved at once, whatever follows (single
use); the chain is computed again on the current model with the envelope's own stamp (P41); the request it now makes
must equal the grant's, else the step is refused `Stale`; then the step is decided with the grant presented, through
the host's `Decide`, and commits only on `Allow`. The expiry and the generation are read by `Gate.grantVerdict` as
data from the context, joined into the step's decision once per step — so a step with no invocation is held to them
too — and neither is lifted by the grant itself: a grant lifts a needs-approval and nothing else. Termination resolves
every parked step as cancelled, as before (`StepMachine.terminate_spec`).

Three alternatives lost. **Core's cryptographic digest** (`Hash.sha256Hex` over the pre-image) was rejected: the
pre-image is already injective, short enough to carry, and a hash adds a premise nothing here needs; **the 32-bit
cache hash** was refused outright (Core's D249, the design's §24.5). **Keeping `Approve: int -> bool`** beside the
grant form was rejected: an approval without a grant is the bypass the gate exists to close — Phase 647's `Approve`
committed on equality without a decision. The change is within the unreleased 0.1.0 draft, which nothing pins.

### D27.5 — What raises the registry's generation

`Gate.create` is generation one; `Gate.rebuild previous …` — a kind added or removed, a host gate, a restriction or
the policy changed, a hot reload — is the previous generation plus one. A grant minted under another generation is
refused (`grantVerdict`), so every outstanding grant is invalidated by a rebuild, as the design's §9.5 asks.

The alternative, **a generation inside Core's registry**, lost: Core's registry is a value with no generation, and
the gate value is the unit the host replaces.

### D27.6 — The mapping from a principal to an actor id, and to Core's `Actor`

`Principal.actorId` (D26.1) prefixes per case: `user:<id>`, `host`, `agent:<model>/<version>/<id>`,
`foreign:<source>`. `Principal.toActor name version` maps onto Core's `Actor` with no new case (P40): a user is
`Human "user:<id>"`; the host is `Agent(name, version, "host")` under the application's name and version; an agent is
`Agent(model, version, "agent:<id>")`; a foreign source is `Agent(source, "", "foreign:<source>")`. Both mappings are
injective — the id's prefix tells the cases apart — and tested so (`GateLaws`), so a foreign source can never share an
actor with a user, the host or an agent.

### D27.7 — The mount check

`Gate.mount gate interpreter` hands the decision function over only when every kind the interpreter registers is in
the gate's host registry (`CapabilityRegistry.tryFind`), and otherwise refuses, naming the kinds the host does not
cover (`Gate.uncovered`). The check is the runtime's, against Core's registry, as the design's §10.3 item 7 asks; a
host that bypasses it by taking `Gate.decide` directly still gets D26.9's refusal of an unregistered kind at the
first step that asks for one.

### D27.8 — The two Core pins, one property, and the restore path

`Fuaran.Core.Function` (the registry, the gate, `PolicyDecision`, `Grant`) and `Fuaran.Core.AiSurface` (the guarded
decision and approval), both at the one property `FuaranCoreVersion` (0.43.0) in `Directory.Packages.props`; the
restore path is declared `public` in `Directory.Build.props`, which is what it is: this repository has no
`nuget.config` and restores from nuget.org alone. The kernel references neither; `Fuaran.MVU.Testing` references
neither. Both packages compile under Fable 5 from the sources they ship under `fable/`, and the gate's laws and
differential run under Node.

### D27.9 — What the seam's types gained

`Proposal` gained `At` (the clock as data) and `Grant` (the grant presented, `None` for a step as it is first
decided; only `Runtime.Approve` sets it, no message can). `Trust` gained `FromModel`. `Decide`'s shape is unchanged
and `Decide.allowAll` stays: a host without a registry is legitimate (D26.5), and such a host's `Approve` commits on
the request check alone.

Amended 2026-11-20 (Phase 716, operator ruling): Core removes `Placement` (fuaran-core#711), so the gate stops
declaring one when this repository raises its Core pin.

### D27.10 — The decision under several ceilings

`Gate.decideUnder gate ceilings` decides a step through the author's registry joined with each further principal's:
the stricter of them all. It is how an effect a hosted program lowers to is decided under the program's author as
well as the triggering message's (the design's P38, claim 7). This runtime hosts no program yet; the bridge that will
calls it, and `Authority.two_ceilings_never_looser` is its theorem. The alternative, **a second author on the
proposal**, lost: it would fix one shape for every host before any host needs it.

### D27.11 — The gate's model

`Authority.fst`, the sixth extracted module. Core's `PolicyDecision.join` is a PARAMETER of every function, and every
theorem requires `is_join join`: the join is the argument of the larger rank, the left on a tie — Core's own
definition, of which `join_is_max`, `policy_join_monotone_in_context` and `with_gate_tightens_in_context` are Core's
theorems (`Fuaran-Core/proofs/Capability.fst`, v0.43.0). They are hypotheses here, never re-proved: a copied theorem
is a fork, and Core's `Capability.fst` is not in the kit asset to open. The differential hands the extraction Core's
join itself, so the premise is discharged by the join it was stated about. The seven theorems are rank inequalities
over two folds (the gates of a registry, the invocations of a step) and the grant's rules; the model does not carry
the approver (Core's refusal, tested), the guidance text, the destination as a field (it rides in the arguments), or
the runtime's lookup of the parked step (`gparked` is compared).

### D27.12 — Premises that did not hold, and what was built instead

- The shard's key files named `src/Fuaran.MVU/`, `tests/Fuaran.MVU.Tests/` and `proofs/` as new; Phases 646 and
  647 had landed all three, and this phase extended them without restructuring the hosts, the differentials or the
  ladder family.
- Phase 647's `Trust` could make a reply the host's or foreign, never an agent's; "a reply to a model call is the
  agent's" needed the case `FromModel` (D27.1).
- The brief named `submitGuarded` as the decision; the decision it decides by, `decideGuardedIn`, is used, and the
  park and apply halves stay the step machine's (D27.3).
- A grant gate run per invocation, as Core's gates run, would leave a parked step with no invocation ungoverned by
  the grant's expiry and generation; the grant verdict is joined once per step instead (D27.4).
- Phase 647's `Approve` committed on equality of the recomputed effects and plan without a decision; with a gate
  that is the bypass the design's §9.5 closes, so the verb takes a grant and decides again (D27.4).
- Core's approval reads the domain policy for the APPROVER and treats its needs-approval as met; the model's policy
  verdict is therefore lifted by a grant for exactly the request, as the registry's is (`Authority.lifted`); the
  model's first draft left the policy unlifted and would have read stricter than Core at every approved step whose
  policy asked for one.

## D28 — The session log: an optional package over Core's op-stream (2026-11-16)

Phase 649 cut `Fuaran.MVU.Session`, the last package of the first stage. The runtime records nothing unless a host
attaches a sink: deciding, previewing and approving need no log. Replay, restore and attestation do, and a host that
wants none of them takes no code for them. Eleven decisions were taken; each records the alternative that lost.
Where a premise of the phase did not hold, the record (D28.11) says what was true instead. Phase 716 added a twelfth,
the bound on the runtime's in-memory log (D28.12).

### D28.1 — The record's shape and its canonical form

The log is a Core op-stream whose op is an `Entry`: either a decided step (`Entry.Step`) or the bare fact that the
step at a sequence was not recorded, and why (`Entry.Lost of sequence * LostReason`, which carries no field at all).
A step entry holds the stamp (time, sequence, seed), the author (the `Principal` itself), the source (`Fuaran.MVU`'s
`Source` with its strings recorded under disclosure), the cause, the message's fields under disclosure, which boot it
was (a boot only: `Init`'s, or a restore's with what the restore settled, D28.5), the outcome and the grant it was
decided with. The record's actor is the author mapped by `Principal.toActor` (D27.6), which Core folds into the
record's hash, so attribution is as tamper-evident as the op.

The canonical form is one JSON object, `{"f":[…]}`, whose one member is a flat array of strings in a fixed order:
numbers as their decimal text (a non-canonical one is refused on read), the seed as sixteen hexadecimal characters,
a principal as four strings, a recorded field as a tag (`=` clear, `#` committed) and its text, a list as its count
and its items. It is written with Core's string spelling (`Jsonl.quote`) and read with Core's line reader
(`Jsonl.parseLine`, `Jsonl.stringsField`); anything left over is refused. It is the op's `Encode`, so it is what Core
hashes, and Core's JSONL embeds it verbatim.

Two alternatives lost. **A nested JSON object per entry** was rejected: Core's line reader reads flat objects and
string arrays, and a nested reader would be a second JSON parser in this repository. **Core's `Attributed`
envelope** was rejected: it carries the actor and a timestamp as strings inside the op, where the typed actor
already rides in the record and the time is the stamp's, so it would duplicate both.

### D28.2 — What a decision record and a grant record hold

The decision is the step's `Outcome` as the runtime took it: committed, parked with its id, or refused with the
reason (under disclosure). The grant is the one the step was decided with, carried on the runtime's `StepRecord`,
which gains `Grant: Grant option`: `Some` for a parked step decided again by `Runtime.Approve`, committed or refused
(stale, denied, or not lifted), and `None` for a step as it is first decided and for a rejection. A grant record
holds the parked id, the request (under disclosure: it carries the invocations' arguments), the approver, the expiry
and the generation. A step parked and later approved is recorded twice, once at each decision, in the order the
decisions were taken; the replay commits it at the second, on the model of that moment, as the runtime did.

The alternative, **decisions and grants as ops of their own beside the steps**, lost: a decision is about exactly one
step and is taken at exactly one moment, which is the moment the runtime hands the step to the sink; two ops would
need a link between them that one op has by construction.

### D28.3 — Disclosure, the commitment, its key, and secrets

The host passes a function from a field's path and value to `Shown`, `Withheld` or `Secret`, and a codec that names
its message's fields. The function is applied to EVERY string field the log holds: each message field
(`msg/<path>`), an answer's kind (`source/kind`), each segment of a resource event's key (`source/key/<i>`), a
refusal's reason (`outcome/reason`), a grant's request (`grant/request`) and each field of a message a restore gave
the component (`settled/<i>/<path>`). The principal is the exception: it is the record's attribution, which is the
log's point; a host that must not record a person's identity maps it to a pseudonymous id before it dispatches.

A withheld value is recorded as its commitment: HMAC-SHA-256 under the host's key over Core's injective field
encoding of a domain tag, the step's sequence, the field's path and the value. The sequence and the path make every
occurrence distinct, so two equal values at two places commit differently and a reader without the key learns
neither value nor that they are equal; the key makes a guessable value uncheckable by anyone but its holder. HMAC is
this package's (`Hmac.sha256`, over Core's portable `Hash.sha256Bytes`; Core ships no keyed construction and says so),
held to RFC 4231's vectors on both hosts and to the platform's HMAC on .NET. The key is a `CommitmentKey` of at least
32 bytes: the host holds it, it is never in an entry or a record, and it prints nothing of itself. The host's vault
(`OnWithheld`) receives each withheld value of a step that is recorded, with its place; a replay asks it back through
`reveal`, and the value is accepted only if its commitment opens to it.

A value declared secret is never written: the step that carried it is recorded as `Entry.Lost(sequence, Secret)`,
the vault never sees it, and a replay stops there. A secret is a reference (the design's rule): a message carrying one
is the application's defect, and a log that cannot be replayed past it says so rather than hiding it.

Three alternatives lost. **A bare hash** was rejected outright: a hash of a guessable value reveals it. **A random
salt per value** was rejected: it needs randomness the recorder would have to read, and a salt stored beside the
commitment lets anyone with the log brute-force a guessable value. **Recording a secret as an empty field** was
rejected: the log would then read as complete while it could not reproduce the step.

### D28.4 — The component as a stream witness; replay

`Log.witness` is the component as Core's `StreamWitness`: the op is a recorded step, the state is the model (with what
a restore needs: the effects owed an answer and the steps left parked, `Folded`), and apply is `update` under the
step's recorded stamp, for a step the runtime committed — `Chain.compute` on the message, `Chain.continueFrom` on a
boot's steps. A parked or refused step changes nothing. `Log.replay` is Core's `OpStream.replay` of that witness:
it performs nothing, reads no clock and consults no decision, so it replays the recorded decisions and does not take
them again; ids and times come from the recorded stamps (D26.4). It reads the interpreter's description of an effect
(its invocation's kind) to know which effects owe an answer, and never asks it to perform. Core's snapshots and
compaction take `Log.stream`, the same witness under the session's hash; this package reimplements none of them.

The alternative, **the model alone as the state, and a second fold for restore**, lost: the outstanding effects and
the parked steps are functions of the same records in the same order, and a second fold would recompute every chain.

### D28.5 — Restore, the idempotency declaration, and the interrupted answer

`Log.restore` resumes the runtime from a replayed log: it boots from the folded model, its boot step lists the
resources again (the runtime starts what the model wants), sends again every outstanding effect of a kind declared
idempotent, gives the component the host's `Interrupted` message for every other outstanding effect, and gives it the
interpreter's `Refused Cancelled` message for every step left parked, as termination would (D27.4). The boot records
what it settled, so a replay of the whole log, the restore included, reproduces the restored model.

An effect is outstanding when a committed step emitted it, its kind is call-shaped (one answer is owed), and no
recorded step is its answer. An answer names its effect: `Source.Answer` gains the effect's position in its step's
emission order, so a step that emits one kind twice and has one answer says which. A signal-shaped kind owes nothing
and is never outstanding; a stream-shaped one is a resource, and the resources are listed again.

The idempotency declaration is the host's, at the session's registration: `Log.declareIdempotent` takes kind ids and
refuses, by name, one the interpreter does not register or one that is not call-shaped. "The same key" is the effect
itself: it is recomputed from the recorded stamp, so its value — and any key the application minted into it with
`Stamp.freshId` — is the one first sent, and an idempotent kind's performer deduplicates by it. The interrupted
answer reaches the component as a follow-up of the restore's boot step, authored by the host, as every message the
runtime gives the component is.

`Runtime.Resume` (in `Fuaran.MVU`) boots at the sequence after the log's last, so every stamp and fresh id after a
restore derives from a sequence no earlier step used; a restored runtime never mints an id the model already holds.

Three alternatives lost. **A field on `EffectKind`** for idempotency was rejected: only a restore reads it, and a host
without a session would carry a declaration nothing of its reads, in every kind literal. **A `Refusal` case for the
interrupted answer** was rejected for the same reason: a closed union every host matches would gain a case only the
session raises. **A fresh root seed after a restore** was rejected: ids would then be unique only with high
probability, where D26.4 makes them unique by derivation. The restore's boot is decided like any boot, under the
host's decision; a re-sent effect the host's registry would not allow refuses the boot, and the next restore settles
it again.

One limit, stated: a performer that threw when its effect was handed over (`Report.PerformerThrew`) is not a step and
is not recorded, so a restore cannot tell it from an interrupted one and treats it so; the component may hear of that
effect twice.

### D28.6 — The sink seam in `Fuaran.MVU`

`Host.Record: StepRecord -> unit` (Phase 647) is the seam: the session's `Session.Record` is attached there, and
`Host.create` leaves it `ignore`, so a host that attaches none records nothing. Nothing in `Fuaran.MVU` references the
session package (a .NET law reads the assembly's references). The phase changed the seam only where the log could not
be complete without it: the grant on the step record (D28.2), the answer's position (D28.5), `Runtime.Resume` and
`StepOf.sequence` of a boot reading the boot stamp's sequence (zero, as before, for a fresh boot). All four are within
the unreleased 0.1.0 draft. `Replay.run` replays a log from a fresh boot; a resumed boot is the session's to replay.

The alternative, **a sink interface of its own in `Fuaran.MVU`**, lost: the record hook already hands over every step
as it is decided, which is all a sink needs, and a second hook would be a second place the runtime reports a step.

### D28.7 — The store seam, and the browser's default

`Store { Append; Records }`. `Store.inMemory` is the default, and the only store in a browser unless the host declares
one of its own (an encrypted store is the host's to declare; none is built here). `Store.lines` persists through Core's
JSONL — each record one line (`Jsonl.tryWrite`), the text read back through Core's verified reader
(`Jsonl.readVerified`), which checks the chain — over two functions the host supplies. `Store.file` is `lines` over a
file, compiled on .NET only. `Session.Open` refuses a store whose records do not verify, and continues the chain the
store holds. A store that cannot write throws to the runtime's caller rather than drop a step silently.

The alternative, **the line store behind `#if` as a whole**, lost: Core's JSONL compiles and runs under Fable, so the
format is host-neutral; only the file is not.

### D28.8 — What "attestable" requires

A chain a client holds and can recompute proves nothing against that client: it can rewrite the log and rehash it.
Tamper-evidence of a client-held log is therefore not claimed (the design's not-claimed item 7). An attestable log
needs its head anchored outside the client: `Session.Head` (the last record's hash) signed through Core's attestation
sink (`IAttestationSink`, or an `AttestationDescriptor` sealed over a segment), countersigned or anchored by a party
the client does not control. That is a requirement on a host that wants evidence; no attestation service or anchoring
scheme is built here.

### D28.9 — Snapshots and compaction write no model

Snapshots and compaction are Core's (`Snapshots.take`, `Compacted.compactAt`, `Compacted.replayFrom`) over
`Log.stream`; a law compacts a log and replays it from its snapshot. This package writes no snapshot: a model holds
withheld values in the clear, so encoding one would put on disk what disclosure kept out of the log. A host that
persists a snapshot encodes the model itself, outside the disclosure function, and a chain-only snapshot binds no
state; the boundary is stated rather than hidden.

### D28.10 — The dependencies

`Fuaran.MVU.Session` references `Fuaran.MVU` by project, FSharp.Core and `Fuaran.Core.OpStream` at the one property
`FuaranCoreVersion` (0.43.0), as the design's dependency graph has it, and nothing else: no disclosure library (the
host passes a function), no `Fuaran.Core.ContentAddress` (the commitment uses the text kernel's SHA-256, which
OpStream already references), no program library and no Fable package. Its sources compile under Fable 5 with Core's
own, and its laws run under Node in the memory store.

### D28.11 — Premises that did not hold, and what was built instead

- The phase's brief allowed for a sink seam to be added to `Fuaran.MVU` if Phases 647 and 648 left none. Phase 647
  left one, `Host.Record`; what it lacked was the grant, the answer's position and a resume (D28.6).
- The brief described Core's JSONL as .NET-only. It is host-neutral (its writer and its verified reader run under
  Fable, and this package's laws use them under Node); only a file is .NET's (D28.7).
- The shard's "restore folds to the model, lists the resources again" needed the restored runtime to continue the
  sequence, or ids minted after a restore could repeat ids minted before it; `Runtime.Resume` was added (D28.5).
- The runtime keeps every step record in memory (`Runtime.Log`, Phase 647) whether or not a sink is attached. It
  writes nothing anywhere, so "a runtime with no sink attached writes nothing" holds; that the in-memory log grows
  for the runtime's life is outside this phase and is put to the operator. The operator ruled a ring (D28.12).

### D28.12 — The step log is a ring the host sizes (Phase 716, 2026-11-20)

`Runtime.Log` kept every step record for the runtime's whole life, with or without a sink, so a long-lived browser
session grew it without bound. It is now a ring of fixed capacity: the host sets it as `Fuel.Log`, the third of the
bounds it sets beside the chain and the drain (`Fuel.defaults.Log` is 1,024, one drain's worth). When the ring is full
the oldest record is dropped and counted: `Runtime.LogDropped` is every record made and not held, so zero means `Log`
is the whole history and a reader can tell a short history from a truncated one. The sink is unchanged: `Host.Record`
receives every record as it is decided, whatever the ring drops, and the whole history is the sink's to keep. A bound
of zero (or less) keeps no log at all: `Log` is empty, every record counts as dropped, and the sink still receives
each one. The ring is the kernel's `FixedRing` (D25), a push onto a full one being refused, so the runtime pops the
oldest and pushes again; its storage is allocated at boot at the bound, so the bound is a reservation as well as a
ceiling. `Log` reads the ring by turning it once round, so a read leaves it as it was.

Premises at take. What read `Runtime.Log`: the session package does not (it is the sink, `Session.Record`, and its
`Log` module replays the session's own op-stream); `Replay.run` and the testing package's `Law.replayReproduces` fold
a log from a fresh boot and need every record, which the in-memory log now is only while `LogDropped` is zero, so a
host that replays a long session replays the session log (D28.4); the step and gate differentials and the ported checks read
it over short runs, well within the default. No reader needed every record from the in-memory log beyond a test.

The ruling is the operator's (2026-11-20): of the three options put, the ring is the only one that bounds memory in
every configuration, sink or none, and it keeps the recent steps available. The phase that carried the ruling did not
record the other two, so they are not restated here. The ring is ordinary code over the kernel's proved ring; if
fuaran-core#717's proved ring replaces the kernel's, this log may use it.

## D29 — The React adapter: a store, one publish per drain, and a bridge a governed surface refuses (2026-11-20)

Phase 720 cut `Fuaran.MVU.React`, the package a React application mounts a component with (the design's §14; stage
V2). It holds the mount, the model store, the scheduler, hydration, hot reload, the element-view bridge and the
after-the-next-paint kind. It depends on `Fuaran.MVU`, FSharp.Core, Fable.Core and Fable.React.Types, and not on
`Fuaran.MVU.Browser` (Phase 719): the two are siblings over the runtime, and an application that wants the standard
kinds takes both. Nine decisions were taken; each records the alternative that lost.

### D29.1 — The public surface

Six modules, in compile order:

- `ElementView.fs`: `IGovernedView`, `ElementView<'msg>` (`ofElement`, `ofView`, `toElement`) and `Governed.admit`.
- `Store.fs`: `Ref`, `Root`, `ReactApi`, `StoreSnapshot`, `ModelStore<'model, 'view>`, `RootProps`, `StoreRoot.root`
  and `ModelStore.useSelector` / `refEquals`.
- `Scheduler.fs`: `Timing`, `Scheduler` and `AfterPaint` (`Kind`, `kind`, `handle`).
- `Hydration.fs`: `Attach` (`Fresh` / `Hydrate`) and `Hydration.choose` / `attach`.
- `HotReload.fs`: `Carried` and `HotReload` (`enabled`, `shapeOf`, `take`, `register`, `forget`, and `Marker` under
  `DEBUG`).
- `Mount.fs`: `Binding`, `MountOptions`, `Mounted`, `Mount.options` / `mount`, and `Live`.

The mount is one function over values: `Mount.mount binding options comp interpreter flags`. The options carry the
host the application builds (clock, seed, decision, fuel, queue, reports, record sink). The mount replaces only that
host's `Render` and `Schedule`, which are the adapter's. The options also carry:

- the container, and how the root attaches;
- how a view becomes an element;
- the principal a view's dispatch is attributed to (`User "local"` by default);
- the store, which a view can read slices of when it is made before the mount (forge's sliced shell, D29.9);
- the hot-reload key and the model's shape.

`Mounted` answers the runtime, the store, the scheduler, `Dispatch` and `Unmount`.

The alternative that lost was **a mount per view kind** (`Mount.elements`, `Mount.trees`, …) as the primary surface:
the Fuaran-UI renderer adapter (V4) brings its own view type and its own `toElement`, so the mount takes the function
and stays one. `Live.elements` is the one convenience, for the bridge, because it is what forge's shell needs.

### D29.2 — The React binding: React's types, a slice of React as a value, and an inline live binding

The package compiles against **Fable.React.Types** 18.4.0, the package of React's own F# types. It is not Feliz.
Feliz 3 is built on the same package, so an element Feliz builds IS this package's `ReactElement`. Forge's Feliz-built
views therefore cross the bridge with no conversion until the Feliz exit's flip (M6), and after the flip the bridge
still compiles against React's types alone. Fable.Core is pinned at 5.0.0, which the published `Fuaran.Core.*` packages
already require (pinning it lower is a restore downgrade error).

The adapter calls React through `ReactApi`, a record of the five things it uses: `createElement`,
`useSyncExternalStore`, `useRef`, `createRoot` and `hydrateRoot`. It is a value, so the laws run against a stand-in.
The live value, `Live.react ()`, and the browser's timing, `Live.timing ()`, are `inline`. Fable emits an import, or a
global's use, in the module that calls an inline function and not in the one that declares it. So the package's own
modules import nothing (checked: the transpiled package and laws carry no `react` import), and the laws load under
Node with no React installed.

Two alternatives lost. **Import-attributed bindings in a module** (forge's shape) were rejected: the declaring module
imports `react`, so loading the package under Node without React fails, and the laws could not run there. **Depending
on Fable.ReactDom.Types for the root type** was rejected: two members (`render`, `unmount`) do not justify a second
package. `Root` is the adapter's own two-field record.

### D29.3 — How the store and the drain agree: React renders once per drain

The runtime already renders once per drain: its `Render` hook is called once at the end of each drain, with the view
of the model the drain ended on (D26, `machine-one-render-per-drain`). The mount's hook publishes that render to the
`ModelStore`, synchronously, as a new snapshot (model, view, number). React's root reads the store through
`useSyncExternalStore`. One drain is therefore one publish and at most one React render, and React's own batching
merges the publishes of one task. A render whose model is the same instance as the last one published is not
published. The test is the kernel's `ResourceDiff.sameInstance`, the runtime's own test for "the model moved", so
React is never asked to draw a model it has drawn.

A yielded drain renders what it did and schedules its continuation through the host's `Schedule`, which the mount
gives to `Scheduler`. At most one continuation is pending. It runs in a task of its own (`Timing.Task`, a zero-delay
timeout), so React renders the yielded drain's publish before the rest runs. When it runs, it opens a drain only if
the queue still holds work.

The work check is what keeps the continuation from rendering twice. A dispatch after the yield opens a drain of its
own, which may take everything. A continuation that then ran anyway would open an empty drain, and the runtime renders
at the end of every drain, so it would render the model it had just rendered. The law checks this on the runtime's
render count, not only React's, because the store's identity check would hide it from React. An unmount closes the
scheduler, and nothing pending runs.

The view is built when React renders, not when the runtime does: an `ElementView` is a function of the dispatch, and
the root calls it inside its render. A view that only closes over the model costs nothing to publish.

Two alternatives lost:

- **Coalescing publishes per task, or per frame** (forge's `RenderScheduler`, Phase 851). Forge built its F# view
  outside React, so coalescing saved F# work. Here the element is built inside React's render, and React already
  batches the publishes of one task. A second coalescing layer would make "once per drain" "at most once per several
  drains" and save nothing. A per-frame mode for high-rate streams is left to a measured need.
- **Rendering through `root.render` per drain** (a push binding). React answers a render that arrives before
  hydration completes by discarding the server's markup (forge found this, its Phase 931).

### D29.4 — Hydration without a second initial render, and Init once

The first publish is the boot paint: the runtime paints the `Init` model before the boot step runs (rule 15). The mount
attaches the root on that first publish, so the root's first read is the `Init` model. `Init` runs once, in the
runtime's constructor, and the adapter never calls it. With `Hydrate`, the root is attached with `hydrateRoot` and
never rendered again.

The store keeps its first snapshot as the server snapshot (`getServerSnapshot`). A boot step that moves the model
before React hydrates therefore does not mismatch the server's markup. React hydrates the server's model, then renders
the moved one once, which is the behaviour `useSyncExternalStore` specifies. A mount that replaces a hot-reloaded
instance never hydrates, because the unmount emptied the container (`Hydration.choose`).

The alternative that lost was **`getServerSnapshot` reading the current snapshot** (forge's store binding passes
`getSnapshot` for both). Under a boot step that moves the model, React would hydrate the moved model over the server's
markup and report a mismatch. A probe with that line put back fails the boot-step hydration law.

### D29.5 — The element-view bridge, and the mark a governed surface demands

`ElementView<'msg>` is a sealed class over `('msg -> unit) -> ReactElement`: a view that is a React element, built
against the mount's dispatch. `ElementView.ofView` takes forge's present shape, `model -> dispatch -> element`.

`IGovernedView` is the mark a governed surface demands of a view: a value a model can read and a gate can check before
anything is drawn. `Governed.admit` is the admission a governed-surface mount makes: a constraint
`'view :> IGovernedView`, and nothing at run time. `ElementView` is sealed and does not carry the mark, so a component
whose view is an element view does not compile there. The gate builds that case (`-p:FuaranMvuNegative=governed`) and
requires the refusal: the compiler's FS0001, naming `ElementView<Msg>` and `IGovernedView`. This is the same shape as
the gate's requirement that `equality-fails.fsx` fail. A view type that carries the mark is admitted (the laws compile
one).

Two alternatives lost:

- **Equality as the mark** (`'view : equality`, which no element carrying a function has). Equality says nothing
  about whether a model can read the view, and an unrelated record would pass.
- **A phantom surface parameter on the mount.** It marks the mount rather than the view, and the design's sentence is
  about the view ("a component using it cannot mount a governed surface").

The mark lives here because the governed surface does not exist yet (stage V7). When V7 lands, it either adopts
`IGovernedView` and `Governed.admit`, or moves them into `Fuaran.MVU` beside the component if a tree view must carry
the mark without depending on this package. That move is additive and is V7's to make: this phase cannot know the
tree's package graph.

### D29.6 — "After the next paint" as a kind with a performer

`AfterPaint.kind` is a call (`fuaran.mvu.react/after-paint`), answered by the host because its one answer is the
application's own message. `AfterPaint.handle timing answer` is the interpreter's line for it. It is the invocation,
and a performer that asks `Timing.AfterPaint` to call back once the next frame is painted, then delivers `answer` once.
A timing that calls back twice still answers once, and the runtime's one-shot reply token would refuse a second answer
anyway.

The live timing schedules a zero-delay timeout from the next animation frame's callback. That callback runs before the
frame is painted, so the timeout runs after it. The kernel knows nothing about painting: an application registers the
kind and routes its own effect case to `AfterPaint.handle`.

The alternative that lost was **a resource that emits on every frame**: an agent loop waits for one paint, and a
resource it had to start and stop around each wait would be a stream used as a call.

### D29.7 — Hot reload, and how a production build drops it

A mount that names a `HotKey` registers its dispose under it. A later mount under the same key is the application's
entry module evaluated again by the bundler. It unmounts the registered instance and takes its model and last
sequence. It resumes from that model (`Runtime.Resume`, D28.5) when the model's type has the shape it had, and runs
`Init` fresh when it does not.

The shape is `HotReload.shapeOf typeof<'model>`: the type's name, and its record fields or union cases with their
types, recursively to a fixed depth. It is read through FSharp.Reflection, which both hosts carry. Resources restart
from the model at the resumed boot, and the old instance's resources stopped at its unmount, so a reload leaks no
listener (forge's HMR existed to stop that leak, and kept no model). In the browser the registry lives on
`globalThis`, so it survives the reload evaluating this module again.

Everything that does this is compiled only under `DEBUG`, and Fable defines `DEBUG` only in a Debug build. Its CLI
uses Debug in watch mode, which is the development server, and Release otherwise, which is a production build. Without
`DEBUG` the module is the inert one: `enabled` is false, nothing is registered or carried, and no reflection is read.
The surface is the same in both, so an application compiles the same calls either way.

The check that fails the build is in the gate's fable stage. It transpiles the package in Release and fails if any of
its JavaScript carries `Marker` (`fuaran-mvu-react:hot-reload`, the registry's name). It also transpiles the package
in Debug and fails if that does NOT carry it, so the check can fail. An application can run the same search over its
own bundle.

Two alternatives lost:

- **Detecting HMR at run time** (forge's `import.meta.hot` probe). The code ships in every production bundle and is
  merely inert there, where the phase asks for it to be compiled out.
- **Keeping the model whatever its shape** (Fable.Elmish's HMR). A model of the old shape handed to the new `update`
  fails in ways the reload hides.

### D29.8 — The laws, the stand-in, and what was not run in a real browser

`tests/Fuaran.MVU.React.Tests/ReactLaws.fs` holds ten laws against a stand-in for React and the DOM, on .NET and under
Fable on Node (transpiled in Debug, so hot reload is in). They cover:

- render counts per drain, including none for a drain that left the model as it was;
- a yielded drain's continuation, run or skipped;
- unmount revocation;
- hydration with no second initial render and `Init` once, and hydration under a boot step that moves the model;
- the stand-in's own discard check, held to be able to fail;
- the after-paint kind's one answer;
- hot reload, kept and fresh;
- the bridge, and the slice hook.

Four probes each put one line back, and each failed the law it should: the continuation's work check, the first
snapshot as the server snapshot, the performer's one-answer flag, and the store's identity check.

The stand-in models what the adapter relies on:

- A root renders in a task of its own, as a concurrent root does.
- `useSyncExternalStore` subscribes once per component and renders again only when `getSnapshot` answers something
  other than what the component last read.
- A hydrating root reads `getServerSnapshot`, compares what it draws with the markup it adopts, and renders again if
  the store moved past the server snapshot.
- A fresh root over existing markup is reported as a discard.
- A paint runs the callbacks waiting for it.

**Not run in a real browser, and not shown by any law here:**

- the live binding itself (`Live.react`, `Live.timing`, `Live.container`, `Live.elements`);
- React's own scheduling, batching and concurrent rendering;
- that a frame is actually painted before the after-paint timeout runs;
- `hydrateRoot` against real server-rendered HTML;
- a real bundler's hot reload (Vite evaluating the entry module again);
- a production bundle built by an application's bundler (the gate checks the package's Release transpile, not a
  minified bundle).

### D29.9 — What forge's shell asks of the adapter, and §26 question 11

Read at toolup-forge `b57d90b57` (2026-11-17):

- `SDK.Client.fs`: `programWith`, `runStoreBound`, `programLifetimeEffects` and `viewSlicedOver`;
- `AIClientConfig.fs`: `run` and `composerProgram`;
- the fork's React binding and HMR: `Elmish/React.fs` and `Elmish/HMR.fs`.

Each need, and where it is met:

| Forge's shell | Under the adapter |
|---|---|
| `Program.withReactStore store "elmish-app"`, with `ModelStore.create` before the program so the sliced view closes over it | `MountOptions.Store` (a store made before the mount), and `ModelStore.useSelector` ported with its reference semantics; met here |
| `Program.withReactStoreHydrate` (prerendered routes) | `Attach.Hydrate`; met here |
| `ModelStore.useSelector` over a projection (`Client.shellStore store _.Shell`) | the selector composes with the projection; the `ShellStore` type is the shell's, spillover to forge#1355 |
| `Program.withStableDispatch build view`: dispatchers built once per loop | the mount's dispatch is one function for the mount's life, so the shell builds its `Dispatchers` once from it inside its `ElementView`; spillover to forge#1355 |
| `Program.withDispatcherHandle` (`IDispatcher`: `Dispatch`, `IsActive`, `Terminate`) | `Mounted.Dispatch`, `Mounted.Runtime.IsTerminated` and `Mounted.Unmount`; the shell's capture is spillover to forge#1355 |
| the nine `EffectHandle.programLifetime` effects | resources in the component's `Resources`, started by the runtime at boot after the paint and stopped at unmount; the runtime's, with no adapter gap; their rewrite is forge#1355 |
| `withErrorReporter`, `withTrace` | the host's `Report` and `Record` (the runtime's); forge#1355 |
| the fork's `beforeunload` listener that calls `Terminate` | not installed by the adapter: the shell calls `Mounted.Unmount` from its own listener; spillover to forge#1355 |
| the fork's last good element when a view throws (`buildHoldingLastGood`) | not in the adapter: a view that throws inside React's render unmounts the root unless an error boundary holds it; spillover to forge#1355 (an error boundary in the shell, or a later task here if a second consumer needs it) |
| `withReactBatched` (per-frame coalescing) | no call site in the shell; not built (D29.3) |
| the HMR teardown of the previous build | `HotKey`: the adapter's hot reload unmounts the previous instance and keeps the model where the shape allows; met here |

**§26 question 11: does forge's `Prefetch` survive?** As far as the adapter is concerned, no. `Prefetching` is a phase
of the shell's model (`InitPhase`), and its only use is a boolean gate in `update` and the view, which shows the
loading indicator. The adapter needs nothing to support it. The boot paint draws the `Init` model, whatever phase it is
in, before `Init`'s effects run (rule 15), which is the order the present loop keeps and the loading indicator relies
on. Whether the phase survives in the shell's model is the shell's half, forge#1355.

**Premises checked at take:**

- The runtime renders once per drain and exposes no drain hook beyond `Render` and `Schedule`. This held
  (`Runtime.fs`), and the adapter needed no change to the runtime.
- A yielded drain's continuation could render twice. This is true of the runtime as it stands: a continuation opened
  on an empty queue renders. The adapter's scheduler prevents it, and the runtime is unchanged.
- Fable's default configuration drops `DEBUG`. This held for a build and not for watch mode, and that split is exactly
  the development/production line the phase asks for.

## D30 — The browser package: the standard vocabulary as one case, a performer per kind, and the browser host (2026-11-21)

Phase 719 cut `Fuaran.MVU.Browser`, the browser's host package for the runtime (the design's §7.3, §7.4 and §8.6;
stage V2). It holds the standard kinds with their classes, a performer per kind, the interpreter's line for the
vocabulary, and the browser host. It depends on `Fuaran.MVU`, FSharp.Core, `Fuaran.ClientEffects` 0.1.0 (from
nuget.org, one version property) and Fable.Core. It does not depend on `Fuaran.MVU.React`, and the adapter does not
depend on it (D29): an application that wants both takes both. Ten decisions were taken; each records the alternative
that lost.

### D30.1 — The public surface

Namespace `Fuaran.MVU.Browser`, three files in compile order:

- `Kinds.fs`: `StorageArea` (`Local` / `Session`), `StandardEffect` (`Client of ClientEffect`, `ReadStorage`,
  `WriteStorage`, `RemoveStorage`, `After`), `StandardResource` (`Every`), `FileReadFailure`, `StandardAnswer`
  (`Confirmed`, `FileBody`, `Stored`, `Elapsed`, `Ticked`), and the module `Standard`: `Prefix`, the four class names
  (`Reads`, `Writes`, `Navigation`, `UserVisible`), `kinds`, `kindOf`, `EveryKind`, `destinationOf`, `invocation`,
  `resourceInvocation`.
- `Performers.fs`: `Dom` (the browser as a value, D30.5), the module `Performers` (a performer per kind, `handle` and
  `start`) and the module `StandardInterpreter` (`create`, `embed`).
- `Host.fs`: the module `BrowserHost` (`describe`, `create`, `mount`) and the module `Live` (`dom`, `host`).

**The eight client arms are not declared here.** `Client` carries a `ClientEffect` of `Fuaran.ClientEffects` whole,
so the vocabulary is declared once (P27, D11) and this package adds only what is its own: storage and the timers. A
step asks for `Do(Client(WriteToClipboard text))`.

**Considered: a flat union re-declaring the eight arms beside storage and the timer.** It reads one constructor
shorter at a call site and lost on the rule it breaks: a second declaration of the vocabulary, which every change to the
library would have to follow by hand.

**Considered: storage and the timers in `Fuaran.ClientEffects`.** Lost for the reason §7.4 gives the library its own
home: a server emits client effects, and has no business naming a client's storage or its timers. They are this
package's kinds.

**A repeating timer is a resource, not an effect.** An effect is one-shot (§7.2, rules 2 and 3), and a timer that ticks
until it is stopped has many answers and must be cancellable. The model lists `Every(label, milliseconds)` while it
wants the ticks, and the runtime's diff stops the interval when it stops listing it. The one-shot `After` is an
effect.

### D30.2 — The classes each standard kind registers with

Four classes, named by `Standard`:

- `reads`: the kind reads state the model does not hold;
- `writes`: it writes state that outlives the page or leaves it;
- `navigation`: it changes the document or its address;
- `user-visible`: it acts on what the reader sees or is asked outside the view the model draws.

A kind carries every class that applies, so a host's `Refusing` or `ApprovalFor` restriction (D27.2) names what it
means and no more.

| Kind (`fuaran.mvu.browser/…`) | Shape | Classes | Answer authored as | Determinism |
|---|---|---|---|---|
| `WriteToClipboard` | signal | `writes` | — | — |
| `Navigate` | signal | `navigation` | — | — |
| `PushState` | signal | `navigation` | — | — |
| `Focus` | signal | `user-visible` | — | — |
| `Download` | signal | `writes`, `user-visible` | — | — |
| `ReadFileBody` | call | `reads` | foreign (`Untrusted "file"`) | — |
| `Print` | signal | `user-visible` | — | — |
| `Confirm` | call | `user-visible` | the host (`FirstParty`) | — |
| `ReadStorage` | call | `reads` | foreign (`Untrusted "storage"`) | — |
| `WriteStorage`, `RemoveStorage` | signal | `writes` | — | — |
| `After` | call | none | the host | clock |
| `Every` | stream | none | the host | clock |

The client kinds' names are the vocabulary's discriminator (`ClientEffect.kind`), so the kind an effect is decided
under follows from the library's own naming. An invocation's arguments are canonical text. For a client effect, that
text is its wire encoding (`ClientEffect.encode`). For storage and the timer, it is Core's canonical rendering of the
effect's members. A navigation and a download go where their URL goes (`destinationOf`): `Remote` of the origin for an
absolute `http`/`https` URL or a scheme-relative one, and `Local` for a route. A host's egress gate therefore reads a
navigation off-origin as it reads any other.

**Who writes an answer.** A file's body is content from outside the trust boundary, so its answer is foreign. Storage
is too: every script of the origin, and the reader, can write it. A host that restricts what a foreign author's step
may do restricts what follows these reads. A confirmation's answer is the host's. It is a yes or a no from the
browser's own dialogue, carrying no content anyone else wrote. `Trust` has no user case, and Core gains no actor for
one (D20).

Two alternatives lost:

- **`Untrusted "reader"` for a confirmation.** It would put the follow-up of every confirmation under the foreign
  registry, so a host that restricts foreign content would refuse the very thing the reader just confirmed.
- **One class per kind.** A policy naming single kinds is what `Only` already is, and a class is worth having only
  where it groups.

### D30.3 — The performers: one answer or none, and what a performer refuses

Each performer is a one-shot answer (`Reply`, for a call) or none (`Signal`). The runtime revokes a reply's token
after its first answer (D26.6). The two performers that answer later, the file read and `After`, also deliver through a
once-only callback, so a reader or a timer that called back twice does not reach the runtime a second time.

A signal reports nothing, as the vocabulary says. A clipboard write the browser rejects (no permission) is dropped.

**A navigation or a download to a `javascript:` URL is refused.** Such a URL would run script in the page. The
performer throws; the runtime reports it and goes on with the step's next effect. Nothing in the vocabulary needs a
script URL.

### D30.4 — Confirm and ReadFileBody's one answer, and node addresses off a Fuaran-UI tree

**The answer goes through the runtime's reply, not a round trip.** On the wire, a confirmation's token says which
confirmation an answer belongs to, because the wire has no other channel. Here, a performer's answer is accepted as a
new envelope. Its source names the answered kind and the effect's position in its step (`Answer(kind, position)`),
and its cause is that step's sequence. So the runtime delivers the answer to the component that asked, and needs no
token to route it.

**The token is kept, as a label.** `Confirmed(token, yes)` carries the token back unchanged, as `Fuaran.ClientEffects`
D5 says an in-process performer may. A component that raises two confirmations in one step tells the answers apart by
its own label rather than by emission order. `ReadFileBody` has no token on the wire. Its answer carries the node
address and the encoding back, so two reads in one step are told apart the same way.

**A node address is resolved by the host's `Dom.Resolve`.** The live binding looks the address up as an element id
(`document.getElementById`), which is what a React view gives its elements with an `id`. A view that addresses its
nodes another way supplies its own: a renderer's map from node id to element, or a registry of refs the view fills.
The address stays opaque to this package, as `Fuaran.ClientEffects` D4 says it is to the vocabulary. An address that
resolves to nothing makes `Focus` do nothing, and makes `ReadFileBody` answer `Error NoNode`. A node with no selected
file answers `Error NoFile`. An in-process effect is never decoded, so the file read checks the closed encoding set
itself (`ClientEffect.readEncodings`) and answers `Error UnknownEncoding` outside it.

Two alternatives lost:

- **A selector for the address.** It would admit a selector language as input, which D4 of the library rejects.
- **A ref registry owned by this package.** It reaches into the view's world. The resolver is the seam, and the
  Fuaran-UI renderer adapter (V4) supplies its own.

### D30.5 — The DOM as a value, and an inline live binding

The performers call the browser through `Dom`, a record of every call they make. The laws run them against a stand-in
on .NET and under Node, and an application passes `Live.dom ()`. `Live` is `inline`, as the React adapter's live
binding is (D29.2): Fable emits a global's use in the module that calls an inline function, so this package's own
modules use none, and the laws load under Node with no DOM.

**Considered: performers that call the DOM directly, compiled under Fable only.** Lost: nothing could run them
without a browser, so no law could show what a performer does, and the vocabulary-facing half would not be testable
on .NET.

### D30.6 — The interpreter's line, and the design's §26 question 10

`StandardInterpreter.create dom answer` is the whole interpreter of an application whose effect type is
`StandardEffect`. `StandardInterpreter.embed dom answer effectCase resourceCase own` embeds the vocabulary as one case
of an application's own effect type, and of its resource type if it wants the repeating timer. It registers the
standard kinds before the application's own, hands a standard effect to the standard performers and everything else
to the application's interpreter, and keeps that interpreter's hooks. `answer` maps a `StandardAnswer` into the
application's message type, so a performer answers in the application's type (M1).

**§26 question 10: can the line be derived, so that a plain-tier application writes nothing for standard effects?**

- **Yes, for an application whose effects are only the standard ones.** It uses `StandardEffect` as its effect type,
  and `create` is its interpreter. It writes the answer mapping and nothing else, and no derivation could remove that
  mapping, because the message type is the application's.
- **No, for an application with effects of its own.** The line is one projection per case:
  `function Browser e -> Some e | _ -> None`. It is not derived, because deriving it needs the inverse of a union case
  constructor, and F# gives that only through reflection. Fable's reflection over unions is partial, and a reflected
  route is checked by no compiler. One match arm costs less than either.

**Considered: a union owned by this package, `Effect<'own> = Standard of StandardEffect | Own of 'own`.** It makes the
projection derivable. It lost because it fixes the shape of every application's effect type and puts every
application effect one constructor deeper. An application that wants that shape declares it in one line and passes
the projection.

### D30.7 — The browser host

`BrowserHost.create dom clock seed render` is `Host.create` with three of its members replaced:

- **The queue** is the kernel's growing queue (`Queue.growing`, a `DrainBuffer`): the browser's premise is the latch
  alone (D26.2, as amended), so a dispatch made while a step runs is queued and processed in the same drain.
- **`Schedule`** runs a yielded drain's continuation in a task of its own (`Dom.Delay 0`). The runtime schedules it
  once its latch is released. At most one is pending, and it opens a drain only if the queue still holds work. A
  dispatch since the yield may have taken that work, and the runtime renders at the end of every drain, so a
  continuation on an empty queue would render a model it had just rendered (D29.9's finding).
- **`Report`** writes every report that `BrowserHost.describe` words to `Dom.Error` (`console.error` live): a
  performer that threw, a refused step, a resource that could not start or stop, resources that threw. A yield and an
  envelope refused at acceptance are the runtime working as designed, and are not written.

The runtime already hands each committed effect to its performer in emission order and catches a throw per effect, so a
performer that throws is reported and the step's other effects still run, in order; the host decides where the report
goes. `BrowserHost.mount` builds the runtime and starts it. `Live.host render` is the browser host over the live DOM,
with `Date.now()` as the clock and a seed from `crypto.getRandomValues`.

**The continuation's work check repeats the React adapter's `Scheduler`.** It was kept as a second copy for three
reasons:

- The two packages are siblings with no dependency between them (D29).
- The React mount replaces `Schedule` with its own, so in a React application only one runs.
- The check's proper home is the runtime: a continuation that opens a drain only when the queue holds work. That
  changes `Runtime.fs`, which is held to the step machine's model, and it is not this phase's change.

If a third host needs the check, the runtime takes it (the rule of three), and both copies go.

### D30.8 — Fable only where it touches the DOM

`Kinds.fs`, `Performers.fs` and `BrowserHost` touch no DOM: they compile, and are tested, on .NET and under Fable.
`Live` is the only code that touches the DOM. It compiles on .NET because it is inline over Fable's emit, and calling
it there fails, since .NET has no DOM. So it is Fable-only by use, not by a compile symbol. `STABILITY.md` records the
split.

**Considered: `Live` under `#if FABLE_COMPILER`.** Lost: an application module shared by a .NET server and a Fable
client, whose browser entry point calls `Live`, would not compile on .NET. The React adapter's `Live` made the same
choice (D29.2).

### D30.9 — The laws, the stand-in, and what was not run in a real browser

`tests/Fuaran.MVU.Browser.Tests/BrowserLaws.fs` holds twenty laws on .NET and under Fable on Node, and one more under
Fable only. They cover:

- every kind registered once, named by the vocabulary's discriminator, with the classes and shapes of D30.2;
- the invocations' arguments and destinations;
- the standard effects as values under `Fuaran.MVU.Testing`'s scripted interpreter, embedded as one case of an
  application's effect type;
- `embed`'s routing and its kinds;
- each performer against the stand-in, and each answer with its author;
- the repeating timer started and stopped by the diff;
- a kind the gate refuses for the author, outside the author's registry, or not registered by the interpreter,
  performing nothing;
- the host's queue, a yielded drain's continuation run and skipped, and a throwing performer reported with the step's
  other effects in order;
- under Fable only, the live binding against stubbed globals.

Two probes each put one line back: `Navigate` registered as `writes`, and the `javascript:` refusal removed. They
failed the three laws they should (the class table, the gate's refusal by class, the throwing performer).

**Not run in a real browser, and not shown by any law here:**

- the live binding against a real DOM (the Fable-only law shows only that the emitted calls reach stubbed globals with
  their arguments in place);
- the clipboard's permission prompt and its promise;
- `FileReader` on a real selected file;
- real timer timing;
- `window.open`'s handling of `noopener,noreferrer`;
- a download's save prompt;
- the print dialogue.

### D30.10 — The Core pin moves to 0.44.1, and the premises checked at take

`Fuaran.ClientEffects` 0.1.0 references `Fuaran.Core.Wire` 0.44.1, and this repository pinned Core at 0.43.0. The
browser package's closure would therefore have mixed Wire 0.44.1 with Function, Ops, Tree and OpStream 0.43.0, which
were compiled against Wire 0.43.0. Wire's `Json.fs` changed substantially between the two. The one property,
`FuaranCoreVersion`, moved to 0.44.1, so the closure holds one Core version.

- The whole existing suite passed on 0.44.1: the Expecto host, the React laws, and the gate and session laws.
- The two Core theorem files the ladder inherits are byte-identical at v0.44.1: `git diff v0.43.0 v0.44.1` over
  `proofs/Capability.fst` and `proofs/Chain.fst` is empty. So `core-join-is-max` and `session-chain-inherited` stand as
  they state, at v0.43.0.
- The proof kit's pin (`proofs/proofs-kit.json`, 0.43.0) is a separate pin, and is unchanged.

**Premises checked at take:**

- **`Fuaran.ClientEffects` 0.1.0 restores from nuget.org.** It held.
- **The runtime already reports a throwing performer and goes on with the next effect.** It held: `Runtime.perform`
  catches per effect. The host adds only where the report goes.
- **A continuation on an empty queue renders again.** It held, as D29.9 found, so the host checks for work.
- **The design's "the token is unnecessary off the wire".** This held for routing: the runtime's reply routes the
  answer. It did not hold for telling two confirmations apart, so the token rides back in the answer (D30.4).

## D31 — The public repository is a fresh cut without the prototype (2026-11-22)

D1 kept the repository private at its cut because the design prototype under `spike/` carried ports of existing
application code, and D4 said the public repository would be a fresh history that never contained them. This
repository is that cut: one commit of the library, its tests, its proofs and its documents as they stood after
Phase 719, with `spike/` left out rather than rewritten. The prototype was evidence for the design, not part of the
implementation; no package was built from it, and the earlier history that holds it is archived privately.

What changes with it:

1. **The gate no longer runs the prototype's scripts.** `run.ps1`'s test stage ran each `spike/*.fsx` with `dotnet
   fsi` and checked that one of them failed to compile; that step is gone, and the format stage checks `src/` and
   `tests/` only. Every law, differential and proof the library is held to is unchanged.
2. **Earlier entries still name `spike/` files.** D1, D4, D6, D19, D21, D23, D24 and D26 cite the prototype or its
   scripts as the evidence for a finding. Those citations are kept as written: they record where the evidence was when the decision was made, and
   the finding each one supports is carried by the tests and the proofs in this repository.
3. **Publication is a `v*` tag.** `.github/workflows/publish-packages.yml` gates the tagged commit (with the proof
   leg, which needs the prover, run in the release's local gate instead), packs the six packages at the one
   `<Version>`, and pushes them to nuget.org through Trusted Publishing.
