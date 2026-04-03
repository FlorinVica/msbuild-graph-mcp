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
public static class ImpactTools
{
    [McpServerTool(Name = "analyze_impact", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description(
        "Analyzes the impact of removing or modifying a specific project. " +
        "Returns all projects that directly or transitively depend on the target project — " +
        "answering 'what breaks if I change this project?' " +
        "Example: analyze_impact(\"C:\\src\\MyApp.sln\", targetProject: \"CoreLib\")")]
    public static async Task<string> AnalyzeImpact(
        [Description("Absolute path to .sln, .slnx, .slnf, or .csproj file")] string entryPath,
        [Description("Project name or filename to analyze impact for (e.g. 'CoreLib' or 'CoreLib.csproj')")] string targetProject,
        CancellationToken cancellationToken = default)
    {
        entryPath = ValidateEntryPath(entryPath);

        if (string.IsNullOrWhiteSpace(targetProject))
            throw new ArgumentException("Target project name cannot be empty.");

        var baseDir = Path.GetDirectoryName(entryPath)!;

        var result = new ImpactAnalysis
        {
            TargetProject = targetProject,
            EntryPath = entryPath
        };

        var evalContext = EvaluationContext.Create(EvaluationContext.SharingPolicy.Shared);
        using var collection = new ProjectCollection();
        collection.IsBuildEnabled = false;

        ProjectGraph graph;
        try
        {
            var entryPoints = await ResolveEntryPointsAsync(entryPath, new Dictionary<string, string>(), cancellationToken);
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
        catch (Exception ex) when (ex is InvalidProjectFileException or AggregateException
                                    || ex.GetType().Name == "CircularDependencyException")
        {
            result.Errors.Add(ex.Message);
            return JsonSerializer.Serialize(result, JsonOptions);
        }

        // Find the target node(s) by name match
        var targetNodes = graph.ProjectNodes
            .Where(n =>
            {
                var name = Path.GetFileNameWithoutExtension(n.ProjectInstance.FullPath);
                var fileName = Path.GetFileName(n.ProjectInstance.FullPath);
                return name.Equals(targetProject, StringComparison.OrdinalIgnoreCase)
                    || fileName.Equals(targetProject, StringComparison.OrdinalIgnoreCase);
            })
            .ToList();

        if (targetNodes.Count == 0)
        {
            result.Errors.Add($"Project '{targetProject}' not found in the graph. Available: " +
                string.Join(", ", graph.ProjectNodes
                    .Select(n => Path.GetFileNameWithoutExtension(n.ProjectInstance.FullPath))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(20)));
            return JsonSerializer.Serialize(result, JsonOptions);
        }

        // BFS to find all transitive dependents
        var directPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(ProjectGraphNode node, bool isDirect)>();

        foreach (var target in targetNodes)
        {
            visited.Add(target.ProjectInstance.FullPath);
            foreach (var parent in target.ReferencingProjects)
            {
                if (visited.Add(parent.ProjectInstance.FullPath))
                {
                    directPaths.Add(parent.ProjectInstance.FullPath);
                    queue.Enqueue((parent, true));
                }
            }
        }

        while (queue.Count > 0)
        {
            var (current, _) = queue.Dequeue();
            foreach (var parent in current.ReferencingProjects)
            {
                if (visited.Add(parent.ProjectInstance.FullPath))
                    queue.Enqueue((parent, false));
            }
        }

        // Separate direct vs transitive
        foreach (var path in visited)
        {
            if (targetNodes.Any(n => n.ProjectInstance.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase)))
                continue;

            var relPath = RelativeTo(path, baseDir);
            if (directPaths.Contains(path))
                result.DirectDependents.Add(relPath);
            else
                result.TransitiveDependents.Add(relPath);
        }

        result.Summary = result.TotalImpacted == 0
            ? $"No projects depend on '{targetProject}'. Safe to remove or modify."
            : $"Changing '{targetProject}' impacts {result.TotalImpacted} project(s): " +
              $"{result.DirectDependents.Count} direct, {result.TransitiveDependents.Count} transitive.";

        return JsonSerializer.Serialize(result, JsonOptions);
    }
}
