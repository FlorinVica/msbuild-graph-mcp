using System.ComponentModel;
using System.Text.Json;
using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Evaluation.Context;
using Microsoft.Build.Exceptions;
using Microsoft.Build.Execution;
using Microsoft.Build.Graph;
using NuGet.Frameworks;
using ModelContextProtocol.Server;
using MsBuildGraphMcp.Models;
using static MsBuildGraphMcp.Tools.Helpers;

namespace MsBuildGraphMcp.Tools;

[McpServerToolType]
public static class BuildIssueTools
{
    [McpServerTool(Name = "detect_build_issues", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description(
        "Analyzes a solution or project for build issues: circular dependencies, " +
        "target framework mismatches between referencing projects, orphan projects " +
        "(libraries that nothing references), and platform mismatches. " +
        "Example: detect_build_issues(\"C:\\src\\MyApp.sln\")")]
    public static async Task<string> DetectBuildIssues(
        [Description("Absolute path to .sln or .csproj file")] string entryPath,
        [Description("Build configuration (e.g. Release). Optional.")] string? configuration = null,
        [Description("Build platform (e.g. AnyCPU). Optional.")] string? platform = null,
        CancellationToken cancellationToken = default)
    {
        entryPath = ValidateEntryPath(entryPath);
        var result = new BuildIssueAnalysis { EntryPath = entryPath };

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
                entryPoints,
                collection,
                (string path, Dictionary<string, string> globals, ProjectCollection pc) =>
                {
                    return ProjectInstance.FromFile(path, new ProjectOptions
                    {
                        GlobalProperties = globals,
                        ProjectCollection = pc,
                        EvaluationContext = evalContext,
                        LoadSettings = ProjectLoadSettings.IgnoreMissingImports
                                     | ProjectLoadSettings.IgnoreInvalidImports
                    });
                },
                Math.Min(Environment.ProcessorCount, 8),
                cancellationToken);
        }
        catch (InvalidProjectFileException ex) when (ex.ErrorCode == "MSB4251")
        {
            result.CircularDependencies.Add(ex.Message);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex) when (ex.GetType().Name == "CircularDependencyException")
        {
            result.CircularDependencies.Add(ex.Message);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (AggregateException ae)
        {
            foreach (var inner in ae.Flatten().InnerExceptions)
                result.Errors.Add(inner.Message);
            return JsonSerializer.Serialize(result, JsonOptions);
        }

        var compat = DefaultCompatibilityProvider.Instance;

        foreach (var node in graph.ProjectNodes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string consumerTfm = node.ProjectInstance.GetPropertyValue("TargetFramework");
            if (string.IsNullOrEmpty(consumerTfm))
                continue; // outer build node

            var consumerFw = NuGetFramework.Parse(consumerTfm);
            string consumerName = Path.GetFileName(node.ProjectInstance.FullPath);

            // TFM mismatches
            foreach (var dep in node.ProjectReferences)
            {
                string depTfm = dep.ProjectInstance.GetPropertyValue("TargetFramework");
                if (string.IsNullOrEmpty(depTfm))
                    continue;

                var depFw = NuGetFramework.Parse(depTfm);

                // IsCompatible(target=consumer, candidate=dependency)
                if (!compat.IsCompatible(consumerFw, depFw))
                {
                    result.FrameworkMismatches.Add(new FrameworkMismatch
                    {
                        Project = consumerName,
                        ProjectTfm = consumerTfm,
                        Dependency = Path.GetFileName(dep.ProjectInstance.FullPath),
                        DependencyTfm = depTfm,
                        Reason = $"{consumerTfm} cannot consume {depTfm}"
                    });
                }
            }

            // Platform mismatches
            string consumerPlatform = node.ProjectInstance.GetPropertyValue("PlatformTarget");
            if (string.IsNullOrEmpty(consumerPlatform))
                consumerPlatform = node.ProjectInstance.GetPropertyValue("Platform");

            foreach (var dep in node.ProjectReferences)
            {
                string depPlatform = dep.ProjectInstance.GetPropertyValue("PlatformTarget");
                if (string.IsNullOrEmpty(depPlatform))
                    depPlatform = dep.ProjectInstance.GetPropertyValue("Platform");

                if (!string.IsNullOrEmpty(consumerPlatform) && !string.IsNullOrEmpty(depPlatform)
                    && !consumerPlatform.Equals("AnyCPU", StringComparison.OrdinalIgnoreCase)
                    && !depPlatform.Equals("AnyCPU", StringComparison.OrdinalIgnoreCase)
                    && !consumerPlatform.Equals(depPlatform, StringComparison.OrdinalIgnoreCase))
                {
                    result.PlatformMismatches.Add(new PlatformMismatch
                    {
                        Project = consumerName,
                        ProjectPlatform = consumerPlatform,
                        Dependency = Path.GetFileName(dep.ProjectInstance.FullPath),
                        DependencyPlatform = depPlatform
                    });
                }
            }
        }

        // Orphan projects — libraries nobody references
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in graph.ProjectNodes)
        {
            string fullPath = node.ProjectInstance.FullPath;
            if (!seen.Add(fullPath))
                continue;

            if (node.ReferencingProjects.Any())
                continue;

            string outputType = node.ProjectInstance.GetPropertyValue("OutputType");
            if (outputType.Equals("Exe", StringComparison.OrdinalIgnoreCase) ||
                outputType.Equals("WinExe", StringComparison.OrdinalIgnoreCase))
                continue;

            if (node.ProjectInstance.GetPropertyValue("IsTestProject")
                    .Equals("true", StringComparison.OrdinalIgnoreCase))
                continue;

            // Skip outer build nodes of multi-targeting projects
            string tfm = node.ProjectInstance.GetPropertyValue("TargetFramework");
            if (string.IsNullOrEmpty(tfm))
                continue;

            result.OrphanProjects.Add(new OrphanProject
            {
                Path = fullPath,
                Name = Path.GetFileNameWithoutExtension(fullPath),
                OutputType = string.IsNullOrEmpty(outputType) ? null : outputType,
                Reason = "Library with no referencing projects (not Exe, not test, not packable)"
            });
        }

        return JsonSerializer.Serialize(result, JsonOptions);
    }
}
