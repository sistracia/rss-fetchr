﻿module App

open Feliz
open Elmish
open Elmish.Navigation
open Elmish.UrlParser
open Elmish.React
open Fable.Remoting.Client
open Feliz.DaisyUI
open Feliz.DaisyUI.Operators
open Shared

#if DEBUG
open Elmish.Debug
open Elmish.HMR
#endif

type ServerState =
    | Idle
    | Loading
    | ServerError of string

type RSSState =
    { ServerState: ServerState
      RSSList: RSS seq }

type SearchState = { Url: string; Urls: string array }

type State =
    { SearchState: SearchState
      RssState: RSSState
      LoginFormState: LoginForm }

type BrowserRoute = Search of string option

type LoginFormField =
    | Username
    | Password

type Msg =
    | UrlChanged of string
    | SetUrlParam of string
    | RemoveUrlParam of string
    | GotRSSList of RSS seq
    | SetLoginFormValue of (LoginFormField * string)
    | Login
    | GotLogin of unit
    | ErrorMsg of exn

let rpcStore: IRPCStore =
    Remoting.createApi ()
    |> Remoting.withRouteBuilder Route.routeBuilder
    |> Remoting.buildProxy<IRPCStore>

let getRSSList (urls: string array) =
    async { return! rpcStore.getRSSList urls }

let loginOrRegister (loginForm: LoginForm) =
    async { return! rpcStore.loginOrRegister loginForm }

let route = oneOf [ map Search (top <?> stringParam "url") ]

let getUrlSearch (route: BrowserRoute option) =
    match route with
    | Some(Search(query: string option)) ->
        match query with
        | Some query -> query.Split [| ',' |] |> Array.distinct
        | None -> [||]
    | _ -> [||]

let generateUrlSearch urls =
    match (urls |> String.concat ",") with
    | "" -> "/"
    | newUrl -> ("?url=" + newUrl)

let changeLoginFormState state fieldName value =
    match fieldName with
    | Username -> { state with Username = value }
    | Password -> { state with Password = value }

let cmdGetRssList urls =
    match urls with
    | [||] -> Cmd.none
    | _ -> Cmd.OfAsync.either getRSSList urls GotRSSList ErrorMsg

let urlUpdate (route: BrowserRoute option) state =
    match route with
    | Some(Search(_)) ->
        let newUrls = (getUrlSearch route)

        let (newServerState, newRSSList) =
            match newUrls with
            | [||] -> (Idle, Seq.empty)
            | _ -> (Loading, state.RssState.RSSList)

        let rssState =
            { state.RssState with
                RSSList = newRSSList
                ServerState = newServerState }

        let searchState =
            { state.SearchState with
                Urls = newUrls
                Url = "" }

        { state with
            SearchState = searchState
            RssState = rssState },
        cmdGetRssList newUrls
    | None ->
        let rssState =
            { state.RssState with
                ServerState = Idle
                RSSList = Seq.empty }

        let searchState =
            { state.SearchState with
                Urls = Array.empty
                Url = "" }

        { state with
            SearchState = searchState
            RssState = rssState },
        Cmd.none

let init (route: BrowserRoute option) =
    let initUrls = getUrlSearch route

    let rssState =
        { RSSList = Seq.empty
          ServerState =
            match initUrls with
            | [||] -> Idle
            | _ -> Loading }

    let searchState = { Urls = initUrls; Url = "" }

    let loginFormState = { Username = ""; Password = "" }

    { SearchState = searchState
      RssState = rssState
      LoginFormState = loginFormState },
    cmdGetRssList initUrls

let update (msg: Msg) (state: State) =
    match msg with
    | UrlChanged url ->
        let searchState = { state.SearchState with Url = url }
        { state with SearchState = searchState }, Cmd.none
    | SetUrlParam url ->
        let isUrlExists =
            state.SearchState.Urls |> Array.exists (fun (elm: string) -> elm = url)

        if url <> "" && not isUrlExists then
            state,
            Array.append state.SearchState.Urls [| url |]
            |> generateUrlSearch
            |> Navigation.newUrl
        else
            let searchState = { state.SearchState with Url = "" }
            { state with SearchState = searchState }, Cmd.none
    | RemoveUrlParam url ->
        state,
        state.SearchState.Urls
        |> Array.filter (fun (elm: string) -> elm <> url)
        |> generateUrlSearch
        |> Navigation.newUrl
    | GotRSSList response ->
        { state with
            RssState =
                { state.RssState with
                    ServerState = Idle
                    RSSList = response } },
        Cmd.none
    | SetLoginFormValue(fieldName, value) ->
        { state with
            LoginFormState = changeLoginFormState state.LoginFormState fieldName value },
        Cmd.none
    | Login -> state, Cmd.OfAsync.either loginOrRegister state.LoginFormState GotLogin ErrorMsg
    | GotLogin() -> state, Navigation.newUrl "/"
    | ErrorMsg e ->
        { state with
            RssState =
                { state.RssState with
                    ServerState = ServerError e.Message } },
        Cmd.none

