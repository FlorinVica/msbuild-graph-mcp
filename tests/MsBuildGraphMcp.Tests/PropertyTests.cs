using System.Text.Json;
using MsBuildGraphMcp.Models;
using MsBuildGraphMcp.Tests.Fixtures;
using MsBuildGraphMcp.Tools;

namespace MsBuildGraphMcp.Tests;

public class PropertyTests
{
    private static PropertyAnalysis Parse(string json) =>
        JsonSerializer.Deserialize<PropertyAnalysis>(json, Helpers.JsonOptions)!;

    // --- Default properties ---

    [Fact]
    public void DefaultProperties_IncludeKeyBuildProps()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib");
        builder.BuildSln();

        var result = Parse(PropertyTools.AnalyzeProjectProperties(builder.GetProjectPath("Lib")));

        var names = result.Properties.Select(p => p.Name).ToList();
        Assert.Contains("TargetFramework", names);
        Assert.Contains("OutputType", names);
        Assert.Contains("Nullable", names);
    }

    [Fact]
    public void DefaultProperties_TargetFrameworkValue()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib", tfm: "net8.0");
        builder.BuildSln();

        var result = Parse(PropertyTools.AnalyzeProjectProperties(builder.GetProjectPath("Lib")));

        var tfm = result.Properties.First(p => p.Name == "TargetFramework");
        Assert.Equal("net8.0", tfm.EvaluatedValue);
    }

    // --- Custom property filter ---

    [Fact]
    public void CustomFilter_ReturnsOnlyRequested()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib");
        builder.BuildSln();

        var result = Parse(PropertyTools.AnalyzeProjectProperties(
            builder.GetProjectPath("Lib"),
            properties: new[] { "OutputType", "TargetFramework" }));

        Assert.Equal(2, result.Properties.Count);
        Assert.All(result.Properties, p =>
            Assert.True(p.Name == "OutputType" || p.Name == "TargetFramework"));
    }

    [Fact]
    public void CustomFilter_NonExistentProperty_StillReported()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib");
        builder.BuildSln();

        var result = Parse(PropertyTools.AnalyzeProjectProperties(
            builder.GetProjectPath("Lib"),
            properties: new[] { "NonExistentProperty12345" }));

        Assert.Single(result.Properties);
        Assert.Equal("NonExistentProperty12345", result.Properties[0].Name);
        Assert.Null(result.Properties[0].EvaluatedValue);
    }

    // --- Source tracking ---

    [Fact]
    public void SourceTracking_PropertyFromCsproj_NotImported()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib");
        builder.BuildSln();

        var result = Parse(PropertyTools.AnalyzeProjectProperties(
            builder.GetProjectPath("Lib"),
            properties: new[] { "OutputType" }));

        var prop = result.Properties[0];
        Assert.False(prop.IsImported);
        Assert.Contains("Lib.csproj", prop.SourceFile);
        Assert.NotNull(prop.SourceLine);
    }

    [Fact]
    public void SourceTracking_PropertyFromDirectoryBuildProps_IsImported()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib").WithDirectoryBuildProps();
        builder.BuildSln();

        var result = Parse(PropertyTools.AnalyzeProjectProperties(
            builder.GetProjectPath("Lib"),
            properties: new[] { "LangVersion" }));

        var prop = result.Properties[0];
        Assert.True(prop.IsImported);
        Assert.Contains("Directory.Build.props", prop.SourceFile);
    }

    // --- Import chain ---

    [Fact]
    public void ImportChain_ContainsSdkImports()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib");
        builder.BuildSln();

        var result = Parse(PropertyTools.AnalyzeProjectProperties(builder.GetProjectPath("Lib")));

        Assert.True(result.ImportChain.Count > 0);
        Assert.Contains(result.ImportChain, i => i.IsSdkImport);
    }

    [Fact]
    public void ImportChain_IncludesDirectoryBuildProps()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib").WithDirectoryBuildProps();
        builder.BuildSln();

        var result = Parse(PropertyTools.AnalyzeProjectProperties(builder.GetProjectPath("Lib")));

        Assert.Contains(result.ImportChain,
            i => i.ImportedFile.Contains("Directory.Build.props"));
    }

    // --- Previous value (property override chain) ---

    [Fact]
    public void PreviousValue_OutputTypeOverridesDefault()
    {
        // OutputType defaults to Library in SDK, then Exe overrides it
        using var builder = new TempSolutionBuilder();
        builder.AddProject("App", outputType: "Exe");
        builder.BuildSln();

        var result = Parse(PropertyTools.AnalyzeProjectProperties(
            builder.GetProjectPath("App"),
            properties: new[] { "OutputType" }));

        var prop = result.Properties[0];
        Assert.Equal("Exe", prop.EvaluatedValue);
        // SDK sets it to Library first, then .csproj overrides to Exe
        Assert.Equal("Library", prop.PreviousValue);
    }

    // --- Validation ---

    [Fact]
    public void InvalidPath_Throws()
    {
        Assert.Throws<FileNotFoundException>(
            () => PropertyTools.AnalyzeProjectProperties(@"C:\nonexistent.csproj"));
    }

    [Fact]
    public void WrongExtension_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => PropertyTools.AnalyzeProjectProperties(@"C:\foo.txt"));
    }
}
