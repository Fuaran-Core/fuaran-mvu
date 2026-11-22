# Fuaran.MVU

A model-update-view runtime whose message loop is formally proved, whose components are pure, and
whose effects are values.

**Status: the runtime, its gate, its session log, its React adapter and its browser package.** Six packages. `Fuaran.MVU.Kernel` holds the proved parts
the loop is built from: a drain buffer (the queue for single-threaded hosts), a fixed-capacity ring (the
queue for hosts that must refuse) and a keyed resource diff. `Fuaran.MVU` is the runtime: the component,
the step type with its follow-ups, the stamp, envelopes with their authors, kinds and the interpreter, the
decision seam, the step machine — each message's chain computed whole, decided as a unit, then committed,
parked or refused — and the gate: each principal acts through its own registry, built on the published
`Fuaran.Core.Function` and `Fuaran.Core.AiSurface` packages, a step is decided through Core's guarded decision,
and an approval is a grant bound to one parked step as it was previewed. The step machine and the gate are each
held to an F\* model by a differential test on .NET and under Fable. `Fuaran.MVU.Session` is the optional session log: a host that
attaches it records each decided step in a `Fuaran.Core` op-stream, under its own disclosure function, and can
replay and restore the session; a host that attaches none records nothing and takes no code for it.
`Fuaran.MVU.React` mounts a component in React: one render per drain through a store React subscribes to, hydration, hot reload in development only, a bridge for views that are React elements, and an after-the-next-paint effect kind. `Fuaran.MVU.Browser` performs the standard client kinds (the client effects of `Fuaran.ClientEffects`, storage and the timers), each registered with its classes so the gate decides it, on a browser host. `Fuaran.MVU.Testing` is the harness an application's tests run a component on. A host without a registry keeps the allow-all decision. The design's
prototype is not part of this repository; see [DECISIONS.md](DECISIONS.md) D4 and D31. What is promised, and at which level, is
[`STABILITY.md`](STABILITY.md) and the claims ladder [`proofs.json`](proofs.json).

## What it is

- **A component is pure.** `init`, `update`, `resources` and `view` are functions from values to
  values. `update` returns the next model and a list of steps: effects to perform, as values, and
  follow-up messages. It never returns a function and never performs anything. A parent is built
  from children of the same type, and an application is a root component
  ([DECISIONS.md](DECISIONS.md) D6).
- **A host interprets.** For each effect the host names a registered kind and supplies a performer.
  An effect is one-shot: it has one answer or none.
- **Anything long-lived is a resource.** A stream, a listener, a cancellable call or a query kept
  fresh exists while the model lists it. A keyed diff starts what appears, stops what vanishes and
  restarts what changed, every stop before every start in key order, so nothing can be started and
  forgotten ([DECISIONS.md](DECISIONS.md) D25).
- **A message is decided before it is applied.** The runtime computes what a message and its
  follow-ups would do, puts that to a gate, and then applies all of it, parks all of it for
  approval, or refuses all of it.
- **Authority follows authorship.** Every message carries the principal that wrote it, and each
  principal acts through its own registry.
- **The kernel is proved.** A queue for each kind of host, a keyed diff, the step machine and the gate, each
  with a model tied to the shipped code by a differential test; the gate's model takes Core's proved join as
  its one premise. What is proved, tested, assumed and not claimed is written down, as data, in
  [`proofs.json`](proofs.json).

## Running an application on the runtime

```fsharp
open Fuaran.MVU

let counter : Component<unit, Model, Msg, NoEffect, NoResource, View> =
    Component.plain init update (fun _ -> Map.empty) view

let interpreter = Interpreter.create kinds effect resource          // the host's kinds and performers
let host = { Host.create clock seed render with Decide = Decide.allowAll }
let runtime = Runtime(counter, interpreter, host, ())
runtime.Start()                                                     // paints Init's model, runs Init's steps
runtime.Dispatch(Increment, User "alice") |> ignore                 // accepted, decided, committed, rendered
```

