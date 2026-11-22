// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Diametrical Ltd

/// The session log's laws (Phase 649, DECISIONS.md D28), host-neutral: one list, run under Expecto on .NET and
/// under Node. A replay performs nothing and reads no clock and still reproduces the model, its ids and its times;
/// a withheld value and a secret are searched for in the log's bytes; the log is a Core op-stream, verified,
/// snapshotted and compacted by Core's own functions; a restore sends an idempotent effect again and answers the
/// rest as interrupted. The .NET-only cases (the reference direction, the platform's HMAC, a file) are
/// `dotnetCases`, in `Program.fs`'s host.
module Fuaran.MVU.Tests.SessionLaws

open System
open System.Collections.Generic
open Fuaran.Core
open Fuaran.MVU
open Fuaran.MVU.Session
open Fuaran.MVU.Tests.Check

type Msg =
    | Add of text: string
    | Saved of id: string
    | Pinged of n: int
    | Interrupted of what: string
    | Cancelled
    | Watch of on: bool
    | Tick of n: int

type Effect =
    | Save of id: string * text: string
    | Ping of n: int
    | Note of text: string

type Resource = | Ticker

type Model = {
    Notes: (string * string * int64) list
    Saved: string list
    Pings: int list
    Interrupted: string list
    Cancelled: int
    Watching: bool
    Ticks: int list
}

let private empty = {
    Notes = []
    Saved = []
    Pings = []
    Interrupted = []
    Cancelled = 0
    Watching = false
    Ticks = []
}

/// A component that mints an id and reads the time on every note, emits two call-shaped effects and a signal per
/// note, and holds a resource while it watches.
let journal: Component<unit, Model, Msg, Effect, Resource, Model> = {
    Init = fun () -> empty, []
    Update =
        fun stamp msg model ->
            match msg with
            | Add text ->
                let id = Stamp.freshId 0 stamp

                {
                    model with
                        Notes = model.Notes @ [ id, text, stamp.At ]
                },
                [ Do(Save(id, text)); Do(Ping(List.length model.Notes)); Do(Note text) ]
            | Saved id ->
                {
                    model with
                        Saved = model.Saved @ [ id ]
                },
                []
            | Pinged n ->
                {
                    model with
                        Pings = model.Pings @ [ n ]
                },
                []
            | Interrupted what ->
                {
                    model with
                        Interrupted = model.Interrupted @ [ what ]
                },
                []
            | Cancelled ->
                {
                    model with
                        Cancelled = model.Cancelled + 1
                },
                []
            | Watch on -> { model with Watching = on }, []
            | Tick n ->
                {
                    model with
                        Ticks = model.Ticks @ [ n ]
                },
                []
    Resources =
        fun model ->
            if model.Watching then
                Map.ofList [ ResourceKey.named "ticker" "main", Ticker ]
            else
                Map.empty
    View = id
}

let codec: MessageCodec<Msg> = {
    Fields =
        fun msg ->
            match msg with
            | Add text -> [ "case", "add"; "text", text ]
            | Saved id -> [ "case", "saved"; "id", id ]
            | Pinged n -> [ "case", "pinged"; "n", string n ]
            | Interrupted what -> [ "case", "interrupted"; "what", what ]
            | Cancelled -> [ "case", "cancelled" ]
            | Watch on -> [ "case", "watch"; "on", (if on then "1" else "0") ]
            | Tick n -> [ "case", "tick"; "n", string n ]
    Read =
        fun fields ->
            let f = Map.ofList fields

            let get k =
                Map.tryFind k f |> Option.defaultValue ""

            match get "case" with
            | "add" -> Ok(Add(get "text"))
            | "saved" -> Ok(Saved(get "id"))
            | "pinged" -> Ok(Pinged(int (get "n")))
            | "interrupted" -> Ok(Interrupted(get "what"))
            | "cancelled" -> Ok Cancelled
            | "watch" -> Ok(Watch(get "on" = "1"))
            | "tick" -> Ok(Tick(int (get "n")))
            | other -> Error("not a message: " + other)
}

let private kind (id: string) (shape: Shape) : EffectKind = {
    Id = id
    Shape = shape
    Classes = Set.empty
    Determinism = Set.empty
    Answers = FirstParty
    Replies = []
}

