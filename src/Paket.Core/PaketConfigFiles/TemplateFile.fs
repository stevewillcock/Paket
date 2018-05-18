﻿namespace Paket

open Paket
open System
open System.IO
open System.Text.RegularExpressions
open Chessie.ErrorHandling
open Paket.Domain
open Paket.Requirements

module private TemplateParser =
    type private ParserState =
        {
            Remaining : string list
            Map : Map<string, string>
            Line : int
        }

    let private single = Regex(@"^(\S+)\s*$", RegexOptions.Compiled)
    let private multi = Regex(@"^(\S+)\s+(\S.*)", RegexOptions.Compiled)

    let private (!!) (i : int) (m : Match) =
        m.Groups.[i].Value.Trim().ToLowerInvariant()

    let private (|SingleToken|_|) line =
        let m = single.Match line
        match m.Success with
        | true -> Some (!! 1 m)
        | false -> None

    let private (|MultiToken|_|) line =
        let m = multi.Match line
        match m.Success with
        | true ->
            Some (!! 1 m, m.Groups.[2].Value.Trim())
        | false -> None

    let private comment = Regex(@"^\s*(#|(\/\/))", RegexOptions.Compiled)
    let private Comment line =
        let m = comment.Match line
        m.Success

    let private indented = Regex(@"^\s+(.*)", RegexOptions.Compiled)
    let private (|Indented|_|) line =
        let i = indented.Match line
        match i.Success with
        | true -> i.Groups.[1].Value.Trim() |> Some
        | false -> None

    let rec private indentedBlock acc i lines =
        match lines with
        | empty :: t when String.IsNullOrWhiteSpace empty -> indentedBlock (empty :: acc) (i + 1) t
        | (Indented h)::t -> indentedBlock (h::acc) (i + 1) t
        | _ -> acc |> List.rev |> String.concat "\n", i, lines

    let rec private inner state =
        match state with

        | { Remaining = [] } -> Choice1Of2 state.Map
        | { Remaining = h::t } ->
            match h with
            | empty when String.IsNullOrWhiteSpace empty ->
                inner { state with Line = state.Line + 1; Remaining = t }
            | comment when Comment comment ->
                inner { state with Line = state.Line + 1; Remaining = t }
            | Indented _ -> Choice2Of2 (sprintf "Indented block with no name line %d" state.Line)
            | MultiToken (key, value) ->
                inner { state with
                            Remaining = t
                            Map = Map.add key (value.TrimEnd()) state.Map
                            Line = state.Line + 1 }
            | SingleToken key ->
                let value, line, remaining = indentedBlock [] state.Line t
                if value = "" then
                    Choice2Of2 (sprintf "No indented block following name '%s' line %d" key line)
                else
                    inner { state with
                                Remaining = remaining
                                Map = Map.add key (value.TrimEnd()) state.Map
                                Line = line }
            | _ ->
                Choice2Of2 (sprintf "Invalid syntax line %d" state.Line)

    let parse (contents : string) =
        let contents = contents.Replace("\r\n","\n").Replace("\r","\n")
        let remaining = 
            contents.Split('\n')
            |> Array.toList

        inner {
            Remaining = remaining
            Line = 1
            Map = Map.empty
        }

type CompleteCoreInfo =
    { Id : string
      Version : SemVerInfo option
      Authors : string list
      Description : string
      Symbols : bool }
    member this.PackageFileName =
        match this.Version with
        | Some v ->
            if this.Symbols
            then sprintf "%s.%O.symbols.nupkg" this.Id v
            else sprintf "%s.%O.nupkg" this.Id v
        | None -> failwithf "No version given for %s" this.Id
    member this.NuspecFileName = this.Id + ".nuspec"

type ProjectCoreInfo =
    { Id : string option
      Version : SemVerInfo option
      Authors : string list option
      Description : string option
      Symbols : bool }
    static member Empty =
        { Id = None
          Authors = None
          Version = None
          Description = None
          Symbols = false }

    member self.ToCoreInfo (Id:string): CompleteCoreInfo =
        {   Id      = Id
            Version = self.Version
            Authors = defaultArg self.Authors []
            Description = defaultArg self.Description String.Empty
            Symbols = self.Symbols}

type OptionalDependencyGroup =
    {  Framework : FrameworkIdentifier option
       Dependencies : (PackageName * VersionRequirement) list }

    static member ForNone = { Framework = None; Dependencies = [] }
    static member ForFramework framework = { Framework = framework; Dependencies = [] }
    static member For framework dependencies = { Framework = framework; Dependencies = dependencies }

