# The proof leg

The kernel's three parts, the runtime's step machine and its gate are each held to an F\* model: `Drain.fst` (the
drain buffer), `FixedRing.fst` (the fixed-capacity ring), `KeyedDiff.fst` (the keyed resource diff),
`StepChain.fst` (the chain of a step, with its fuel), `StepMachine.fst` (the step machine over it:
acceptance, the step decided whole, the drain with its one render, resources with their tokens,
termination) and `Authority.fst` (the gate: a principal's registry as the host's tightened, the step decided as
the join of the policy, the grant verdict and every invocation's registry verdict, and the grant presented — with
Core's join as a parameter under Core's own definition as the premise). What each proves, what is only tested, what
is assumed and what is policy is the claims ladder, [`../proofs.json`](../proofs.json), one row per claim, held to
this tree by the `Proofs.Ladder` test family.

## How the leg runs

`pwsh ./proofs/check.ps1` is the leg; `pwsh ./run.ps1` runs it as its `proofs` stage. The leg is the
Fuaran.Core proof kit, on its **custard** leg: `proofs-kit.json` pins ONE release asset of that kit by
sha256, `check.ps1` (copied once from the kit's template and edited at its three declarations) downloads
it into the gitignored `.kit/`, refuses any other bytes, unpacks it fresh and runs the kit's engine from
it. The engine installs the pinned prover (F\* `v2026.10.11`, Z3 4.13.3, hash-pinned per operating
system) under the gitignored `.fstar/v2026.10.11/`, then:

1. **Checks** every model in `modules.json`'s roster from a cold cache, under `--z3rlimit 40 --quake 3
   --report_assumes error`, and measures each against its declared budget (the six together take about
   twenty seconds on the development machine; the step machine is the largest at five, the gate's model at about one). `-Runs 3` (`run.ps1
   -ProofRuns 3`) repeats it; `-Strict` makes an incomplete budget declaration red.
2. **Extracts** every model in ONE Custard run (`--codegen FSharp --custard_split`), and holds
   `oracle/` byte for byte to what the prover writes: `Drain.fs`, `FixedRing.fs`, `KeyedDiff.fs`,
   `StepChain.fs`, `StepMachine.fs`, `Authority.fs`, the support library `FStarCustard.fs` and `Custard.fsproj`, and
   nothing else. `oracle/` is never edited
   and never formatted; a model edit without `-Extract` is a red leg. The oracle project is built as
   generated and checked for independence: it reaches FSharp.Core, Prims and its own modules, and no
   production code, so a differential can never compare production with itself.
3. **The Fable side** (`../tests/Fuaran.MVU.Tests/Fable/`): the leg cuts the stand-in for the support
   library from the generated file, holds it byte for byte, transpiles the project with the repository's
   `fable` tool and runs its runner under Node. The runner checks the models' answers outright and holds
   their fingerprint over a fixed corpus to the value the .NET host computes.
4. **The host step**: `dotnet run --project tests/Fuaran.MVU.Tests -- --filter Proofs.Oracle` (the
   differentials: the extracted kernel models beside the shipped kernel over generated sequences, the
   extracted step machine beside the shipped `Runtime` over generated envelope scripts, and the
   fingerprint) and `--filter Proofs.Ladder` (the ladder held to the tree).

The differential also runs under Fable, with the kernel's laws, from `../tests/Fuaran.MVU.Tests/Node/`
(`run.ps1`'s `fable` stage) — a test host, which may reach production; the Fable oracle directory
above may not.

## What is in each model

Every model opens nothing but a sibling model (`StepMachine` opens `StepChain`; `Authority` opens nothing): its list
helpers are its own, so the extraction references `Prims` and the sibling and nothing else, and the Fable stand-in
needs the `Prims` block alone. The step machine's model takes `update`, `resources` and `decide` as
parameters, so the differential drives the extraction with the same functions it drives the runtime with. The gate's model takes
Core's `join` as a parameter under the premise that it is Core's definition (`is_join`), the host's gates as one
function and the policy's verdict as a value, so the differential hands it Core's own `PolicyDecision.join` and
the fingerprint the one-line join written out; what it does NOT model — the approver (Core's self-approval refusal,
tested), the guidance text, the destination as a field, the runtime's lookup of the parked step — is its header's
last section and the ladder's `gate-grant-covers-one-step` boundary.
What the step machine's model does NOT cover — the boot, the identity skip, `Resources` throwing, a held
start, the `DrainEnded` hook, envelopes accepted synchronously inside a step, approval and rejection of
parked steps, and the stamp — is the ladder's `step-machine-boundary` row, and each is a case of the
`RuntimeLaws` family. Every model sets `--fuel 0 --ifuel
0` and raises them per section. The theorems and what each requires are in the models' headers and in
the ladder's rows; the headers also say what each model was adapted from (the ToolUp Analytics
open-source SDK's ring models) and what is new.

## Budgets

`modules.json` declares each module's cold-run budget, seeded from the slowest cold run observed at two
times the measurement and floored at twenty seconds. The leg prints the measurement beside the budget
on every green line; an overshoot is a recorded cost finding, never a red leg.
