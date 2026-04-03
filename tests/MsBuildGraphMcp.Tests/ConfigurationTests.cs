using System.Text.Json;
using MsBuildGraphMcp.Models;
using MsBuildGraphMcp.Tests.Fixtures;
using MsBuildGraphMcp.Tools;

namespace MsBuildGraphMcp.Tests;

public class ConfigurationTests
{
    private static ConfigurationDiff Parse(string json) =>
        JsonSerializer.Deserialize<ConfigurationDiff>(json, Helpers.JsonOptions)!;

    // --- Debug vs Release ---

    [Fact]
    public void DebugVsRelease_HasDifferences()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("App", outputType: "Exe");
        builder.BuildSln();

        var result = Parse(ConfigurationTools.CompareConfigurations(builder.GetProjectPath("App")));

        Assert.True(result.TotalDifferences > 0);
        Assert.Equal("Debug", result.Config1);
        Assert.Equal("Release", result.Config2);
    }

    [Fact]
    public void DebugVsRelease_DefineConstants_Differ()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("App", outputType: "Exe");
        builder.BuildSln();

        var result = Parse(ConfigurationTools.CompareConfigurations(builder.GetProjectPath("App")));

        var dc = result.PropertyDifferences.FirstOrDefault(p => p.Name == "DefineConstants");
        Assert.NotNull(dc);
        Assert.Contains("DEBUG", dc.Value1);
        Assert.Contains("RELEASE", dc.Value2);
    }

    [Fact]
    public void DebugVsRelease_Optimize_Differs()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib");
        builder.BuildSln();

        var result = Parse(ConfigurationTools.CompareConfigurations(builder.GetProjectPath("Lib")));

        var opt = result.PropertyDifferences.FirstOrDefault(p => p.Name == "Optimize");
        Assert.NotNull(opt);
        Assert.Equal("false", opt.Value1);  // Debug
        Assert.Equal("true", opt.Value2);   // Release
    }

    [Fact]
    public void DebugVsRelease_OutputPath_Differs()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib");
        builder.BuildSln();

        var result = Parse(ConfigurationTools.CompareConfigurations(builder.GetProjectPath("Lib")));

        var op = result.PropertyDifferences.FirstOrDefault(p => p.Name == "OutputPath");
        Assert.NotNull(op);
        Assert.Contains("Debug", op.Value1);
        Assert.Contains("Release", op.Value2);
    }

    // --- Same config (no differences) ---

    [Fact]
    public void SameConfig_NoDifferences()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib");
        builder.BuildSln();

        var result = Parse(ConfigurationTools.CompareConfigurations(
            builder.GetProjectPath("Lib"), config1: "Release", config2: "Release"));

        Assert.Equal(0, result.TotalDifferences);
    }

    // --- Platform parameter ---

    [Fact]
    public void PlatformParameter_Recorded()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib");
        builder.BuildSln();

        var result = Parse(ConfigurationTools.CompareConfigurations(
            builder.GetProjectPath("Lib"), platform: "x64"));

        Assert.Equal("x64", result.Platform);
    }

    // --- Package/Project reference differences ---

    [Fact]
    public void NoConditionalPackages_EmptyDifferences()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib");
        builder.BuildSln();

        var result = Parse(ConfigurationTools.CompareConfigurations(builder.GetProjectPath("Lib")));

        Assert.Empty(result.PackageReferenceDifferences);
        Assert.Empty(result.ProjectReferenceDifferences);
    }

    // --- ProjectPath in output ---

    [Fact]
    public void ProjectPath_IncludedInOutput()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib");
        builder.BuildSln();

        var result = Parse(ConfigurationTools.CompareConfigurations(builder.GetProjectPath("Lib")));

        Assert.Contains("Lib.csproj", result.ProjectPath);
    }

    // --- Validation ---

    [Fact]
    public void InvalidPath_Throws()
    {
        Assert.Throws<FileNotFoundException>(
            () => ConfigurationTools.CompareConfigurations(@"C:\nonexistent.csproj"));
    }

    [Fact]
    public void WrongExtension_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => ConfigurationTools.CompareConfigurations(@"C:\foo.sln"));
    }
}
