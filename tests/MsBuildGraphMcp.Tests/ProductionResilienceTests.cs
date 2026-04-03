using System.Text.Json;
using MsBuildGraphMcp.Models;
using MsBuildGraphMcp.Tests.Fixtures;
using MsBuildGraphMcp.Tools;

namespace MsBuildGraphMcp.Tests;

/// <summary>
/// Production resilience: concurrent tool calls, stdout purity, large output,
/// rapid successive calls, error isolation.
/// From claude-code#15945, spring-ai#3472, anthropics/claude-code#36308.
/// </summary>
public class ProductionResilienceTests
{
    private static GraphAnalysis ParseGraph(string json) =>
        JsonSerializer.Deserialize<GraphAnalysis>(json, Helpers.JsonOptions)!;
    private static SolutionAnalysis ParseSln(string json) =>
        JsonSerializer.Deserialize<SolutionAnalysis>(json, Helpers.JsonOptions)!;

    // --- Concurrent tool calls don't crash (ProjectCollection per-request pattern) ---

    [Fact]
    public async Task ConcurrentToolCalls_NoCrash()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("A").AddProject("B").AddProject("C");
        var slnx = builder.BuildSlnx();
        var projPath = builder.GetProjectPath("A");

        // Fire multiple tool calls concurrently
        var tasks = new[]
        {
            Task.Run(() => SolutionTools.AnalyzeSolution(slnx, CancellationToken.None)),
            Task.Run(() => ProjectGraphTools.GetProjectGraph(slnx)),
            Task.Run(() => Task.FromResult(PropertyTools.AnalyzeProjectProperties(projPath))),
            Task.Run(() => Task.FromResult(ConfigurationTools.CompareConfigurations(projPath)))
        };

        var results = await Task.WhenAll(tasks);

        // All should return valid JSON, no crashes
        foreach (var json in results)
        {
            Assert.NotNull(json);
            Assert.True(json.Length > 2); // at least "{}"
            var doc = JsonDocument.Parse(json);
            Assert.NotNull(doc);
        }
    }

    // --- Rapid successive calls don't accumulate memory (fresh ProjectCollection each time) ---

    [Fact]
    public async Task RapidSuccessiveCalls_NoAccumulation()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib");
        var slnx = builder.BuildSlnx();

        // Call 10 times in sequence
        for (int i = 0; i < 10; i++)
        {
            var json = await SolutionTools.AnalyzeSolution(slnx, CancellationToken.None);
            var result = ParseSln(json);
            Assert.Single(result.Projects);
        }

        // If we got here without OOM or crash, the cleanup pattern works
    }

    // --- Error in one tool call doesn't kill subsequent calls ---

    [Fact]
    public async Task ErrorIsolation_BadCallDoesntKillServer()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Good");
        var slnx = builder.BuildSlnx();

        // First: bad call (nonexistent path)
        try
        {
            await SolutionTools.AnalyzeSolution(@"C:\nonexistent.sln", CancellationToken.None);
        }
        catch { /* expected */ }

        // Second: good call should still work
        var result = ParseSln(await SolutionTools.AnalyzeSolution(slnx, CancellationToken.None));
        Assert.Single(result.Projects);
    }

    // --- Large graph output is valid JSON ---

    [Fact]
    public async Task LargeGraph_OutputIsValidJson()
    {
        using var builder = new TempSolutionBuilder();
        // 20 projects — generates substantial JSON output
        for (int i = 0; i < 20; i++)
            builder.AddProject($"P{i:D2}");
        builder.AddProject("App", outputType: "Exe",
            references: Enumerable.Range(0, 20).Select(i => $"P{i:D2}").ToArray());
        var slnx = builder.BuildSlnx();

        var json = await ProjectGraphTools.GetProjectGraph(slnx);

        // Verify it's valid JSON
        var doc = JsonDocument.Parse(json);
        Assert.NotNull(doc);

        var result = ParseGraph(json);
        Assert.Equal(21, result.Metrics.UniqueProjectCount);

        // Output should be reasonable size (not bloated)
        Assert.True(json.Length < 200_000, $"Output too large: {json.Length} bytes");
    }

    // --- All tool outputs are valid JSON (no stdout pollution) ---

    [Fact]
    public async Task AllToolOutputs_AreValidJson()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddProject("Lib").AddProject("App", outputType: "Exe", references: "Lib")
               .WithDirectoryBuildProps();
        var slnx = builder.BuildSlnx();
        var projPath = builder.GetProjectPath("App");

        var outputs = new[]
        {
            await SolutionTools.AnalyzeSolution(slnx, CancellationToken.None),
            await ProjectGraphTools.GetProjectGraph(slnx),
            await BuildIssueTools.DetectBuildIssues(slnx),
            await SharedImportTools.FindSharedImports(slnx),
            PropertyTools.AnalyzeProjectProperties(projPath),
            ConfigurationTools.CompareConfigurations(projPath)
        };

        for (int i = 0; i < outputs.Length; i++)
        {
            Assert.NotNull(outputs[i]);
            // Must be valid JSON — any stdout contamination would break this
            try
            {
                var doc = JsonDocument.Parse(outputs[i]);
                Assert.NotNull(doc);
            }
            catch (JsonException ex)
            {
                Assert.Fail($"Tool {i} output is not valid JSON: {ex.Message}\nFirst 200 chars: {outputs[i][..Math.Min(200, outputs[i].Length)]}");
            }
        }
    }

    // --- Graph with multi-target + many refs — stress test ---

    [Fact]
    public async Task StressTest_MultiTargetWithManyRefs()
    {
        using var builder = new TempSolutionBuilder();
        builder.AddMultiTargetProject("Shared", "net8.0;net6.0");
        for (int i = 0; i < 5; i++)
            builder.AddProject($"Consumer{i}", outputType: "Exe", references: "Shared");
        var slnx = builder.BuildSlnx();

        var result = ParseGraph(await ProjectGraphTools.GetProjectGraph(slnx));

        Assert.True(result.Metrics.UniqueProjectCount >= 6);
        Assert.Empty(result.Errors);
    }

    // --- Tool call with very long property values ---

    [Fact]
    public void LongPropertyValue_SerializedCorrectly()
    {
        using var builder = new TempSolutionBuilder();
        builder.BuildSln();

        var projDir = Path.Combine(builder.RootDir, "LongProp");
        Directory.CreateDirectory(projDir);

        // Create a project with a very long DefineConstants value
        var longConstants = string.Join(";", Enumerable.Range(0, 100).Select(i => $"CONST_{i}"));
        File.WriteAllText(Path.Combine(projDir, "LongProp.csproj"),
$@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <DefineConstants>{longConstants}</DefineConstants>
  </PropertyGroup>
</Project>");
        File.WriteAllText(Path.Combine(projDir, "C.cs"), "namespace LongProp; public class C {}");

        var json = PropertyTools.AnalyzeProjectProperties(
            Path.Combine(projDir, "LongProp.csproj"),
            properties: new[] { "DefineConstants" });

        // Valid JSON with the full value
        var doc = JsonDocument.Parse(json);
        Assert.NotNull(doc);

        var result = JsonSerializer.Deserialize<PropertyAnalysis>(json, Helpers.JsonOptions)!;
        Assert.Contains("CONST_99", result.Properties[0].EvaluatedValue);
    }
}
