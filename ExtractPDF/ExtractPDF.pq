// This file contains your Data Connector logic
section ExtractPDF;

[DataSource.Kind="ExtractPDF", Publish="ExtractPDF.Publish"]
shared ExtractPDF.Contents = (optional message as text) =>
    let
        _message = if (message <> null) then message else "(no message)",
        a = "Hello from ExtractPDF: " & _message
    in
        a;

// Data Source Kind description
ExtractPDF = [
    Authentication = [
        // Key = [],
        // UsernamePassword = [],
        // Windows = [],
        Implicit = []
    ],
    Label = Extension.LoadString("DataSourceLabel")
];

// Data Source UI publishing description
ExtractPDF.Publish = [
    Beta = true,
    Category = "Other",
    ButtonText = { Extension.LoadString("ButtonTitle"), Extension.LoadString("ButtonHelp") },
    LearnMoreUrl = "https://powerbi.microsoft.com/",
    SourceImage = ExtractPDF.Icons,
    SourceTypeImage = ExtractPDF.Icons
];

ExtractPDF.Icons = [
    Icon16 = { Extension.Contents("ExtractPDF16.png"), Extension.Contents("ExtractPDF20.png"), Extension.Contents("ExtractPDF24.png"), Extension.Contents("ExtractPDF32.png") },
    Icon32 = { Extension.Contents("ExtractPDF32.png"), Extension.Contents("ExtractPDF40.png"), Extension.Contents("ExtractPDF48.png"), Extension.Contents("ExtractPDF64.png") }
];
