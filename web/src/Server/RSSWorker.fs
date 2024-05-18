module RSSWorker

open System
open System.IO
open System.Threading
open System.Threading.Tasks

open Shared
open Types

type IRSSProcessingService =
    abstract member DoWork: stoppingToken: CancellationToken -> Task

type RSSProcessingService(connectionString: string, publicHost: string, mailService: Mail.IMailService) =

    member private __.GetLatestRemoteRSS(histories: RSSHistory array) =
        async {
            return!
                histories
                |> Array.map (fun (history: RSSHistory) ->
                    // Transform to async function that return tuple of url and RSS list
                    async {
                        let! (remoteRSSList: RSS seq) = RSSFetcher.parseRSS history.Url
                        return (history.Url, remoteRSSList)
                    })
                |> Async.Parallel
        }

    /// New RSS determined by latest updated is higher compare to the stored one in database.
    member private __.FilterNewRemoteRSS (history: RSSHistory) (remoteRSS: RSS) : bool =
        DateTime.Compare(remoteRSS.PublishDate, history.LatestUpdated) >= 0

    member private this.MapRSSHistoryWithRemote (remoteRSSListMap: Map<string, RSS seq>) (history: RSSHistory) =
        remoteRSSListMap.Item history.Url
        |> Seq.filter (this.FilterNewRemoteRSS history)

    /// Get new RSS from remote URL and compare with RSS history in database
    /// to get detect new publised RSS from remote URL
    member this.FilterNewRSS (histories: RSSHistory array) (remoteRSSList: (string * RSS seq) array) : RSS seq array =
        // Create map used for value lookup
        let remoteRSSListMap: Map<string, RSS seq> = remoteRSSList |> Map.ofArray
        histories |> Array.map (this.MapRSSHistoryWithRemote remoteRSSListMap)

    member __.FlattenNewRSS(recentRemoteRSSList: RSS seq array) : RSS seq =
        recentRemoteRSSList
        |> Array.fold (fun (acc: RSS seq) (elem: RSS seq) -> Seq.concat [ acc; elem ]) [] // Flatten the RSS list

    member __.LatestNewRSS(rssListOfList: RSS seq array) : RSS array =
        rssListOfList
        |> Array.map (fun ((rssList: RSS seq)) -> rssList |> Seq.tryHead)
        |> Array.choose (function
            | Some(rss: RSS) -> Some(rss)
            | _ -> None)
        |> Array.map (fun (rss: RSS) -> rss)

    member private __.CreateEmailRecipient(recipient: string) : Mail.MailRecipient =
        { Mail.MailRecipient.EmailToId = recipient
          Mail.MailRecipient.EmailToName =
            match recipient.Split "@" |> Array.tryHead with
            | None -> recipient
            | Some(username: string) -> username }

    member private __.CreateEmailTextBody(newRSSList: RSS seq) : string =
        newRSSList |> Seq.map (fun (rss: RSS) -> rss.Title) |> String.concat ", "

    member private __.CreateEmailHtmlBody (newRSSList: RSS seq) (recipientEmail: string) : string =
        let templateFilePath =
            Directory.GetCurrentDirectory() + "/Templates/email-notification.html"

        let emailTemplateText = File.ReadAllText(templateFilePath)

        let itemFilePath =
            Directory.GetCurrentDirectory() + "/Templates/email-notification-item.html"

        let itemText = File.ReadAllText(itemFilePath)

        let emailBody =
            newRSSList
            |> Seq.map (fun (rss: RSS) ->
                String.Format(itemText, rss.Link, rss.Title, rss.OriginHostUrl, rss.OriginHost))
            |> String.concat ""

        emailTemplateText
            .Replace("{0}", emailBody)
            .Replace("{1}", publicHost)
            .Replace("{2}", recipientEmail)

    member private __.SendEmail (recipient: Mail.MailRecipient) (htmlBody: string) (textBody: string) =
        let mailData =
            { Mail.MailData.EmailTextBody = textBody
              Mail.MailData.EmailHtmlBody = htmlBody
              Mail.MailData.EmailSubject = "New RSS Release!"
              Mail.MailData.EmailRecipient = recipient }

        mailService.SendMail mailData

    member private this.ProceedSubscriber(rssAggregate: RSSEmailsAggregate) : Async<(string * RSS seq) option> =
        async {
            let email: string = rssAggregate.Email
            let rssHistories: RSSHistory array = rssAggregate.HistoryPairs |> Array.choose id

            let! (rssList: (string * RSS seq) array) = this.GetLatestRemoteRSS rssHistories
            let newRSS: RSS seq array = this.FilterNewRSS rssHistories rssList
            let flatNewRSS: RSS seq = this.FlattenNewRSS newRSS

            if (newRSS |> Seq.length) <> 0 && email <> "" then
                try
                    this.SendEmail
                        (this.CreateEmailRecipient email)
                        (this.CreateEmailHtmlBody flatNewRSS email)
                        (this.CreateEmailTextBody flatNewRSS)

                // Ignore if there is an error when sending email because invalid email or etc
                with _ ->
                    ()

                return Some(rssAggregate.UserId, (this.LatestNewRSS newRSS))
            else
                return None
        }

    interface IRSSProcessingService with

        member this.DoWork(stoppingToken: CancellationToken) =
            task {
                let! (newRSSList: (string * RSS seq) option array) =
                    DataAccess.aggreateRssEmails stoppingToken connectionString
                    |> List.map (this.ProceedSubscriber)
                    |> Async.Parallel

                newRSSList
                |> Array.choose id
                |> Array.iter (fun (newRSS: (string * RSS seq)) ->
                    snd newRSS
                    |> Seq.map _.Origin
                    |> Seq.toArray
                    |> DataAccess.insertUrlsWithCancellation stoppingToken connectionString (fst newRSS))
            }
