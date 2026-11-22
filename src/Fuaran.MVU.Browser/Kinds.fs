// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Diametrical Ltd

namespace Fuaran.MVU.Browser

open Fuaran.Core
open Fuaran.ClientEffects
open Fuaran.MVU

/// Which of the browser's two storage areas a storage effect reads or writes.
[<RequireQualifiedAccess>]
type StorageArea =
    /// `localStorage`: kept across sessions.
    | Local
    /// `sessionStorage`: kept for the tab's session.
    | Session

/// The standard vocabulary (DECISIONS.md D30.1): the eight client effects of `Fuaran.ClientEffects`, carried whole in
/// one case and declared nowhere else, and this package's two browser-only kinds, storage and the one-shot timer. An
/// application embeds it as one case of its own effect type. A value with equality, so a test compares the effects a
/// step asked for without a browser.
type StandardEffect =
    /// A client effect: clipboard, navigation, history, focus, download, file read, print or confirm.
    | Client of ClientEffect
    /// Read `key` from `area`; answers `Stored` with the value, or `None` when the key holds nothing.
    | ReadStorage of area: StorageArea * key: string
    /// Write `value` under `key` in `area`. No answer.
    | WriteStorage of area: StorageArea * key: string * value: string
    /// Remove `key` from `area`. No answer.
    | RemoveStorage of area: StorageArea * key: string
    /// Answer `Elapsed label` once, after `milliseconds`.
    | After of label: string * milliseconds: int

/// The standard resource (DECISIONS.md D30.1): a repeating timer is long-lived, so it is a resource the model lists
/// while it wants the ticks, never an effect. It emits `Ticked label` every `milliseconds` until the model stops
/// listing it.
type StandardResource = Every of label: string * milliseconds: int

/// Why a file body was not read (DECISIONS.md D30.4).
type FileReadFailure =
    /// The node address resolved to nothing.
    | NoNode
    /// The node holds no selected file.
    | NoFile
    /// The encoding is not one of `ClientEffect.readEncodings`.
    | UnknownEncoding
    /// The browser's reader failed.
    | ReadFailed of reason: string

/// The one answer a standard effect gives, or an event of the standard resource (DECISIONS.md D30.4). An application
/// maps it into its own message type with one function, so a performer answers in the application's type.
type StandardAnswer =
    /// The reader answered the confirmation `token` asked: `yes` for OK.
    | Confirmed of token: string * yes: bool
    /// The body of the file selected at `nodeId`, read with `encoding`, or why it was not read.
    | FileBody of nodeId: string * encoding: string * body: Result<string, FileReadFailure>
    /// What `key` held in `area`.
    | Stored of area: StorageArea * key: string * value: string option
    /// The one-shot timer `label` elapsed.
    | Elapsed of label: string
    /// The repeating timer `label` ticked.
    | Ticked of label: string

