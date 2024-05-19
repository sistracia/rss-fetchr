module Handler

open System

open Shared
open Types

let getRSSList (urls: string array) : RSS seq Async =
    async {
        let! (rssList: RSS seq array) = urls |> RSSFetcher.parseRSSList

        return
            rssList
            |> Seq.fold (fun (acc: RSS seq) (elem: RSS seq) -> Seq.concat [ acc; elem ]) []
            |> Seq.sortByDescending _.PublishDate
    }

let register (connectionString: string) (sessionId: string) (loginForm: LoginForm) : LoginResponse =
    let userId: string = (DataAccess.insertUser connectionString loginForm)

    let loginResult: LoginResult =
        { LoginResult.UserId = userId
          LoginResult.RssUrls = Array.empty
          LoginResult.SessionId = sessionId
          LoginResult.Email = "" }

    Success loginResult

let login (connectionString: string) (sessionId: string) (loginForm: LoginForm) (user: User) : LoginResponse =
    if user.Password = loginForm.Password then
        let loginResult: LoginResult =
            { LoginResult.UserId = user.Id
              LoginResult.RssUrls = (DataAccess.getRSSUrls connectionString user.Id) |> List.toArray
              LoginResult.SessionId = sessionId
              LoginResult.Email = user.Email }

        Success loginResult
    else
        let loginError: LoginError = { LoginError.Message = "Password not match." }
        Failed loginError

let loginOrRegister (connectionString: string) (loginForm: LoginForm) : LoginResponse Async =
    async {
        let sessionId: string = Guid.NewGuid().ToString()

        let loginResponse: LoginResponse =
            match DataAccess.getUser connectionString loginForm with
            | None -> register connectionString sessionId loginForm
            | Some(user: User) -> login connectionString sessionId loginForm user

        match loginResponse with
        | Success(user: LoginResult) -> DataAccess.insertSession connectionString user.UserId sessionId
        | _ -> ()

        return loginResponse
    }

let saveRSSUrls (connectionString: string) (saveRSSUrlReq: SaveRSSUrlReq) : unit Async =
    async {
        let existingUrls: string array =
            (DataAccess.getRSSUrls connectionString saveRSSUrlReq.UserId) |> List.toArray

        let newUrls: string array =
            saveRSSUrlReq.Urls
            |> Array.filter (fun (url: string) -> not <| Array.contains url existingUrls)

        let deletedUrls: string array =
            existingUrls
            |> Array.filter (fun (url: string) -> not <| Array.contains url saveRSSUrlReq.Urls)

        if newUrls.Length <> 0 then
            DataAccess.insertUrls connectionString saveRSSUrlReq.UserId newUrls

        if deletedUrls.Length <> 0 then
            DataAccess.deleteUrls connectionString saveRSSUrlReq.UserId deletedUrls
    }

let initLogin (connectionString: string) (initLoginReq: InitLoginReq) : LoginResponse Async =
    async {
        return
            initLoginReq.SessionId
            |> DataAccess.getUserSession connectionString
            |> (function
            | None ->
                let loginError: LoginError = { LoginError.Message = "Session invalid." }
                Failed loginError
            | Some(user: User) ->
                let loginResult: LoginResult =
                    { LoginResult.UserId = user.Id
                      LoginResult.RssUrls = (DataAccess.getRSSUrls connectionString user.Id) |> List.toArray
                      LoginResult.SessionId = initLoginReq.SessionId
                      LoginResult.Email = user.Email }

                Success loginResult)
    }

let subscribe (connectionString: string) (subscribeReq: SubscribeReq) : unit Async =
    async { DataAccess.setUserEmail connectionString (subscribeReq.UserId, subscribeReq.Email) }

let unsubscribe (connectionString: string) (unsubscribeReq: UnsubscribeReq) : unit Async =
    async { (DataAccess.unsetUserEmail connectionString unsubscribeReq.Email) |> ignore }
