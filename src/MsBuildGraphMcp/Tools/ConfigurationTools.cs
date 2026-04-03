using System.ComponentModel;
using System.Text.Json;
using Microsoft.Build.Evaluation;
using ModelContextProtocol.Server;
using MsBuildGraphMcp.Models;
using static MsBuildGraphMcp.Tools.Helpers;

namespace MsBuildGraphMcp.Tools;

[McpServerToolType]
public static class ConfigurationTools
{
    private static readonly string[] ComparedProperties =
    [
        "DefineConstants", "Optimize", "DebugType", "DebugSymbols",
        "OutputPath", "IntermediateOutputPath", "TreatWarningsAsErrors",
        "GenerateDocumentationFile", "DocumentationFile",
        "NoWarn", "WarningsAsErrors", "WarningLevel",
        "PlatformTarget", "Prefer32Bit", "AllowUnsafeBlocks",
        "CheckForOverflowUnderflow", "Deterministic"
    ];

    [McpServerTool(Name = "compare_configurations", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description(
        "Diffs two build configurations (e.g. Debug vs Release) for a project. " +
        "Shows property differences, conditional PackageReference items, and " +
        "conditional ProjectReference items between the two configurations. " +
        "Example: compare_configurations(\"C:\\src\\MyApp\\MyApp.csproj\") " +
        "Example: compare_configurations(\"...\", config1: \"Debug\", config2: \"Release\", platform: \"x64\")")]
    public static string CompareConfigurations(
        [Description("Absolute path to .csproj, .vbproj, or .vcxproj file")] string projectPath,
        [Description("First configuration name (default: Debug)")] string config1 = "Debug",
        [Description("Second configuration name (default: Release)")] string config2 = "Release",
        [Description("Platform (default: AnyCPU)")] string platform = "AnyCPU",
        CancellationToken cancellationToken = default)
    {
        projectPath = ValidateProjectPath(projectPath);

        var result = new ConfigurationDiff
        {
            ProjectPath = projectPath,
            Config1 = config1,
            Config2 = config2,
            Platform = platform
        };

        // Two separate ProjectCollection instances — one per configuration.
        // Same collection can't hold two evaluations of the same project with different globals.
        using var pc1 = new ProjectCollection(
            new Dictionary<string, string>
            {
                ["Configuration"] = config1,
                ["Platform"] = platform
            });
        pc1.IsBuildEnabled = false;

        using var pc2 = new ProjectCollection(
            new Dictionary<string, string>
            {
                ["Configuration"] = config2,
                ["Platform"] = platform
            });
        pc2.IsBuildEnabled = false;

        Project? proj1 = null, proj2 = null;
        try
        {
            proj1 = new Project(projectPath, null, null, pc1,
                ProjectLoadSettings.IgnoreMissingImports | ProjectLoadSettings.IgnoreInvalidImports);
            proj2 = new Project(projectPath, null, null, pc2,
                ProjectLoadSettings.IgnoreMissingImports | ProjectLoadSettings.IgnoreInvalidImports);

            // Compare properties
            foreach (string propName in ComparedProperties)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string val1 = proj1.GetPropertyValue(propName);
                string val2 = proj2.GetPropertyValue(propName);

                if (!string.Equals(val1, val2, StringComparison.Ordinal))
                {
                    result.PropertyDifferences.Add(new PropertyDifference
                    {
                        Name = propName,
                        Value1 = string.IsNullOrEmpty(val1) ? null : val1,
                        Value2 = string.IsNullOrEmpty(val2) ? null : val2
                    });
                }
            }

            // Compare PackageReference items
            CompareItems(proj1, proj2, "PackageReference", config1, config2, result.PackageReferenceDifferences);

            // Compare ProjectReference items
            CompareItems(proj1, proj2, "ProjectReference", config1, config2, result.ProjectReferenceDifferences);
        }
        finally
        {
            if (proj1 != null) pc1.UnloadProject(proj1);
            if (proj2 != null) pc2.UnloadProject(proj2);
        }

        return JsonSerializer.Serialize(result, JsonOptions);
    }

    private static void CompareItems(
        Project proj1, Project proj2,
        string itemType,
        string config1, string config2,
        List<ItemDifference> differences)
    {
        // GroupBy + First to handle duplicate items (possible with conditional includes)
        var items1 = proj1.GetItems(itemType)
            .GroupBy(i => i.EvaluatedInclude, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().GetMetadataValue("Version"), StringComparer.OrdinalIgnoreCase);
        var items2 = proj2.GetItems(itemType)
            .GroupBy(i => i.EvaluatedInclude, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().GetMetadataValue("Version"), StringComparer.OrdinalIgnoreCase);

        // In config1 but not config2
        foreach (var (name, ver) in items1)
        {
            if (!items2.ContainsKey(name))
            {
                differences.Add(new ItemDifference
                {
                    ItemName = name,
                    DiffType = $"Only in {config1}",
                    Value1 = string.IsNullOrEmpty(ver) ? null : ver
                });
            }
        }

        // In config2 but not config1
        foreach (var (name, ver) in items2)
        {
            if (!items1.ContainsKey(name))
            {
                differences.Add(new ItemDifference
                {
                    ItemName = name,
                    DiffType = $"Only in {config2}",
                    Value2 = string.IsNullOrEmpty(ver) ? null : ver
                });
            }
        }

        // Version differences
        foreach (var (name, ver1) in items1)
        {
            if (items2.TryGetValue(name, out var ver2)
                && !string.Equals(ver1, ver2, StringComparison.OrdinalIgnoreCase)
                && (!string.IsNullOrEmpty(ver1) || !string.IsNullOrEmpty(ver2)))
            {
                differences.Add(new ItemDifference
                {
                    ItemName = name,
                    DiffType = "VersionDifference",
                    Value1 = string.IsNullOrEmpty(ver1) ? null : ver1,
                    Value2 = string.IsNullOrEmpty(ver2) ? null : ver2
                });
            }
        }
    }
}
