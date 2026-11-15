module NHAMBS.Cli.ChtPdfText

open System
open System.ComponentModel
open System.Diagnostics
open System.Text

let private runProcess (fileName: string) (arguments: string list) =
    try
        let startInfo = ProcessStartInfo()
        startInfo.FileName <- fileName

        for argument in arguments do
            startInfo.ArgumentList.Add argument

        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        startInfo.UseShellExecute <- false

        use proc = Process.Start startInfo
        let output = proc.StandardOutput.ReadToEnd()
        let error = proc.StandardError.ReadToEnd()
        proc.WaitForExit()

        if proc.ExitCode <> 0 then
            failwithf "%s exited with code %d: %s" fileName proc.ExitCode (error.Trim())

        output
    with :? Win32Exception ->
        failwithf "%s was not found. Install it and ensure it is on PATH." fileName

/// Extract layout-preserving text from an offering circular PDF using Poppler's
/// `pdftotext -layout`.
let extractLayout (path: string) : string =
    runProcess "pdftotext" [ "-layout"; path; "-" ]
