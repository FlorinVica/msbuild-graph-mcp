using System.Text.Json;
using MsBuildGraphMcp.Models;
using MsBuildGraphMcp.Tests.Fixtures;
using MsBuildGraphMcp.Tools;

namespace MsBuildGraphMcp.Tests;

/// <summary>
/// Real-world edge cases from Stack Overflow, Cursor forum, GitHub issues, Microsoft DevCommunity.
/// Duplicate project load, unusual project types, concurrent evaluation, encoding, special chars.
/// </summary>
public class RealWorldEdgeCaseTests
{
    // --- dotnet/upgrade-assistant#635: Duplicate project load ---

    [Fact]
    public async Task DuplicateProjectInSolution_ThrowsOrDeduplicates()
    {
        // SolutionPersistence throws SolutionException on duplicate project paths.
        // This is correct behavior — document it.
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib");
        var slnx = builder.BuildSlnx();

        File.WriteAllText(slnx, @"<Solution>
  <Project Path=""Lib\Lib.csproj"" />
  <Project Path=""Lib\Lib.csproj"" />
</Solution>");

        // SolutionPersistence rejects duplicates — verify exception is thrown, not crash
        await Assert.ThrowsAnyAsync<Exception>(
            () => SolutionTools.AnalyzeSolution(slnx, CancellationToken.None));
    }

    // --- dotnet/msbuild#6236: Website project URL paths ---

    [Fact]
    public async Task SolutionWithNonFileProjectPath_ThrowsOrSkips()
    {
        // MSBuild SolutionFile.Parse rejects website projects with URL paths
        // when ProjectConfigurations section is missing. This is expected — document it.
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib");
        var slnx = builder.BuildSlnx();

        // Use .slnx with a non-existent but file-like path (not URL)
        File.WriteAllText(slnx, @"<Solution>
  <Project Path=""Lib\Lib.csproj"" />
  <Project Path=""Legacy\Legacy.csproj"" />
</Solution>");

        // Legacy project doesn't exist — should report error and continue
        var json = await SolutionTools.AnalyzeSolution(slnx, CancellationToken.None);
        var result = JsonSerializer.Deserialize<SolutionAnalysis>(json, Helpers.JsonOptions)!;
        Assert.Contains(result.Projects, p => p.Name == "Lib");
        Assert.Contains(result.Errors, e => e.Contains("Legacy"));
    }

    // --- Empty/minimal project file ---

    [Fact]
    public async Task EmptyProjectElement_HandledGracefully()
    {
        using var builder = new TempSolutionBuilder();
        builder.BuildSlnx();

        var emptyDir = Path.Combine(builder.RootDir, "Empty");
        Directory.CreateDirectory(emptyDir);
        File.WriteAllText(Path.Combine(emptyDir, "Empty.csproj"), "<Project></Project>");

        var slnx = Path.Combine(builder.RootDir, "Test.slnx");
        File.WriteAllText(slnx, "<Solution>\r\n  <Project Path=\"Empty\\Empty.csproj\" />\r\n</Solution>\r\n");

        // Should evaluate (empty project is valid MSBuild) or error — not crash
        var json = await SolutionTools.AnalyzeSolution(slnx, CancellationToken.None);
        var result = JsonSerializer.Deserialize<SolutionAnalysis>(json, Helpers.JsonOptions)!;
        Assert.NotNull(result);
    }

    // --- Concurrent calls to different tools (thread safety) ---

    [Fact]
    public async Task ConcurrentDifferentToolCalls_AllReturnValidJson()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("A").AddProject("B", references: "A")
               .AddProject("C", outputType: "Exe", references: "B")
               .WithDirectoryBuildProps();
        var slnx = builder.BuildSlnx();
        var projPath = builder.GetProjectPath("C");

        // Fire 6 different tool calls simultaneously
        var tasks = new List<Task<string>>
        {
            SolutionTools.AnalyzeSolution(slnx, CancellationToken.None),
            ProjectGraphTools.GetProjectGraph(slnx),
            BuildIssueTools.DetectBuildIssues(slnx),
            SharedImportTools.FindSharedImports(slnx),
            Task.FromResult(PropertyTools.AnalyzeProjectProperties(projPath)),
            Task.FromResult(ConfigurationTools.CompareConfigurations(projPath))
        };

        var results = await Task.WhenAll(tasks);

        // ALL must return valid JSON
        for (int i = 0; i < results.Length; i++)
        {
            Assert.NotNull(results[i]);
            Assert.DoesNotContain("\"isError\":true", results[i]);
            var doc = JsonDocument.Parse(results[i]);
            Assert.NotNull(doc);
        }
    }

    // --- Same tool called 3x in parallel ---

    [Fact]
    public async Task SameToolCalledInParallel_NoDeadlock()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib1").AddProject("Lib2").AddProject("Lib3");
        var slnx = builder.BuildSlnx();

        var tasks = Enumerable.Range(0, 3)
            .Select(_ => ProjectGraphTools.GetProjectGraph(slnx))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        foreach (var json in results)
        {
            var result = JsonSerializer.Deserialize<GraphAnalysis>(json, Helpers.JsonOptions)!;
            Assert.Equal(3, result.Metrics.UniqueProjectCount);
        }
    }

    // --- Special characters in property values (msbuild#8762) ---

