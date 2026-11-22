# Fuaran.MVU — API stability

**Status:** pre-1.0. The version is single-sourced from `<Version>` in `Directory.Build.props`; this
document records what each version holds, by class, and what the kernel claims at which level.

## Versioning policy

Every package this repository produces ships at the one `<Version>`. A change to a package's public
contract ships on a version AHEAD of every version that has been tagged. The standing `<Version>` is a
**draft slot** while it is untagged: an additive change rides it (its entry grows here, the number does
not move), and a change of a higher class than the draft already carries advances it. A draft is not
releasable by default: the release is a separate, recorded act. 0.1.0 is the first release.

## 0.1.0 — released 2026-11-22 as `v0.1.0`

The first slot. Nothing before it was released. Phase 646 opened it with one package; Phase 647 added two
(an additive change, which rides the draft). Phase 648 extended `Fuaran.MVU` with the gate over Core's registry,
the grant and the principal-to-actor mapping, and took the package's first `Fuaran.Core.*` references; the one
verb whose shape changed within the draft, `Runtime.Approve`, is recorded in `DECISIONS.md` D27.4 (the slot is
untagged and unpinned, so there is no consumer contract it breaks). Phase 649 added a fourth package,
`Fuaran.MVU.Session` (additive, riding the draft), and changed `Fuaran.MVU`'s record seam within the draft: an
answer names its effect's position, a step record carries the grant it was decided with, and a runtime can resume
from a restored log (`DECISIONS.md` D28.6). Phase 720 added a fifth package, `Fuaran.MVU.React` (additive, riding
the draft), and touched no existing package's surface. Phase 719 added a sixth, `Fuaran.MVU.Browser` (additive, riding
the draft), touched no existing package's surface, and moved the one Core pin from 0.43.0 to 0.44.1, the Core version
`Fuaran.ClientEffects` references (`DECISIONS.md` D30.10).

### `Fuaran.MVU.Kernel` (Phase 646, `DECISIONS.md` D25) — new

FSharp.Core only. Namespace `Fuaran.MVU.Kernel`.

| Surface | What it is |
|---|---|
| `DrainBuffer<'item>(capacity: int)` | The queue for single-threaded hosts and for .NET under the runtime's lock. `Push: 'item -> unit` (never refused; grows), `Pop: unit -> 'item option`, `Take: unit -> 'item` (refuses an empty buffer with an exception), `Count`, `IsEmpty`, `Capacity` (the slots held now), `static MinimumCapacity = 2` |
| `FixedRing<'item>(capacity: int)` | The queue for a host that must refuse: a server connection, a device. `Push: 'item -> bool` (`false` when full, and nothing changed), `Pop: unit -> 'item option`, `Count`, `IsEmpty`, `IsFull`, `Capacity` (the configured capacity, floored), `static MinimumCapacity = 1` |
| `ResourceDiff.Plan<'k, 'd>` | `{ Stops: 'k list; Keeps: 'k list; Starts: ('k * 'd) list }`, each in key order; a key in both `Stops` and `Starts` is a restart |
| `ResourceDiff.isEmpty` | `Plan -> bool`: nothing to stop and nothing to start |
| `ResourceDiff.plan` | `running: Map<'k, 'd * 'h> -> held: Map<'k, 'd> -> wanted: Map<'k, 'd> -> Plan<'k, 'd>` when `'k: comparison` and `'d: equality` |
| `ResourceDiff.apply` | `stop: ('k -> 'h -> unit) -> start: ('k -> 'd -> 'h option) -> running -> held -> wanted -> Plan -> Map<'k, 'd * 'h> * Map<'k, 'd>`: every stop, then every start, each in key order; a start answering `None` holds the key |
| `ResourceDiff.sameInstance` | `'model -> 'model -> bool`: reference equality, the one condition under which a host may skip the diff |

Neither queue is thread-safe: one operation at a time is the host's premise.

### `Fuaran.MVU` (Phase 647, `DECISIONS.md` D26; the gate Phase 648, D27) — new

References `Fuaran.MVU.Kernel`, FSharp.Core, and the published `Fuaran.Core.Function` and `Fuaran.Core.AiSurface`
packages (one pin, `FuaranCoreVersion`, 0.44.1). Namespace `Fuaran.MVU`.

