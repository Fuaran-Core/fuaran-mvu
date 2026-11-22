// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Diametrical Ltd

/// The claims ladder (`proofs.json` at the repository root) held to the tree: every `proved` row names a
/// theorem that is a top-level declaration of the model it names, and that model is on the proof leg's
/// roster; every checked model has a `proved` row; every `tested` row's family is one of the closed forms
/// and its cases exist in that family; levels and phases are the closed forms; every row carries a
/// non-empty statement and boundary; every document a `policy` row names exists. What it does NOT check is
/// the prose. Beside each clause sits a go-red fixture, so a green ladder is known to be one that could
/// have failed. .NET only: it reads the tree.
module Fuaran.MVU.Tests.Ladder

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open Fuaran.MVU.Tests.Check

/// The repository root: the nearest ancestor of the test assembly holding `proofs.json` and the solution.
let private repoRoot =
    let rec up (dir: DirectoryInfo) =
        if isNull dir then
            failwith "the repository root (proofs.json beside Fuaran.MVU.slnx) is not an ancestor of the test assembly"
        elif
            File.Exists(Path.Combine(dir.FullName, "proofs.json"))
            && File.Exists(Path.Combine(dir.FullName, "Fuaran.MVU.slnx"))
        then
            dir.FullName
        else
            up dir.Parent

    up (DirectoryInfo AppContext.BaseDirectory)

let private levels = set [ "proved"; "tested"; "assumed"; "policy" ]

let private str (e: JsonElement) (name: string) =
    match e.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
    | _ -> None

let private strings (e: JsonElement) (name: string) =
    match e.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.Array -> [ for x in v.EnumerateArray() -> x.GetString() ]
    | _ -> []

/// The names of the cases a `tested` family holds, by family.
let private casesOf (family: string) : string list option =
    if family = "Proofs.Oracle" then
        Some(List.map (fun (c: Case) -> c.Name) (Differential.cases @ StepDifferential.cases @ GateDifferential.cases))
    elif family = "Suite:Laws.cases" then
        Some(List.map (fun (c: Case) -> c.Name) Laws.cases)
    elif family = "Suite:PortLaws.cases" then
        Some(List.map (fun (c: Case) -> c.Name) PortLaws.cases)
    elif family = "Suite:RuntimeLaws.cases" then
        Some(List.map (fun (c: Case) -> c.Name) RuntimeLaws.cases)
    elif family = "Suite:GateLaws.cases" then
        Some(List.map (fun (c: Case) -> c.Name) GateLaws.cases)
    elif family = "Suite:SessionLaws.cases" then
        Some(List.map (fun (c: Case) -> c.Name) (SessionLaws.cases @ SessionLaws.dotnetCases))
    elif family.StartsWith "Conformance." || family.StartsWith "Suite:" then
        None
    else
        None

