namespace NHAMBS.Circular

open System.IO
open System.Reflection
open System.Text.Json
open Json.Schema

/// A problem found while validating a circular against the JSON Schema.
type ValidationError =
    {
        /// JSON Pointer to the offending value, e.g. "/mbs/poolNumber".
        Path: string
        /// Human-readable description of the problem.
        Message: string
    }

/// Validation against the CMHC 2834 JSON Schema that is embedded in this assembly.
module Schema =

    let private resourceName = "NHAMBS.Circular.cmhc-2834-i-v2-extended-en.schema.json"

    let private schemaText =
        let assembly = Assembly.GetExecutingAssembly()
        use stream = assembly.GetManifestResourceStream resourceName

        if isNull stream then
            failwithf "Embedded schema resource '%s' was not found." resourceName

        use reader = new StreamReader(stream)
        reader.ReadToEnd()

    let private schema = lazy (Json.Schema.JsonSchema.FromText schemaText)

    let private evaluationPath (result: EvaluationResults) = result.EvaluationPath.ToString()

    let rec private collectErrors (result: EvaluationResults) =
        let schemaPath = evaluationPath result

        // Errors from the `if`/`else` arms of a conditional describe branches
        // that were tried and rejected. They are not useful to the caller; the
        // matching `then` arm already reports the real problem.
        let isConditionalArm = schemaPath.Contains "/if/" || schemaPath.Contains "/else/"

        // JsonSchema.Net reports both a summary error for an object and the
        // individual property errors. The summaries add noise; the leaf errors
        // are the ones that tell the caller what to fix.
        let isSummary (message: string) =
            message.StartsWith("Value does not match all of the specified schemas", System.StringComparison.Ordinal)
            || message.StartsWith("The if condition passed", System.StringComparison.Ordinal)
            || message.StartsWith("Some properties did not match the required schema", System.StringComparison.Ordinal)

        [
            if not isConditionalArm && not result.IsValid && not (isNull result.Errors) then
                for KeyValue(_, message) in result.Errors do
                    if not (isSummary message) then
                        let instancePath = result.InstanceLocation.ToString()

                        yield
                            {
                                Path =
                                    if System.String.IsNullOrEmpty instancePath then
                                        "$"
                                    else
                                        instancePath
                                Message = message
                            }

            if not (isNull result.Details) then
                for child in result.Details do
                    yield! collectErrors child
        ]

    /// Validate raw JSON text against the embedded CMHC 2834 schema.
    ///
    /// This uses the real `cmhc-2834-i-v2-extended-en.schema.json` Draft 2020-12
    /// schema, not the flattened provider-inference copy. It therefore enforces
    /// the conditional rules (`if`/`then`/`allOf`) as well as the structure.
    let validate (json: string) : Result<JsonElement, ValidationError list> =
        try
            use document = JsonDocument.Parse(json)

            let result =
                schema.Value.Evaluate(document.RootElement, EvaluationOptions(OutputFormat = OutputFormat.List))

            if result.IsValid then
                Ok(document.RootElement.Clone())
            else
                Error(collectErrors result |> List.distinctBy (fun error -> error.Path, error.Message))
        with :? JsonException as ex ->
            Error [ { Path = "$"; Message = ex.Message } ]
