#module "Cake.Longpath.Module"

#addin "Cake.FileHelpers"
#addin "Cake.Powershell"
#addin nuget:?package=Cake.GitVersioning&version=2.3.38

using System;
using System.Linq;
using System.Text.RegularExpressions;

//////////////////////////////////////////////////////////////////////
// ARGUMENTS
//////////////////////////////////////////////////////////////////////

var target = Argument("target", "Default");
var configuration = Argument("configuration", "Release");

//////////////////////////////////////////////////////////////////////
// VARIABLES
//////////////////////////////////////////////////////////////////////

var baseDir = MakeAbsolute(Directory("../")).ToString();
var buildDir = $"{baseDir}/build";
var toolsDir = $"{buildDir}/tools";

var binDir = $"{baseDir}/bin";
var nupkgDir = $"{binDir}/nupkg";
var packageDir = $"{baseDir}/packages";

var nuspec = $"{baseDir}/SceneLoader/NugetPackager/SceneLoaderComponent.nuspec";
var sceneLoaderSln = $"{baseDir}/SceneLoader.sln";

var styler = $"{toolsDir}/XamlStyler.Console/tools/xstyler.exe";
var stylerFile = $"{baseDir}/settings.xamlstyler";

string Version = null;

//////////////////////////////////////////////////////////////////////
// METHODS
//////////////////////////////////////////////////////////////////////

// Builds the solution with the given target setting the given build properties.
void MSBuildSolution(
    string target,
    params (string Name, string Value)[] properties)
{
    MSBuildSettings SettingsWithTarget() =>
        new MSBuildSettings
        {
            MaxCpuCount = 0,
        }.WithTarget(target);

    MSBuildSettings SetProperties(MSBuildSettings settings)
    {
        foreach(var property in properties)
        {
            settings = settings.WithProperty(property.Name, property.Value);
        }
        return settings;
    }

    var msBuildSettings = SetProperties(SettingsWithTarget().SetConfiguration(configuration));

    foreach (var platformTarget in new []
    {
        PlatformTarget.x86,
        PlatformTarget.x64,
        PlatformTarget.ARM,
        PlatformTarget.MSIL
    })
    {
        msBuildSettings.PlatformTarget = platformTarget;
        MSBuild($"{baseDir}/SceneLoader.sln", msBuildSettings);
    }
}

// Returns true if the given file has a name that indicates it is
// generated code.
static bool IsAutoGenerated(FilePath path)
{
    var fileName = path.GetFilename().ToString();
    // Exclude these auto-generated files.
    return  fileName.EndsWith(".g.cs") ||
            fileName.EndsWith(".i.cs") ||
            fileName.Contains("TemporaryGeneratedFile");
}

static bool IsExcludedDirectory(FilePath path)
{
    var segments = path.Segments;

    return
        segments.Contains("bin") ||
        segments.Contains("internal") ||
        segments.Contains("obj") ||
        segments.Contains("Generated Files") ||
        segments.Contains("tools") ||
        segments.Contains("packages");
}

// Returns true if the given file is source that the build system
// should use directly i.e. it is not generated in the build and
// is not being excluded for some reason.
static bool IsBuildInput(FilePath path)
{
    var filename = path.GetFilename().ToString();
    return !IsExcludedDirectory(path) && !IsAutoGenerated(path) && 
            (filename.EndsWith(".cs") || filename.EndsWith(".cpp") || 
                filename.EndsWith(".h"));
}

void VerifyHeaders(bool updateHeaders)
{
    var header = FileReadText("header.txt") + "\r\n";
    bool hasMissing = false;

    // Source files need copyright headers
    var files = GetFiles($"{baseDir}/**/*").Where(IsBuildInput);

    Information($"\r\nChecking {files.Count()} file header(s)");
    foreach(var file in files)
    {
        var oldContent = FileReadText(file);
        if(oldContent.Contains("// <auto-generated>"))
        {
           continue;
        }
        var rgx = new Regex("^(//.*\r?\n)*\r?\n");
        var newContent = header + rgx.Replace(oldContent, "");

        if(!newContent.Equals(oldContent, StringComparison.Ordinal))
        {
            if(updateHeaders)
            {
                Information($"\r\nUpdating {file} header...");
                FileWriteText(file, newContent);
            }
            else
            {
                Error($"\r\nWrong/missing header on {file}");
                hasMissing = true;
            }
        }
    }

    if(!updateHeaders && hasMissing)
    {
        throw new Exception("Please run UpdateHeaders.bat or '.\\build.ps1 -target=UpdateHeaders' and commit the changes.");
    }
}