type OptionalPackagingInfo =
    { Title : string option
      Owners : string list
      ReleaseNotes : string option
      Summary : string option
      Language : string option
      ProjectUrl : string option
      IconUrl : string option
      LicenseUrl : string option
      Copyright : string option
      RequireLicenseAcceptance : bool
      Tags : string list
      DevelopmentDependency : bool
      DependencyGroups : OptionalDependencyGroup list
      ExcludedDependencies : Set<PackageName>
      ExcludedGroups : Set<GroupName>
      References : string list
      FrameworkAssemblyReferences : string list
      /// (src * target) list
      Files : (string * string) list
      FilesExcluded : string list 
      PackageTypes : string list
      IncludePdbs : bool 
      IncludeReferencedProjects : bool
      }
    static member Empty : OptionalPackagingInfo =
        { Title = None
          Owners = []
          ReleaseNotes = None
          Summary = None
          Language = None
          ProjectUrl = None
          LicenseUrl = None
          IconUrl = None
          Copyright = None
          RequireLicenseAcceptance = false
          Tags = []
          DevelopmentDependency = false
          DependencyGroups = []
          ExcludedDependencies = Set.empty
          ExcludedGroups = Set.empty
          References = []
          FrameworkAssemblyReferences = []
          Files = []
          FilesExcluded = [] 
          PackageTypes = []
          IncludeReferencedProjects = false
          IncludePdbs = false }

type CompleteInfo = CompleteCoreInfo * OptionalPackagingInfo

type TemplateFileContents =
    | CompleteInfo of CompleteInfo
    | ProjectInfo of ProjectCoreInfo * OptionalPackagingInfo

type TemplateFile =
    { FileName : string
      Contents : TemplateFileContents }

    with
        member x.IncludeReferencedProjects = 
            match x.Contents with
            | CompleteInfo(_,c) -> c.IncludeReferencedProjects
            | ProjectInfo (_,c) ->  c.IncludeReferencedProjects

