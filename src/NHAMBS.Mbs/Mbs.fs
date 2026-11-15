// Learn more about F# at http://fsharp.org

module NHAMBS.Mbs

open System
open FSharp.Data
open NHAMBS.Sources

type Issuer =
    {
        IssuerCode: string
        IssuerName: string
    }

type Pool =
    {
        CUSIP: string
        PoolName: string
        Url: string
    }

[<Literal>]
let baseUrl =
    "https://www.cmhc-schl.gc.ca/en/professionals/project-funding-and-mortgage-financing/securitization/nha-mbs/mbs-information-circulars/mbs-information-circulars-search-page"

let absoluteUrl (url: string) =
    if Source.isUrl url then
        url
    else
        Uri(Uri(baseUrl), url).ToString()

let parsePoolPage (document: HtmlDocument) =
    document.CssSelect("tr")
    |> List.tail
    |> List.choose (fun row ->
        let cells = row.Elements()
        let poolNumber = cells.[1].Elements() |> List.exactlyOne
        let href = poolNumber.AttributeValue("href")

        if href = "\\#" then
            None
        else
            Some
                {
                    CUSIP = cells.[2].Elements() |> List.exactlyOne |> string
                    PoolName =
                        poolNumber.Elements()
                        |> List.exactlyOne
                        |> string
                        |> (fun value -> value.Trim())
                    Url = absoluteUrl href
                })

let getPoolPage pageNumber issuerCode =
    let query = sprintf "?page=%i&issuercode=%s" pageNumber issuerCode
    HtmlDocument.Load(baseUrl + query) |> parsePoolPage

let parsePoolPages (document: HtmlDocument) =
    document.CssSelect("#mbs-results > a[href^='?page']")
    |> List.tryLast
    |> Option.map (fun x -> x.Elements() |> List.exactlyOne |> (string >> int))
    |> Option.defaultValue 1

let getPoolPages issuerCode =
    let query = sprintf "?page=1&issuercode=%s" issuerCode
    HtmlDocument.Load(baseUrl + query) |> parsePoolPages

let getPools issuerCode =
    let pages = getPoolPages issuerCode
    [ 1..pages ] |> List.map (fun i -> getPoolPage i issuerCode) |> List.concat

let parseIssuers (document: HtmlDocument) =
    document.CssSelect("#mbs_Issuer > option")
    |> List.filter (fun x -> not (x.AttributeValue("id") = "blank_option"))
    |> List.map (fun x ->
        {
            IssuerCode = x.AttributeValue("value")
            IssuerName = String.concat " " (x.Elements() |> List.map string)
        })

let getIssuers () =
    HtmlDocument.Load(baseUrl) |> parseIssuers
