module NHAMBS.NormalizationTests

open Xunit
open NHAMBS.Etl.Common

[<Fact>]
let ``Issuer legal suffixes and OCR spacing collapse to one key`` () =
    Assert.Equal(normalizeIssuer "The Bank of Nova Scotia", normalizeIssuer "BANK OF NOVA SCOTIA INC.")
    Assert.Equal(normalizeIssuer "First National Financial", normalizeIssuer "First National Financial GP")

[<Fact>]
let ``Issuer display names keep the source text for the alias table`` () =
    Assert.Equal("Bank of Montrea)", issuerDisplayName "Bank of Montrea)")
    Assert.Equal("MERWL LYNCH CANADA LNC", issuerDisplayName "MERWL LYNCH CANADA LNC")

[<Fact>]
let ``Unreadable issuer text becomes Unknown issuer`` () =
    Assert.Equal("Unknown issuer", issuerDisplayName "POOL NO. : 97507987 FIRST PAYMENT DUE : 15 décembre")

[<Fact>]
let ``Insurer names normalize across case spacing and legal variants`` () =
    let cases =
        [
            "CMHC"
            "Canada Mortgage and Housing Corporation (CMHC)"
            "Canada   Mortgage  and  Housing   Corporation  (CMHC)"
            "Canada Mortgage Housing Corporation (CMHC)"
            "Canada Housing and Mortgage Corporation"
        ]

    for value in cases do
        Assert.Equal("Canada Mortgage and Housing Corporation (CMHC)", canonicalInsurerName value)

[<Fact>]
let ``Insurer boilerplate after the name is removed`` () =
    let value =
        "Genworth Financial Mortgage Insurance Company Canada The term “mortgage”, as used herein, includes both a debt and the mortgage"

    Assert.Equal("Genworth Financial Mortgage Insurance Company Canada", canonicalInsurerName value)

[<Fact>]
let ``Combined insurer labels keep each canonical insurer`` () =
    Assert.Equal(
        "Canada Mortgage and Housing Corporation (CMHC) and Sagen Mortgage Insurance Company Canada",
        canonicalInsurerName "CMHC and Sagen"
    )

[<Fact>]
let ``Rate-band spacing and precision variants snap to one bucket`` () =
    let cases =
        [
            "3.751 - 4.000"
            "3.76 - 4.00"
            "3.76     -        4.00"
            "3.7501 - 4.0000"
            "3.751-4.000"
        ]

    for value in cases do
        let band = canonicalRateBand value
        Assert.Equal("3.7500|4.0000", band.Key)
        Assert.Equal("3.75 - 4.00", band.Label)
        Assert.Equal(Some 3.75m, band.LowerPercent)
        Assert.Equal(Some 4.00m, band.UpperPercent)

[<Fact>]
let ``Malformed rate band keeps its source label and raw key`` () =
    let band = canonicalRateBand "n/a"
    Assert.Equal("raw:n/a", band.Key)
    Assert.Equal("n/a", band.Label)
    Assert.Equal(None, band.LowerPercent)
    Assert.Equal(None, band.UpperPercent)

[<Fact>]
let ``Inverted rate band snaps to a forward quarter-point bucket`` () =
    let band = canonicalRateBand "6.01 - 5.25"
    Assert.Equal("6.0000|6.2500", band.Key)
    Assert.Equal("6.00 - 6.25", band.Label)
