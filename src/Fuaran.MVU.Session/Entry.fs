// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Diametrical Ltd

namespace Fuaran.MVU.Session

open Fuaran.Core
open Fuaran.MVU

/// How the host's disclosure function answers for one field (DECISIONS.md D28.3).
type Disclosure =
    /// Recorded in the clear.
    | Shown
    /// Recorded as a keyed commitment; the value is not in the log.
    | Withheld
    /// Never recorded: a value that must not reach a log, a model or a message (a secret is a reference, and the
    /// performer resolves it). The step that carried it is recorded as lost.
    | Secret

/// A field as the log holds it: the value in the clear, or the commitment to it.
[<RequireQualifiedAccess>]
type Recorded =
    | Clear of value: string
    | Committed of commitment: string

/// A withheld field's place in the log: the sequence of the step that carried it, its path and its commitment. What
/// the host's vault is keyed by, and what a replay asks the vault to reveal.
type WithheldField = {
    Sequence: int64
    Path: string
    Commitment: string
}

/// The host's codec for its messages: a message as named fields (each path once), and back. The disclosure
/// function is applied to every field it names.
type MessageCodec<'msg> = {
    Fields: 'msg -> (string * string) list
    Read: (string * string) list -> Result<'msg, string>
}

/// Where a recorded step's message came from: `Fuaran.MVU`'s `Source` with every string recorded under disclosure.
[<RequireQualifiedAccess>]
type EntrySource =
    | External
    | Answer of kind: Recorded * effect: int
    | Resource of key: Recorded list * instance: int
    | Runtime
    | Boot

/// How a recorded step was decided: the decision the runtime took, which a replay replays and never retakes.
[<RequireQualifiedAccess>]
type EntryOutcome =
    | Committed
    | Parked of id: int
    | Refused of reason: Recorded

/// The grant a step was decided with: `Fuaran.MVU`'s `Grant`, its request recorded under disclosure.
type GrantEntry = {
    Parked: int
    Request: Recorded
    Approver: Principal
    ExpiresAt: int64
    Generation: int
}

/// What a restore settled, recorded on the resumed boot (DECISIONS.md D28.5): an outstanding effect sent again under
/// the same key, an outstanding effect answered as interrupted (with the message the component was given, if any),
/// or a parked step cancelled (with the message the interpreter's `Refused` gave, if any).
[<RequireQualifiedAccess>]
type Settled =
    | Resent of cause: int64 * effect: int
    | Interrupted of cause: int64 * effect: int * answer: (string * Recorded) list option
    | Cancelled of sequence: int64 * answer: (string * Recorded) list option

/// Which boot a boot step was: `Init`'s, or a restore's.
[<RequireQualifiedAccess>]
type EntryBoot =
    | Init
    | Resumed of settled: Settled list

/// One decided step as the log records it: the stamp, the author (the principal itself; the op-stream's actor is
/// its mapping), the source, the cause, the message's fields under disclosure, which boot it was (a boot only), the
/// decision and the grant it was decided with.
type StepEntry = {
    Stamp: Stamp
    Author: Principal
    Source: EntrySource
    Cause: int64 option
    Message: (string * Recorded) list
    Boot: EntryBoot option
    Outcome: EntryOutcome
    Grant: GrantEntry option
}

/// Why a step was recorded as lost rather than recorded.
[<RequireQualifiedAccess>]
type LostReason =
    /// The disclosure function declared a field secret.
    | Secret
    /// The host's codec threw, or named a path twice.
    | Codec

/// One op of the session log (DECISIONS.md D28.1): a decided step, or the bare fact that the step at a sequence was
/// not recorded, and why. A lost step carries no field at all.
[<RequireQualifiedAccess>]
type Entry =
    | Step of StepEntry
    | Lost of sequence: int64 * reason: LostReason

