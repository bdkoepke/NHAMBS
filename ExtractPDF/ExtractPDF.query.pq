// Use this file to write queries to test your data connector
let
    Source = Pdf.Tables(File.Contents("C:\Users\brandon\source\repos\NHAMBS\NHAMBS\bin\Debug\netcoreapp3.1\97518025.pdf"), [Implementation="1.2"]),
    Table005 = Source{[Id="Table005"]}[Data],
    #"Changed Type" = Table.TransformColumnTypes(Table005,{{"Column1", type text}, {"Column2", type number}, {"Column3", type number}}),
    #"Renamed Columns" = Table.RenameColumns(#"Changed Type",{{"Column1", "Issuer"}, {"Column2", "Dollar Value"}, {"Column3", "% of Loans"}}),
    #"Filtered Rows" = Table.SelectRows(#"Renamed Columns", each ([Issuer] <> ""))
in
    #"Filtered Rows"