let kinds = [
    kind "save" Shape.Call
    kind "ping" Shape.Call
    kind "note" Shape.Signal
    kind "ticker" Shape.Stream
]

let describe (effect: Effect) : Invocation =
    match effect with
    | Save(id, text) -> {
        Kind = "save"
        Args = Some(id + ":" + text)
        Destination = Local
      }
    | Ping n -> {
        Kind = "ping"
        Args = Some(string n)
        Destination = Local
      }
    | Note text -> {
        Kind = "note"
        Args = Some text
        Destination = Local
      }

let private tickerInvocation: Invocation = {
    Kind = "ticker"
    Args = None
    Destination = Local
}

/// The interpreter a replay is handed: it describes an effect and fails if anything is ever performed or started.
let failing: Interpreter<Msg, Effect, Resource> =
    Interpreter.create
        kinds
        (fun effect -> {
            Invocation = describe effect
            Perform = fun () -> failwith "a replay asked an effect to be performed"
        })
        (fun _ _ -> {
            Invocation = tickerInvocation
            Start = fun _ -> failwith "a replay asked a resource to start"
        })

/// A live host: a scripted interpreter that holds every reply until the test answers it, the resources it started
/// and the emitters it was handed.
type Live() =
    let performed = ResizeArray<Effect>()
    let outstanding = ResizeArray<Effect * (Msg -> unit)>()
    let started = ResizeArray<ResourceKey>()
    let emitters = ResizeArray<Msg -> unit>()

    let interpreter =
        let scripted =
            Interpreter.create
                kinds
                (fun effect -> {
                    Invocation = describe effect
                    Perform =
                        fun () ->
                            performed.Add effect

                            match effect with
                            | Note _ -> Signal ignore
                            | _ -> Reply(fun deliver -> outstanding.Add((effect, deliver)))
                })
                (fun key _ -> {
                    Invocation = tickerInvocation
                    Start =
                        fun emit ->
                            started.Add key
                            emitters.Add emit

                            { new IDisposable with
                                member _.Dispose() = ()
                            }
                })

        {
            scripted with
                Refused =
                    fun refusal ->
                        match refusal with
                        | Refusal.Cancelled -> Some Cancelled
                        | _ -> None
        }

    member _.Interpreter: Interpreter<Msg, Effect, Resource> = interpreter

    member _.Performed = List.ofSeq performed
    member _.Started = List.ofSeq started

    /// Answer the outstanding reply for `effect` with `msg`.
    member _.Answer(effect: Effect, msg: Msg) =
        match Seq.tryFindIndex (fun (e, _) -> e = effect) outstanding with
        | Some i ->
            let _, deliver = outstanding.[i]
            outstanding.RemoveAt i
            deliver msg
        | None -> failwithf "no outstanding reply for %A" effect

    member _.Emit(msg: Msg) = emitters.[emitters.Count - 1] msg

/// A note whose text starts with `ask` needs approval unless a grant is presented; a note `deny` is refused.
let decide: Decide<Effect, Resource> =
    fun proposal ->
        let notes =
            proposal.Effects
            |> List.choose (fun (_, e) ->
                match e with
                | Note t -> Some t
                | _ -> None)

        if notes |> List.contains "deny" then
            Deny "notes of deny are refused"
        elif notes |> List.exists (fun t -> t.StartsWith "ask") && proposal.Grant.IsNone then
            NeedsApproval
        else
            Allow

let key: CommitmentKey =
    match CommitmentKey.OfBytes(Array.init 32 (fun i -> byte (i * 7 + 3))) with
    | Ok k -> k
    | Error e -> failwith e

let otherKey: CommitmentKey =
    match CommitmentKey.OfBytes(Array.init 32 (fun i -> byte (255 - i))) with
    | Ok k -> k
    | Error e -> failwith e

let options: SessionOptions<Msg> = SessionOptions.create "journal" "1.0" codec key

let private openSession (opts: SessionOptions<Msg>) (store: Store) : Session<Msg> =
    match Session.Open(opts, store) with
    | Ok s -> s
    | Error e -> failwith e