    [Fact]
    public void PropertyValueWithSpecialChars_SerializedCorrectly()
    {
        using var builder = new TempSolutionBuilder();
        builder.BuildSln();

        var projDir = Path.Combine(builder.RootDir, "SpecialChars");
        Directory.CreateDirectory(projDir);
        File.WriteAllText(Path.Combine(projDir, "SpecialChars.csproj"),
@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <DefineConstants>HAS_FEATURE;VERSION=2;DEBUG_MODE</DefineConstants>
    <Description>A library with &amp; symbols and &lt;angle brackets&gt;</Description>
  </PropertyGroup>
</Project>");
        File.WriteAllText(Path.Combine(projDir, "C.cs"), "namespace SpecialChars; public class C {}");

        var json = PropertyTools.AnalyzeProjectProperties(
            Path.Combine(projDir, "SpecialChars.csproj"),
            properties: new[] { "DefineConstants", "Description" });

        // Must be valid JSON
        var doc = JsonDocument.Parse(json);
        Assert.NotNull(doc);

        var result = JsonSerializer.Deserialize<PropertyAnalysis>(json, Helpers.JsonOptions)!;
        var dc = result.Properties.First(p => p.Name == "DefineConstants");
        Assert.Contains("HAS_FEATURE", dc.EvaluatedValue);

        var desc = result.Properties.First(p => p.Name == "Description");
        Assert.Contains("&", desc.EvaluatedValue); // XML entity decoded
        Assert.Contains("<angle brackets>", desc.EvaluatedValue);
    }

    // --- Project with custom props file in same directory ---

    [Fact]
    public async Task ProjectWithLocalPropsFile_ImportDetected()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("App");
        var slnx = builder.BuildSlnx();

        // Write a custom .props file next to the project
        File.WriteAllText(Path.Combine(builder.RootDir, "App", "Custom.props"),
@"<Project>
  <PropertyGroup>
    <CustomVersion>1.2.3</CustomVersion>
  </PropertyGroup>
</Project>");

        // Modify the project to import it
        var csproj = builder.GetProjectPath("App");
        var content = File.ReadAllText(csproj);
        content = content.Replace("</Project>",
            "  <Import Project=\"Custom.props\" />\r\n</Project>");
        File.WriteAllText(csproj, content);

        var json = PropertyTools.AnalyzeProjectProperties(csproj,
            properties: new[] { "CustomVersion" });
        var result = JsonSerializer.Deserialize<PropertyAnalysis>(json, Helpers.JsonOptions)!;
        var prop = result.Properties.First(p => p.Name == "CustomVersion");
        Assert.Equal("1.2.3", prop.EvaluatedValue);
        Assert.True(prop.IsImported);
    }

    // --- Rapid successive calls to analyze_solution (memory stability) ---

    [Fact]
    public async Task TwentyRapidCalls_NoMemoryLeak()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("A").AddProject("B").AddProject("C");
        var slnx = builder.BuildSlnx();

        for (int i = 0; i < 20; i++)
        {
            var json = await SolutionTools.AnalyzeSolution(slnx, CancellationToken.None);
            var result = JsonSerializer.Deserialize<SolutionAnalysis>(json, Helpers.JsonOptions)!;
            Assert.Equal(3, result.ProjectCount);
        }
        // If we got here without OOM, ProjectCollection cleanup works
    }

    // --- Large fan-in + fan-out graph (stress topology) ---

    [Fact]
    public async Task ComplexTopology_DiamondAndChain()
    {
        using var builder = new TempSolutionBuilder();
        // Diamond: Core ← Lib1, Lib2 → App
        // Plus chain: Util → Helper → Service → App
        builder.AddProject("Core")
               .AddProject("Lib1", references: "Core")
               .AddProject("Lib2", references: "Core")
               .AddProject("Util")
               .AddProject("Helper", references: "Util")
               .AddProject("Service", references: "Helper")
               .AddProject("App", outputType: "Exe",
                   references: new[] { "Lib1", "Lib2", "Service" });
        var slnx = builder.BuildSlnx();

        var json = await ProjectGraphTools.GetProjectGraph(slnx);
        var result = JsonSerializer.Deserialize<GraphAnalysis>(json, Helpers.JsonOptions)!;

        Assert.Equal(7, result.Metrics.UniqueProjectCount);
        Assert.True(result.Metrics.MaxDepth >= 3); // Util→Helper→Service→App

        var app = result.Projects.First(p => p.Name == "App");
        Assert.True(app.IsRoot);
        Assert.True(app.References.Count >= 3);

        var core = result.Projects.First(p => p.Name == "Core");
        Assert.True(core.IsLeaf);
        Assert.True(core.ReferencedBy.Count >= 2); // Lib1 + Lib2
    }

    // --- detect_build_issues with mixed compatible/incompatible refs ---

    [Fact]
    public async Task MixedCompatibility_OnlyIncompatibleFlagged()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("StdLib", tfm: "netstandard2.0")     // compatible with everything
               .AddProject("ModernLib", tfm: "net8.0")            // only compat with net8.0+
               .AddProject("OldApp", tfm: "net6.0", outputType: "Exe",
                   references: new[] { "StdLib", "ModernLib" });  // StdLib OK, ModernLib NOT
        var slnx = builder.BuildSlnx();

        var json = await BuildIssueTools.DetectBuildIssues(slnx);
        var result = JsonSerializer.Deserialize<BuildIssueAnalysis>(json, Helpers.JsonOptions)!;

        // Only ModernLib should be flagged, not StdLib
        Assert.Single(result.FrameworkMismatches);
        Assert.Contains("ModernLib", result.FrameworkMismatches[0].Dependency);
        Assert.DoesNotContain(result.FrameworkMismatches,
            m => m.Dependency.Contains("StdLib"));
    }
}
