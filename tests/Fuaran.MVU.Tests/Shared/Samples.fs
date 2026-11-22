// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Diametrical Ltd

/// The sample components the ported laws run on: the prototype's four ports, restated against the real
/// types with their domains cut to what each check needs. Host-neutral. None names a value `component`.
module Fuaran.MVU.Tests.Samples

open Fuaran.MVU

/// Port A: the smallest application. The view carries a message value, so a test can send what a control
/// would.
module Counter =

    type Model = { Count: int }

    type Msg =
        | Increment
        | Decrement

    type View = { Shown: int; OnClick: Msg }

    let counter: Component<unit, Model, Msg, NoEffect, NoResource, View> =
        Component.plain
            (fun () -> { Count = 0 }, [])
            (fun msg m ->
                match msg with
                | Increment -> { Count = m.Count + 1 }, []
                | Decrement -> { Count = m.Count - 1 }, [])
            (fun _ -> Map.empty)
            (fun m -> { Shown = m.Count; OnClick = Increment })

/// Port B: a module with many remote calls, one effect case per call and one reply case per answer; a
/// constant listener resource and a latest-wins search resource whose description is the query.
module Portfolio =

    type Portfolio = { Id: string; Name: string }

    type Section =
        | Overview
        | Parties
        | Tensions

    type Effect =
        | ListPortfolios
        | GetPortfolio of id: string
        | GetTensions of id: string
        | GetParties of id: string
        | SavePortfolio of Portfolio
        | CreatePortfolio of name: string
        | RouteWrite of string

    type Resource =
        | RouteChanges of prefix: string
        | ProjectSearch of query: string

    type Msg =
        | PortfoliosLoaded of Result<Portfolio list, string>
        | PortfolioLoaded of Result<Portfolio, string>
        | TensionsLoaded of Result<string list, string>
        | PartiesLoaded of Result<string list, string>
        | Saved of Result<unit, string>
        | Created of Result<Portfolio, string>
        | ShowSection of Section
        | LoadTensions
        | RouteChanged of string
        | SelectPortfolio of string
        | SearchProjects of string
        | ProjectsFound of query: string * string list
        | Save of Portfolio
        | Create of string

    type Model = {
        Portfolios: Portfolio list
        Selected: Portfolio option
        Section: Section
        Tensions: string list
        Parties: string list
        Refusal: string option
        Notice: string option
        Search: string option
        Projects: string list
        Route: string
    }

    let private sectionOf (route: string) =
        if route.EndsWith "/parties" then Parties
        elif route.EndsWith "/tensions" then Tensions
        else Overview

    /// The id a deep link names: `portfolio/<id>[/section]`.
    let private addressed (route: string) =
        match route.Split '/' |> List.ofArray with
        | "portfolio" :: id :: _ when id <> "" -> Some id
        | _ -> None

    let private loadsFor (id: string) : Step<Msg, Effect> list = [
        Do(GetPortfolio id)
        Do(GetTensions id)
        Do(GetParties id)
        Do(RouteWrite("portfolio/" + id))
    ]

    let init (route: string) : Model * Step<Msg, Effect> list =
        {
            Portfolios = []
            Selected = None
            Section = sectionOf route
            Tensions = []
            Parties = []
            Refusal = None
            Notice = None
            Search = None
            Projects = []
            Route = route
        },
        [ Do ListPortfolios ]
        @ (addressed route |> Option.map (fun id -> loadsFor id) |> Option.defaultValue [])

    let update (msg: Msg) (m: Model) : Model * Step<Msg, Effect> list =
        match msg with
        | PortfoliosLoaded(Ok ps) ->
            let selected =
                addressed m.Route
                |> Option.bind (fun id -> ps |> List.tryFind (fun p -> p.Id = id))

            {
                m with
                    Portfolios = ps
                    Selected = selected
            },
            []
        | PortfolioLoaded(Ok p) -> { m with Selected = Some p }, []
        | TensionsLoaded(Ok ts) -> { m with Tensions = ts }, []
        | PartiesLoaded(Ok ps) -> { m with Parties = ps }, []
        | Saved(Ok()) -> { m with Notice = Some "saved" }, [ Then LoadTensions ]
        | Created(Ok p) ->
            {
                m with
                    Notice = Some("created " + p.Name)
            },
            [ Do ListPortfolios ]
        | PortfoliosLoaded(Error e)
        | PortfolioLoaded(Error e)
        | TensionsLoaded(Error e)
        | PartiesLoaded(Error e)
        | Saved(Error e)
        | Created(Error e) -> { m with Refusal = Some e }, []
        | ShowSection s ->
            let route =
                "portfolio/"
                + (m.Selected |> Option.map (fun p -> p.Id) |> Option.defaultValue "")
                + "/"
                + (string s).ToLower()

            { m with Section = s }, [ Do(RouteWrite route); Then LoadTensions ]
        | LoadTensions ->
            match m.Selected with
            | Some p -> m, [ Do(GetTensions p.Id) ]
            | None -> m, []
        | RouteChanged route ->
            { m with Route = route },
            (addressed route
             |> Option.map (fun id -> [ Then(SelectPortfolio id) ])
             |> Option.defaultValue [])
        | SelectPortfolio id ->
            {
                m with
                    Selected = m.Portfolios |> List.tryFind (fun p -> p.Id = id)
            },
            loadsFor id
        | SearchProjects q -> { m with Search = Some q }, []
        | ProjectsFound(q, projects) ->
            (if m.Search = Some q then
                 {
                     m with
                         Projects = projects
                         Search = None
                 }
             else
                 m),
            []
        | Save p -> m, [ Do(SavePortfolio p) ]
        | Create name -> m, [ Do(CreatePortfolio name) ]

    let resources (m: Model) : Map<ResourceKey, Resource> =
        Map.ofList (
            [ ResourceKey.constant "route", RouteChanges "portfolio" ]
            @ (m.Search
               |> Option.map (fun q -> [ ResourceKey.constant "project-search", ProjectSearch q ])
               |> Option.defaultValue [])
        )

    let portfolio: Component<string, Model, Msg, Effect, Resource, Model> =
        Component.plain init update resources id