| Surface | What it is |
|---|---|
| `Step<'msg, 'effect>` | `Do of 'effect` or `Then of 'msg`, with `op_Implicit` from an effect; `Step.map`, `mapAll`, `bind`, `effects`, `followUps`. `NoEffect`, `NoResource` are the placeholders |
| `Stamp` | `{ At: int64; Sequence: int64; Seed: uint64 }`; `Stamp.freshId: int -> Stamp -> string` (16 hex characters), `forChild: string -> Stamp -> Stamp`, `forFollowUp: int -> Stamp -> Stamp`, `forTest: int64 -> Stamp`; the `Seed` module (`mix`, `ofSequence`, `forChild`, `forFollowUp`, `fresh`) |
| `Principal` | `User of id` / `Host` / `Agent of model * version * id` / `Foreign of source`; `Principal.actorId` (injective, a prefix per case); `Principal.toActor: string -> string -> Principal -> Fuaran.Core.Actor` (a user is `Human`; the host, an agent and a foreign source are `Agent`, ids prefixed per case; injective) |
| `ResourceKey` | `ResourceKey of string list`; `constant`, `named`, `prefix`, `segments` |
| `Source`, `Envelope<'msg>` | `External` / `Answer of kind * effect` (the answered effect's position in its step's emission order, Phase 649) / `FromResource of key * instance` / `FromRuntime` / `Boot`; `{ Sequence; Msg; Author; Source; Cause; Stamp }` |
| `Component<'flags, 'model, 'msg, 'effect, 'resource, 'view>` | `{ Init: 'flags -> 'model * Step list; Update: Stamp -> 'msg -> 'model -> 'model * Step list; Resources: 'model -> Map<ResourceKey, 'resource>; View: 'model -> 'view }`; `Component.plain` (no stamp), `lift`, `liftWith`, `prefixResources`; `Lifted<…>` |
| `LegacyEffect<'msg>` | A sealed class over `LegacySite(site)`: equal by site label; `Run: ('msg -> unit) -> unit` |
| `EffectKind` and friends | `{ Id; Shape; Classes; Determinism; Answers; Replies }`, `Shape` (`Call` / `Signal` / `Stream`), `DeterminismSource`, `Trust` (`FirstParty` / `Untrusted of who` / `FromModel of model * version * id` — a reply to a model call is the agent's), `Invocation { Kind; Args: string option; Destination }`, `Destination` (`Local` / `Remote of origin`) |
| `Performance<'msg>` | `Reply of (('msg -> unit) -> unit)` (one answer) or `Signal of (unit -> unit)` (none) |
| `Handled<'msg>`, `Started<'msg>` | `{ Invocation; Perform }`, `{ Invocation; Start: ('msg -> unit) -> IDisposable }`; `Handled.mapMsg`, `Started.mapMsg` |
| `Refusal<'effect>`, `NotAccepted` | `Denied` / `UpdateThrew` / `ChainTooLong` / `Unregistered` / `PerformerThrew` / `Stale` / `Rejected` / `Cancelled`; `Terminated` / `Revoked` / `QueueFull` |
| `Interpreter<'msg, 'effect, 'resource>` | `{ Kinds; Effect; Resource; Refused; Lost; DrainEnded }`; `Interpreter.create`, `tryKind` |
| `Decision`, `Proposal<'effect, 'resource>`, `Decide<…>` | `Allow` / `NeedsApproval` / `Deny of reason`; `{ Author; Sequence; At: int64; Effects: (Invocation * 'effect) list; Starts: (ResourceKey * Invocation * 'resource) list; Stops; Grant: Grant option }` (`At` and `Grant` Phase 648); `Proposal.invocations`, `Proposal.request` (the injective request identity, Core's field encoding); `Decide.allowAll` |
| `Grant` | `{ Parked: int; Request: string; Approver: Principal; ExpiresAt: int64; Generation: int }`; `Grant.mint: int -> Proposal -> Principal -> int64 -> int -> Grant`. Single use; presented through `Runtime.Approve` only |
| `DecisionContext`, `Restriction` | `{ Actor; Grant: Grant option; At; Generation }` — what Core's gates read; `Only of kinds` / `ApprovalFor of classes` / `Refusing of classes` / `Gated of PolicyGate<DecisionContext, Capability, (string * string) list>` |
| `Gate<'effect, 'resource>` | `{ Kinds; Host: CapabilityRegistry<DecisionContext>; Restrictions: Principal -> Restriction list; Policy: Principal -> Proposal -> PolicyDecision; Generation }`; `Gate.create kinds hostGates restrictions policy` (generation one; a repeated kind id refused), `Gate.rebuild previous …` (generation plus one), `Gate.allowAll`, `Gate.capabilityOf`, `Gate.bindings`, `Gate.classesOf`, `Gate.grantVerdict`, `Gate.registryOf`, `Gate.uncovered`, `Gate.decideFor`, `Gate.decide: Gate -> Decide`, `Gate.decideUnder: Gate -> Principal list -> Decide`, `Gate.mount: Gate -> Interpreter -> Result<Decide, string list>`, `Gate.grant: Gate -> ParkedStep -> Principal -> int64 -> Grant`; `Gate.ArgsAddress`, `Gate.DestinationAddress` |
| `Chain<'model, 'effect>`, `ChainOutcome`, `Chain.compute`, `Chain.continueFrom` | The chain as a pure function: `{ Model; Effects; Messages }`, `Computed` / `TooLong of limit` / `Threw of exn` |
| `Fuel`, `Queue<'item>` | `{ Chain: int; Drain: int; Log: int }` (`Fuel.defaults` = 256 / 1024 / 1024; Phase 716 added `Log`, the step log's ring bound, zero keeping none); `Growing of DrainBuffer` / `Bounded of FixedRing` (`Queue.growing`, `bounded`) |
| `Host<'model, 'msg, 'effect, 'resource, 'view>` | `{ Clock; Seed; Render; Schedule; Report; Record; Decide; Fuel; Queue; OnTerminated }`; `Host.create clock seed render` (`Record` is `ignore`: no sink, nothing written). `Record` is the session seam |
| `StepOf<'msg>`, `StepOutcome`, `StepRecord<'msg>`, `Report<…>` | `BootStep of Stamp` / `Step of Envelope` (`StepOf.sequence` of a boot is its stamp's); `Committed` / `Parked of id` / `Refused of reason`; `{ Of; Outcome; Grant: Grant option }` (`Grant` Phase 649: the grant a step was decided with); the reports |
| `Runtime<'flags, 'model, 'msg, 'effect, 'resource, 'view>(comp, interpreter, host, flags)` | `Start`, `Dispatch: 'msg * Principal -> Result<int64, NotAccepted>`, `Preview: Stamp * 'msg * Principal -> Preview`, `Approve: Grant -> bool` (Phase 648, was `int -> bool`: the parked step the grant names is resolved once, the chain recomputed, the request checked against the grant's — `Stale` otherwise — and the step decided again with the grant presented), `Reject: int -> bool`, `Terminate`; `Runtime.Resume(comp, interpreter, host, model, steps, after)` (Phase 649: boots from a restored model with `steps`, at sequence `after + 1`); observables `Model`, `Renders`, `Yields`, `RefusedAtAcceptance`, `Refusals`, `IsTerminated`, `Running`, `InstanceOf`, `Held`, `Parked`, `Log` (Phase 716: the most recent `Fuel.Log` records, oldest first), `LogDropped` (Phase 716: records dropped to stay within the bound), `Sequence` |
| `Replay.run` | `Component -> 'flags -> StepRecord list -> 'model`, nothing performed; a log from a fresh boot (a resumed boot is the session's) |

Not thread-safe: the host premise per kind of host is `DECISIONS.md` D26.2.

### `Fuaran.MVU.Session` (Phase 649, `DECISIONS.md` D28) — new

References `Fuaran.MVU`, FSharp.Core and the published `Fuaran.Core.OpStream` package (the one pin,
`FuaranCoreVersion`, 0.44.1). Nothing in `Fuaran.MVU` references it. Namespace `Fuaran.MVU.Session`.

| Surface | What it is |
|---|---|
| `Disclosure`, `Recorded`, `WithheldField`, `MessageCodec<'msg>` | `Shown` / `Withheld` / `Secret`; `Recorded.Clear of value` / `Recorded.Committed of commitment`; `{ Sequence: int64; Path; Commitment }`; `{ Fields: 'msg -> (string * string) list; Read: (string * string) list -> Result<'msg, string> }` |
| `Entry` and its parts | `Entry.Step of StepEntry` / `Entry.Lost of sequence * LostReason` (`Secret` / `Codec`); `StepEntry { Stamp; Author; Source: EntrySource; Cause; Message: (string * Recorded) list; Boot: EntryBoot option; Outcome: EntryOutcome; Grant: GrantEntry option }`; `EntrySource`, `EntryOutcome`, `EntryBoot` (`Init` / `Resumed of Settled list`), `Settled` (`Resent` / `Interrupted` / `Cancelled`), `GrantEntry` |
| `EntryCodec.encode`, `EntryCodec.decode` | The canonical form `{"f":[…]}`, written with Core's string spelling and read with Core's line reader |
| `Hmac.sha256`, `Hmac.hex`, `CommitmentKey`, `Commitment` | HMAC-SHA-256 over Core's SHA-256; `CommitmentKey.OfBytes: byte[] -> Result<CommitmentKey, string>` (at least 32 bytes; never printed); `Commitment.take: CommitmentKey -> int64 -> string -> string -> string`, `Commitment.opens`, `Commitment.tag` |
| `LogStream` | `witness: StreamWitness<Entry, unit, unit>`, `create: HashFn -> Stream<Entry, unit, unit>`, `verify: HashFn -> OpRecord<Entry> list -> bool` (Core's `OpStream.verifyChain`) |
| `Store` | `{ Append: OpRecord<Entry> -> unit; Records: unit -> Result<OpRecord<Entry> list, string> }`; `Store.inMemory` (the default; the browser's), `Store.lines: HashFn -> (string -> unit) -> (unit -> string) -> Store` (Core's JSONL, read back verified), `Store.file: HashFn -> string -> Store` (.NET only) |
| `SessionOptions<'msg>` | `{ Name; Version; Codec; Disclose: string -> string -> Disclosure; Key; OnWithheld: WithheldField -> string -> unit; HashFn }`; `SessionOptions.create name version codec key` (every field shown, no vault, SHA-256) |
| `Session<'msg>` | `Session.Open: SessionOptions * Store -> Result<Session, string>` (continues the store's chain; refuses one that does not verify); `Record: StepRecord -> unit` (the sink: `{ host with Record = session.Record }`), `Records`, `Head`, `Count`, `Options` |
| `Paths` | The paths the disclosure function is handed: `message`, `answerKind`, `resourceKey`, `reason`, `grantRequest`, `settled` |
| `Folded<'model, 'effect>`, `ReplayFault` | `{ Model; Outstanding: Map<int64 * int, 'effect>; Parked: Map<int64, int>; Last: int64 }`; `Lost` / `Withheld` / `RevealMismatch` / `Unreadable` / `UpdateThrew` / `NotOutstanding` |
| `RestoreOptions<'msg, 'effect>` | `{ Idempotent: Set<string>; Interrupted: 'effect -> 'msg option }` |
| `Log` | `initial`, `witness` (the component as Core's stream witness), `stream` (for Core's snapshots and compaction), `replay: Component -> 'flags -> Interpreter -> SessionOptions -> (WithheldField -> string option) -> OpRecord<Entry> list -> Result<Folded, int * ReplayFault>` (Core's `OpStream.replay`; performs nothing, reads no clock, decides nothing), `declareIdempotent: Interpreter -> string list -> Result<Set<string>, string list>`, `restore: Component -> 'flags -> Interpreter -> Host -> RestoreOptions -> Session -> Folded -> Runtime` |

### `Fuaran.MVU.Testing` (Phase 647, `DECISIONS.md` D26.7) — new

References `Fuaran.MVU` and FSharp.Core; no test framework. Namespace `Fuaran.MVU.Testing`.

| Surface | What it is |
|---|---|
| `Answer<'msg>` | `Immediate of 'msg` / `Outstanding` / `NoAnswer`: how the scripted interpreter answers an effect |
| `HarnessOptions<'msg, 'effect, 'resource>` | `{ Kinds; Describe; DescribeResource; Answers; Decide; Clock; Seed; Fuel }`; `HarnessOptions.defaults`, `testKind`, `countingClock` |
| `Harness<…>` | `Harness.start comp flags`, `Harness.startWith options comp flags`; `Runtime`, `Model`, `Send`, `SendAs`, `Performed`, `ClearPerformed`, `Outstanding`, `Answer`, `AnswerWhere`, `Started`, `Stopped`, `Emit`, `EmitterOf`, `Views`, `Reports`, `ClockReads`, `Preview` |
| `Law.deterministic` | `Component -> (Stamp * 'msg * 'model) list -> string list`: `Update` twice on each input gives the same result |
| `Law.replayReproduces` | `Component -> 'flags -> StepRecord list -> 'model -> string list` |

### `Fuaran.MVU.React` (Phase 720, `DECISIONS.md` D29) — new

References `Fuaran.MVU`, FSharp.Core, Fable.Core 5.0.0 and Fable.React.Types 18.4.0 (React's types; not Feliz).
Namespace `Fuaran.MVU.React`.

| Surface | What it is |
|---|---|
| `IGovernedView`, `Governed.admit` | The mark a governed surface demands of a view; `admit: Component<…, 'view> -> Component<…, 'view>` when `'view :> IGovernedView`, so a component whose view is an `ElementView` does not compile there (D29.5) |
| `ElementView<'msg>` | Sealed; `Build: ('msg -> unit) -> ReactElement`. `ElementView.ofElement`, `ofView: ('model -> ('msg -> unit) -> ReactElement) -> 'model -> ElementView<'msg>`, `toElement` |
| `ReactApi`, `Ref`, `Root` | The slice of React the adapter calls: `{ CreateElement; UseSyncExternalStore; UseRef; CreateRoot; HydrateRoot }`; `Ref = { mutable current: obj }`; `Root = { Render; Unmount }` |
| `StoreSnapshot<'model, 'view>`, `ModelStore<'model, 'view>` | `{ Model; View; Version }`; the store: `Snapshot`, `First` (the server snapshot), `Published`, `Subscribers`, `IsClosed`, `Subscribe`, `GetSnapshot`, `GetServerSnapshot`, `Publish: 'model * 'view -> bool` (not after `Close`, not for the same model instance), `Close` |
| `RootProps`, `StoreRoot.root` | The root component (`Func<obj, ReactElement>`) and its props |
| `ModelStore.useSelector`, `ModelStore.refEquals` | `ReactApi -> ModelStore -> ('model -> 'slice) -> ('slice -> 'slice -> bool) -> 'slice`: a hook answering the previous slice while `equals` holds |
| `Timing` | `{ Task: (unit -> unit) -> unit; AfterPaint: (unit -> unit) -> unit }` |
| `Scheduler` | `Scheduler(timing)`: `Schedule: (unit -> bool) -> (unit -> unit) -> unit` (one pending; skipped when the check fails), `Close`, `Ran`, `Skipped`, `Pending` |
| `AfterPaint` | `Kind = "fuaran.mvu.react/after-paint"`, `kind: EffectKind` (a call, answered by the host), `handle: Timing -> 'msg -> Handled<'msg>` (one answer, after the next paint) |
| `Attach`, `Hydration` | `Fresh` / `Hydrate`; `Hydration.choose: Attach -> bool -> Attach`, `Hydration.attach: ReactApi -> obj -> Attach -> ReactElement -> Root` |
| `Carried`, `HotReload` | `{ Model: obj; Sequence; Shape }`; `enabled`, `shapeOf: Type -> string`, `take`, `register`, `forget`, and `Marker` in a `DEBUG` build only; inert in any other (D29.7) |
| `Binding`, `MountOptions<…>`, `Mounted<…>`, `Mount` | `{ React; Timing }`; `{ Container; Attach; ToElement; Author; Host; Store; HotKey; Shape }`; `Runtime`, `Store`, `Scheduler`, `Resumed`, `Dispatch: 'msg -> Result<int64, NotAccepted>`, `Unmount`; `Mount.options`, `Mount.mount: Binding -> MountOptions -> Component -> Interpreter -> 'flags -> Mounted` |
| `Live` | Inline, for Fable only: `react`, `timing`, `binding`, `container`, `shapeOf<'model>`, `elements` (mount a bridged component by element id) |

### `Fuaran.MVU.Browser` (Phase 719, `DECISIONS.md` D30) — new

References `Fuaran.MVU`, FSharp.Core, `Fuaran.ClientEffects` 0.1.0 (its own pin, `FuaranClientEffectsVersion`) and
Fable.Core 5.0.0. Not `Fuaran.MVU.React`, and the adapter does not reference it. Namespace `Fuaran.MVU.Browser`.

| Surface | What it is |
|---|---|
| `StandardEffect` | `Client of ClientEffect` (the eight client arms, declared only in `Fuaran.ClientEffects`) / `ReadStorage of StorageArea * key` / `WriteStorage of StorageArea * key * value` / `RemoveStorage of StorageArea * key` / `After of label * milliseconds`; equality, so a test compares effects as values |
| `StandardResource` | `Every of label * milliseconds`: the repeating timer, a resource |
| `StorageArea`, `FileReadFailure` | `Local` / `Session`; `NoNode` / `NoFile` / `UnknownEncoding` / `ReadFailed of reason` |
| `StandardAnswer` | `Confirmed of token * yes` / `FileBody of nodeId * encoding * Result<string, FileReadFailure>` / `Stored of StorageArea * key * string option` / `Elapsed of label` / `Ticked of label` |
| `Standard` | `Prefix = "fuaran.mvu.browser/"`; the classes `Reads`, `Writes`, `Navigation`, `UserVisible`; `kinds: EffectKind list` (thirteen, each with its classes, D30.2); `kindOf: StandardEffect -> string`; `EveryKind`; `destinationOf: string -> Destination`; `invocation: StandardEffect -> Invocation`; `resourceInvocation: ResourceKey -> StandardResource -> Invocation` |
| `Dom` | `{ WriteClipboard; Assign; OpenBlank; PushState; Resolve; Focus; Download; ReadFile; Print; Confirm; StorageGet; StorageSet; StorageRemove; Delay; Repeat; Error }`: the browser as the performers and the host use it |
| `Performers` | One performer per kind (`writeToClipboard`, `navigate`, `pushState`, `focus`, `download`, `readFileBody`, `print`, `confirm`, `readStorage`, `writeStorage`, `removeStorage`, `after`), each answering `Performance<'msg>`; `handle: Dom -> (StandardAnswer -> 'msg) -> StandardEffect -> Handled<'msg>`; `start: Dom -> (StandardAnswer -> 'msg) -> ResourceKey -> StandardResource -> Started<'msg>` |
| `StandardInterpreter` | `create: Dom -> (StandardAnswer -> 'msg) -> Interpreter<'msg, StandardEffect, StandardResource>`; `embed: Dom -> (StandardAnswer -> 'msg) -> ('effect -> StandardEffect option) -> ('resource -> StandardResource option) -> Interpreter<'msg, 'effect, 'resource> -> Interpreter<'msg, 'effect, 'resource>` (the standard kinds first) |
| `BrowserHost` | `describe: Report -> string option`; `create: Dom -> (unit -> int64) -> uint64 -> ('view -> unit) -> Host` (the growing queue, a continuation in a task of its own when work remains, reports to `Dom.Error`); `mount: Host -> Component -> Interpreter -> 'flags -> Runtime` (built and started) |
| `Live` | Inline, for Fable only: `dom` (a node address is an element id), `host` (the browser's clock and a random seed) |

**Where it runs (D30.8).** `Kinds.fs`, `Performers.fs` and `BrowserHost` touch no DOM: they compile and are tested on
.NET and under Fable, so an application's tests compare its standard effects as values and run its performers against
a stand-in with no browser. `Live` is the only code that touches the DOM. It compiles on .NET, being inline over
Fable's emit, but runs only under Fable in a browser.

## What is proved, tested, assumed and not claimed

The ladder is [`proofs.json`](proofs.json), one row per claim; this table is its summary and names the
rows. The `Proofs.Ladder` test family holds every row to the tree.

| Part | Proved (`proofs.json` rows) | Tested | Assumed | Not claimed |
|---|---|---|---|---|
| Drain buffer | `drain-fifo`, `drain-reset-agrees-with-empty`, `drain-yield-drops-nothing`, `drain-cleared-outside-window`, `drain-placeholder-unobserved`, `drain-take-is-pop` | `drain-agrees-with-kernel`, `kernel-laws-on-both-hosts`, `models-fingerprint-across-hosts` | `extractor-and-compiler-trusted`, `host-serialises-the-queues` | Anything about two operations at once; what the host does while yielded |
| Fixed ring | `ring-bounded-fifo`, `ring-push-refused-iff-full`, `ring-refused-push-unchanged`, `ring-placeholder-unobserved`, `ring-cleared-outside-window` | `ring-agrees-with-kernel`, `kernel-laws-on-both-hosts`, `models-fingerprint-across-hosts` | the same two | Anything about two operations at once; what a host tells a refused producer |
| Keyed diff | `diff-start-spec`, `diff-stop-spec`, `diff-keep-spec`, `diff-restart-spec`, `diff-keys-after-are-wanted`, `diff-held-unchanged-not-started`, `diff-plan-twice-empty`, `diff-sequence-shape`, `diff-identity-skip-agrees`, `diff-sorted-is-a-map` | `diff-agrees-with-kernel`, `kernel-laws-on-both-hosts`, `models-fingerprint-across-hosts` | `extractor-and-compiler-trusted`, `start-outcome-is-a-function`, `key-order-is-irreflexive` | Liveness tokens, level-triggering and when the skip is taken are the step machine's |
| The chain | `chain-messages-bounded`, `chain-fuel-monotone`, `chain-whole` | `step-machine-agrees-with-runtime`, `runtime-rules-on-both-hosts`, `models-fingerprint-across-hosts` | `update-is-total` | Which seed each follow-up reads (the stamp laws test it, `prototype-laws-on-both-hosts`) |
| The step machine | `machine-exactly-once-in-order`, `machine-step-whole-or-nothing`, `machine-revoked-instance-refused`, `machine-effects-handed-once-in-order`, `machine-one-render-per-drain`, `machine-yield-drops-nothing`, `machine-terminated-absorbing`, `machine-full-queue-refuses`, `machine-invariants-from-initial` | `step-machine-agrees-with-runtime`, `runtime-rules-on-both-hosts`, `prototype-laws-on-both-hosts`, `models-fingerprint-across-hosts` | `update-is-total`, `decision-is-a-function` (realised by the gate), `performer-is-the-hosts`, `host-serialises-the-queues` | The boot, the identity skip, `Resources` throwing, a held start, `DrainEnded`, synchronous acceptances inside a step, rejection, the approval's lookup and single use, the stamp: tested, not proved (`step-machine-boundary`) |
| The gate | `gate-permitted-or-granted`, `gate-restricted-never-looser`, `gate-unregistered-refused`, `gate-two-ceilings`, `gate-grant-covers-one-step` | `gate-agrees-with-model`, `gate-laws-on-both-hosts`, `models-fingerprint-across-hosts` | `core-join-is-max` (Core's `join_is_max`, `policy_join_monotone_in_context`, `with_gate_tightens_in_context` at v0.43.0), `grant-identity-injective` (Core's D249) | The approver (Core's self-approval refusal, tested), the guidance text, the destination as a field (it rides in the arguments), the runtime's lookup of the parked step; anything about a host's own `Decide`; that an approver understands what they approve |

| The session log | — (no model of its own: the chain is Core's) | `session-laws-on-both-hosts` (replay performs nothing and reproduces the model, ids and times; the withheld and secret values absent from the log's bytes; no sink, nothing written; decisions and grants replayed; restore) | `session-chain-inherited` (INHERITED from Core's proved chain at v0.43.0: integrity and attribution, `intact_verifies`, `tamper_detected`, `tamper_actor_detected`, `compact_preserves_verify`), `update-is-total` | Tamper-evidence of a log held only by the client (an attestable log needs an anchored head, `DECISIONS.md` D28.8); a snapshot's disclosure (none is written, D28.9); telling a performer that threw from an interrupted one at restore (D28.5) |
| The React adapter | — (no model: it decides nothing the runtime decides) | the React laws (`tests/Fuaran.MVU.React.Tests`, on .NET and under Node): one publish per drain, a yielded drain's continuation, unmount revocation, hydration with `Init` once, the after-paint kind's one answer, hot reload, the bridge, the slice hook; the negative build (a governed admission of an element view must not compile); a Release transpile carries no hot reload | the runtime's `machine-one-render-per-drain`; React honours `useSyncExternalStore`'s contract | Anything run in a real browser: the live binding, React's scheduling, the paint, real server markup, a bundler's hot reload, an application's minified bundle (`DECISIONS.md` D29.8) |
| The browser package | — (no model: it decides nothing the gate decides) | the browser laws (`tests/Fuaran.MVU.Browser.Tests`, on .NET and under Node): every kind registered once with its classes, the invocations, the standard effects as values under the scripted interpreter, each performer against a DOM stand-in, a refused or unregistered kind performing nothing, the host's queue, continuation and reports; under Node only, the live binding against stubbed globals | the gate's rows; the runtime's `machine-effects-handed-once-in-order`; the browser honours the DOM calls `Live` makes | Anything run in a real browser: the live binding against a real DOM, the clipboard's permission, `FileReader`, timer timing, `window.open`'s `noopener`, a save prompt, the print dialogue (`DECISIONS.md` D30.9) |

The prover, its pin and the leg's posture are the `proof-leg` row and [`proofs/README.md`](proofs/README.md).