//////////////////////////////////////////////////////////////////////
// TASK TARGETS
//////////////////////////////////////////////////////////////////////

Task("Clean")
    .Description("Clean the output folder and run the solution Clean target")
    .Does(() =>
{
    if(DirectoryExists(binDir))
    {
        Information("\r\nCleaning Working Directory");
        CleanDirectory(binDir);
    }
    else
    {
        CreateDirectory(binDir);
    }

    // Run the clean target on the solution.
    // MSBuildSolution("Clean");
});

Task("VerifyWindowsSDK")
    .Description("Run pre-build verifications")
    .Does(() =>
{
    // Verifies the correct Windows SDK is installed.
    StartPowershellFile("./Find-WindowsSDKVersions.ps1");
});

Task("VerifyHeaders")
    .Description("Verify that headers in source files are updated")
    .Does(() =>
{
    // Outputs warning message if any source files do not have necessary licensing.
    VerifyHeaders(false);
});

Task("UpdateVersionInfo")
    .Description("Updates the version information in all Projects")
    .Does(() =>
{
    Information("\r\nRetrieving version...");
    Version = GitVersioningGetVersion().NuGetPackageVersion;
    Information($"\r\nBuild Version: {Version}");
});

Task("RestoreNugetPackages")
    .Description("Restore all Nuget packages used by solution")
    .Does(() =>
{
    Information("\r\nRestoring Nuget Packages");
    // Restore nuget packages for SceneLoader vcxproj
    var solution = new FilePath(@"..\SceneLoader\packages.config");
    var nugetRestoreSettings = new NuGetRestoreSettings {
        PackagesDirectory = new DirectoryPath(packageDir),
    };
    NuGetRestore(solution, nugetRestoreSettings);
    
    // Restore nuget packages for all csproj files
    var buildSettings = new MSBuildSettings
    {
        MaxCpuCount = 0
    }
    .SetConfiguration("Release")
    .WithTarget("Restore");

    MSBuild(sceneLoaderSln, buildSettings);
});

Task("BuildSolution")
    .Description("Build all projects and get the assemblies")
    .IsDependentOn("VerifyWindowsSDK")
    .IsDependentOn("Clean")
    .IsDependentOn("UpdateVersionInfo")
    .IsDependentOn("VerifyHeaders")
    .IsDependentOn("RestoreNugetPackages")
    .Does(() =>
{
    Information("\r\nBuilding Solution");
    EnsureDirectoryExists(nupkgDir);

    MSBuildSolution("Build", ("GenerateLibraryLayout", "true"));
});

Task("PackageNuget")
    .Description("Pack the NuPkg")
    .IsDependentOn("BuildSolution")
    .Does(() =>
{
    Information("\r\nCopy files needed for Nuget package into a directory and create the package");
    MSBuildSolution("Pack", ("GenerateLibraryLayout", "true"), ("PackageOutputPath", nupkgDir));
    var nuGetPackSettings = new NuGetPackSettings 
    {
        OutputDirectory = nupkgDir, 
        Version = Version 
    }; 
    NuGetPack(nuspec, nuGetPackSettings);
});

Task("UpdateHeaders")
    .Description("Updates the headers in source files")
    .Does(() =>
{
    VerifyHeaders(true);
});

Task("StyleXaml")
    .Description("Ensures XAML Formatting is clean")
    .Does(() =>
{
    Information("\r\nDownloading XamlStyler...");
    var installSettings = new NuGetInstallSettings {
        ExcludeVersion  = true,
        OutputDirectory = toolsDir
    };

    NuGetInstall(new []{"xamlstyler.console"}, installSettings);

    var files = GetFiles($"{baseDir}/**/*.xaml").Where(IsBuildInput);
    Information($"\r\nChecking {files.Count()} file(s) for XAML Structure");
    foreach(var file in files)
    {
        StartProcess(styler, $"-f \"{file}\" -c \"{stylerFile}\"");
    }
});

Task("Default")
    .IsDependentOn("PackageNuget");

//////////////////////////////////////////////////////////////////////
// EXECUTION
//////////////////////////////////////////////////////////////////////

RunTarget(target);