/// Port C: a shell composing two children with different types, typed by wrapping, and erased for a
/// dynamic set of modules. The notes child is also the stamped module of `stamp-argument.fsx`.
module Notes =

    type Note = { Id: string; Text: string; At: int64 }

    type Effect =
        | Persist of Note
        | OpenTeam of teamId: string

    type Msg =
        | Add of string
        | Saved of string

    type Model = {
        Notes: Note list
        Audit: (string * int64) list
    }

    let init () : Model * Step<Msg, Effect> list = { Notes = []; Audit = [] }, []

    /// `update` reads the stamp: the note is created and timestamped in one message, and its id is minted
    /// from the stamp's seed.
    let update (stamp: Stamp) (msg: Msg) (m: Model) : Model * Step<Msg, Effect> list =
        match msg with
        | Add text ->
            let note = {
                Id = Stamp.freshId 0 stamp
                Text = text
                At = stamp.At
            }

            { m with Notes = note :: m.Notes }, [ Do(Persist note) ]
        | Saved id ->
            {
                m with
                    Audit = (id, stamp.At) :: m.Audit
            },
            []

    let notes: Component<unit, Model, Msg, Effect, NoResource, Model> = {
        Init = init
        Update = update
        Resources = fun _ -> Map.empty
        View = id
    }

module Shell =

    type Msg =
        | Navigate of string
        | TokenAcquired of string
        | TokenLost
        | Notification of string
        | SetTeam of string
        | NotesMsg of Notes.Msg
        | CounterMsg of Counter.Msg
        | FeedEvent of string
        | ModuleMsg of moduleId: string * obj

    type Effect =
        | NotesEffect of Notes.Effect
        | ModuleEffect of moduleId: string * obj

    type Resource =
        | NavListener
        | BusListener
        | KeyListener
        | Notifications of token: string
        | NotesFeed
        | ModuleResource of moduleId: string * obj

    type Model = {
        Active: string
        Token: string option
        Toasts: string list
        Team: string option
        Notes: Notes.Model
        Counter: Counter.Model
        Events: string list
        Modules: Map<string, obj>
    }

    /// The notes child, lifted under `notes`: its `OpenTeam` effect is an up-call the shell intercepts into
    /// `SetTeam`, every other effect is wrapped.
    let liftedNotes =
        Component.liftWith
            (fun stamp key -> Stamp.forChild key stamp)
            "notes"
            NotesMsg
            (fun e ->
                match e with
                | Notes.OpenTeam team -> Then(SetTeam team)
                | other -> Do(NotesEffect other))
            (fun (_: NoResource) -> NotesFeed)
            Notes.notes

    let liftedCounter =
        Component.lift
            "counter"
            CounterMsg
            (fun (_: NoEffect) -> ModuleEffect("counter", box ()))
            (fun (_: NoResource) -> ModuleResource("counter", box ()))
            Counter.counter

    let init () : Model * Step<Msg, Effect> list =
        let notes, notesSteps = liftedNotes.Init()
        let counter, counterSteps = liftedCounter.Init()

        {
            Active = "home"
            Token = None
            Toasts = []
            Team = None
            Notes = notes
            Counter = counter
            Events = []
            Modules = Map.empty
        },
        notesSteps @ counterSteps

    let update (stamp: Stamp) (msg: Msg) (m: Model) : Model * Step<Msg, Effect> list =
        match msg with
        | Navigate page -> { m with Active = page }, []
        | TokenAcquired t -> { m with Token = Some t }, [ Then(FeedEvent "token") ]
        | TokenLost -> { m with Token = None }, []
        | Notification text -> { m with Toasts = text :: m.Toasts }, []
        | SetTeam team -> { m with Team = Some team }, []
        | NotesMsg inner ->
            let notes, steps = liftedNotes.Update stamp inner m.Notes
            { m with Notes = notes }, steps
        | CounterMsg inner ->
            let counter, steps = liftedCounter.Update stamp inner m.Counter
            { m with Counter = counter }, steps
        | FeedEvent e -> { m with Events = e :: m.Events }, [ Then(NotesMsg(Notes.Add("event:" + e))) ]
        | ModuleMsg(id, payload) ->
            match id with
            | "counter" ->
                // The erased route into the counter: the payload is tested for the module's message type on
                // both hosts (a type test without type arguments, which Fable honours), and a mis-addressed
                // payload is a failure in update.
                match payload with
                | :? Counter.Msg as inner ->
                    let counter, _ = Counter.counter.Update stamp inner m.Counter
                    { m with Counter = counter }, []
                | _ -> failwith "mis-addressed module message"
            | _ -> m, []

    let resources (m: Model) : Map<ResourceKey, Resource> =
        Map.ofList (
            [
                ResourceKey.constant "nav", NavListener
                ResourceKey.constant "bus", BusListener
                ResourceKey.constant "keys", KeyListener
            ]
            @ (m.Token
               |> Option.map (fun t -> [ ResourceKey.constant "notifications", Notifications t ])
               |> Option.defaultValue [])
            @ (if m.Active = "notes" then
                   [ ResourceKey.prefix "notes" (ResourceKey.constant "feed"), NotesFeed ]
               else
                   [])
        )

    let shell: Component<unit, Model, Msg, Effect, Resource, Model> = {
        Init = init
        Update = update
        Resources = resources
        View = id
    }