/// A host over a clock that counts from 1000 in steps of 10, root seed 42, the test's decision, and the sink.
let hostFor (record: StepRecord<Msg> -> unit) : Host<Model, Msg, Effect, Resource, Model> =
    let mutable now = 1000L

    let clock () =
        now <- now + 10L
        now

    let host: Host<Model, Msg, Effect, Resource, Model> = Host.create clock 42UL ignore

    {
        host with
            Decide = decide
            Record = record
    }

/// A started runtime over a live host, recording into a new session over `store` under `opts`.
let start (opts: SessionOptions<Msg>) (store: Store) =
    let live = Live()
    let session = openSession opts store
    let host = hostFor session.Record
    let runtime = Runtime(journal, live.Interpreter, host, ())
    runtime.Start()
    runtime, session, live

let records (session: Session<Msg>) : OpRecord<Entry> list =
    match session.Records with
    | Ok r -> r
    | Error e -> failwith e

/// The log's bytes as Core's JSONL writes them.
let bytesOf (rs: OpRecord<Entry> list) : string = Jsonl.write LogStream.witness rs

let private noReveal: WithheldField -> string option = fun _ -> None

let private replayed (opts: SessionOptions<Msg>) reveal rs =
    Log.replay journal () failing opts reveal rs

let private contains (needle: string) (haystack: string) =
    haystack.IndexOf(needle, StringComparison.Ordinal) >= 0

let private steps (rs: OpRecord<Entry> list) =
    rs
    |> List.choose (fun r ->
        match r.Op with
        | Entry.Step e -> Some e
        | Entry.Lost _ -> None)

/// A session with notes, answers, a refusal, an approval and a resource event.
let private busy (opts: SessionOptions<Msg>) (store: Store) =
    let runtime, session, live = start opts store
    runtime.Dispatch(Add "first", User "alice") |> ignore
    runtime.Dispatch(Add "second", Agent("model", "7", "helper")) |> ignore

    let saveId =
        match runtime.Model.Notes with
        | (id, _, _) :: _ -> id
        | [] -> ""

    live.Answer(Save(saveId, "first"), Saved saveId)
    live.Answer(Ping 0, Pinged 0)
    runtime.Dispatch(Add "deny", User "alice") |> ignore
    runtime.Dispatch(Add "ask-once", User "alice") |> ignore
    let parked = runtime.Parked.Head

    runtime.Approve(Grant.mint parked.Id parked.Proposal (User "bob") 999_999L 1)
    |> ignore

    runtime.Dispatch(Watch true, User "alice") |> ignore
    live.Emit(Tick 5)
    runtime, session, live