A test runs the same component on the harness: `let run = Harness.start counter ()`, then `run.Send`,
`run.Performed`, `run.Answer`, `run.Model`. A component's `Update` reads a stamp the runtime fixed when it
accepted the message; a test supplies one with `Stamp.forTest`.

### A registry per principal, and a grant

The gate registers the host's kinds once in Core's capability registry, tightens it per principal, and decides
each step through Core; a person's approval is a grant the host mints for one parked step.

```fsharp
let restrictions principal =
    match principal with
    | Agent _ -> [ ApprovalFor(set [ "writes" ]); Refusing(set [ "egress" ]) ]   // an agent asks before it writes
    | Foreign _ -> [ Only Set.empty ]                                            // a foreign source may do nothing
    | User _ | Host -> []                                                        // the host's registry as it is

let gate = Gate.create kinds [ egressAllowList ] restrictions Gate.allowAll |> Result.defaultWith failwith
let decide = Gate.mount gate interpreter |> Result.defaultWith (fun missing -> failwithf "uncovered kinds: %A" missing)
let runtime = Runtime(app, interpreter, { host with Decide = decide }, ())

// A model's reply parks: the person who reads the preview on the host's controls approves it.
let parked = runtime.Parked.Head
let grant = Gate.grant gate parked (User "alice") (clock () + 60_000L)   // one step, as previewed, for a minute
runtime.Approve grant |> ignore                                          // commits, or refuses as stale
```

No message can carry a grant, the author of a step cannot approve it, a grant is spent the moment it is
presented, and every refusal of the host's registry stands for every principal.

### A session log, replayed and restored

Nothing is recorded unless the host attaches a sink. The log is a Core op-stream; the host decides what each field
discloses, and a withheld value is recorded as a keyed commitment. The runtime itself keeps only the recent steps in
memory: `Runtime.Log` is a ring of `Fuel.Log` records (1,024 by default; zero keeps none), and `Runtime.LogDropped`
says how many it dropped, so the whole history is the sink's.

```fsharp
open Fuaran.MVU.Session

let options =
    { SessionOptions.create "notes" "1.0" codec key with                 // key: a CommitmentKey the host holds
        Disclose = fun path _ -> if path = Paths.message "body" then Withheld else Shown
        OnWithheld = fun field value -> vault.[field.Commitment] <- value } // the host's vault, for a later replay
let session = Session.Open(options, Store.inMemory ()) |> Result.defaultWith failwith
let runtime = Runtime(app, interpreter, { host with Record = session.Record }, ())

// Later, over the same store (a durable one, `Store.lines` or `Store.file`, to outlive the process): replay
// performs nothing, then restore resumes the runtime and the log.
let reveal (field: WithheldField) = match vault.TryGetValue field.Commitment with | true, v -> Some v | _ -> None
let records = session.Records |> Result.defaultWith failwith
let folded = Log.replay app () interpreter options reveal records |> Result.defaultWith (failwithf "%A")
let idempotent = Log.declareIdempotent interpreter [ "save" ] |> Result.defaultWith (failwithf "%A")
let restored =
    Log.restore app () interpreter host { Idempotent = idempotent; Interrupted = fun e -> Some(Interrupted e) } session folded
restored.Start()   // re-sends each outstanding save; every other outstanding effect is answered `Interrupted`
```

### Mounting in React

`Fuaran.MVU.React` mounts a component in a React application. The runtime renders once per drain, and each render is
published to a store React reads through `useSyncExternalStore`. A component whose view is a React element (a Feliz
view, today) runs through the element-view bridge. Hot reload carries the model across a development reload and is
compiled out of a production build.