/// The standard kinds and their classes (DECISIONS.md D30.2), and the invocation each standard effect is decided as.
/// Nothing here touches the browser: this half compiles and is tested on .NET and under Fable.
module Standard =

    /// The namespace every standard kind's id is minted under.
    [<Literal>]
    let Prefix = "fuaran.mvu.browser/"

    /// The class of a kind that reads state the model does not hold: a selected file, storage.
    [<Literal>]
    let Reads = "reads"

    /// The class of a kind that writes state that outlives the page or leaves it: the clipboard, storage, a file.
    [<Literal>]
    let Writes = "writes"

    /// The class of a kind that changes the document or its address: a navigation, a history entry.
    [<Literal>]
    let Navigation = "navigation"

    /// The class of a kind that acts on what the reader sees or is asked outside the view the model draws: a
    /// dialogue, the focus, a save prompt.
    [<Literal>]
    let UserVisible = "user-visible"

    let private kind
        (name: string)
        (shape: Shape)
        (classes: string list)
        (determinism: DeterminismSource list)
        (answers: Trust)
        (replies: string list)
        : EffectKind =
        {
            Id = Prefix + name
            Shape = shape
            Classes = Set.ofList classes
            Determinism = Set.ofList determinism
            Answers = answers
            Replies = replies
        }

    /// Every standard kind, each registered once with its classes: the eight client kinds in the vocabulary's
    /// declaration order (their names are its discriminator, `ClientEffect.kind`), the three storage kinds, the
    /// one-shot timer and the repeating timer. A kind's classes are fixed here; an effect cannot state its own.
    let kinds: EffectKind list = [
        kind "WriteToClipboard" Shape.Signal [ Writes ] [] FirstParty []
        kind "Navigate" Shape.Signal [ Navigation ] [] FirstParty []
        kind "PushState" Shape.Signal [ Navigation ] [] FirstParty []
        kind "Focus" Shape.Signal [ UserVisible ] [] FirstParty []
        kind "Download" Shape.Signal [ Writes; UserVisible ] [] FirstParty []
        kind "ReadFileBody" Call [ Reads ] [] (Untrusted "file") [ "FileBody" ]
        kind "Print" Shape.Signal [ UserVisible ] [] FirstParty []
        kind "Confirm" Call [ UserVisible ] [] FirstParty [ "Confirmed" ]
        kind "ReadStorage" Call [ Reads ] [] (Untrusted "storage") [ "Stored" ]
        kind "WriteStorage" Shape.Signal [ Writes ] [] FirstParty []
        kind "RemoveStorage" Shape.Signal [ Writes ] [] FirstParty []
        kind "After" Call [] [ Clock ] FirstParty [ "Elapsed" ]
        kind "Every" Stream [] [ Clock ] FirstParty [ "Ticked" ]
    ]

    /// The id of the kind an effect is decided under.
    let kindOf (effect: StandardEffect) : string =
        match effect with
        | Client clientEffect -> Prefix + ClientEffect.kind clientEffect
        | ReadStorage _ -> Prefix + "ReadStorage"
        | WriteStorage _ -> Prefix + "WriteStorage"
        | RemoveStorage _ -> Prefix + "RemoveStorage"
        | After _ -> Prefix + "After"

    /// The id of the repeating timer's kind.
    [<Literal>]
    let EveryKind = Prefix + "Every"

    let private areaName (area: StorageArea) : string =
        match area with
        | StorageArea.Local -> "Local"
        | StorageArea.Session -> "Session"

    /// Where a URL goes (DECISIONS.md D30.2): `Remote` of its origin, lower-cased, when it is an absolute `http` or
    /// `https` URL or a scheme-relative one; `Local` otherwise, which is every relative route.
    let destinationOf (url: string) : Destination =
        let lower = url.Trim().ToLowerInvariant()

        let authorityFrom (start: int) =
            let rest = lower.Substring start

            let stop =
                [ '/'; '?'; '#' ]
                |> List.map (fun c -> rest.IndexOf c)
                |> List.filter (fun i -> i >= 0)
                |> List.fold min rest.Length

            rest.Substring(0, stop)

        if lower.StartsWith "https://" then
            Remote("https://" + authorityFrom 8)
        elif lower.StartsWith "http://" then
            Remote("http://" + authorityFrom 7)
        elif lower.StartsWith "//" then
            Remote("//" + authorityFrom 2)
        else
            Local

    /// The invocation an effect is decided as: its kind, its arguments in canonical text and its destination. A
    /// client effect's arguments are its wire encoding (`ClientEffect.encode`); a storage or timer effect's are Core's
    /// canonical rendering of its members. A navigation and a download go where their URL goes.
    let invocation (effect: StandardEffect) : Invocation =
        let render members = Some(Json.render (JObj members))

        let args, destination =
            match effect with
            | Client clientEffect ->
                let destination =
                    match clientEffect with
                    | Navigate(route, _) -> destinationOf route
                    | Download(url, _) -> destinationOf url
                    | _ -> Local

                Some(ClientEffect.encode clientEffect), destination
            | ReadStorage(area, key)
            | RemoveStorage(area, key) -> render [ "area", JStr(areaName area); "key", JStr key ], Local
            | WriteStorage(area, key, value) ->
                render [ "area", JStr(areaName area); "key", JStr key; "value", JStr value ], Local
            | After(label, milliseconds) -> render [ "label", JStr label; "milliseconds", JInt milliseconds ], Local

        {
            Kind = kindOf effect
            Args = args
            Destination = destination
        }

    /// The invocation a repeating timer is decided as when the resource plan starts it.
    let resourceInvocation (_: ResourceKey) (resource: StandardResource) : Invocation =
        let (Every(label, milliseconds)) = resource

        {
            Kind = EveryKind
            Args = Some(Json.render (JObj [ "label", JStr label; "milliseconds", JInt milliseconds ]))
            Destination = Local
        }
