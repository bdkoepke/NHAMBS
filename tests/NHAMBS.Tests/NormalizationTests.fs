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
