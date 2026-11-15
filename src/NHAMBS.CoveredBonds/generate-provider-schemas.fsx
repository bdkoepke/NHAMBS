#!/usr/bin/env -S dotnet fsi
/// Generate the FSharp.Data JsonProvider schemas from the covered-bond JSON Schemas.
///
/// FSharp.Data's JsonProvider can infer types from a JSON Schema through the
/// `Schema` static parameter, but its schema reader only understands:
///
///   type, properties, required, items, format
///
/// It does not resolve `$ref` (or use `enum`/`allOf`/`anyOf`/`if`/`then` to
/// build types), so a schema that uses `$defs` and references them produces
/// untyped `IJsonDocument` properties. This script produces flattened,
/// provider-safe copies of the real schemas:
///
///   * every local `$ref` is replaced by the referenced schema;
///   * enum-only properties get an explicit `type`;
///   * keywords that only affect validation (conditionals, patterns, limits,
///     `$defs`, etc.) are dropped.
///
/// The real schemas remain the source of truth. Re-run this script whenever
/// either schema changes:
///
///     dotnet fsi generate-provider-schemas.fsx

open System.IO
open System.Text.Encodings.Web
open System.Text.Json
open System.Text.Json.Nodes

let here = __SOURCE_DIRECTORY__ |> Path.GetFullPath
let root = Path.GetFullPath(Path.Combine(here, "..", ".."))

let schemas =
    [
        "cmhc-covered-bonds-registry.schema.json"
        "cmhc-covered-bond-program.schema.json"
    ]

// Keywords that do not influence the provider's type inference. Keeping them
// would only make the generated files harder to read.
let dropKeys =
    Set.ofList
        [
            "$defs"
            "definitions"
            "allOf"
            "anyOf"
            "oneOf"
            "if"
            "then"
            "else"
            "not"
            "additionalProperties"
            "pattern"
            "minimum"
            "maximum"
            "multipleOf"
            "minItems"
            "maxItems"
            "minLength"
            "maxLength"
            "uniqueItems"
            "propertyNames"
            "dependentSchemas"
            "dependentRequired"
            "const"
            "examples"
            "default"
        ]

let rec clone (node: JsonNode) : JsonNode =
    if isNull node then
        null
    else
        match node with
        | :? JsonObject as obj ->
            let copy = JsonObject()

            for property in obj do
                copy[property.Key] <- clone property.Value

            copy :> JsonNode
        | :? JsonArray as array ->
            let copy = JsonArray()

            for item in array do
                copy.Add(clone item)

            copy :> JsonNode
        | _ -> node.DeepClone()

let inferTypeFromEnum (values: JsonArray) =
    let kindOf (value: JsonNode) =
        match value with
        | :? JsonValue as jsonValue -> jsonValue.GetValueKind()
        | _ -> JsonValueKind.Undefined

    let kinds = values |> Seq.map kindOf |> Seq.toList

    if List.forall ((=) JsonValueKind.String) kinds then
        Some "string"
    elif List.forall (fun kind -> kind = JsonValueKind.True || kind = JsonValueKind.False) kinds then
        Some "boolean"
    elif List.forall ((=) JsonValueKind.Number) kinds then
        Some "number"
    else
        None

let generate (fileName: string) =
    let source = Path.Combine(here, "schema", fileName)

    let targetName =
        Path.GetFileName(fileName).Replace(".schema.json", ".provider.schema.json")

    let target = Path.Combine(here, "schema", targetName)
    let schema = JsonNode.Parse(File.ReadAllText source) :?> JsonObject
    let resolving = ResizeArray<string>()

    let resolveRef (ref: string) : JsonNode =
        if not (ref.StartsWith "#/") then
            failwithf "Only local references are supported: %s" ref

        if resolving.Contains ref then
            failwithf "Recursive reference is not supported: %s" ref

        let mutable node = schema :> JsonNode

        for rawPart in ref.Substring(2).Split('/') do
            let part = rawPart.Replace("~1", "/").Replace("~0", "~")
            node <- (node :?> JsonObject)[part]

        clone node

    let rec resolve (node: JsonNode) : JsonNode =
        if isNull node then
            null
        else
            match node with
            | :? JsonArray as array ->
                let result = JsonArray()

                for item in array do
                    result.Add(resolve item)

                result :> JsonNode
            | :? JsonObject as obj ->
                if obj.ContainsKey "$ref" then
                    let ref = obj["$ref"].GetValue<string>()
                    let referenced = resolveRef ref
                    resolving.Add ref

                    let resolved =
                        try
                            resolve referenced
                        finally
                            resolving.RemoveAt(resolving.Count - 1)

                    // A $ref may legally have sibling annotations. Keep
                    // descriptions so that IDE tooltips remain useful.
                    match resolved with
                    | :? JsonObject as resolvedObj ->
                        if obj.ContainsKey "description" then
                            resolvedObj["description"] <- clone obj["description"]
                    | _ -> ()

                    resolved
                else
                    let result = JsonObject()

                    for property in obj do
                        if not (dropKeys.Contains property.Key) then
                            result[property.Key] <- resolve property.Value

                    if result.ContainsKey "enum" && not (result.ContainsKey "type") then
                        match inferTypeFromEnum (result["enum"] :?> JsonArray) with
                        | Some inferred -> result["type"] <- JsonValue.Create inferred
                        | None -> ()

                    result :> JsonNode
            | _ -> node.DeepClone()

    let providerSchema = resolve schema :?> JsonObject

    providerSchema["$comment"] <-
        JsonValue.Create(
            "Generated by generate-provider-schemas.fsx from "
            + fileName
            + ". Do not edit by hand. This flattened copy exists only because "
            + "FSharp.Data's JsonProvider does not resolve $ref when inferring types."
        )

    let options =
        JsonSerializerOptions(WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    Directory.CreateDirectory(Path.GetDirectoryName target) |> ignore
    File.WriteAllText(target, providerSchema.ToJsonString(options) + "\n")
    printfn "Wrote %s" (Path.GetRelativePath(root, target))

for fileName in schemas do
    generate fileName
