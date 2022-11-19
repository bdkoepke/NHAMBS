// Learn more about F# at http://fsharp.org

open System.IO
open FSharp.Data
open iText
open iText.Kernel.Pdf
open iText.Kernel.Pdf.Canvas.Parser.Listener
open iText.Kernel.Pdf.Canvas.Parser

type Issuer = {
    IssuerCode: string
    IssuerName: string
}

type Pool = {
    CUSIP: string
    PoolName: string
    Url: string
}

[<Literal>]
let baseUrl = "https://www.cmhc-schl.gc.ca/en/professionals/project-funding-and-mortgage-financing/securitization/nha-mbs/mbs-information-circulars/mbs-information-circulars-search-page"

let getPoolPage pageNumber issuerCode =
    let query = sprintf "?page=%i&issuercode=%s" pageNumber issuerCode
    let doc = HtmlDocument.Load(baseUrl + query)
    doc.CssSelect("tr")
    |> List.tail
    |> List.map (fun x ->
        let xs = x.Elements()
        let poolNumber = xs.[1].Elements() |> List.exactlyOne
        { CUSIP = xs.[2].Elements() |> List.exactlyOne |> string
          PoolName = poolNumber.Elements() |> List.exactlyOne |> string |> (fun x -> x.Trim())
          Url = poolNumber.AttributeValue("href")
        })
    |> List.filter (fun x -> x.Url <> "\\#")

let getPoolPages issuerCode =
    let query = sprintf "?page=1&issuercode=%s" issuerCode
    let doc = HtmlDocument.Load(baseUrl + query)
    doc.CssSelect("#mbs-results > a[href^='?page']")
    |> List.tryLast
    |> Option.map (fun x -> x.Elements() |> List.exactlyOne |> (string >> int))
    |> Option.defaultValue 1

let getPools issuerCode =
    let pages = getPoolPages issuerCode
    [1..pages]
    |> List.map (fun i -> getPoolPage i issuerCode)
    |> List.concat

let getIssuers () =
    let doc = HtmlDocument.Load(baseUrl)
    doc.CssSelect("#mbs_Issuer > option")
    |> List.filter (fun x -> not(x.AttributeValue("id") = "blank_option"))
    |> List.map (fun x -> { IssuerCode = x.AttributeValue("value"); IssuerName = String.concat " " (x.Elements() |> List.map string) })

[<EntryPoint>]
let main argv =
    let issuers = getIssuers()

    let issuerPools =
        issuers
        |> List.map (fun issuer -> issuer, getPools(issuer.IssuerCode))

    use file = new StreamWriter(File.OpenWrite("pool_urls.txt"))
    fprintf file "PoolName,CUSIP,Url\n"
    issuerPools
    |> List.map snd
    |> List.concat
    |> List.iter (fun x -> fprintf file "%s,%s,%s\n" x.PoolName x.CUSIP x.Url)
    
    0 // return an integer exit code