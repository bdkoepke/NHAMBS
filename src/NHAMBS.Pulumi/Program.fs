module NHAMBS.Pulumi.Program

open Pulumi
open Pulumi.Docker
open Pulumi.Docker.Inputs
open Pulumi.FSharp
open Pulumi.FSharp.Ops

let private configValue (key: string) (fallback: string) =
    let config = Config()

    match config.Get key with
    | null -> fallback
    | value -> value

let infra () =
    let image = configValue "image" "postgres:18-alpine"
    let port = configValue "port" "54322"
    let database = configValue "database" "nhambs"
    let username = configValue "username" "nhambs"
    let password = configValue "password" "nhambs"

    let dataVolume = Volume("nhambs-pgdata", VolumeArgs(Name = "nhambs-pgdata"))

    let postgres =
        Container(
            "nhambs-postgres",
            ContainerArgs(
                Name = "nhambs-postgres",
                Image = image,
                Restart = "unless-stopped",
                Ports = toInputList [| ContainerPortArgs(Internal = 5432, External = int port) |],
                Envs =
                    toInputList
                        [|
                            "POSTGRES_DB=" + database
                            "POSTGRES_USER=" + username
                            "POSTGRES_PASSWORD=" + password
                        |],
                Volumes =
                    toInputList
                        [|
                            ContainerVolumeArgs(
                                ContainerPath = "/var/lib/postgresql/data",
                                VolumeName = dataVolume.Name
                            )
                        |],
                Healthcheck =
                    ContainerHealthcheckArgs(
                        Tests = toInputList [| "CMD-SHELL"; "pg_isready -U " + username + " -d " + database |],
                        Interval = "5s",
                        Timeout = "3s",
                        Retries = 20
                    )
            )
        )

    dict
        [
            "containerName", postgres.Name :> obj
            "connectionString",
            box (sprintf "Host=localhost;Port=%s;Database=%s;Username=%s;Password=%s" port database username password)
            "postgresPort", box (int port)
        ]

Deployment.run (fun () -> infra ())