/// The entry's canonical form (DECISIONS.md D28.1): one JSON object `{"f":[…]}` whose one member is a flat array of
/// strings in a fixed order, written with Core's string spelling and read back with Core's line reader. Every
/// number is its decimal text, the seed sixteen hexadecimal characters, a recorded field a tag (`=` clear, `#`
/// committed) and its text.
module EntryCodec =

    let private hexChars = "0123456789abcdef"

    let private seedText (x: uint64) : string =
        let chars = Array.zeroCreate<char> 16

        for i in 0..15 do
            chars.[i] <- hexChars.[int ((x >>> (60 - 4 * i)) &&& 0xFUL)]

        System.String chars

    let private seedOf (s: string) : Result<uint64, string> =
        if s.Length <> 16 then
            Error("a seed is sixteen hexadecimal characters: " + s)
        else
            let mutable acc = 0UL
            let mutable ok = true

            for c in s do
                let d = hexChars.IndexOf c

                if d < 0 then
                    ok <- false
                else
                    acc <- (acc <<< 4) ||| uint64 d

            if ok then
                Ok acc
            else
                Error("a seed is sixteen hexadecimal characters: " + s)

    let private encodeTo (out: ResizeArray<string>) (entry: Entry) =
        let add (s: string) = out.Add s
        let num (n: int64) = add (string n)
        let count (n: int) = add (string n)

        let recorded r =
            match r with
            | Recorded.Clear v ->
                add "="
                add v
            | Recorded.Committed c ->
                add "#"
                add c

        let principal p =
            match p with
            | User id -> List.iter add [ "user"; id; ""; "" ]
            | Host -> List.iter add [ "host"; ""; ""; "" ]
            | Agent(model, version, id) -> List.iter add [ "agent"; model; version; id ]
            | Foreign source -> List.iter add [ "foreign"; source; ""; "" ]

        let fields (fs: (string * Recorded) list) =
            count (List.length fs)

            for path, r in fs do
                add path
                recorded r

        let answer a =
            match a with
            | None -> add "none"
            | Some fs ->
                add "some"
                fields fs

        match entry with
        | Entry.Lost(sequence, reason) ->
            add "lost"
            num sequence

            add (
                match reason with
                | LostReason.Secret -> "secret"
                | LostReason.Codec -> "codec"
            )
        | Entry.Step e ->
            add "step"
            num e.Stamp.At
            num e.Stamp.Sequence
            add (seedText e.Stamp.Seed)
            principal e.Author

            match e.Source with
            | EntrySource.External -> add "external"
            | EntrySource.Answer(kind, effect) ->
                add "answer"
                recorded kind
                count effect
            | EntrySource.Resource(key, instance) ->
                add "resource"
                count (List.length key)
                List.iter recorded key
                count instance
            | EntrySource.Runtime -> add "runtime"
            | EntrySource.Boot -> add "boot"

            match e.Cause with
            | None -> add ""
            | Some c -> num c

            fields e.Message

            match e.Boot with
            | None -> add "none"
            | Some EntryBoot.Init -> add "init"
            | Some(EntryBoot.Resumed settled) ->
                add "resumed"
                count (List.length settled)

                for s in settled do
                    match s with
                    | Settled.Resent(cause, effect) ->
                        add "resent"
                        num cause
                        count effect
                    | Settled.Interrupted(cause, effect, a) ->
                        add "interrupted"
                        num cause
                        count effect
                        answer a
                    | Settled.Cancelled(sequence, a) ->
                        add "cancelled"
                        num sequence
                        answer a

            match e.Outcome with
            | EntryOutcome.Committed -> add "committed"
            | EntryOutcome.Parked id ->
                add "parked"
                count id
            | EntryOutcome.Refused reason ->
                add "refused"
                recorded reason

            match e.Grant with
            | None -> add "none"
            | Some g ->
                add "grant"
                count g.Parked
                recorded g.Request
                principal g.Approver
                num g.ExpiresAt
                count g.Generation

    /// The entry's canonical JSON text: the op a record hashes.
    let encode (entry: Entry) : string =
        let out = ResizeArray<string>()
        encodeTo out entry
        "{\"f\":[" + (out |> Seq.map Jsonl.quote |> String.concat ",") + "]}"

    /// A cursor over the entry's strings; every read answers the next item or refuses.
    type private Cursor(items: string[]) =
        let mutable at = 0

        member _.Next() : Result<string, string> =
            if at < items.Length then
                at <- at + 1
                Ok items.[at - 1]
            else
                Error "the entry ended early"

        member _.AtEnd = at = items.Length

    let private int64Of (s: string) : Result<int64, string> =
        match System.Int64.TryParse s with
        | true, v when string v = s -> Ok v
        | _ -> Error("not a canonical integer: " + s)

    let private intOf (s: string) : Result<int, string> =
        match System.Int32.TryParse s with
        | true, v when string v = s -> Ok v
        | _ -> Error("not a canonical integer: " + s)

    type private ResultBuilder() =
        member _.Bind(r: Result<'a, string>, f: 'a -> Result<'b, string>) : Result<'b, string> = Result.bind f r
        member _.Return(x: 'a) : Result<'a, string> = Ok x
        member _.ReturnFrom(r: Result<'a, string>) : Result<'a, string> = r

    let private result = ResultBuilder()

    let rec private many (n: int) (read: unit -> Result<'a, string>) (acc: 'a list) : Result<'a list, string> =
        if n = 0 then
            Ok(List.rev acc)
        else
            read () |> Result.bind (fun x -> many (n - 1) read (x :: acc))

    let private decodeFrom (c: Cursor) : Result<Entry, string> =
        let next () = c.Next()

        let number () = result {
            let! s = next ()
            return! int64Of s
        }

        let count () = result {
            let! s = next ()
            return! intOf s
        }

        let recorded () = result {
            let! tag = next ()
            let! v = next ()

            match tag with
            | "=" -> return Recorded.Clear v
            | "#" -> return Recorded.Committed v
            | t -> return! Error("not a recorded-field tag: " + t)
        }

        let principal () = result {
            let! case = next ()
            let! a = next ()
            let! b = next ()
            let! d = next ()

            match case with
            | "user" -> return User a
            | "host" -> return Host
            | "agent" -> return Agent(a, b, d)
            | "foreign" -> return Foreign a
            | other -> return! Error("not a principal: " + other)
        }

        let fields () = result {
            let! n = count ()

            return!
                many
                    n
                    (fun () -> result {
                        let! path = next ()
                        let! r = recorded ()
                        return path, r
                    })
                    []
        }

        let answer () = result {
            let! tag = next ()

            match tag with
            | "none" -> return None
            | "some" ->
                let! fs = fields ()
                return Some fs
            | t -> return! Error("not an answer: " + t)
        }

        let settlement () = result {
            let! t = next ()

            match t with
            | "resent" ->
                let! cause = number ()
                let! effect = count ()
                return Settled.Resent(cause, effect)
            | "interrupted" ->
                let! cause = number ()
                let! effect = count ()
                let! a = answer ()
                return Settled.Interrupted(cause, effect, a)
            | "cancelled" ->
                let! sequence = number ()
                let! a = answer ()
                return Settled.Cancelled(sequence, a)
            | other -> return! Error("not a settlement: " + other)
        }

        let source () = result {
            let! tag = next ()

            match tag with
            | "external" -> return EntrySource.External
            | "answer" ->
                let! k = recorded ()
                let! e = count ()
                return EntrySource.Answer(k, e)
            | "resource" ->
                let! n = count ()
                let! key = many n recorded []
                let! instance = count ()
                return EntrySource.Resource(key, instance)
            | "runtime" -> return EntrySource.Runtime
            | "boot" -> return EntrySource.Boot
            | t -> return! Error("not a source: " + t)
        }

        let boot () = result {
            let! tag = next ()

            match tag with
            | "none" -> return None
            | "init" -> return Some EntryBoot.Init
            | "resumed" ->
                let! n = count ()
                let! settled = many n settlement []
                return Some(EntryBoot.Resumed settled)
            | t -> return! Error("not a boot: " + t)
        }

        let outcome () = result {
            let! tag = next ()

            match tag with
            | "committed" -> return EntryOutcome.Committed
            | "parked" ->
                let! id = count ()
                return EntryOutcome.Parked id
            | "refused" ->
                let! reason = recorded ()
                return EntryOutcome.Refused reason
            | t -> return! Error("not an outcome: " + t)
        }

        let grant () = result {
            let! tag = next ()

            match tag with
            | "none" -> return None
            | "grant" ->
                let! parked = count ()
                let! request = recorded ()
                let! approver = principal ()
                let! expiresAt = number ()
                let! generation = count ()

                return
                    Some {
                        Parked = parked
                        Request = request
                        Approver = approver
                        ExpiresAt = expiresAt
                        Generation = generation
                    }
            | t -> return! Error("not a grant: " + t)
        }

        result {
            let! kind = next ()

            match kind with
            | "lost" ->
                let! sequence = number ()
                let! reason = next ()

                match reason with
                | "secret" -> return Entry.Lost(sequence, LostReason.Secret)
                | "codec" -> return Entry.Lost(sequence, LostReason.Codec)
                | r -> return! Error("not a lost reason: " + r)
            | "step" ->
                let! at = number ()
                let! sequence = number ()
                let! seedText = next ()
                let! seed = seedOf seedText
                let! author = principal ()
                let! source = source ()
                let! causeText = next ()

                let! cause =
                    if causeText = "" then
                        Ok None
                    else
                        int64Of causeText |> Result.map Some

                let! message = fields ()
                let! boot = boot ()
                let! outcome = outcome ()
                let! grant = grant ()

                return
                    Entry.Step {
                        Stamp = {
                            At = at
                            Sequence = sequence
                            Seed = seed
                        }
                        Author = author
                        Source = source
                        Cause = cause
                        Message = message
                        Boot = boot
                        Outcome = outcome
                        Grant = grant
                    }
            | other -> return! Error("not an entry: " + other)
        }

    /// The entry an `encode`d text holds, read with Core's line reader; anything else, or anything left over, is
    /// refused with the reason.
    let decode (text: string) : Result<Entry, string> =
        match Jsonl.parseLine 1 text |> Result.bind (Jsonl.stringsField "f") with
        | Error fault -> Error(JsonlFault.toString fault)
        | Ok items ->
            let cursor = Cursor(Array.ofList items)

            match decodeFrom cursor with
            | Ok entry when cursor.AtEnd -> Ok entry
            | Ok _ -> Error "the entry has items left over"
            | Error e -> Error e
