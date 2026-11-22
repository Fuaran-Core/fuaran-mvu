// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Diametrical Ltd

namespace Fuaran.MVU.Session

open Fuaran.Core

/// The session log as a Core op-stream: its op is the `Entry`, encoded canonically (`EntryCodec`), hashed under the
/// canonical config by the host's chain hash. This stream reads only the codec, which is all Core's verifiers and
/// line reader need; the replay's stream (`Log.stream`) is the same codec with the component as its reducer.
module LogStream =

    /// The witness Core's verifiers and line reader read: the codec, and a reducer that changes nothing.
    let witness: StreamWitness<Entry, unit, unit> = {
        Apply = fun _ state -> Ok state
        Encode = EntryCodec.encode
        Decode = EntryCodec.decode
    }

    /// The log's stream under `hashFn`.
    let create (hashFn: HashFn) : Stream<Entry, unit, unit> = OpStream.canonical hashFn witness

    /// Whether `records` verify as a chain under `hashFn`: Core's `OpStream.verifyChain`.
    let verify (hashFn: HashFn) (records: OpRecord<Entry> list) : bool =
        OpStream.verifyChain (create hashFn) records

/// Where a session's records go (DECISIONS.md D28.7). The session appends each record as it is chained; `Records`
/// reads the log back. A store that cannot write throws to the runtime's caller rather than drop a step.
type Store = {
    Append: OpRecord<Entry> -> unit
    Records: unit -> Result<OpRecord<Entry> list, string>
}

module Store =

    /// The default, and the only store in a browser unless the host declares one of its own: the records in memory,
    /// gone with the page.
    let inMemory () : Store =
        let records = ResizeArray<OpRecord<Entry>>()

        {
            Append = records.Add
            Records = fun () -> Ok(List.ofSeq records)
        }

    /// Line persistence through Core's JSONL: each record is written as one line (`Jsonl.tryWrite`) through
    /// `append`, and `read` hands back the whole text, which Core's verified reader (`Jsonl.readVerified`) reads and
    /// checks as a chain. The text goes wherever the host's two functions put it.
    let lines (hashFn: HashFn) (append: string -> unit) (read: unit -> string) : Store =
        let stream = LogStream.create hashFn

        {
            Append =
                fun record ->
                    match Jsonl.tryWrite stream.Witness [ record ] with
                    | Ok line -> append (line + "\n")
                    | Error fault ->
                        failwith ("the session log cannot write a record: " + JsonlWriteFault.toString fault)
            Records =
                fun () ->
                    let text = read ()

                    if text.Length = 0 then
                        Ok []
                    else
                        Jsonl.readVerified stream (text.TrimEnd('\n'))
                        |> Result.mapError StreamLoadFault.toString
        }

#if !FABLE_COMPILER
    /// Line persistence to a file on .NET: `lines` over appending to and reading `path`. Not compiled under Fable,
    /// where a browser has no file to write.
    let file (hashFn: HashFn) (path: string) : Store =
        lines hashFn (fun line -> System.IO.File.AppendAllText(path, line)) (fun () ->
            if System.IO.File.Exists path then
                System.IO.File.ReadAllText path
            else
                "")
#endif
