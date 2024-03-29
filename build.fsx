#load ".fake/build.fsx/intellisense.fsx"
open Fake.Core
open Fake.DotNet
open Fake.IO
open Fake.IO.FileSystemOperators
open Fake.IO.Globbing.Operators
open Fake.Core.TargetOperators
open Fake.Tools.Git

type ToolDir =
    /// Global tool dir must be in PATH - ${PATH}:/root/.dotnet/tools
    | Global
    /// Just a dir name, the location will be used as: ./{LocalDirName}
    | Local of string

// ========================================================================================================
// === F# / Application fake build =========================================================== 2019-11-14 =
// --------------------------------------------------------------------------------------------------------
// Options:
//  - no-clean   - disables clean of dirs in the first step (required on CI)
//  - no-lint    - lint will be executed, but the result is not validated
// --------------------------------------------------------------------------------------------------------
// Table of contents:
//      1. Information about project, configuration
//      2. Utilities, DotnetCore functions
//      3. FAKE targets
//      4. FAKE targets hierarchy
// ========================================================================================================

// --------------------------------------------------------------------------------------------------------
// 1. Information about the project to be used at NuGet and in AssemblyInfo files and other FAKE configuration
// --------------------------------------------------------------------------------------------------------

let project = "Monadster"
let summary = ""

let gitCommit = Information.getCurrentSHA1(".")
let gitBranch = Information.getBranchName(".")

let toolsDir = Global

// --------------------------------------------------------------------------------------------------------
// 2. Utilities, DotnetCore functions, etc.
// --------------------------------------------------------------------------------------------------------

[<AutoOpen>]
module private Utils =
    let tee f a =
        f a
        a

    let skipOn option action p =
        if p.Context.Arguments |> Seq.contains option
        then Trace.tracefn "Skipped ..."
        else action p

module private DotnetCore =
    let run cmd workingDir =
        let options =
            DotNet.Options.withWorkingDirectory workingDir
            >> DotNet.Options.withRedirectOutput true

        DotNet.exec options cmd ""

    let runOrFail cmd workingDir =
        run cmd workingDir
        |> tee (fun result ->
            if result.ExitCode <> 0 then failwithf "'dotnet %s' failed in %s" cmd workingDir
        )
        |> ignore

    let runInRoot cmd = run cmd "."
    let runInRootOrFail cmd = runOrFail cmd "."

    let installOrUpdateTool toolDir tool =
        let toolCommand action =
            match toolDir with
            | Global -> sprintf "tool %s --global %s" action tool
            | Local dir -> sprintf "tool %s --tool-path ./%s %s" action dir tool

        match runInRoot (toolCommand "install") with
        | { ExitCode = code } when code <> 0 -> runInRootOrFail (toolCommand "update")
        | _ -> ()

    let execute command args (dir: string) =
        let cmd =
            sprintf "%s/%s"
                (dir.TrimEnd('/'))
                command

        let processInfo = System.Diagnostics.ProcessStartInfo(cmd)
        processInfo.RedirectStandardOutput <- true
        processInfo.RedirectStandardError <- true
        processInfo.UseShellExecute <- false
        processInfo.CreateNoWindow <- true
        processInfo.Arguments <- args |> String.concat " "

        use proc =
            new System.Diagnostics.Process(
                StartInfo = processInfo
            )
        if proc.Start() |> not then failwith "Process was not started."
        proc.WaitForExit()

        if proc.ExitCode <> 0 then failwithf "Command '%s' failed in %s." command dir
        (proc.StandardOutput.ReadToEnd(), proc.StandardError.ReadToEnd())

// --------------------------------------------------------------------------------------------------------
// 3. Targets for FAKE
// --------------------------------------------------------------------------------------------------------

Target.create "Clean" <| skipOn "no-clean" (fun _ ->
    !! "./**/bin/Release"
    ++ "./**/bin/Debug"
    ++ "./**/obj"
    |> Shell.cleanDirs
)

Target.create "AssemblyInfo" (fun _ ->
    let getAssemblyInfoAttributes projectName =
        [
            AssemblyInfo.Title projectName
            AssemblyInfo.Product project
            AssemblyInfo.Description summary
            AssemblyInfo.InternalsVisibleTo "tests"
            AssemblyInfo.Metadata("gitbranch", gitBranch)
            AssemblyInfo.Metadata("gitcommit", gitCommit)
        ]

    let getProjectDetails projectPath =
        let projectName = System.IO.Path.GetFileNameWithoutExtension(projectPath)
        (
            projectPath,
            projectName,
            System.IO.Path.GetDirectoryName(projectPath),
            (getAssemblyInfoAttributes projectName)
        )

    !! "**/*.*proj"
    -- "example/**/*.*proj"
    |> Seq.map getProjectDetails
    |> Seq.iter (fun (projFileName, _, folderName, attributes) ->
        match projFileName with
        | proj when proj.EndsWith("fsproj") -> AssemblyInfoFile.createFSharp (folderName </> "AssemblyInfo.fs") attributes
        | _ -> ()
    )
)

Target.create "Build" (fun _ ->
    !! "**/*.*proj"
    -- "example/**/*.*proj"
    |> Seq.iter (DotNet.build id)
)

Target.create "Lint" (fun p ->
    DotnetCore.installOrUpdateTool toolsDir "dotnet-fsharplint"

    let checkResult (messages: string list) =
        let rec check: string list -> unit = function
            | [] -> failwithf "Lint does not yield a summary."
            | head :: rest ->
                if head.Contains "Summary" then
                    match head.Replace("= ", "").Replace(" =", "").Replace("=", "").Replace("Summary: ", "") with
                    | "0 warnings" -> Trace.tracefn "Lint: OK"
                    | warnings ->
                        if p.Context.Arguments |> List.contains "no-lint"
                        then Trace.traceErrorfn "Lint ends up with %s." warnings
                        else failwithf "Lint ends up with %s." warnings
                else check rest
        messages
        |> List.rev
        |> check

    !! "**/*.fsproj"
    |> Seq.map (fun fsproj ->
        match toolsDir with
        | Global ->
            DotnetCore.runInRoot (sprintf "fsharplint -f %s" fsproj)
            |> fun (result: ProcessResult) -> result.Messages
        | Local dir ->
            DotnetCore.execute "dotnet-fsharplint" ["-f"; fsproj] dir
            |> fst
            |> tee (Trace.tracefn "%s")
            |> String.split '\n'
            |> Seq.toList
    )
    |> Seq.iter checkResult
)

Target.create "Tests" (fun _ ->
    if !! "tests/*.fsproj" |> Seq.isEmpty
    then Trace.tracefn "There are no tests yet."
    else DotnetCore.runOrFail "run" "tests"
)

Target.create "Release" (fun _ ->
    DotnetCore.runInRootOrFail "publish -c Release -o /app"
)

Target.create "Watch" (fun _ ->
    DotnetCore.runInRootOrFail "watch run"
)

Target.create "Run" (fun _ ->
    DotnetCore.runInRootOrFail "run"
)

// --------------------------------------------------------------------------------------------------------
// 4. FAKE targets hierarchy
// --------------------------------------------------------------------------------------------------------

"Clean"
    ==> "AssemblyInfo"
    ==> "Build"
    ==> "Lint"
    ==> "Tests"
    ==> "Release" <=> "Watch" <=> "Run"

Target.runOrDefaultWithArguments "Build"
