using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Nuke.Common;
using Nuke.Common.CI;
using Nuke.Common.Execution;
using Nuke.Common.Git;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.GitHub;
using Nuke.Common.Tools.GitVersion;
using Nuke.Common.Tools.Octopus;
using Nuke.Common.Utilities.Collections;
using Octokit;
using static Nuke.Common.EnvironmentInfo;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.IO.PathConstruction;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
using FileMode = System.IO.FileMode;
using Project = Nuke.Common.ProjectModel.Project;

class Build : NukeBuild
{
    /// Support plugins are available for:
    ///   - JetBrains ReSharper        https://nuke.build/resharper
    ///   - JetBrains Rider            https://nuke.build/rider
    ///   - Microsoft VisualStudio     https://nuke.build/visualstudio
    ///   - Microsoft VSCode           https://nuke.build/vscode

    public static int Main ()
    {
        Serilog.Log.Information($"Building for {Configuration}");

        return Execute<Build>(x => x.Compile);
    }
    
    [GitVersion] GitVersion GitVersion;
    [GitRepository] GitRepository GitRepository;

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    static Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    [Parameter("Game path")] static AbsolutePath GamePath;
    
    [Parameter("Should the build be copied to the game folder")] static bool ShouldCopyToGame = false;
    
    [Parameter("Test release")] static bool TestRelease = false;
    
    [Parameter("Github token")] static string GithubToken = "";

    const string ProjectAlias = "SFLoader";
    static string ProjectFolder => "_" + ProjectAlias;
    static AbsolutePath OutputDir => RootDirectory / "Output" / Configuration / ProjectFolder;

    [Solution(GenerateProjects = true)] readonly Solution Solution;

    [PathVariable] Tool Cargo;

    Target Clean => _ => _
        .Before(Restore)
        .Executes(() =>
        {
            DotNetClean(x=>x.SetProcessLogOutput(false));
            OutputDir.CreateOrCleanDirectory();
        });

    Target Restore => _ => _
        .Executes(() =>
        {
            DotNetRestore();
        });

