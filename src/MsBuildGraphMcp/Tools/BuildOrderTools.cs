using System.ComponentModel;
using System.Text.Json;
using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Evaluation.Context;
using Microsoft.Build.Exceptions;
using Microsoft.Build.Execution;
using Microsoft.Build.Graph;
using ModelContextProtocol.Server;
using MsBuildGraphMcp.Models;
using static MsBuildGraphMcp.Tools.Helpers;

namespace MsBuildGraphMcp.Tools;

[McpServerToolType]
public static class BuildOrderTools
{
    [McpServerTool(Name = "get_build_order", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description(
        "Returns the correct build order (topological sort) for a solution or project. " +
        "Lightweight output — just project names in order, plus critical path length. " +
        "Ideal for understanding 'what builds first?' and parallelism potential. " +
        "Example: get_build_order(\"C:\\src\\MyApp.sln\")")]
    public static async Task<string> GetBuildOrder(
        [Description("Absolute path to .sln, .slnx, .slnf, or .csproj file")] string entryPath,
        [Description("Build configuration (e.g. Release). Optional.")] string? configuration = null,
        [Description("Build platform (e.g. AnyCPU). Optional.")] string? platform = null,
        CancellationToken cancellationToken = default)
    {
        entryPath = ValidateEntryPath(entryPath);

        var result = new BuildOrderAnalysis { EntryPath = entryPath };

        var globalProps = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(configuration))
            globalProps["Configuration"] = configuration;
        if (!string.IsNullOrEmpty(platform))
            globalProps["Platform"] = platform;

        var evalContext = EvaluationContext.Create(EvaluationContext.SharingPolicy.Shared);
        using var collection = new ProjectCollection();
        collection.IsBuildEnabled = false;

        ProjectGraph graph;
        try
        {
            var entryPoints = await ResolveEntryPointsAsync(entryPath, globalProps, cancellationToken);
            graph = new ProjectGraph(
                entryPoints, collection,
                (string path, Dictionary<string, string> globals, ProjectCollection pc) =>
                    ProjectInstance.FromFile(path, new ProjectOptions
                    {
                        GlobalProperties = globals, ProjectCollection = pc,
                        EvaluationContext = evalContext,
                        LoadSettings = ProjectLoadSettings.IgnoreMissingImports | ProjectLoadSettings.IgnoreInvalidImports
                    }),
                Math.Min(Environment.ProcessorCount, 8), cancellationToken);
        }
        catch (InvalidProjectFileException ex) when (ex.ErrorCode == "MSB4251")
        {
            result.CircularDependencyError = ex.Message;
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex) when (ex.GetType().Name == "CircularDependencyException")
        {
            result.CircularDependencyError = ex.Message;
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (AggregateException ae)
        {
            foreach (var inner in ae.Flatten().InnerExceptions)
                result.Errors.Add(inner.Message);
            return JsonSerializer.Serialize(result, JsonOptions);
        }

        // Deduplicated build order (by project path, not inner/outer builds)
        result.BuildOrder = graph.ProjectNodesTopologicallySorted
            .Select(n => n.ProjectInstance.FullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => Path.GetFileNameWithoutExtension(p))
            .ToList();

        result.TotalProjects = result.BuildOrder.Count;

        // Critical path = max depth
        var depths = new Dictionary<ProjectGraphNode, int>();
        foreach (var node in graph.ProjectNodesTopologicallySorted)
        {
            int depth = !node.ProjectReferences.Any()
                ? 0
                : node.ProjectReferences.Max(r => depths.GetValueOrDefault(r, 0)) + 1;
            depths[node] = depth;
        }
        result.CriticalPathLength = depths.Values.DefaultIfEmpty(0).Max();

        return JsonSerializer.Serialize(result, JsonOptions);
    }
}
