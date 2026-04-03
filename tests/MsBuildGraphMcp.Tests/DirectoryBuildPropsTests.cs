using System.Text.Json;
using MsBuildGraphMcp.Models;
using MsBuildGraphMcp.Tests.Fixtures;
using MsBuildGraphMcp.Tools;

namespace MsBuildGraphMcp.Tests;

/// <summary>
/// Directory.Build.props evaluation traps from dotnet/msbuild#1991, #9877, #5249.
/// Configuration is empty during Dir.Build.props eval, nested overrides, CPM detection.
/// </summary>
public class DirectoryBuildPropsTests
{
    private static PropertyAnalysis ParseProps(string json) =>
        JsonSerializer.Deserialize<PropertyAnalysis>(json, Helpers.JsonOptions)!;
    private static SharedImportAnalysis ParseImports(string json) =>
        JsonSerializer.Deserialize<SharedImportAnalysis>(json, Helpers.JsonOptions)!;

    // --- #1991: $(Configuration) is empty in Directory.Build.props ---

    [Fact]
    public void ConfigurationEmpty_InDirectoryBuildProps()
    {
        using var builder = new TempSolutionBuilder();
        // Dir.Build.props tries to use $(Configuration) — it's empty at this evaluation point
        builder.AddProject("Lib").WithDirectoryBuildProps(
@"<Project>
  <PropertyGroup>
    <CustomConfigCheck>$(Configuration)</CustomConfigCheck>
  </PropertyGroup>
</Project>");
        builder.BuildSln();

        var result = ParseProps(PropertyTools.AnalyzeProjectProperties(
            builder.GetProjectPath("Lib"),
            properties: new[] { "CustomConfigCheck" }));

        var prop = result.Properties.First(p => p.Name == "CustomConfigCheck");
        // $(Configuration) is empty during Dir.Build.props evaluation
        // (it gets set later by Microsoft.Common.props)
        Assert.True(string.IsNullOrEmpty(prop.EvaluatedValue),
            $"Expected empty but got '{prop.EvaluatedValue}' — Configuration should be empty during Dir.Build.props eval");
    }

    // --- Nested Directory.Build.props — subdirectory overrides parent ---

    [Fact]
    public void NestedDirectoryBuildProps_SubdirWins()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib");
        builder.BuildSln();

        // Root Dir.Build.props
        File.WriteAllText(Path.Combine(builder.RootDir, "Directory.Build.props"),
@"<Project>
  <PropertyGroup>
    <RootCustomProp>FromRoot</RootCustomProp>
  </PropertyGroup>
</Project>");

        // Nested Dir.Build.props in Lib directory (overrides root)
        File.WriteAllText(Path.Combine(builder.RootDir, "Lib", "Directory.Build.props"),
@"<Project>
  <PropertyGroup>
    <RootCustomProp>FromNested</RootCustomProp>
  </PropertyGroup>
</Project>");

        var result = ParseProps(PropertyTools.AnalyzeProjectProperties(
            builder.GetProjectPath("Lib"),
            properties: new[] { "RootCustomProp" }));

        var prop = result.Properties.First(p => p.Name == "RootCustomProp");
        // MSBuild: first Dir.Build.props found walking upward wins
        Assert.Equal("FromNested", prop.EvaluatedValue);
    }

    // --- CPM: ManagePackageVersionsCentrally detected ---

    [Fact]
    public void CpmEnabled_DetectedInProperties()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib").WithDirectoryPackagesProps();
        builder.BuildSln();

        var result = ParseProps(PropertyTools.AnalyzeProjectProperties(
            builder.GetProjectPath("Lib"),
            properties: new[] { "ManagePackageVersionsCentrally" }));

        var prop = result.Properties.First(p => p.Name == "ManagePackageVersionsCentrally");
        Assert.Equal("true", prop.EvaluatedValue);
        Assert.True(prop.IsImported); // Comes from Directory.Packages.props
    }

    // --- CPM: Directory.Packages.props detected in shared imports ---

    [Fact]
    public async Task CpmFile_DetectedInSharedImports()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("A").AddProject("B").WithDirectoryPackagesProps();
        var slnx = builder.BuildSlnx();

        var result = ParseImports(await SharedImportTools.FindSharedImports(slnx));

        Assert.Contains(result.SpecialFiles,
            sf => sf.FileType == "CentralPackageManagement");
    }

    // --- Dir.Build.props property traced to correct file and line ---

    [Fact]
    public void PropertyFromDirBuildProps_CorrectSourceTracking()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib").WithDirectoryBuildProps(
@"<Project>
  <PropertyGroup>
    <LangVersion>12</LangVersion>
  </PropertyGroup>
</Project>");
        builder.BuildSln();

        var result = ParseProps(PropertyTools.AnalyzeProjectProperties(
            builder.GetProjectPath("Lib"),
            properties: new[] { "LangVersion" }));

        var prop = result.Properties.First(p => p.Name == "LangVersion");
        Assert.Equal("12", prop.EvaluatedValue);
        Assert.True(prop.IsImported);
        Assert.Contains("Directory.Build.props", prop.SourceFile);
        Assert.Equal(3, prop.SourceLine); // Line 3 in the file
    }

    // --- Dir.Build.props + Dir.Build.targets both in import chain ---

    [Fact]
    public void BothPropsAndTargets_VisibleInImportChain()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib").WithDirectoryBuildProps();
        builder.BuildSln();

        // Also create Directory.Build.targets
        File.WriteAllText(Path.Combine(builder.RootDir, "Directory.Build.targets"),
            "<Project>\r\n</Project>\r\n");

        var result = ParseProps(PropertyTools.AnalyzeProjectProperties(builder.GetProjectPath("Lib")));

        Assert.Contains(result.ImportChain,
            i => i.ImportedFile.Contains("Directory.Build.props"));
        Assert.Contains(result.ImportChain,
            i => i.ImportedFile.Contains("Directory.Build.targets"));
    }
}