```fsharp
open Fuaran.MVU.React

let app : Component<unit, Model, Msg, Effect, Resource, ElementView<Msg>> =
    Component.plain init update resources (ElementView.ofView view)   // view: Model -> (Msg -> unit) -> ReactElement

// The interpreter registers "after the next paint" and routes the application's case to its performer.
let interpreter =
    Interpreter.create (AfterPaint.kind :: kinds) (function
        | WaitForPaint answer -> AfterPaint.handle (Live.timing ()) answer
        | other -> effect other) resource

let mounted = Live.elements "app" Fresh host app interpreter ()       // `Hydrate` over server-rendered markup
mounted.Dispatch Increment |> ignore
mounted.Unmount()                                                     // nothing from it is accepted after
```

A view that reads slices of the model takes a store made before the mount (`MountOptions.Store`) and
`ModelStore.useSelector`. A component whose view is an element view cannot mount a governed surface:
`Governed.admit` does not compile for it.

### The standard client kinds in the browser

`Fuaran.MVU.Browser` performs the effects nearly every browser application needs: the client effects of
`Fuaran.ClientEffects` (clipboard, navigation, history, focus, download, file read, print, confirm), storage, a one-shot
timer, and a repeating timer as a resource. Each kind is registered once with its classes (`reads`, `writes`,
`navigation`, `user-visible`), so a gate decides it; registering a performer permits nothing. An application embeds
the vocabulary as one case of its effect type, and maps the standard answers into its messages with one function.

```fsharp
open Fuaran.ClientEffects
open Fuaran.MVU.Browser

type Effect =
    | Browser of StandardEffect
    | Save of Document

let update msg model =
    match msg with
    | Copy -> model, [ Do(Browser(Client(WriteToClipboard model.Text))) ]
    | AskDelete -> model, [ Do(Browser(Client(Confirm("Delete this?", "delete")))) ]
    | Answered(Confirmed("delete", true)) -> { model with Deleted = true }, []
    | _ -> model, []

// The standard kinds are registered before the application's own; the projection is the whole line for them.
let interpreter dom =
    StandardInterpreter.embed dom Answered (function Browser e -> Some e | _ -> None) (fun _ -> None) own

let host = Live.host render                  // the growing queue, a continuation in a task of its own, console reports
let runtime = BrowserHost.mount host app (interpreter (Live.dom ())) ()
```

An application whose effects are only the standard ones uses `StandardEffect` as its effect type and
`StandardInterpreter.create dom Answered` as its interpreter. Its tests compare the standard effects as values under
`Fuaran.MVU.Testing`, and run the performers against a stand-in for `Dom` with no browser. A node address (`Focus`,
`ReadFileBody`) is an element id by default; a view that addresses its nodes another way replaces `Dom.Resolve`.

## Running

```
pwsh ./run.ps1
```

Needs PowerShell 7, the .NET SDK that `global.json` pins, and Node. Six stages: tools, format, build,
tests (the Expecto suite — the kernel's laws, the prototype's checks ported, the runtime's rules and the
ladder — then the prototype scripts), proofs (the pinned proof kit verifies the six models, holds the
committed oracle to a fresh extraction and runs the differential hosts on .NET and under Node) and fable
(every law, the session log's included, and all three differentials under Node, then the React adapter's laws
under Node and the check that a production transpile of it carries no hot reload, then the browser package's laws
under Node). The tests stage also runs the React adapter's laws on .NET and requires its negative build to fail, and
runs the browser package's laws on .NET. `-SkipFormat`, `-SkipBuild`, `-SkipTests`,
`-SkipProofs` and `-SkipFable` skip a stage while iterating; a skipped stage is reported as skipped,
never as passed. `-ProofRuns 3 -Strict` is the recorded form of the proof leg; `-KitAsset <file>` takes
the kit's release asset from a local file (its sha256 is verified all the same).

## Layout

