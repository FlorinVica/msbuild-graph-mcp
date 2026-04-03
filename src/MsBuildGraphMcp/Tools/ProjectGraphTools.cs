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
public static class ProjectGraphTools
{
    [McpServerTool(Name = "get_project_graph", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description(
        "Builds the MSBuild project dependency graph from a .sln or .csproj entry point. " +
        "Returns all projects with their dependency edges, target frameworks, build order " +
        "(topological sort), graph depth, and construction metrics. " +
        "Multi-targeting projects are deduplicated — outer/inner builds grouped by path. " +
        "Detects circular dependencies. " +
        "Example: get_project_graph(\"C:\\src\\MyApp.sln\", configuration: \"Release\")")]
    public static async Task<string> GetProjectGraph(
        [Description("Absolute path to .sln, .slnx, .slnf, or .csproj file")] string entryPath,
        [Description("Build configuration (e.g. Debug, Release). Optional.")] string? configuration = null,
        [Description("Build platform (e.g. AnyCPU, x64). Optional.")] string? platform = null,
        [Description("Max projects to return (default 200). Larger solutions are truncated by dependency count.")] int maxProjects = 200,
        CancellationToken cancellationToken = default)
    {
        entryPath = ValidateEntryPath(entryPath);
        var baseDir = Path.GetDirectoryName(entryPath)!;

        var globalProps = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(configuration))
            globalProps["Configuration"] = configuration;
        if (!string.IsNullOrEmpty(platform))
            globalProps["Platform"] = platform;

        var result = new GraphAnalysis();

        // EvaluationContext.Shared is the single most impactful optimization (2-5x speedup).
        // ProjectGraph does NOT use it automatically (GitHub issue #7002).
        var evalContext = EvaluationContext.Create(EvaluationContext.SharingPolicy.Shared);

        using var collection = new ProjectCollection();
        collection.IsBuildEnabled = false;

        ProjectGraph graph;
        try
        {
            // .slnx needs pre-parsing — MSBuild's SolutionFile parser doesn't support it on .NET 8
            var entryPoints = await ResolveEntryPointsAsync(entryPath, globalProps, cancellationToken);

            // Apply configurable timeout (default 120s, env: MSBUILD_MCP_TIMEOUT_SECONDS)
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(GraphTimeout);
            cancellationToken = timeoutCts.Token;

            graph = new ProjectGraph(
                entryPoints,
                collection,
                (string path, Dictionary<string, string> globals, ProjectCollection pc) =>
                {
                    var options = new ProjectOptions
                    {
                        GlobalProperties = globals,
                        ProjectCollection = pc,
                        EvaluationContext = evalContext,
                        LoadSettings = ProjectLoadSettings.IgnoreMissingImports
                                     | ProjectLoadSettings.IgnoreInvalidImports
                    };
                    return ProjectInstance.FromFile(path, options);
                },
                degreeOfParallelism: Math.Min(Environment.ProcessorCount, 8),
                cancellationToken: cancellationToken);
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
            {
                if (inner is InvalidProjectFileException ipfe)
                    result.Errors.Add($"{Path.GetFileName(ipfe.ProjectFile)}: {ipfe.BaseMessage}");
                else
                    result.Errors.Add(inner.Message);
            }
            return JsonSerializer.Serialize(result, JsonOptions);
        }

        // Deduplicate multi-targeting nodes (outer + inner builds share FullPath)
        var grouped = graph.ProjectNodes
            .GroupBy(n => n.ProjectInstance.FullPath, StringComparer.OrdinalIgnoreCase);

        foreach (var group in grouped)
        {
            var nodes = group.ToList();
            var first = nodes[0];

            var tfms = nodes
                .Select(n => n.ProjectInstance.GetPropertyValue("TargetFramework"))
                .Where(tf => !string.IsNullOrEmpty(tf))
                .Distinct()
                .ToList();

            var refs = nodes
                .SelectMany(n => n.ProjectReferences)
                .Select(r => RelativeTo(r.ProjectInstance.FullPath, baseDir))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var referencedBy = nodes
                .SelectMany(n => n.ReferencingProjects)
                .Select(r => RelativeTo(r.ProjectInstance.FullPath, baseDir))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            result.Projects.Add(new GraphProjectNode
            {
                Path = RelativeTo(group.Key, baseDir),
                Name = Path.GetFileNameWithoutExtension(group.Key),
                TargetFrameworks = tfms,
                IsMultiTargeting = nodes.Count > 1,
                OutputType = first.ProjectInstance.GetPropertyValue("OutputType") is { Length: > 0 } ot ? ot : null,
                IsRoot = nodes.Any(n => !n.ReferencingProjects.Any()),
                IsLeaf = nodes.Any(n => !n.ProjectReferences.Any()),
                References = refs,
                ReferencedBy = referencedBy
            });
        }

        // Compute max depth via DP on topological order
        var depths = new Dictionary<ProjectGraphNode, int>();
        foreach (var node in graph.ProjectNodesTopologicallySorted)
        {
            int depth = !node.ProjectReferences.Any()
                ? 0
                : node.ProjectReferences.Max(r => depths.GetValueOrDefault(r, 0)) + 1;
            depths[node] = depth;
        }

        // Truncate if too many projects (for LLM context window)
        if (result.Projects.Count > maxProjects)
        {
            result.Truncated = true;
            result.Projects = result.Projects
                .OrderByDescending(p => p.References.Count + p.ReferencedBy.Count)
                .Take(maxProjects)
                .ToList();
        }

        result.Metrics = new GraphMetrics
        {
            NodeCount = graph.ConstructionMetrics.NodeCount,
            EdgeCount = graph.ConstructionMetrics.EdgeCount,
            UniqueProjectCount = result.Projects.Count,
            MaxDepth = depths.Values.DefaultIfEmpty(0).Max(),
            ConstructionTimeMs = graph.ConstructionMetrics.ConstructionTime.TotalMilliseconds
        };

        result.TopologicalOrder = graph.ProjectNodesTopologicallySorted
            .Select(n => n.ProjectInstance.FullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => RelativeTo(p, baseDir))
            .ToList();

        return JsonSerializer.Serialize(result, JsonOptions);
    }
}
