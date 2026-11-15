module NHAMBS.Tests

open FSharp.Data
open Xunit
open NHAMBS.Mbs

let private poolPageHtml =
    """
    <html>
      <body>
        <table>
          <tr><th>Issuer</th><th>Pool</th><th>CUSIP</th></tr>
          <tr>
            <td>Issuer A</td>
            <td><a href="https://example.test/pools/97500001.pdf">97500001</a></td>
            <td>62945ZR69</td>
          </tr>
          <tr>
            <td>Issuer A</td>
            <td><a href="https://example.test/pools/97500002.pdf">97500002</a></td>
            <td>62945ZR77</td>
          </tr>
          <tr>
            <td>Issuer A</td>
            <td><a href="\#">Not a pool</a></td>
            <td>000000000</td>
          </tr>
        </table>
      </body>
    </html>
    """

[<Fact>]
let ``parsePoolPage returns pool rows and ignores placeholder links`` () =
    let pools = HtmlDocument.Parse poolPageHtml |> parsePoolPage

    Assert.Equal(2, pools.Length)
    Assert.Equal("97500001", pools.[0].PoolName)
    Assert.Equal("62945ZR69", pools.[0].CUSIP)
    Assert.Equal("https://example.test/pools/97500001.pdf", pools.[0].Url)

[<Fact>]
let ``parsePoolPages returns the last page link`` () =
    let html =
        """
        <html>
          <body>
            <div id="mbs-results">
              <a href="?page=1&issuercode=975">1</a>
              <a href="?page=2&issuercode=975">2</a>
              <a href="?page=4&issuercode=975">4</a>
            </div>
          </body>
        </html>
        """

    Assert.Equal(4, HtmlDocument.Parse html |> parsePoolPages)

[<Fact>]
let ``parsePoolPages defaults to one page when there is no pager`` () =
    let html = """<html><body><div id="mbs-results"></div></body></html>"""

    Assert.Equal(1, HtmlDocument.Parse html |> parsePoolPages)

[<Fact>]
let ``parseIssuers skips the blank option`` () =
    let html =
        """
        <html>
          <body>
            <select id="mbs_Issuer">
              <option id="blank_option" value="">Select an issuer</option>
              <option value="975">1st Choice Savings and Credit Union Ltd.</option>
              <option value="980">Bank of Montreal</option>
            </select>
          </body>
        </html>
        """

    let issuers = HtmlDocument.Parse html |> parseIssuers

    Assert.Equal(2, issuers.Length)
    Assert.Equal("975", issuers.[0].IssuerCode)
    Assert.Equal("1st Choice Savings and Credit Union Ltd.", issuers.[0].IssuerName)
    Assert.Equal("980", issuers.[1].IssuerCode)
    Assert.Equal("Bank of Montreal", issuers.[1].IssuerName)

[<Fact>]
let ``parsePoolPage resolves relative pool links`` () =
    let html =
        """
        <html>
          <body>
            <table>
              <tr><th>Issuer</th><th>Pool</th><th>CUSIP</th></tr>
              <tr>
                <td>Issuer A</td>
                <td><a href="/en/pools/97500001.pdf">97500001</a></td>
                <td>62945ZR69</td>
              </tr>
            </table>
          </body>
        </html>
        """

    let pools = HtmlDocument.Parse html |> parsePoolPage

    Assert.Equal(1, pools.Length)
    Assert.Equal("https://www.cmhc-schl.gc.ca/en/pools/97500001.pdf", pools.[0].Url)