let cases: Case list = [
    case
        "t1: a replay under an interpreter that fails if asked to perform, and no clock, reproduces the model, its ids and its times"
    <| fun expect ->
        let runtime, session, _ = busy options (Store.inMemory ())

        match replayed options noReveal (records session) with
        | Ok folded ->
            expect "the model" (folded.Model = runtime.Model)

            expect
                "the ids and times are in it"
                (runtime.Model.Notes
                 |> List.forall (fun (id, _, at) -> id.Length = 16 && at > 1000L))
        | Error(i, fault) -> expect (sprintf "the replay stopped at %d: %A" i fault) false

    case "t1: a withheld value is not in the log's bytes, and two equal withheld values do not record equally"
    <| fun expect ->
        let opts = {
            options with
                Disclose = fun path _ -> if path = Paths.message "text" then Withheld else Shown
        }

        let runtime, session, _ = start opts (Store.inMemory ())
        runtime.Dispatch(Add "private-note-31337", User "alice") |> ignore
        runtime.Dispatch(Add "private-note-31337", User "alice") |> ignore
        let rs = records session
        let bytes = bytesOf rs
        expect "the value is absent" (not (contains "private-note-31337" bytes))
        expect "its bare digest is absent" (not (contains (Hash.sha256Hex "private-note-31337") bytes))

        let commitments =
            steps rs
            |> List.collect (fun e ->
                e.Message
                |> List.choose (fun (path, r) ->
                    match r with
                    | Recorded.Committed c when path = "text" -> Some(e.Stamp.Sequence, c)
                    | _ -> None))

        expect "both are committed" (List.length commitments = 2)
        expect "the two commitments differ" (List.length (List.distinct (List.map snd commitments)) = 2)

        expect
            "each opens to the value under the host's key and not under another"
            (commitments
             |> List.forall (fun (sequence, c) ->
                 Commitment.opens key sequence (Paths.message "text") "private-note-31337" c
                 && not (Commitment.opens otherKey sequence (Paths.message "text") "private-note-31337" c)))

    case
        "t1: a value declared secret appears nowhere: not in the bytes, not in the vault, and the step is recorded as lost"
    <| fun expect ->
        let vault = ResizeArray<string>()

        let opts = {
            options with
                Disclose = fun _ value -> if value.StartsWith "tok-" then Secret else Shown
                OnWithheld = fun _ value -> vault.Add value
        }

        let runtime, session, _ = start opts (Store.inMemory ())
        runtime.Dispatch(Add "tok-5ecret-99", User "alice") |> ignore
        let rs = records session
        expect "the value is absent from the bytes" (not (contains "tok-5ecret-99" (bytesOf rs)))
        expect "the vault never saw it" (vault.Count = 0)

        expect
            "the step is lost, by its sequence"
            (rs |> List.exists (fun r -> r.Op = Entry.Lost(1L, LostReason.Secret)))

        match replayed opts noReveal rs with
        | Error(_, ReplayFault.Lost(1L, LostReason.Secret)) -> ()
        | other -> expect (sprintf "a replay past it stops at the lost step, not %A" other) false

    case "t1: a runtime with no sink attached writes nothing"
    <| fun expect ->
        let store = Store.inMemory ()
        let _session = openSession options store
        let live = Live()
        let host = hostFor ignore
        let runtime = Runtime(journal, live.Interpreter, host, ())
        runtime.Start()
        runtime.Dispatch(Add "kept nowhere", User "alice") |> ignore
        live.Answer(Ping 0, Pinged 0)
        expect "the runtime ran" (runtime.Model.Pings = [ 0 ])
        expect "the store holds nothing" (store.Records() = Ok [])

    case
        "t5: the disclosure function is handed every field: the message's, an answer's kind, a resource key, a reason and a grant's request"
    <| fun expect ->
        let seen = HashSet<string>()

        let opts = {
            options with
                Disclose =
                    fun path _ ->
                        seen.Add path |> ignore
                        Shown
        }

        busy opts (Store.inMemory ()) |> ignore

        for path in
            [
                Paths.message "case"
                Paths.message "text"
                Paths.message "id"
                Paths.answerKind
                Paths.resourceKey 0
                Paths.resourceKey 1
                Paths.reason
                Paths.grantRequest
            ] do
            expect (sprintf "%s was disclosed" path) (seen.Contains path)

    case "t5: a withheld field replays through the host's vault, and only to the value its commitment opens to"
    <| fun expect ->
        let vault = Dictionary<string, string>()

        let opts = {
            options with
                Disclose = fun path _ -> if path.StartsWith "msg/" then Withheld else Shown
                OnWithheld = fun field value -> vault.[field.Commitment] <- value
        }

        let runtime, session, _ = busy opts (Store.inMemory ())
        let rs = records session
        expect "the notes are absent" (not (contains "second" (bytesOf rs)))

        let fromVault (field: WithheldField) =
            match vault.TryGetValue field.Commitment with
            | true, v -> Some v
            | _ -> None

        match replayed opts fromVault rs with
        | Ok folded -> expect "the vault's replay reproduces the model" (folded.Model = runtime.Model)
        | Error(i, f) -> expect (sprintf "the vault's replay stopped at %d: %A" i f) false

        match replayed opts noReveal rs with
        | Error(_, ReplayFault.Withheld _) -> ()
        | other -> expect (sprintf "without the vault the replay stops at a withheld field, not %A" other) false

        match replayed opts (fun f -> fromVault f |> Option.map (fun v -> v + "!")) rs with
        | Error(_, ReplayFault.RevealMismatch _) -> ()
        | other -> expect (sprintf "a wrong reveal is refused, not %A" other) false

    case "t2: every decision and every grant is recorded, and a replay replays them without deciding"
    <| fun expect ->
        let runtime, session, _ = busy options (Store.inMemory ())
        let es = steps (records session)

        expect
            "the denied step is recorded refused"
            (es
             |> List.exists (fun e ->
                 e.Message |> List.contains ("text", Recorded.Clear "deny")
                 && (match e.Outcome with
                     | EntryOutcome.Refused _ -> true
                     | _ -> false)))

        let asked =
            es
            |> List.filter (fun e -> e.Message |> List.contains ("text", Recorded.Clear "ask-once"))

        expect
            "the step needing approval is recorded parked, then committed with its grant"
            (match asked with
             | [ first; second ] ->
                 first.Outcome = EntryOutcome.Parked 1
                 && first.Grant.IsNone
                 && second.Outcome = EntryOutcome.Committed
                 && (match second.Grant with
                     | Some g -> g.Parked = 1 && g.Approver = User "bob" && g.ExpiresAt = 999_999L
                     | None -> false)
             | _ -> false)

        // The replay has no decision to consult: the witness takes none.
        match replayed options noReveal (records session) with
        | Ok folded ->
            expect "the replay reproduces the model" (folded.Model = runtime.Model)
            expect "nothing is left parked" (folded.Parked.IsEmpty)
        | Error(i, f) -> expect (sprintf "stopped at %d: %A" i f) false

    case
        "t3: the log is a Core op-stream: verified by Core, each record attributed by the principal's mapping, tampering detected"
    <| fun expect ->
        let text = Text.StringBuilder()

        let store =
            Store.lines options.HashFn (fun line -> text.Append line |> ignore) (fun () -> string text)

        let _, session, _ = busy options store
        let rs = records session
        expect "Core verifies the chain" (LogStream.verify options.HashFn rs)
        expect "the head is the last record's hash" (session.Head = (List.last rs).Hash)

        let authors =
            rs
            |> List.map (fun r ->
                match r.Op with
                | Entry.Step e -> Principal.toActor "journal" "1.0" e.Author = r.Actor
                | Entry.Lost _ -> false)

        expect "every actor is its author's mapping" (List.forall id authors)

        expect
            "an agent's step carries the agent's actor"
            (rs
             |> List.exists (fun r -> r.Actor = Fuaran.Core.Actor.Agent("model", "7", "agent:helper")))

        let tampered =
            rs
            |> List.mapi (fun i r ->
                if i = 1 then
                    {
                        r with
                            Actor = Fuaran.Core.Actor.Human "user:mallory"
                    }
                else
                    r)

        expect "a re-attributed record breaks the chain" (not (LogStream.verify options.HashFn tampered))

        match Jsonl.readVerified (LogStream.create options.HashFn) (string text) with
        | Ok read -> expect "the lines read back as the same records" (read = rs)
        | Error f -> expect (StreamLoadFault.toString f) false

    case "t3: the entry's canonical form reads back exactly, and refuses anything left over"
    <| fun expect ->
        let _, session, _ = busy options (Store.inMemory ())

        for r in records session do
            let text = EntryCodec.encode r.Op
            expect (sprintf "record %d reads back" r.Seq) (EntryCodec.decode text = Ok r.Op)

        expect
            "a trailing item is refused"
            (match EntryCodec.decode "{\"f\":[\"lost\",\"3\",\"secret\",\"x\"]}" with
             | Error _ -> true
             | Ok _ -> false)

    case "t3: snapshots and compaction are Core's: a compacted log replays from its snapshot to the same model"
    <| fun expect ->
        let runtime, session, _ = busy options (Store.inMemory ())
        let rs = records session
        let stream = Log.stream journal () failing options noReveal
        let encodeState (_: Folded<Model, Effect>) = "null"

        match
            Compacted.compactAt SnapshotMode.ChainOnly stream encodeState (fun _ -> None) (Log.initial journal ()) rs 5
        with
        | Error f -> expect (sprintf "compaction refused: %A" f) false
        | Ok compacted ->
            expect "Core verifies the compacted log" (Compacted.verify stream encodeState compacted)
            expect "the tail is what is left" (List.length compacted.Tail = List.length rs - 5)

            match Compacted.replayFrom stream.Witness compacted with
            | Ok folded -> expect "the snapshot and the tail reproduce the model" (folded.Model = runtime.Model)
            | Error f -> expect (sprintf "replay from the snapshot refused: %A" f) false

    case
        "t4: restore sends an idempotent effect again under the same key, answers the rest as interrupted, lists the resources again, and continues the log"
    <| fun expect ->
        let store = Store.inMemory ()
        let runtime, session, live = start options store
        runtime.Dispatch(Watch true, User "alice") |> ignore
        runtime.Dispatch(Add "x", User "alice") |> ignore
        let xId, _, _ = List.head runtime.Model.Notes
        expect "before: the save, the ping and the note were performed" (List.length live.Performed = 3)

        // The session stops with both replies outstanding. A new process folds the log and restores.
        match replayed options noReveal (records session) with
        | Error(i, f) -> expect (sprintf "stopped at %d: %A" i f) false
        | Ok folded ->
            expect
                "two effects are outstanding"
                (Map.toList folded.Outstanding |> List.map snd = [ Save(xId, "x"); Ping 0 ])

            let idempotent =
                match Log.declareIdempotent failing [ "save" ] with
                | Ok ids -> ids
                | Error e -> failwithf "%A" e

            let restoreOptions: RestoreOptions<Msg, Effect> = {
                Idempotent = idempotent
                Interrupted =
                    fun effect ->
                        match effect with
                        | Ping n -> Some(Interrupted("ping " + string n))
                        | _ -> Some(Interrupted "other")
            }

            let live2 = Live()
            let resumedSession = openSession options store
            let host = hostFor ignore

            let restored =
                Log.restore journal () live2.Interpreter host restoreOptions resumedSession folded

            restored.Start()
            expect "the save is sent again, the same value under the same key" (live2.Performed = [ Save(xId, "x") ])
            expect "the ping is answered as interrupted" (restored.Model.Interrupted = [ "ping 0" ])
            expect "the resource is listed and started again" (live2.Started = [ ResourceKey.named "ticker" "main" ])
            expect "the sequence continues past the log's last" (restored.Sequence = folded.Last + 1L)

            live2.Answer(Save(xId, "x"), Saved xId)
            restored.Dispatch(Add "y", User "alice") |> ignore
            let yId, _, _ = List.last restored.Model.Notes
            expect "an id minted after the restore is fresh" (yId <> xId)
            live2.Answer(Ping 1, Pinged 1)
            live2.Answer(Save(yId, "y"), Saved yId)
            let all = records resumedSession
            expect "the log continues as one chain" (LogStream.verify options.HashFn all)

            match replayed options noReveal all with
            | Ok again ->
                expect
                    "the whole log, the restore included, replays to the restored model"
                    (again.Model = restored.Model)

                expect "nothing is outstanding once everything was answered" again.Outstanding.IsEmpty
            | Error(i, f) -> expect (sprintf "the whole log stopped at %d: %A" i f) false

    case "t4: a step left parked when the session stopped is cancelled on restore, and the component is told"
    <| fun expect ->
        let store = Store.inMemory ()
        let runtime, session, _ = start options store
        runtime.Dispatch(Add "ask-later", User "alice") |> ignore
        expect "parked" (runtime.Parked.Length = 1)

        match replayed options noReveal (records session) with
        | Error(i, f) -> expect (sprintf "stopped at %d: %A" i f) false
        | Ok folded ->
            expect "the log holds it parked" (folded.Parked = Map.ofList [ 1L, 1 ])

            let restoreOptions: RestoreOptions<Msg, Effect> = {
                Idempotent = Set.empty
                Interrupted = fun _ -> None
            }

            let live2 = Live()
            let resumed = openSession options store
            let host = hostFor ignore

            let restored =
                Log.restore journal () live2.Interpreter host restoreOptions resumed folded

            restored.Start()
            expect "the component was told" (restored.Model.Cancelled = 1)
            expect "nothing is parked" restored.Parked.IsEmpty

            match replayed options noReveal (records resumed) with
            | Ok again ->
                expect "the log replays to the restored model" (again.Model = restored.Model && again.Parked.IsEmpty)
            | Error(i, f) -> expect (sprintf "stopped at %d: %A" i f) false

    case
        "t4: an idempotency declaration is checked against the registration: a kind not registered, or not call-shaped, is refused"
    <| fun expect ->
        expect "save is declarable" (Log.declareIdempotent failing [ "save" ] = Ok(set [ "save" ]))

        expect
            "a signal and an unknown kind are refused, by name"
            (Log.declareIdempotent failing [ "save"; "note"; "nope" ] = Error [ "note"; "nope" ])

    case "t5: the commitment's HMAC-SHA-256 meets RFC 4231, and a short key is refused"
    <| fun expect ->
        let ascii (s: string) = s |> Seq.map byte |> Array.ofSeq
        let mac k m = Hmac.hex (Hmac.sha256 k m)

        expect
            "test case 1"
            (mac (Array.create 20 0x0buy) (ascii "Hi There") = "b0344c61d8db38535ca8afceaf0bf12b881dc200c9833da726e9376c2e32cff7")

        expect
            "test case 2"
            (mac (ascii "Jefe") (ascii "what do ya want for nothing?") = "5bdcc146bf60754e6a042426089575c75a003f089d2739839dec58b964ec3843")

        expect
            "test case 6 (a key longer than the block)"
            (mac (Array.create 131 0xaauy) (ascii "Test Using Larger Than Block-Size Key - Hash Key First") = "60e431591ee0b67f0d8a26aacbf5b77f8e0bc6213728c5140546040f0ee37f54")

        expect
            "a key shorter than 32 bytes is refused"
            (match CommitmentKey.OfBytes(Array.create 31 1uy) with
             | Error _ -> true
             | Ok _ -> false)

        expect "the key prints nothing of itself" (not (contains "3" (string key)))
]