/// Hold the ladder text to the tree at `root`; the failures, each naming its row.
let check (root: string) (ladderText: string) : string list =
    let failures = ResizeArray<string>()
    let fail (s: string) = failures.Add s

    let roster =
        use doc =
            JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "proofs", "modules.json")))

        [
            for m in doc.RootElement.GetProperty("modules").EnumerateArray() ->
                m.GetProperty("module").GetString(), m.GetProperty("kind").GetString()
        ]

    use doc = JsonDocument.Parse ladderText
    let ladder = doc.RootElement

    if str ladder "kind" <> Some "proofs" then
        fail "the ladder's kind is not \"proofs\""

    if (str ladder "subject" |> Option.defaultValue "") = "" then
        fail "the ladder names no subject"

    let claims =
        match ladder.TryGetProperty "claims" with
        | true, c when c.ValueKind = JsonValueKind.Array -> [ for x in c.EnumerateArray() -> x ]
        | _ ->
            fail "the ladder has no claims array"
            []

    let ids = claims |> List.choose (fun c -> str c "id")

    for duplicate in ids |> List.countBy id |> List.filter (fun (_, n) -> n > 1) |> List.map fst do
        fail (sprintf "row id %s is not unique" duplicate)

    let provedModels = ResizeArray<string>()

    for c in claims do
        let id = str c "id" |> Option.defaultValue "<no id>"
        let level = str c "level" |> Option.defaultValue ""

        if not (levels.Contains level) then
            fail (sprintf "%s: level %A is not one of proved | tested | assumed | policy" id level)

        for field in [ "statement"; "boundary" ] do
            if (str c field |> Option.defaultValue "").Trim() = "" then
                fail (sprintf "%s: %s is empty" id field)

        if not (Regex.IsMatch(str c "phase" |> Option.defaultValue "", "^[a-z0-9-]+#[0-9]+$")) then
            fail (sprintf "%s: phase is not <side>#<number>" id)

        match c.TryGetProperty "evidence" with
        | false, _ -> fail (sprintf "%s: no evidence" id)
        | true, ev ->
            match level with
            | "proved" ->
                let model = str ev "model" |> Option.defaultValue ""
                let path = Path.Combine(root, model.Replace('/', Path.DirectorySeparatorChar))

                if not (model.StartsWith "proofs/" && File.Exists path) then
                    fail (sprintf "%s: model %A is not a file under proofs/" id model)
                else
                    let moduleName = Path.GetFileNameWithoutExtension model

                    if not (roster |> List.exists (fun (m, _) -> m = moduleName)) then
                        fail (sprintf "%s: model %s is not on the roster (proofs/modules.json)" id moduleName)

                    provedModels.Add moduleName
                    let text = File.ReadAllText path

                    for name in (str ev "theorem" |> Option.toList) @ strings ev "supporting" do
                        if not (Regex.IsMatch(text, sprintf "(?m)^(let rec |let |val )%s\\b" (Regex.Escape name))) then
                            fail (sprintf "%s: %s is not a top-level declaration of %s" id name model)

                    if str ev "theorem" = None then
                        fail (sprintf "%s: a proved row names no theorem" id)
            | "tested" ->
                let family = str ev "family" |> Option.defaultValue ""

                match casesOf family with
                | None ->
                    fail (
                        sprintf
                            "%s: family %A is not one this tree holds (Proofs.Oracle, Suite:Laws.cases, Suite:PortLaws.cases, Suite:RuntimeLaws.cases, Suite:GateLaws.cases)"
                            id
                            family
                    )
                | Some known ->
                    let cases = strings ev "cases"

                    if cases.IsEmpty then
                        fail (sprintf "%s: a tested row names no cases" id)

                    for case in cases do
                        if not (List.contains case known) then
                            fail (sprintf "%s: case %A is not in family %s" id case family)
            | "assumed" ->
                if (str ev "assumption" |> Option.defaultValue "").Trim() = "" then
                    fail (sprintf "%s: an assumed row states no assumption" id)
            | "policy" ->
                match str ev "document" with
                | Some d when File.Exists(Path.Combine(root, d.Replace('/', Path.DirectorySeparatorChar))) -> ()
                | d -> fail (sprintf "%s: document %A does not exist" id d)
            | _ -> ()

    for m, kind in roster do
        if (kind = "extracted" || kind = "proofOnly") && not (provedModels.Contains m) then
            fail (sprintf "model %s is on the roster and has no proved row" m)

    List.ofSeq failures

let private ladderText =
    lazy (File.ReadAllText(Path.Combine(repoRoot, "proofs.json")))

let private goesRed (what: string) (mutate: string -> string) =
    case (sprintf "Proofs.Ladder go-red: %s is refused" what) (fun expect ->
        let mutated = mutate ladderText.Value
        expect "the mutation took" (mutated <> ladderText.Value)
        expect "the checker refuses it" (not (check repoRoot mutated).IsEmpty))

/// The ladder held to the tree, and the go-red fixtures beside it.
let cases: Case list = [
    case "Proofs.Ladder: proofs.json agrees with this tree" (fun expect ->
        for failure in check repoRoot ladderText.Value do
            expect failure false)
    goesRed "a theorem that is not declared" (fun t ->
        t.Replace("\"theorem\": \"drain_is_queue\"", "\"theorem\": \"drain_is_a_stack\""))
    goesRed "a level outside the closed set" (fun t -> t.Replace("\"level\": \"policy\"", "\"level\": \"guessed\""))
    goesRed "a case the family does not hold" (fun t ->
        t.Replace(
            "Proofs.Oracle drain: the extracted model and DrainBuffer answer alike",
            "Proofs.Oracle drain: a case nobody wrote"
        ))
    goesRed "a model off the roster" (fun t ->
        t.Replace("\"model\": \"proofs/KeyedDiff.fst\"", "\"model\": \"proofs/check.ps1\""))
    goesRed "an empty boundary" (fun t ->
        Regex.Replace(t, "\"boundary\": \"[^\"]+\"", "\"boundary\": \"\"", RegexOptions.None, TimeSpan.FromSeconds 5.0))
]
