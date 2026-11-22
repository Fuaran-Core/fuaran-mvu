// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Diametrical Ltd

namespace Fuaran.MVU.Session

open System
open Fuaran.Core
open Fuaran.MVU

/// What a host attaches a session with (DECISIONS.md D28): the application's name and version (the host's actor),
/// the codec for its messages, the disclosure function applied to every field, the key its commitments are taken
/// under, where a withheld value goes so the host can reveal it to a later replay, and the chain hash.
type SessionOptions<'msg> = {
    Name: string
    Version: string
    Codec: MessageCodec<'msg>
    /// The host's disclosure function: a field's path and its value, to how it is recorded.
    Disclose: string -> string -> Disclosure
    Key: CommitmentKey
    /// Called once for each withheld value of a step that is recorded, with its place in the log: the host's
    /// vault, which a replay's `Reveal` reads back. Never called for a secret.
    OnWithheld: WithheldField -> string -> unit
    HashFn: HashFn
}

module SessionOptions =

    /// Options that show every field, keep no vault, and chain under SHA-256 (`OpStream.sha256Hash`).
    let create
        (name: string)
        (version: string)
        (codec: MessageCodec<'msg>)
        (key: CommitmentKey)
        : SessionOptions<'msg> =
        {
            Name = name
            Version = version
            Codec = codec
            Disclose = fun _ _ -> Shown
            Key = key
            OnWithheld = fun _ _ -> ()
            HashFn = OpStream.sha256Hash
        }

/// What a restore settles for one outstanding effect or parked step, before the sink records it.
type internal Settlement<'msg> =
    | Resend of cause: int64 * effect: int
    | Interrupt of cause: int64 * effect: int * answer: 'msg option
    | Cancel of sequence: int64 * answer: 'msg option

/// The paths the disclosure function is handed, one per field the log holds.
module Paths =

    /// A field of the step's message.
    let message (path: string) = "msg/" + path

    /// The kind of the effect an answer answers.
    let answerKind = "source/kind"

    /// The `i`-th segment of a resource event's key.
    let resourceKey (i: int) = "source/key/" + string i

    /// A refusal's reason.
    let reason = "outcome/reason"

    /// The request a grant was minted for.
    let grantRequest = "grant/request"

    /// A field of the message a restore gave the component for its `i`-th settlement.
    let settled (i: int) (path: string) = "settled/" + string i + "/" + path

exception internal LostStep of LostReason

/// The session sink (DECISIONS.md D28.6). Attach it as the host's `Record` (`{ host with Record = session.Record }`):
/// each step the runtime decides is recorded, under disclosure, as one op of a Core op-stream whose actor is the
/// author mapped by `Principal.toActor`, and appended to the store. A host that attaches none records nothing.
[<Sealed>]
type Session<'msg> private (options: SessionOptions<'msg>, store: Store, existing: OpRecord<Entry> list) =
    let stream = LogStream.create options.HashFn

    let mutable next =
        match List.tryLast existing with
        | Some r -> r.Seq + 1
        | None -> 0

    let mutable head = OpStream.head stream.Config existing
    let mutable pending: Settlement<'msg> list option = None

    let entryOf (record: StepRecord<'msg>) : Entry * (WithheldField * string) list =
        let sequence = StepOf.sequence record.Of
        let withheld = ResizeArray<WithheldField * string>()

        let field (path: string) (value: string) : Recorded =
            match options.Disclose path value with
            | Shown -> Recorded.Clear value
            | Withheld ->
                let c = Commitment.take options.Key sequence path value

                withheld.Add(
                    {
                        Sequence = sequence
                        Path = path
                        Commitment = c
                    },
                    value
                )

                Recorded.Committed c
            | Secret -> raise (LostStep LostReason.Secret)

        let fieldsOf (pathOf: string -> string) (msg: 'msg) : (string * Recorded) list =
            let fields =
                try
                    options.Codec.Fields msg
                with _ ->
                    raise (LostStep LostReason.Codec)

            if (fields |> List.map fst |> List.distinct |> List.length) <> List.length fields then
                raise (LostStep LostReason.Codec)

            fields |> List.map (fun (path, value) -> path, field (pathOf path) value)

        let settled (plan: Settlement<'msg> list) =
            plan
            |> List.mapi (fun i s ->
                let answer = Option.map (fieldsOf (Paths.settled i))

                match s with
                | Resend(cause, effect) -> Settled.Resent(cause, effect)
                | Interrupt(cause, effect, a) -> Settled.Interrupted(cause, effect, answer a)
                | Cancel(seq, a) -> Settled.Cancelled(seq, answer a))

        try
            let author, source, cause, message, boot =
                match record.Of with
                | BootStep _ ->
                    let boot =
                        match pending with
                        | Some plan -> EntryBoot.Resumed(settled plan)
                        | None -> EntryBoot.Init

                    Host, EntrySource.Boot, None, [], Some boot
                | Step envelope ->
                    let source =
                        match envelope.Source with
                        | External -> EntrySource.External
                        | Answer(kind, effect) -> EntrySource.Answer(field Paths.answerKind kind, effect)
                        | FromResource(ResourceKey key, instance) ->
                            EntrySource.Resource(key |> List.mapi (fun i s -> field (Paths.resourceKey i) s), instance)
                        | FromRuntime -> EntrySource.Runtime
                        | Source.Boot -> EntrySource.Boot

                    envelope.Author, source, envelope.Cause, fieldsOf Paths.message envelope.Msg, None

            let stamp =
                match record.Of with
                | BootStep stamp -> stamp
                | Step envelope -> envelope.Stamp

            let outcome =
                match record.Outcome with
                | StepOutcome.Committed -> EntryOutcome.Committed
                | Parked id -> EntryOutcome.Parked id
                | Refused reason -> EntryOutcome.Refused(field Paths.reason reason)

            let grant =
                record.Grant
                |> Option.map (fun g -> {
                    GrantEntry.Parked = g.Parked
                    Request = field Paths.grantRequest g.Request
                    Approver = g.Approver
                    ExpiresAt = g.ExpiresAt
                    Generation = g.Generation
                })

            Entry.Step {
                Stamp = stamp
                Author = author
                Source = source
                Cause = cause
                Message = message
                Boot = boot
                Outcome = outcome
                Grant = grant
            },
            List.ofSeq withheld
        with LostStep reason ->
            Entry.Lost(sequence, reason), []

    /// Open a session over `store`, continuing the chain the store already holds; refused when the store's records
    /// cannot be read or do not verify as a chain under the options' hash (Core's `OpStream.verifyChain`).
    static member Open(options: SessionOptions<'msg>, store: Store) : Result<Session<'msg>, string> =
        match store.Records() with
        | Error e -> Error e
        | Ok records when not (LogStream.verify options.HashFn records) ->
            Error "the store's records do not verify as a chain"
        | Ok records -> Ok(Session(options, store, records))

    /// The sink: record one decided step. The runtime calls it through the host's `Record`.
    member _.Record(record: StepRecord<'msg>) : unit =
        let author =
            match record.Of with
            | BootStep _ -> Host
            | Step envelope -> envelope.Author

        let entry, withheld = entryOf record

        match record.Of with
        | BootStep _ -> pending <- None
        | Step _ -> ()

        let actor = Principal.toActor options.Name options.Version author
        let encoded = stream.Witness.Encode entry
        let hash = OpStream.chainHashOf stream.Config stream.HashFn next actor encoded head

        store.Append {
            Seq = next
            Actor = actor
            Op = entry
            PrevHash = head
            Hash = hash
        }

        next <- next + 1
        head <- hash

        for field, value in withheld do
            options.OnWithheld field value

    /// The records the store holds.
    member _.Records: Result<OpRecord<Entry> list, string> = store.Records()

    /// The chain's head: the last record's hash, or the genesis for an empty log. What an attestation signs.
    member _.Head: string = head

    /// How many records the log holds.
    member _.Count: int = next

    /// The options the session records under.
    member _.Options = options

    /// The settlements the next boot record carries: set by a restore before the resumed runtime starts.
    member internal _.Resuming(plan: Settlement<'msg> list) = pending <- Some plan

/// The state a replay folds to (DECISIONS.md D28.4): the model, the effects of call-shaped kinds that committed steps
/// emitted and no recorded answer answers (by the step's sequence and the effect's position), the steps parked and
/// not yet resolved (by sequence, with the parked id) and the last sequence a recorded step carried.
type Folded<'model, 'effect> = {
    Model: 'model
    Outstanding: Map<int64 * int, 'effect>
    Parked: Map<int64, int>
    Last: int64
}

/// Why a replay stopped, at the record it names.
[<RequireQualifiedAccess>]
type ReplayFault =
    /// A step was recorded as lost: past it the log cannot reproduce the model.
    | Lost of sequence: int64 * reason: LostReason
    /// A withheld field the replay needs, and the reveal gave no value for it.
    | Withheld of sequence: int64 * path: string
    /// The reveal gave a value the commitment does not open to.
    | RevealMismatch of sequence: int64 * path: string
    /// The codec could not read the recorded message back.
    | Unreadable of sequence: int64 * reason: string
    /// `update` threw on a step the runtime had committed: the component is not a function of its inputs.
    | UpdateThrew of sequence: int64 * message: string
    /// A resumed boot settles an effect the log does not hold as outstanding.
    | NotOutstanding of cause: int64 * effect: int

/// What a restore does with what was in flight (DECISIONS.md D28.5).
type RestoreOptions<'msg, 'effect> = {
    /// The kinds declared idempotent, by id: an outstanding effect of one is sent again, its value recomputed from
    /// the recorded stamp and so carrying the same key.
    Idempotent: Set<string>
    /// The message the component is given for an outstanding effect of any other kind: interrupted, outcome
    /// unknown. `None` settles the effect with no message.
    Interrupted: 'effect -> 'msg option
}

/// Replay and restore over a session log.
module Log =

    let private unbounded = Int32.MaxValue

    /// The state before the first record: `Init`'s model, nothing outstanding, nothing parked, no sequence.
    let initial
        (comp: Component<'flags, 'model, 'msg, 'effect, 'resource, 'view>)
        (flags: 'flags)
        : Folded<'model, 'effect> =
        {
            Model = fst (comp.Init flags)
            Outstanding = Map.empty
            Parked = Map.empty
            Last = -1L
        }

    /// Whether an effect is of a call-shaped kind: one answer is owed. Reads the interpreter's description of the
    /// effect (its invocation's kind) and never asks it to perform.
    let private owesAnswer (interpreter: Interpreter<'msg, 'effect, 'resource>) (effect: 'effect) : bool =
        let kind = (interpreter.Effect effect).Invocation.Kind

        match Interpreter.tryKind kind interpreter with
        | Some k -> k.Shape = Call
        | None -> false

    /// The component as a stream witness (DECISIONS.md D28.4): the op is a recorded step, the state is the model
    /// (with what restore needs), and apply is `update` under the step's recorded stamp, for a step the runtime
    /// committed. A recorded decision is replayed, never retaken: a parked or refused step changes nothing, and no
    /// gate, clock or performer is consulted. A withheld field is read through `reveal`, and checked against its
    /// commitment under the session's key.
    let witness
        (comp: Component<'flags, 'model, 'msg, 'effect, 'resource, 'view>)
        (flags: 'flags)
        (interpreter: Interpreter<'msg, 'effect, 'resource>)
        (options: SessionOptions<'msg>)
        (reveal: WithheldField -> string option)
        : StreamWitness<Entry, Folded<'model, 'effect>, ReplayFault> =

        let read (sequence: int64) (pathOf: string -> string) (fields: (string * Recorded) list) =
            let rec go acc fs =
                match fs with
                | [] -> Ok(List.rev acc)
                | (path, Recorded.Clear v) :: rest -> go ((path, v) :: acc) rest
                | (path, Recorded.Committed c) :: rest ->
                    let full = pathOf path

                    match
                        reveal {
                            Sequence = sequence
                            Path = full
                            Commitment = c
                        }
                    with
                    | None -> Error(ReplayFault.Withheld(sequence, full))
                    | Some v when Commitment.opens options.Key sequence full v c -> go ((path, v) :: acc) rest
                    | Some _ -> Error(ReplayFault.RevealMismatch(sequence, full))

            go [] fields
            |> Result.bind (fun fs ->
                options.Codec.Read fs
                |> Result.mapError (fun e -> ReplayFault.Unreadable(sequence, e)))

        let apply (entry: Entry) (state: Folded<'model, 'effect>) : Result<Folded<'model, 'effect>, ReplayFault> =
            match entry with
            | Entry.Lost(sequence, reason) -> Error(ReplayFault.Lost(sequence, reason))
            | Entry.Step e ->
                let sequence = e.Stamp.Sequence

                let state = {
                    state with
                        Last = max state.Last sequence
                        Outstanding =
                            match e.Source, e.Cause with
                            | EntrySource.Answer(_, effect), Some cause -> Map.remove (cause, effect) state.Outstanding
                            | _ -> state.Outstanding
                }

                match e.Outcome with
                | EntryOutcome.Parked id ->
                    Ok {
                        state with
                            Parked = Map.add sequence id state.Parked
                    }
                | EntryOutcome.Refused _ ->
                    Ok {
                        state with
                            Parked = Map.remove sequence state.Parked
                    }
                | EntryOutcome.Committed ->
                    let state = {
                        state with
                            Parked = Map.remove sequence state.Parked
                    }

                    let chainFrom =
                        match e.Boot with
                        | Some EntryBoot.Init ->
                            Ok(
                                state,
                                Chain.continueFrom comp.Update e.Stamp unbounded 0 state.Model (snd (comp.Init flags))
                            )
                        | Some(EntryBoot.Resumed settled) ->
                            let rec settle (i: int) (st: Folded<'model, 'effect>) acc items =
                                match items with
                                | [] -> Ok(st, List.rev acc)
                                | s :: rest ->
                                    let answer a =
                                        match a with
                                        | None -> Ok []
                                        | Some fs ->
                                            read sequence (Paths.settled i) fs |> Result.map (fun m -> [ Then m ])

                                    match s with
                                    | Settled.Resent(cause, effect) ->
                                        match Map.tryFind (cause, effect) st.Outstanding with
                                        | None -> Error(ReplayFault.NotOutstanding(cause, effect))
                                        | Some eff ->
                                            settle
                                                (i + 1)
                                                {
                                                    st with
                                                        Outstanding = Map.remove (cause, effect) st.Outstanding
                                                }
                                                (Do eff :: acc)
                                                rest
                                    | Settled.Interrupted(cause, effect, a) ->
                                        if not (Map.containsKey (cause, effect) st.Outstanding) then
                                            Error(ReplayFault.NotOutstanding(cause, effect))
                                        else
                                            answer a
                                            |> Result.bind (fun steps ->
                                                settle
                                                    (i + 1)
                                                    {
                                                        st with
                                                            Outstanding = Map.remove (cause, effect) st.Outstanding
                                                    }
                                                    (List.rev steps @ acc)
                                                    rest)
                                    | Settled.Cancelled(parked, a) ->
                                        answer a
                                        |> Result.bind (fun steps ->
                                            settle
                                                (i + 1)
                                                {
                                                    st with
                                                        Parked = Map.remove parked st.Parked
                                                }
                                                (List.rev steps @ acc)
                                                rest)

                            settle 0 state [] settled
                            |> Result.map (fun (st, steps) ->
                                st, Chain.continueFrom comp.Update e.Stamp unbounded 0 st.Model steps)
                        | None ->
                            read sequence Paths.message e.Message
                            |> Result.map (fun msg ->
                                state, Chain.compute comp.Update e.Stamp unbounded state.Model msg)

                    chainFrom
                    |> Result.bind (fun (state, outcome) ->
                        match outcome with
                        | Computed chain ->
                            let outstanding =
                                chain.Effects
                                |> List.indexed
                                |> List.fold
                                    (fun acc (position, effect) ->
                                        if owesAnswer interpreter effect then
                                            Map.add (sequence, position) effect acc
                                        else
                                            acc)
                                    state.Outstanding

                            Ok {
                                state with
                                    Model = chain.Model
                                    Outstanding = outstanding
                            }
                        | Threw ex -> Error(ReplayFault.UpdateThrew(sequence, ex.Message))
                        | TooLong limit ->
                            Error(ReplayFault.UpdateThrew(sequence, sprintf "chain longer than %d" limit)))

        {
            Apply = apply
            Encode = EntryCodec.encode
            Decode = EntryCodec.decode
        }

    /// The log's stream with the component as its reducer: what Core's snapshots and compaction take
    /// (`Snapshots.take`, `Compacted.compactAt`, `Compacted.replayFrom`).
    let stream
        (comp: Component<'flags, 'model, 'msg, 'effect, 'resource, 'view>)
        (flags: 'flags)
        (interpreter: Interpreter<'msg, 'effect, 'resource>)
        (options: SessionOptions<'msg>)
        (reveal: WithheldField -> string option)
        : Stream<Entry, Folded<'model, 'effect>, ReplayFault> =
        OpStream.canonical options.HashFn (witness comp flags interpreter options reveal)

    /// Replay: Core's `OpStream.replay` of the component's witness from `initial`. Performs nothing, reads no clock
    /// and decides nothing; the first fault stops it, with the record's index.
    let replay
        (comp: Component<'flags, 'model, 'msg, 'effect, 'resource, 'view>)
        (flags: 'flags)
        (interpreter: Interpreter<'msg, 'effect, 'resource>)
        (options: SessionOptions<'msg>)
        (reveal: WithheldField -> string option)
        (records: OpRecord<Entry> list)
        : Result<Folded<'model, 'effect>, int * ReplayFault> =
        OpStream.replay (witness comp flags interpreter options reveal) (initial comp flags) records

    /// The kinds a host declares idempotent, checked against its interpreter's registration: refused, naming them,
    /// for an id the interpreter does not register or a kind that is not call-shaped.
    let declareIdempotent
        (interpreter: Interpreter<'msg, 'effect, 'resource>)
        (ids: string list)
        : Result<Set<string>, string list> =
        let refused =
            ids
            |> List.filter (fun id ->
                match Interpreter.tryKind id interpreter with
                | Some k -> k.Shape <> Call
                | None -> true)

        if refused.IsEmpty then
            Ok(Set.ofList ids)
        else
            Error refused

    /// Restore (DECISIONS.md D28.5): a runtime resumed from a replayed log. It boots from the folded model at the
    /// sequence after the log's last, attached to `session` so the log continues; its boot step lists the
    /// resources again (the runtime starts what the model wants), sends again every outstanding effect of an
    /// idempotent kind, gives the component `Interrupted` for every other outstanding effect, and gives it the
    /// interpreter's `Refused Cancelled` message for every step left parked. A log with no boot restores as a
    /// fresh runtime. The runtime is returned unstarted: the host calls `Start`.
    let restore
        (comp: Component<'flags, 'model, 'msg, 'effect, 'resource, 'view>)
        (flags: 'flags)
        (interpreter: Interpreter<'msg, 'effect, 'resource>)
        (host: Host<'model, 'msg, 'effect, 'resource, 'view>)
        (options: RestoreOptions<'msg, 'effect>)
        (session: Session<'msg>)
        (folded: Folded<'model, 'effect>)
        : Runtime<'flags, 'model, 'msg, 'effect, 'resource, 'view> =
        let host = { host with Record = session.Record }

        if folded.Last < 0L then
            Runtime(comp, interpreter, host, flags)
        else
            let effects =
                folded.Outstanding
                |> Map.toList
                |> List.map (fun ((cause, position), effect) ->
                    if options.Idempotent.Contains (interpreter.Effect effect).Invocation.Kind then
                        Resend(cause, position), [ Do effect ]
                    else
                        let answer = options.Interrupted effect
                        Interrupt(cause, position, answer), (answer |> Option.map Then |> Option.toList))

            let parked =
                folded.Parked
                |> Map.toList
                |> List.map (fun (sequence, _) ->
                    let answer = interpreter.Refused Cancelled
                    Cancel(sequence, answer), (answer |> Option.map Then |> Option.toList))

            let plan = effects @ parked
            session.Resuming(List.map fst plan)
            Runtime.Resume(comp, interpreter, host, folded.Model, List.collect snd plan, folded.Last)