| Path | What it is |
|---|---|
| `src/Fuaran.MVU.Kernel/` | The kernel: `Drain.fs`, `Ring.fs`, `Diff.fs`. FSharp.Core only |
| `src/Fuaran.MVU/` | The runtime: `Step.fs`, `Stamp.fs`, `Envelope.fs`, `Component.fs`, `Interpreter.fs`, `Decision.fs` (the seam, the grant), `Chain.fs`, `Runtime.fs`, `Gate.fs` (the gate over Core's registry). The kernel, FSharp.Core, and the published `Fuaran.Core.Function` and `Fuaran.Core.AiSurface` packages |
| `src/Fuaran.MVU.Session/` | The optional session log: `Commitment.fs` (the keyed commitment), `Entry.fs` (the record and its canonical form), `Store.fs` (the log's stream and the stores), `Session.fs` (the sink, replay, restore). `Fuaran.MVU`, FSharp.Core, and the published `Fuaran.Core.OpStream` package |
| `src/Fuaran.MVU.React/` | The React adapter: `ElementView.fs` (the bridge, the governed mark), `Store.fs` (the slice of React it calls, the model store, the root, the slice hook), `Scheduler.fs` (timing, the continuation, after the next paint), `Hydration.fs`, `HotReload.fs` (DEBUG only), `Mount.fs` (the mount, the inline live binding). `Fuaran.MVU`, FSharp.Core, Fable.Core and Fable.React.Types |
| `src/Fuaran.MVU.Browser/` | The browser package: `Kinds.fs` (the standard vocabulary, its kinds and classes, the invocations), `Performers.fs` (the DOM as a value, a performer per kind, the interpreter's line and `embed`), `Host.fs` (the browser host, the inline live binding). `Fuaran.MVU`, FSharp.Core, `Fuaran.ClientEffects` and Fable.Core |
| `src/Fuaran.MVU.Testing/` | The harness over a scripted interpreter (`Harness.fs`) and the laws (`Law.fs`). No test framework |
| `tests/Fuaran.MVU.Tests/` | The Expecto host: the kernel's laws, the prototype's checks (`Shared/PortLaws.fs`), the runtime's rules (`Shared/RuntimeLaws.fs`), the gate's laws (`Shared/GateLaws.fs`), the session log's laws (`Shared/SessionLaws.fs`), the three differentials against the extracted models (`Shared/`, host-neutral), the claims ladder held to the tree |
| `tests/Fuaran.MVU.React.Tests/` | The React adapter's laws against a stand-in for React and the DOM, on .NET and under Node; and the negative build a governed admission of an element view must fail |
| `tests/Fuaran.MVU.Browser.Tests/` | The browser package's laws: the standard effects as values, each performer against a stand-in for the DOM, the gate's refusals and the browser host, on .NET and under Node; under Node, the live binding against stubbed globals |
| `tests/Fuaran.MVU.Tests/Node/` | The same laws and differentials, transpiled with Fable and run under Node |
| `tests/Fuaran.MVU.Tests/Fable/` | The proof leg's Fable oracle: the extracted models over the derived support stand-in, fingerprinted across hosts |
| `proofs/` | The models (`Drain.fst`, `FixedRing.fst`, `KeyedDiff.fst`, `StepChain.fst`, `StepMachine.fst`, `Authority.fst`), the proof leg (`check.ps1`, `modules.json`, `proofs-kit.json`) and the committed oracle the prover writes (`oracle/`) |
| `proofs.json` | The claims ladder: proved, tested, assumed, policy |
| `run.ps1` | The one entry point; `verify.ps1` forwards to it |
| `gate-reads.json` | What the gate reads and builds |
| `copies.json` | What this repository copies from elsewhere: nothing |
| `STABILITY.md` | The public surface, its version, and what is proved, tested, assumed and not claimed |
| `DECISIONS.md` | The decisions this repository was cut under |

## Licence

Apache-2.0. See [LICENSE](LICENSE) and [NOTICE](NOTICE). The ring and the diff descend from the ring
buffer and the subscription diff of Fable.Elmish and of the ToolUp Analytics open-source SDK
(`toolup-forge`), both Apache-2.0; the files that descend from them carry their copyright lines.