    Target Compile => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            //DotNetBuild(x => x.SetNoConsoleLogger(true));
            foreach (var project in Solution.AllProjects)
            {
                if(project.Name == "_build")
                    continue;
                
                BuildToOutput(project, ShouldCopyToGame);
            }
        });
    
    Target Pack => _ => _
        .DependsOn(Restore)
        .Requires(() => Configuration == Configuration.Release)
        .Executes(() =>
        {
            Serilog.Log.Information("=============================");
            Serilog.Log.Information($"===   Packing for {GitVersion.MajorMinorPatch}   ===");
            Serilog.Log.Information("=============================");

            foreach (var project in Solution.AllProjects)
            {
                if(project.Name == "_build")
                    continue;
                
                BuildToOutput(project);
            }

            Cargo(arguments: "+nightly build --target x86_64-pc-windows-msvc --release", workingDirectory: RootDirectory);
            
            CopyFileToDirectory(RootDirectory / "target" / "x86_64-pc-windows-msvc" / "release" / "Bootstrap.dll", OutputDir / "Dependencies", FileExistsPolicy.Overwrite);
            CopyFileToDirectory(RootDirectory / "target" / "x86_64-pc-windows-msvc" / "release" / "version.dll", OutputDir / "..", FileExistsPolicy.Overwrite);
            CopyFileToDirectory(RootDirectory / "BaseLibs" / "dobby_x64.dll", OutputDir / "..", FileExistsPolicy.Overwrite);
            RenameFile(OutputDir / ".." / "dobby_x64.dll", "dobby.dll", FileExistsPolicy.Overwrite);

            var zip = RootDirectory / $"{ProjectAlias}.zip";
            
            if(zip.FileExists())
                zip.DeleteFile();
            
            (OutputDir / "..").ZipTo(zip, compressionLevel: CompressionLevel.SmallestSize, fileMode:FileMode.CreateNew);

            if (GamePath.DirectoryExists() && TestRelease)
            {
                Serilog.Log.Information("===> Copying to game folder for testing");
                Serilog.Log.Information("=> Deleting old files");
                
                var loaderDir = GamePath / ProjectFolder;
                if (loaderDir.DirectoryExists())
                    loaderDir.DeleteDirectory();
                
                var dobby = GamePath / "dobby.dll";
                if (dobby.FileExists())
                    dobby.DeleteFile();
                
                var version = GamePath / "version.dll";
                if (version.FileExists())
                    version.DeleteFile();

                Serilog.Log.Information("=> Files deleted");
                Serilog.Log.Information("=> Press enter to continue...");
                Console.ReadLine();
                
                Serilog.Log.Information("=> Copying new files");
                
                zip.UnZipTo(GamePath);
                
                Serilog.Log.Information("=> Files copied");
                Serilog.Log.Information("=> Press enter to start the game...");
                Console.ReadLine();
                
                var processInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = GamePath / "SonsOfTheForest.exe",
                    WorkingDirectory = GamePath,
                    UseShellExecute = false
                };

                Process.Start(processInfo);
            }
        });
    
    Target Upload => _ => _
        .Requires(() => Configuration == Configuration.Release)
        .Requires(() => GithubToken != "")
        .Executes(() =>
        {
            GitHubTasks.GitHubClient = new GitHubClient(new ProductHeaderValue(nameof(NukeBuild)))
            {
                Credentials = new Credentials(GithubToken)
            };

            var release = new NewRelease(GitVersion.MajorMinorPatch)
            {
                Name = $"{ProjectAlias} {GitVersion.MajorMinorPatch}",
                Body = "Version " + GitVersion.MajorMinorPatch
            };

            var createdRelease = GitHubTasks.GitHubClient.Repository.Release
                .Create(GitRepository.GetGitHubOwner(), GitRepository.GetGitHubName(), release).Result;
            
            UploadReleaseAssetToGithub(createdRelease, RootDirectory / $"{ProjectAlias}.zip");
        });

    IReadOnlyCollection<Output> BuildToOutput(Project project, bool copyToGame = false)
    {
        var newOutputPath = GetTargetOutputPath(project);

        var copyMode = GetBoolProp(project, "CopyToOuput");
        var dontOutput = GetBoolProp(project, "DontOuput");

        Serilog.Log.Information($"===> Building {project.Name} to {newOutputPath}");
        
        var build = DotNetBuild(x => SetAdditionalSettings(project, x)
                .SetProjectFile(project)
                .SetConfiguration(Configuration)
                .SetPlatform("Windows - x64")
                .SetAssemblyVersion(GitVersion.MajorMinorPatch)
                .SetFileVersion(GitVersion.MajorMinorPatch)
                //.SetOutputDirectory(skipOutput ? x.OutputDirectory : newOutputPath)
                .SetNoConsoleLogger(true));
        
        if(copyToGame)
        {
            CopyToGame(project);
        }
        else
        {
            if (copyMode)
            {
                CopyBuiltFiles(project, newOutputPath);
            } 
            else if (!dontOutput)
            {
                CopyDirectoryRecursively(
                    project.Directory / "bin" / "Windows - x64" / Configuration, 
                    OutputDir / GetRelativeOutputPath(project), 
                    DirectoryExistsPolicy.Merge, FileExistsPolicy.Overwrite);
            }
        }

        return build;
    }

    void CopyToGame(Project project)
    {
        if(string.IsNullOrEmpty(GamePath))
        {
            Serilog.Log.Error("GamePath is not set");
            return;
        }

        AbsolutePath gamePath = GamePath / ProjectFolder;

        if(HasTargetFrameworkAppended(project))
            gamePath /= project.GetProperty("TargetFramework");
        
        gamePath /= GetRelativeOutputPath(project);
        
        CopyBuiltFiles(project, gamePath);
    }

    void CopyBuiltFiles(Project project, AbsolutePath to)
    {
        var (dll, pdb) = GetBuiltAssemblyPath(project);
        CopyFileToDirectory(dll, to, FileExistsPolicy.Overwrite);
        if(pdb.FileExists())
            CopyFileToDirectory(pdb, to, FileExistsPolicy.Overwrite);
    }
    
    AbsolutePath GetTargetOutputPath(Project project)
    {
        var ouput = OutputDir / GetRelativeOutputPath(project);
        
        if(HasTargetFrameworkAppended(project))
            ouput /= project.GetProperty("TargetFramework");
        
        return ouput;
    }
    
    AbsolutePath GetBuildOutputPath(Project project)
    {
        var ouput = project.Directory / "bin" / "Windows - x64" / Configuration;
        
        if(HasTargetFrameworkAppended(project))
            ouput /= project.GetProperty("TargetFramework");
        
        return ouput;
    }
    
    (AbsolutePath dll, AbsolutePath pdb) GetBuiltAssemblyPath(Project project)
    {
        var ouput = GetBuildOutputPath(project);
        var assemblyName = project.GetProperty("AssemblyName");
        
        if(string.IsNullOrEmpty(assemblyName))
            assemblyName = project.Name;
        
        var dllPath = ouput / $"{assemblyName}.dll";
        var pdbPath = ouput / $"{assemblyName}.pdb";
        return (dllPath, pdbPath);
    }

    bool GetBoolProp(Project project, string name, bool defaultValue = false)
    {
        var prop = project.GetProperty(name);
        if (string.IsNullOrEmpty(prop))
            return defaultValue;
        return bool.Parse(prop);
    }

    bool HasTargetFrameworkAppended(Project project)
    {
        return GetBoolProp(project, "AppendTargetFrameworkToOutputPath", true);
    }

    string GetRelativeOutputPath(Project project)
    {
        return project.GetProperty("RelativeOutputPath");
    }
    
    void UploadReleaseAssetToGithub(Release release, AbsolutePath asset)
    {
        if (!asset.FileExists())
        {
            return;
        }

        var assetContentType = "application/x-binary";

        var releaseAssetUpload = new ReleaseAssetUpload
        {
            ContentType = assetContentType,
            FileName = Path.GetFileName(asset),
            RawData = File.OpenRead(asset)
        };
        var _ = GitHubTasks.GitHubClient.Repository.Release.UploadAsset(release, releaseAssetUpload).Result;
    }

    DotNetBuildSettings SetAdditionalSettings(Project project, DotNetBuildSettings settings)
    {
        if (project.Name != "MelonLoader")
        {
            return settings;
        }

        settings = settings.SetTitle("SFLoader based on MelonLoader");
        settings = settings.SetDescription("SFLoader based on MelonLoader");
        settings = settings.SetAuthors("Lava Gang & Toni Macaroni");
        settings = settings.SetCopyright("Created by Lava Gang & Toni Macaroni");
        return settings;
    }
}