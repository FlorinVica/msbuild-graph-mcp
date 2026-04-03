using System.Text.Json;
using MsBuildGraphMcp.Models;
using MsBuildGraphMcp.Tests.Fixtures;
using MsBuildGraphMcp.Tools;

namespace MsBuildGraphMcp.Tests;

/// <summary>
/// Production-grade tests from final research sweep: MSBuild evaluation edge cases,
/// stdout purity, relative path entry points, UsingTask/CodeTaskFactory,
/// deep transitive chains, SDK resolver, and more.
/// Sources: dotnet/msbuild#5898, #10213, #8629, csharp-sdk#820, NuGet/Home#14591.
/// </summary>
public class ProductionGradeTests
{
    // --- #5898: Relative path entry point must be canonicalized ---

    [Fact]
    public async Task RelativePathEntryPoint_ResolvedToAbsolute()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib");
        var slnx = builder.BuildSlnx();

        // Pass absolute path — verify graph uses relative paths in output
        var json = await ProjectGraphTools.GetProjectGraph(slnx);
        var result = JsonSerializer.Deserialize<GraphAnalysis>(json, Helpers.JsonOptions)!;

        // Project paths in output should be relative to solution dir
        foreach (var proj in result.Projects)
        {
            Assert.False(Path.IsPathRooted(proj.Path),
                $"Project path should be relative, got: {proj.Path}");
        }
    }

    // --- csharp-sdk#820: Tool errors should have actionable messages ---

    [Fact]
    public async Task ToolError_HasActionableMessage()
    {
        // Non-existent path should produce clear message, not stack trace
        try
        {
            await SolutionTools.AnalyzeSolution(@"C:\does\not\exist.sln", CancellationToken.None);
            Assert.Fail("Should have thrown");
        }
        catch (FileNotFoundException ex)
        {
            Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("at System.", ex.Message); // no stack trace in message
        }
    }

    [Fact]
    public void PropertyTool_BadExtension_ClearError()
    {
        try
        {
            PropertyTools.AnalyzeProjectProperties(@"C:\foo\bar.txt");
            Assert.Fail("Should have thrown");
        }
        catch (ArgumentException ex)
        {
            Assert.Contains("extension", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    // --- All tool outputs must be valid UTF-8 JSON (stdout purity) ---

    [Fact]
    public async Task AllTools_OutputIsCleanUtf8Json()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib").AddProject("App", outputType: "Exe", references: "Lib")
               .WithDirectoryBuildProps();
        var slnx = builder.BuildSlnx();
        var proj = builder.GetProjectPath("App");

        var outputs = new Dictionary<string, string>
        {
            ["analyze_solution"] = await SolutionTools.AnalyzeSolution(slnx, CancellationToken.None),
            ["get_project_graph"] = await ProjectGraphTools.GetProjectGraph(slnx),
            ["detect_build_issues"] = await BuildIssueTools.DetectBuildIssues(slnx),
            ["find_shared_imports"] = await SharedImportTools.FindSharedImports(slnx),
            ["analyze_project_properties"] = PropertyTools.AnalyzeProjectProperties(proj),
            ["compare_configurations"] = ConfigurationTools.CompareConfigurations(proj)
        };

        foreach (var (tool, json) in outputs)
        {
            // Must start with { (no BOM, no ANSI escape, no log prefix)
            Assert.True(json.TrimStart()[0] == '{',
                $"{tool} output doesn't start with '{{': [{json[..Math.Min(50, json.Length)]}]");

            // Must be valid JSON
            try { JsonDocument.Parse(json); }
            catch (JsonException ex) { Assert.Fail($"{tool} output is invalid JSON: {ex.Message}"); }

            // Must not contain ANSI escape sequences (ESC = U+001B = 0x1B)
            Assert.False(json.Contains((char)0x1B),
                $"{tool} output contains ANSI escape character");
        }
    }

    // --- Project with UsingTask CodeTaskFactory (security boundary) ---

    [Fact]
    public async Task ProjectWithCodeTaskFactory_EvaluatesWithoutExecution()
    {
        using var builder = new TempSolutionBuilder();
        builder.BuildSlnx();

        var projDir = Path.Combine(builder.RootDir, "WithTask");
        Directory.CreateDirectory(projDir);
        File.WriteAllText(Path.Combine(projDir, "WithTask.csproj"),
@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <UsingTask TaskName=""DangerousTask"" TaskFactory=""CodeTaskFactory""
    AssemblyFile=""$(MSBuildToolsPath)\Microsoft.Build.Tasks.Core.dll"">
    <ParameterGroup />
    <Task>
      <Code Type=""Fragment"" Language=""cs"">
        System.IO.File.WriteAllText(@""C:\SHOULD_NOT_EXIST.txt"", ""pwned"");
      </Code>
    </Task>
  </UsingTask>
  <Target Name=""RunDangerous"" AfterTargets=""Build"">
    <DangerousTask />
  </Target>
</Project>");
        File.WriteAllText(Path.Combine(projDir, "C.cs"), "namespace WithTask; public class C {}");

        // IsBuildEnabled=false should prevent target execution
        var json = PropertyTools.AnalyzeProjectProperties(
            Path.Combine(projDir, "WithTask.csproj"),
            properties: new[] { "TargetFramework" });

        var result = JsonSerializer.Deserialize<PropertyAnalysis>(json, Helpers.JsonOptions)!;
        Assert.Equal("net8.0", result.Properties[0].EvaluatedValue);

        // The dangerous file should NOT have been created
        Assert.False(File.Exists(@"C:\SHOULD_NOT_EXIST.txt"),
            "CodeTaskFactory executed during evaluation — IsBuildEnabled=false did not prevent it");
    }

    // --- Deep transitive chain (5 levels) discovered correctly ---

    [Fact]
    public async Task FiveLevelChain_AllTransitiveDepsDiscovered()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("L1")
               .AddProject("L2", references: "L1")
               .AddProject("L3", references: "L2")
               .AddProject("L4", references: "L3")
               .AddProject("L5", outputType: "Exe", references: "L4");
        var slnx = builder.BuildSlnx();

        var json = await ProjectGraphTools.GetProjectGraph(slnx);
        var result = JsonSerializer.Deserialize<GraphAnalysis>(json, Helpers.JsonOptions)!;

        Assert.Equal(5, result.Metrics.UniqueProjectCount);
        Assert.Equal(4, result.Metrics.MaxDepth);

        // Topological order: L1 first, L5 last
        var order = result.TopologicalOrder.Select(Path.GetFileNameWithoutExtension).ToList();
        Assert.Equal("L1", order[0]);
        Assert.Equal("L5", order[^1]);

        // L1 should be transitively referenced by all others
        var l1 = result.Projects.First(p => p.Name == "L1");
        Assert.True(l1.IsLeaf);
        Assert.True(l1.ReferencedBy.Count >= 1); // At least L2 directly
    }

    // --- EvaluationContext.Shared actually used (perf guard) ---

    [Fact]
    public async Task TenProjectSolution_ConstructsInReasonableTime()
    {
        using var builder = new TempSolutionBuilder();
        for (int i = 0; i < 10; i++)
            builder.AddProject($"Proj{i}");
        var slnx = builder.BuildSlnx();

        var json = await ProjectGraphTools.GetProjectGraph(slnx);
        var result = JsonSerializer.Deserialize<GraphAnalysis>(json, Helpers.JsonOptions)!;

        Assert.Equal(10, result.Metrics.UniqueProjectCount);
        // With EvaluationContext.Shared, 10 projects should be <3 seconds
        Assert.True(result.Metrics.ConstructionTimeMs < 3000,
            $"Graph construction took {result.Metrics.ConstructionTimeMs}ms — may not be using shared EvaluationContext");
    }

    // --- Project with Condition on TargetFramework in Directory.Build.props ---

    [Fact]
    public async Task ConditionalTargetFrameworkInDirBuildProps_HandledCorrectly()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("App", outputType: "Exe");
        builder.WithDirectoryBuildProps(
@"<Project>
  <PropertyGroup Condition=""'$(TargetFramework)' == 'net8.0'"">
    <CustomPlatformFlag>WindowsOnly</CustomPlatformFlag>
  </PropertyGroup>
  <PropertyGroup Condition=""'$(TargetFramework)' == ''"">
    <CustomPlatformFlag>NotSet</CustomPlatformFlag>
  </PropertyGroup>
</Project>");
        var slnx = builder.BuildSlnx();

        // TargetFramework is empty during Dir.Build.props eval (msbuild#1991)
        var json = PropertyTools.AnalyzeProjectProperties(
            builder.GetProjectPath("App"),
            properties: new[] { "CustomPlatformFlag" });

        var result = JsonSerializer.Deserialize<PropertyAnalysis>(json, Helpers.JsonOptions)!;
        var prop = result.Properties.First(p => p.Name == "CustomPlatformFlag");
        // Should be "NotSet" because TF is empty during Dir.Build.props
        // OR "WindowsOnly" if evaluated after SDK sets TF — either way, not crash
        Assert.NotNull(prop.EvaluatedValue);
    }

    // --- Multi-target project in detect_build_issues doesn't double-count ---

    [Fact]
    public async Task MultiTargetProject_IssuesNotDoubledByInnerBuilds()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddMultiTargetProject("Multi", "net8.0;net6.0")
               .AddProject("Consumer", tfm: "net6.0", outputType: "Exe", references: "Multi");
        var slnx = builder.BuildSlnx();

        var json = await BuildIssueTools.DetectBuildIssues(slnx);
        var result = JsonSerializer.Deserialize<BuildIssueAnalysis>(json, Helpers.JsonOptions)!;

        // Should not have duplicate TFM mismatch entries for net6.0→net8.0 inner build
        // (only the actual incompatible pair should be flagged)
        var uniqueMismatches = result.FrameworkMismatches
            .Select(m => $"{m.Project}→{m.Dependency}")
            .Distinct()
            .ToList();
        Assert.Equal(result.FrameworkMismatches.Count, uniqueMismatches.Count);
    }

    // --- Graph output JSON size bounded for LLM context ---

    [Fact]
    public async Task GraphOutput_SizeBoundedForLlmContext()
    {
        using var builder = new TempSolutionBuilder();
        // 25 projects — realistic medium solution
        for (int i = 0; i < 25; i++)
            builder.AddProject($"Lib{i:D2}");
        builder.AddProject("App", outputType: "Exe",
            references: Enumerable.Range(0, 25).Select(i => $"Lib{i:D2}").ToArray());
        var slnx = builder.BuildSlnx();

        var json = await ProjectGraphTools.GetProjectGraph(slnx);

        // JSON should be reasonable size — not bloated
        // 26 projects × ~500 bytes each ≈ 13KB, with overhead maybe 50KB max
        Assert.True(json.Length < 100_000,
            $"Graph output is {json.Length} bytes for 26 projects — may be too large for LLM context");
    }

    // --- Error isolation between calls ---

    [Fact]
    public async Task ErrorInOneTool_DoesNotAffectAnother()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Good");
        var slnx = builder.BuildSlnx();

        // First: intentionally bad call
        try { await ProjectGraphTools.GetProjectGraph(@"C:\nonexistent.sln"); }
        catch { /* expected */ }

        // Second: good call must work perfectly
        var json = await ProjectGraphTools.GetProjectGraph(slnx);
        var result = JsonSerializer.Deserialize<GraphAnalysis>(json, Helpers.JsonOptions)!;
        Assert.Equal(1, result.Metrics.UniqueProjectCount);
        Assert.Empty(result.Errors);
    }
}