[<CompilationRepresentationAttribute(CompilationRepresentationFlags.ModuleSuffix)>]
module internal TemplateFile =
    open Logging

    let tryGetId (templateFile : TemplateFile) =
        match templateFile.Contents with
        | CompleteInfo(core,_) -> Some(core.Id)
        | ProjectInfo(core,_) -> core.Id

    let setVersion version specificVersions templateFile =
        let version =
            match tryGetId templateFile |> Option.bind (fun id -> Map.tryFind id specificVersions) with
            | Some _ as specificVersion -> specificVersion
            | None -> version

        match version with
        | None -> templateFile
        | Some version ->
            let contents =
                match templateFile.Contents with
                | CompleteInfo(core, optional) -> CompleteInfo({ core with Version = Some version }, optional)
                | ProjectInfo(core, optional) -> ProjectInfo({ core with Version = Some version }, optional)
            { templateFile with Contents = contents }

    let setReleaseNotes releaseNotes templateFile =
        let contents =
            match templateFile.Contents with
            | CompleteInfo(core, optional) -> CompleteInfo(core, { optional with ReleaseNotes = Some releaseNotes })
            | ProjectInfo(core, optional) -> ProjectInfo(core, { optional with ReleaseNotes = Some releaseNotes })
        { templateFile with Contents = contents }

    let setProjectUrl url templateFile = 
        let contents =
            match templateFile.Contents with
            | CompleteInfo(core, optional) -> CompleteInfo(core, { optional with ProjectUrl = Some url })
            | ProjectInfo(core, optional) -> ProjectInfo(core, { optional with ProjectUrl = Some url })
        { templateFile with Contents = contents }

    let private failP file str = fail (PackagingConfigParseError(file,str))

    type private PackageConfigType =
        | FileType
        | ProjectType

    let private parsePackageConfigType file map =
        match Map.tryFind "type" map with
        | Some s ->
            match s with
            | "file" -> ok FileType
            | "project" -> ok ProjectType
            | s -> failP file (sprintf "Unknown package config type.")
        | None -> failP file (sprintf "First line of paket.template file had no 'type' declaration.")

    let private getId file map =
        match Map.tryFind "id" map with
        | None -> failP file "No id line in paket.template file."
        | Some m -> ok m

    let private getAuthors file (map : Map<string, string>) =
        match Map.tryFind "authors" map with
        | None -> failP file "No authors line in paket.template file."
        | Some m ->
            m.Split ',' |> Array.map String.trim
            |> List.ofArray|> ok

    let private getDescription file map =
        Map.tryFind "description" map |> function
        | Some m -> ok m
        | None -> failP file "No description line in paket.template file."

    let private (|Framework|_|) (line:string) =        
        match line.Trim()  with
        | String.RemovePrefix "framework:" trimmed ->
            match FrameworkDetection.Extract trimmed with
            | Some _ as fw -> Some fw
            | None ->
                failwithf "Unable to identify a framework from '%s'" trimmed
        | _ -> None

    let private (|Empty|_|) (line:string) =
        match line.Trim()  with
        | _ when String.IsNullOrWhiteSpace line -> Some (Empty line)
        | String.RemovePrefix "//" _ -> Some (Empty line)
        | String.RemovePrefix "#" _ -> Some (Empty line)
        | _ -> None
   
    let private getDependencyByLine (fileName, lockFile:LockFile,currentVersion:SemVerInfo option, specificVersions:Map<string, SemVerInfo>, line:string, framework:FrameworkIdentifier option) =
        let reg = Regex(@"(?<id>\S+)(?<version>.*)").Match line
        let name = PackageName reg.Groups.["id"].Value
        let versionRequirement =
            let versionString =
                let s = reg.Groups.["version"].Value.Trim()
                if s.Contains "CURRENTVERSION" then
                    match specificVersions.TryFind (string name) with
                    | Some v -> s.Replace("CURRENTVERSION", string v)
                    | None ->
                        match currentVersion with
                        | Some v -> s.Replace("CURRENTVERSION", string v)
                        | None -> failwithf "The template file %s contains the placeholder CURRENTVERSION, but no version was given." fileName

                elif s.Contains "LOCKEDVERSION" then
                    let groupRegex = Regex("LOCKEDVERSION-(?<group>\w+)")
                    let replaceGroup (m : Match) =
                        let groupName = GroupName m.Groups.["group"].Value
                        match lockFile.Groups |> Map.tryFind groupName with
                        | None -> failwithf "The template file %s contains the placeholder LOCKEDVERSION-%O, but no group %O was found in paket.lock" fileName groupName groupName
                        | Some gp ->
                            match gp.Resolution |> Map.tryFind name with
                            | None -> failwithf "The template file %s contains the placeholder LOCKEDVERSION-%O, but no version was given for package %O in group %O in paket.lock" fileName groupName name groupName
                            | Some p -> string p.Version

                    let s = groupRegex.Replace(s, replaceGroup)

                    match lockFile.Groups.[Constants.MainDependencyGroup].Resolution |> Map.tryFind name with
                    | Some p -> s.Replace("LOCKEDVERSION", string p.Version)
                    | None ->
                        let packages = 
                            lockFile.GetGroupedResolution()
                            |> Seq.filter (fun kv -> snd kv.Key = name)
                            |> Seq.toList

                        match packages with
                        | [] -> failwithf "The template file %s contains the placeholder LOCKEDVERSION, but no version was given for package %O in paket.lock." fileName name
                        | [kv] -> s.Replace("LOCKEDVERSION", string kv.Value.Version)
                        | _ -> failwithf "The template file %s contains the placeholder LOCKEDVERSION, but more than one group contains package %O in paket.lock." fileName name

                else s
                                
            DependenciesFileParser.parseVersionRequirement versionString
        name, versionRequirement

    let private getDependenciesByTargetFramework fileName lockFile currentVersion  specificVersions (lineNo, state: OptionalDependencyGroup list) line =
        let lineNo = lineNo + 1
        match state with
        | current::other ->
            match line with
            | Framework framework ->
                let groups = OptionalDependencyGroup.ForFramework framework::current::other
                lineNo, groups
            | Empty  _ -> lineNo, current::other
            | _ -> 
                let dependency = getDependencyByLine(fileName, lockFile, currentVersion, specificVersions, line, current.Framework)
                lineNo, { current with Dependencies = current.Dependencies @ [dependency] }::other
        | [] ->
            match line with
            | Framework framework -> lineNo, [OptionalDependencyGroup.ForFramework framework]
            | Empty  _ -> lineNo, []
            | _ ->
                let dependency = getDependencyByLine(fileName, lockFile, currentVersion, specificVersions, line, None)
                lineNo, [{ Framework = None; Dependencies = [dependency] }]
            
   
    let private getDependencyGroups (fileName, lockFile:LockFile, info : Map<string, string>,currentVersion:SemVerInfo option, specificVersions:Map<string, SemVerInfo>) =
        match Map.tryFind "dependencies" info with
        | None -> []
        | Some d ->
            d.Split '\n'
            |> Array.fold (getDependenciesByTargetFramework fileName lockFile currentVersion specificVersions) (0, [])
            |> snd
            |> List.rev

    let private getExcludedDependencies (info : Map<string, string>) =
        match Map.tryFind "excludeddependencies" info with
        | None -> []
        | Some d -> 
            d.Split '\n'
            |> Array.map (fun d ->
                let reg = Regex(@"(?<id>\S+)(?<version>.*)").Match d
                PackageName reg.Groups.["id"].Value)
            |> Array.toList

    let private getExcludedGroups (info : Map<string, string>) =
        match Map.tryFind "excludedgroups" info with
        | None -> []
        | Some d -> 
            d.Split '\n'
            |> Array.map (fun d ->
                let reg = Regex(@"(?<id>\S+)").Match d
                GroupName reg.Groups.["id"].Value)
            |> Array.toList


    let private isExclude = Regex("\s*!\S", RegexOptions.Compiled)
    let private isComment = Regex(@"^\s*(#|(\/\/))", RegexOptions.Compiled)
    let private getFiles (map : Map<string, string>) =
        match Map.tryFind "files" map with
        | None -> []
        | Some d -> 
            d.Split '\n'
            |> Array.filter (fun s -> not (String.IsNullOrWhiteSpace s || isExclude.IsMatch s || isComment.IsMatch s))
            |> Seq.map (fun (line:string) ->
                let splitted = 
                    line.Split([|"==>"|],StringSplitOptions.None)
                    |> Array.collect (fun line -> line.Split([|"=>"|],StringSplitOptions.None))
                    |> Array.map String.trim
                let target = if splitted.Length < 2 then "lib" else splitted.[1]
                splitted.[0],target)
            |> List.ofSeq

    let private getFileExcludes (map : Map<string, string>) =
        match Map.tryFind "files" map with
        | None -> []
        | Some d -> 
            d.Split '\n'
            |> Array.filter (fun s -> not (String.IsNullOrWhiteSpace s))
            |> Array.filter isExclude.IsMatch
            |> Array.filter (isComment.IsMatch >> not)
            |> Seq.map (String.trim >> String.trimStart [|'!'|])
            |> List.ofSeq

    let private getReferences (map : Map<string, string>) =
        match Map.tryFind "references" map with
        | None -> []
        | Some d -> d.Split '\n' |> List.ofArray

    let private getFrameworkReferences (map : Map<string, string>) =
        match Map.tryFind "frameworkassemblies" map with
        | None -> []
        | Some  d -> d.Split '\n' |> List.ofArray

    let private getOptionalInfo (fileName,lockFile:LockFile, map : Map<string, string>, currentVersion, specificVersions) =
        let get (n : string) = Map.tryFind (n.ToLowerInvariant()) map

        let owners =
            match Map.tryFind "owners" map with
            | None -> []
            | Some o ->
                o.Split ',' |> Array.map String.trim |> Array.toList

        let requireLicenseAcceptance =
            match get "requireLicenseAcceptance" with
            | Some x when String.equalsIgnoreCase x "true" -> true
            | _ -> false

        let tags =
            match get "tags" with
            | None -> []
            | Some t ->
                t.Split ' ' 
                |> Array.filter (fun s -> not (String.IsNullOrWhiteSpace s))
                |> Array.map (String.trim >> String.trimChars [|','|])
                |> Array.toList

        let developmentDependency =
            match get "developmentDependency" with
            | Some x when String.equalsIgnoreCase x "true" -> true
            | _ -> false

        let dependencyGroups = getDependencyGroups(fileName,lockFile,map,currentVersion,specificVersions)

        let excludedDependencies = map |> getExcludedDependencies
        let excludedGroups = map |> getExcludedGroups

        let packageTypes =
            []
        
        let includePdbs = 
            match get "include-pdbs" with
            | Some x when String.equalsIgnoreCase x "true" -> true
            | _ -> false

        let includeReferencedProjects = 
            match get "include-referenced-projects" with
            | Some x when String.equalsIgnoreCase x "true" -> true
            | _ -> false

        { Title = get "title"
          Owners = owners
          ReleaseNotes = get "releaseNotes"
          Summary = get "summary"
          Language = get "language"
          ProjectUrl = get "projectUrl"
          IconUrl = get "iconUrl"
          LicenseUrl = get "licenseUrl"
          Copyright = get "copyright"
          RequireLicenseAcceptance = requireLicenseAcceptance
          Tags = tags
          DevelopmentDependency = developmentDependency
          DependencyGroups = dependencyGroups
          ExcludedDependencies = Set.ofList excludedDependencies
          ExcludedGroups = Set.ofList excludedGroups
          References = getReferences map
          FrameworkAssemblyReferences = getFrameworkReferences map
          Files = getFiles map
          FilesExcluded = getFileExcludes map
          PackageTypes = packageTypes
          IncludeReferencedProjects = includeReferencedProjects 
          IncludePdbs = includePdbs }

    let Parse(file,lockFile,currentVersion,specificVersions,contentStream : Stream) =
        trial {
            use sr = new StreamReader(contentStream)
            let! map =
                match TemplateParser.parse (sr.ReadToEnd()) with
                | Choice1Of2 m -> ok m
                | Choice2Of2 f -> failP file f
            sr.Dispose()

            let! type' = parsePackageConfigType file map

            let resolveCurrentVersion id =
                match id |> Option.bind (fun id -> Map.tryFind id specificVersions) with
                | Some _ as specificVersion -> specificVersion
                | None ->
                    match currentVersion with
                    | None -> Map.tryFind "version" map |> Option.map SemVer.Parse
                    | _ -> currentVersion

            match type' with
            | ProjectType ->
                let id = Map.tryFind "id" map
                let core : ProjectCoreInfo =
                    { Id = id
                      Version = resolveCurrentVersion id
                      Authors =
                          match Map.tryFind "authors" map with
                          | None -> None
                          | Some s ->
                            String.split [|','|] s
                            |> Array.map String.trim
                            |> Array.toList |> Some
                      Description = Map.tryFind "description" map
                      Symbols = false }

                let optionalInfo = getOptionalInfo(file,lockFile,map,currentVersion,specificVersions)
                return ProjectInfo(core, optionalInfo)
            | FileType ->
                let! id' = getId file map
                let! authors = getAuthors file map
                let! description = getDescription file map
                let core : CompleteCoreInfo =
                    { Id = id'
                      Version = resolveCurrentVersion (Some id')
                      Authors = authors
                      Description = description
                      Symbols = false }

                let optionalInfo = getOptionalInfo(file,lockFile,map,currentVersion,specificVersions)
                return CompleteInfo(core, optionalInfo)
        }

    let Load(fileName,lockFile,currentVersion,specificVersions) =
        let fi = FileInfo fileName
        let root = fi.Directory.FullName
        let contents = Parse(fi.FullName,lockFile,currentVersion,specificVersions, File.OpenRead fileName) |> returnOrFail
        let getFiles files =
            [ for source, target in files do
                match Fake.Globbing.search root source with
                | [] ->
                    if source.Contains "*" || source.Contains "?" then
                        traceWarnfn "The file pattern \"%s\" in %s did not match any files." source fileName
                    else
                        failwithf "The file \"%s\" requested in %s does not exist." source fileName
                | searchResult ->
                    for file in searchResult do
                        if source.Contains("**") then
                            let sourceRoot = FileInfo(Path.Combine(root,source.Substring(0,source.IndexOf("**")))).FullName |> normalizePath
                            let fullFile = FileInfo(file).Directory.FullName |> normalizePath
                            let newTarget = Path.Combine(target,fullFile.Replace(sourceRoot,"").TrimStart(Path.DirectorySeparatorChar))
                            yield file, newTarget
                        else
                            yield file, target ]

        { FileName = fileName
          Contents =
              match contents with
              | CompleteInfo(core, optionalInfo) ->
                  let files = getFiles optionalInfo.Files
                  CompleteInfo(core, { optionalInfo with Files = files })
              | ProjectInfo(core, optionalInfo) ->
                  let files = getFiles optionalInfo.Files
                  ProjectInfo(core, { optionalInfo with Files = files }) }

    let IsProjectType (filename: string) : bool =
        match TemplateParser.parse (File.ReadAllText filename) with
        | Choice1Of2 m ->
            let type' = parsePackageConfigType filename m
            if type' |> failed then false
            else 
                match (returnOrFail type') with
                | ProjectType -> true
                | FileType -> false
        | Choice2Of2 f -> false


    let FindTemplateFiles root =
        let findTemplates dir = Directory.EnumerateFiles(dir, "*" + Constants.TemplateFile, SearchOption.AllDirectories)
        Directory.EnumerateDirectories(root)
        |> Seq.filter (fun di -> 
             let name = DirectoryInfo(di).Name.ToLower() 
             name <> "packages" && name <> "paket-files")
        |> Seq.collect findTemplates
        |> Seq.append (Directory.EnumerateFiles(root, "*" + Constants.TemplateFile, SearchOption.TopDirectoryOnly))