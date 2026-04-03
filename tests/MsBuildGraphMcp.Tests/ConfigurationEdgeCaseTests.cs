using System.Text.Json;
using MsBuildGraphMcp.Models;
using MsBuildGraphMcp.Tests.Fixtures;
using MsBuildGraphMcp.Tools;

namespace MsBuildGraphMcp.Tests;

/// <summary>Tests for conditional items, cancellation, custom config names.</summary>
public class ConfigurationEdgeCaseTests
{
    private static ConfigurationDiff Parse(string json) =>
        JsonSerializer.Deserialize<ConfigurationDiff>(json, Helpers.JsonOptions)!;

    // --- Conditional PackageReference ---

    [Fact]
    public void ConditionalPackageReference_DetectedAsDifference()
    {
        using var builder = new TempSolutionBuilder();
        builder.BuildSln(); // write base

        // Create project with conditional PackageReference
        var projDir = Path.Combine(builder.RootDir, "App");
        Directory.CreateDirectory(projDir);
        File.WriteAllText(Path.Combine(projDir, "App.csproj"), @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup Condition=""'$(Configuration)' == 'Debug'"">
    <PackageReference Include=""BenchmarkDotNet"" Version=""0.14.0"" />
  </ItemGroup>
</Project>");
        File.WriteAllText(Path.Combine(projDir, "Program.cs"), "// empty\n");

        var projPath = Path.Combine(projDir, "App.csproj");
        var result = Parse(ConfigurationTools.CompareConfigurations(projPath));

        // BenchmarkDotNet should only be in Debug
        Assert.Contains(result.PackageReferenceDifferences,
            d => d.ItemName == "BenchmarkDotNet" && d.DiffType.Contains("Debug"));
    }

    // --- Conditional ProjectReference ---

    [Fact]
    public void ConditionalProjectReference_DetectedAsDifference()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("DebugHelper");
        builder.BuildSln();

        // Create App that only references DebugHelper in Debug
        var projDir = Path.Combine(builder.RootDir, "App");
        Directory.CreateDirectory(projDir);
        File.WriteAllText(Path.Combine(projDir, "App.csproj"), @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup Condition=""'$(Configuration)' == 'Debug'"">
    <ProjectReference Include=""..\DebugHelper\DebugHelper.csproj"" />
  </ItemGroup>
</Project>");
        File.WriteAllText(Path.Combine(projDir, "Program.cs"), "// empty\n");

        var result = Parse(ConfigurationTools.CompareConfigurations(
            Path.Combine(projDir, "App.csproj")));

        Assert.Contains(result.ProjectReferenceDifferences,
            d => d.ItemName.Contains("DebugHelper") && d.DiffType.Contains("Debug"));
    }

    // --- Custom config names ---

    [Fact]
    public void CustomConfigNames_Work()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib");
        builder.BuildSln();

        // Custom config names like "Staging" vs "Production" won't differ much
        // since MSBuild defaults apply, but the tool should still work
        var result = Parse(ConfigurationTools.CompareConfigurations(
            builder.GetProjectPath("Lib"), config1: "Staging", config2: "Production"));

        Assert.Equal("Staging", result.Config1);
        Assert.Equal("Production", result.Config2);
        // Properties should still differ (DefineConstants contains config name)
        Assert.True(result.PropertyDifferences.Count > 0);
    }

    // --- CancellationToken ---

    [Fact]
    public void CancellationToken_PreCancelled_Throws()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib");
        builder.BuildSln();

        var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(
            () => ConfigurationTools.CompareConfigurations(
                builder.GetProjectPath("Lib"), cancellationToken: cts.Token));
    }

    // --- DebugType difference ---

    [Fact]
    public void DebugType_DiffersBetweenConfigs()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib");
        builder.BuildSln();

        var result = Parse(ConfigurationTools.CompareConfigurations(builder.GetProjectPath("Lib")));

        // DebugSymbols should differ (true for Debug, false for Release)
        var debugSymbols = result.PropertyDifferences.FirstOrDefault(p => p.Name == "DebugSymbols");
        Assert.NotNull(debugSymbols);
    }
}
