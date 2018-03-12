#r @"packages/build/FAKE/tools/FakeLib.dll"
open System.IO
open Fake
open Fake.Git
open Fake.AssemblyInfoFile
open Fake.ReleaseNotesHelper
open Fake.UserInputHelper
open System

let release = LoadReleaseNotes "RELEASE_NOTES.md"
let srcGlob = "*.csproj"
// let testsGlob = "tests/**/*.fsproj"


Target "Clean" (fun _ ->
    [ "obj" ;"dist"]
    |> CleanDirs
    )

Target "DotnetRestore" (fun _ ->
    !! srcGlob
    |> Seq.iter (fun proj ->
        DotNetCli.Restore (fun c ->
            { c with
                Project = proj
                //This makes sure that Proj2 references the correct version of Proj1
                AdditionalArgs = [sprintf "/p:PackageVersion=%s" release.NugetVersion]
            })
))



Target "DotnetPack" (fun _ ->
    !! srcGlob
    |> Seq.iter (fun proj ->
        DotNetCli.Pack (fun c ->
            { c with
                Project = proj
                Configuration = "Release"
                OutputPath = IO.Directory.GetCurrentDirectory() @@ "dist"
                AdditionalArgs =
                    [
                        sprintf "/p:PackageVersion=%s" release.NugetVersion
                        sprintf "/p:PackageReleaseNotes=\"%s\"" (String.Join("\n",release.Notes))
                    ]
            })
    )
)

Target "Publish" (fun _ ->
    Paket.Push(fun c ->
            { c with
                PublishUrl = "https://www.nuget.org"
                WorkingDir = "dist"
            }
        )
)


let dispose (disposable : #IDisposable) = disposable.Dispose()
[<AllowNullLiteral>]
type DisposableDirectory (directory : string) =
    do
        tracefn "Created disposable directory %s" directory
    static member Create() =
        let tempPath = IO.Path.Combine(IO.Path.GetTempPath(), Guid.NewGuid().ToString("n"))
        IO.Directory.CreateDirectory tempPath |> ignore

        new DisposableDirectory(tempPath)
    member x.Directory = directory
    member x.DirectoryInfo = IO.DirectoryInfo(directory)

    interface IDisposable with
        member x.Dispose() =
            tracefn "Deleting directory %s" directory
            IO.Directory.Delete(x.Directory,true)

type DisposeablePushd (directory : string) =
    do FileUtils.pushd directory
    interface IDisposable with
        member x.Dispose() =
            FileUtils.popd()


Target "IntegrationTests" (fun _ ->
    // uninstall current MiniScaffold
    DotNetCli.RunCommand id
        "new -u MiniScaffold"
    // install from dist/
    DotNetCli.RunCommand id
        "new -i dist/MiniScaffold.0.6.1.nupkg"

    // new mini-scaffold
    [
        "-n MyCoolLib --githubUsername CoolPersonNo2", "DotnetPack"
        "-n MyCoolLib --githubUsername CoolPersonNo2 --outputType Library", "DotnetPack"
        "-n MyCoolExe --githubUsername CoolPersonNo2 --outputType Exe", "DotnetPack"
    ]
    |> Seq.iter(fun (param, testTarget) ->
        use directory = DisposableDirectory.Create()
        use pushd1 = new DisposeablePushd(directory.Directory)
        DotNetCli.RunCommand (fun commandParams ->
            { commandParams with WorkingDir = directory.Directory}
        )
            <| sprintf "new mini-scaffold -lang F# %s" param
        use pushd2 =
            directory.DirectoryInfo.GetDirectories ()
            |> Seq.head
            |> string
            |> fun x -> new DisposeablePushd(x)
        let ok =
            ProcessHelper.execProcess (fun psi ->
                psi.FileName <- "./build.sh"
                psi.Arguments <- sprintf "%s -nc" testTarget
                psi.WorkingDirectory <- directory.Directory
                ) (TimeSpan.FromMinutes(5.))
        if not ok then
            failwithf "Intregration test failed with params %s" param
    )

)

Target "Release" (fun _ ->

    if Git.Information.getBranchName "" <> "master" then failwith "Not on master"

    let releaseNotesGitCommitFormat = ("",release.Notes |> Seq.map(sprintf "* %s\n")) |> String.Join

    StageAll ""
    Git.Commit.Commit "" (sprintf "Bump version to %s \n%s" release.NugetVersion releaseNotesGitCommitFormat)
    Branches.push ""

    Branches.tag "" release.NugetVersion
    Branches.pushTag "" "origin" release.NugetVersion
)

"Clean"
  ==> "DotnetRestore"
  ==> "DotnetPack"
  ==> "IntegrationTests"
  ==> "Publish"
  ==> "Release"

RunTargetOrDefault "DotnetPack"