#if !FABLE_COMPILER
/// The .NET-only cases: what reflection and the platform can check, and a file.
let dotnetCases: Case list = [
    case "t1: Fuaran.MVU references no session code: a host without a sink takes none"
    <| fun expect ->
        let runtimeAssembly = typedefof<Runtime<_, _, _, _, _, _>>.Assembly

        expect
            "Fuaran.MVU does not reference Fuaran.MVU.Session"
            (runtimeAssembly.GetReferencedAssemblies()
             |> Array.forall (fun a -> a.Name <> "Fuaran.MVU.Session"))

        expect
            "and the session package is a separate assembly that references Fuaran.MVU"
            (typedefof<Session<_>>.Assembly <> runtimeAssembly
             && (typedefof<Session<_>>.Assembly.GetReferencedAssemblies()
                 |> Array.exists (fun a -> a.Name = "Fuaran.MVU")))

    case "t5: the commitment's HMAC agrees with the platform's HMAC-SHA-256"
    <| fun expect ->
        let rand = System.Random 649

        for n in [ 0; 1; 55; 64; 65; 200 ] do
            let k = Array.zeroCreate<byte> (32 + n)
            let m = Array.zeroCreate<byte> (n * 3)
            rand.NextBytes k
            rand.NextBytes m
            use platform = new System.Security.Cryptography.HMACSHA256(k)
            expect (sprintf "length %d" n) (Hmac.sha256 k m = platform.ComputeHash m)

    case "t3: the line store persists to a file on .NET and reads back verified"
    <| fun expect ->
        let path =
            IO.Path.Combine(IO.Path.GetTempPath(), sprintf "fuaran-mvu-session-%d.jsonl" (System.Random().Next()))

        try
            let store = Store.file options.HashFn path
            let runtime, session, _ = start options store
            runtime.Dispatch(Add "on disk", User "alice") |> ignore
            let fromFile = Store.file options.HashFn path

            match fromFile.Records() with
            | Ok rs ->
                expect "the file holds the log" (rs = records session && not rs.IsEmpty)

                match replayed options noReveal rs with
                | Ok folded -> expect "and replays to the model" (folded.Model = runtime.Model)
                | Error(i, f) -> expect (sprintf "stopped at %d: %A" i f) false
            | Error e -> expect e false
        finally
            if IO.File.Exists path then
                IO.File.Delete path
]
#endif
