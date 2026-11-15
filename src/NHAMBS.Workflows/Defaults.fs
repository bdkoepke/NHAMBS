module private NHAMBS.Workflows.Defaults

open System
open System.IO
open NHAMBS.Etl.Common

let private environment name =
    match Environment.GetEnvironmentVariable name with
    | null
    | "" -> None
    | value -> Some value

/// Root containing the parsed JSON extracts consumed by the ETL loaders.
let dataRoot () =
    environment "NHAMBS_DATA_ROOT"
    |> Option.defaultValue (Path.Combine(repoRoot (), "out"))

/// Directory containing the parsed NHA MBS pool JSON files.
let poolsDir () =
    environment "NHAMBS_POOLS_DIR"
    |> Option.defaultValue (Path.Combine(dataRoot (), "pools"))

/// Manifest of source PDF URLs used to attach circular links to MBS pools.
let manifestPath () =
    environment "NHAMBS_MANIFEST"
    |> Option.defaultValue (Path.Combine(repoRoot (), "src", "NHAMBS.Mbs", "pool_urls.txt"))