/// Port D: an agent loop as a component. The provider call is a cancellable resource keyed by request id,
/// the active clock a resource listed only while the loop runs, tools are effects, the budget is model
/// state.
module Agent =

    type Request = { Id: int; Messages: string list }

    type Reply = {
        Text: string
        Tools: (string * string) list
        Usage: int
    }

    type Phase =
        | Idle
        | Calling of Request
        | Painting of pendingTool: string
        | Asking of question: string
        | Halted of string

    type Effect =
        | AwaitPaint
        | ReadDom of tool: string

    type Resource =
        | ProviderCall of Request
        | ActiveClock

    type Msg =
        | Ask of string
        | ProviderReplied of requestId: int * Reply
        | Painted
        | DomRead of tool: string * result: string
        | Tick
        | AskAnswered of string
        | Stop

    type Model = {
        Phase: Phase
        Turns: string list
        Tokens: int
        MaxTokens: int
        ActiveSeconds: int
        NextRequest: int
    }

    let init (maxTokens: int) : Model * Step<Msg, Effect> list =
        {
            Phase = Idle
            Turns = []
            Tokens = 0
            MaxTokens = maxTokens
            ActiveSeconds = 0
            NextRequest = 1
        },
        []

    let private call (m: Model) (turn: string) =
        let request = {
            Id = m.NextRequest
            Messages = m.Turns @ [ turn ]
        }

        {
            m with
                Phase = Calling request
                Turns = m.Turns @ [ turn ]
                NextRequest = m.NextRequest + 1
        }

    let update (msg: Msg) (m: Model) : Model * Step<Msg, Effect> list =
        match msg with
        | Ask text when m.Tokens >= m.MaxTokens -> { m with Phase = Halted "max tokens" }, []
        | Ask text -> call m ("user:" + text), []
        | ProviderReplied(id, reply) ->
            match m.Phase with
            | Calling request when request.Id = id ->
                let m = {
                    m with
                        Tokens = m.Tokens + reply.Usage
                }

                if m.Tokens >= m.MaxTokens then
                    { m with Phase = Halted "max tokens" }, []
                else
                    match reply.Tools with
                    | [] when reply.Text = "" -> call m "assistant:broken", []
                    | [] ->
                        {
                            m with
                                Phase = Halted "completed"
                                Turns = m.Turns @ [ "assistant:" + reply.Text ]
                        },
                        []
                    | ("askUser", question) :: _ -> { m with Phase = Asking question }, []
                    | (tool, _) :: _ -> { m with Phase = Painting tool }, [ Do AwaitPaint ]
            | _ -> m, []
        | Painted ->
            match m.Phase with
            | Painting tool -> m, [ Do(ReadDom tool) ]
            | _ -> m, []
        | DomRead(tool, result) -> call m ("tool:" + tool + "=" + result), []
        | Tick ->
            {
                m with
                    ActiveSeconds = m.ActiveSeconds + 1
            },
            []
        | AskAnswered answer -> call m ("user:" + answer), []
        | Stop -> { m with Phase = Halted "aborted" }, []

    let resources (m: Model) : Map<ResourceKey, Resource> =
        match m.Phase with
        | Calling request ->
            Map.ofList [
                ResourceKey.constant "provider", ProviderCall request
                ResourceKey.constant "active-clock", ActiveClock
            ]
        | Painting _ -> Map.ofList [ ResourceKey.constant "active-clock", ActiveClock ]
        | _ -> Map.empty

    let agent: Component<int, Model, Msg, Effect, Resource, Model> =
        Component.plain init update resources id
