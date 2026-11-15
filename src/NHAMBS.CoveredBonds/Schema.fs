namespace NHAMBS.CoveredBonds

open System.Collections.Concurrent
open System.IO
open System.Reflection
open System.Text.Json
open Json.Schema

/// A problem found while validating covered-bond JSON against its JSON Schema.
type ValidationError =
    {
        /// JSON Pointer to the offending value, e.g. "/issuers/0/registrationDate".
        Path: string
        /// Human-readable description of the problem.
        Message: string
    }

module private Validator =

    let private schemaText resourceName =
        let assembly = Assembly.GetExecutingAssembly()
        use stream = assembly.GetManifestResourceStream resourceName

        if isNull stream then
            failwithf "Embedded schema resource '%s' was not found." resourceName

        use reader = new StreamReader(stream)
        reader.ReadToEnd()

    let private schemas = ConcurrentDictionary<string, Lazy<JsonSchema>>()

    let private getSchema resourceName =
        schemas.GetOrAdd(resourceName, fun name -> lazy (JsonSchema.FromText(schemaText name)))

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
            || message.StartsWith("Value does not match any of the specified schemas", System.StringComparison.Ordinal)

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

    let validate resourceName (json: string) : Result<JsonElement, ValidationError list> =
        let schema = getSchema resourceName

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

/// Validation against the CMHC Canadian Covered Bonds Registry schema embedded
/// in this assembly.
module RegistrySchema =

    let private resourceName =
        "NHAMBS.CoveredBonds.cmhc-covered-bonds-registry.schema.json"

    /// Validate raw JSON text against `cmhc-covered-bonds-registry.schema.json`.
    let validate (json: string) = Validator.validate resourceName json

/// Validation against the CMHC Canadian Registered Covered Bond Program schema
/// embedded in this assembly.
module ProgramSchema =

    let private resourceName =
        "NHAMBS.CoveredBonds.cmhc-covered-bond-program.schema.json"

    /// Validate raw JSON text against `cmhc-covered-bond-program.schema.json`.
    let validate (json: string) = Validator.validate resourceName json