let render (state: State) (dispatch: Msg -> unit) =
    Html.div
        [ prop.className "max-w-[768px] p-5 mx-auto flex flex-col gap-3"
          prop.children
              [ Daisy.navbar
                    [ prop.className "mb-2 shadow-lg bg-neutral text-neutral-content rounded-box"
                      prop.children
                          [ Daisy.navbarStart [ Html.h1 [ prop.text "RSS Fetchr" ] ]
                            Daisy.navbarEnd
                                [ Daisy.button.label
                                      [ button.square; button.ghost; prop.htmlFor "login-modal"; prop.text "Log In" ] ] ] ]
                Html.div
                    [ prop.className "w-full flex flex-wrap gap-3"
                      prop.children[Daisy.input
                                        [ input.bordered
                                          prop.value state.SearchState.Url
                                          prop.onChange (UrlChanged >> dispatch)
                                          prop.className "flex-1"
                                          prop.placeholder "https://overreacted.io/rss.xml" ]

                                    Daisy.button.button
                                        [ button.neutral
                                          prop.onClick (fun _ -> dispatch (SetUrlParam state.SearchState.Url))
                                          prop.text "Add" ]] ]
                Html.div
                    [ prop.className "flex flex-wrap gap-3"
                      prop.children
                          [ yield!
                                [ for url in state.SearchState.Urls do
                                      Html.div
                                          [ (color.bgNeutral ++ (prop.className "p-1 rounded-lg flex gap-1"))
                                            prop.children
                                                [ Daisy.button.button
                                                      [ button.error
                                                        button.xs
                                                        prop.onClick (fun _ -> dispatch (RemoveUrlParam url))
                                                        prop.text "X" ]
                                                  Html.span [ color.textNeutralContent; prop.text url ] ] ] ] ] ]
                Html.div
                    [ prop.className "flex flex-col gap-3"
                      prop.children
                          [ match state.RssState.ServerState with
                            | Loading ->
                                yield!
                                    [ for _ in 0..5 do
                                          Daisy.card
                                              [ card.bordered
                                                prop.className "flex flex-col gap-3 p-8"
                                                prop.children
                                                    [ Daisy.skeleton [ prop.className "h-6 w-full" ]
                                                      Daisy.skeleton [ prop.className "h-4 w-1/2" ]
                                                      Daisy.skeleton [ prop.className "h-4 w-1/4" ] ] ] ]
                            | Idle ->
                                yield!
                                    [ for rss in state.RssState.RSSList do
                                          Daisy.card
                                              [ card.bordered
                                                prop.children
                                                    [ Daisy.cardBody
                                                          [ Daisy.cardTitle rss.Title
                                                            Html.p (rss.LastUpdatedTime.ToString())
                                                            Daisy.cardActions
                                                                [ Daisy.link
                                                                      [ prop.href rss.Link
                                                                        prop.target "_blank"
                                                                        prop.rel "noopener"
                                                                        prop.text "Read" ] ] ] ] ] ]
                            | ServerError errorMsg -> Daisy.alert [ alert.error; prop.text errorMsg ] ] ]
                Html.div
                    [ Daisy.modalToggle [ prop.id "login-modal" ]
                      Daisy.modal.div
                          [ prop.children
                                [ Daisy.modalBox.div
                                      [ Html.h2 [ prop.text "Log In Form" ]
                                        Daisy.formControl
                                            [ Daisy.label
                                                  [ prop.htmlFor "login-username-field"
                                                    prop.children [ Daisy.labelText "Username" ] ]
                                              Daisy.input
                                                  [ input.bordered
                                                    prop.id "login-username-field"
                                                    prop.placeholder "Username"
                                                    prop.onChange (fun value ->
                                                        dispatch (SetLoginFormValue(Username, value))) ] ]
                                        Daisy.formControl
                                            [ Daisy.label
                                                  [ prop.htmlFor "password-username-field"
                                                    prop.children [ Daisy.labelText "Password" ] ]
                                              Daisy.input
                                                  [ input.bordered
                                                    prop.id "password-username-field"
                                                    prop.type' "password"
                                                    prop.placeholder "******"
                                                    prop.onChange (fun value ->
                                                        dispatch (SetLoginFormValue(Password, value))) ] ]
                                        Html.p "Account will be automatically created if not exist"
                                        Daisy.modalAction
                                            [ Daisy.button.label [ prop.htmlFor "login-modal"; prop.text "Cancel" ]
                                              Daisy.button.label
                                                  [ button.neutral
                                                    prop.htmlFor "login-modal"
                                                    prop.text "Log In"
                                                    prop.onClick (fun _ -> dispatch (Login)) ] ] ] ] ] ] ] ]

Program.mkProgram init update render
|> Program.toNavigable (parsePath route) urlUpdate
|> Program.withReactSynchronous "elmish-app"
#if DEBUG
|> Program.withDebugger
#endif
|> Program.run
