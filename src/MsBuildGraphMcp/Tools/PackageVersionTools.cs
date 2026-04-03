using System.ComponentModel;
using System.Text.Json;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Exceptions;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;
using ModelContextProtocol.Server;
using MsBuildGraphMcp.Models;
using static MsBuildGraphMcp.Tools.Helpers;

namespace MsBuildGraphMcp.Tools;

[McpServerToolType]
public static class PackageVersionTools
{
    [McpServerTool(Name = "check_package_versions", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description(
        "Checks NuGet package version consistency across all projects in a solution. " +
        "Finds packages referenced with different versions in different projects. " +
        "Detects Central Package Management (CPM) and VersionOverride usage. " +
        "Example: check_package_versions(\"C:\\src\\MyApp.sln\")")]
    public static async Task<string> CheckPackageVersions(
        [Description("Absolute path to .sln, .slnx, or .slnf file")] string solutionPath,
        CancellationToken cancellationToken = default)
    {
        solutionPath = ValidateSolutionPath(solutionPath);
        var solutionDir = Path.GetDirectoryName(solutionPath)!;

        var result = new PackageVersionAnalysis { SolutionPath = solutionPath };

        var projectPaths = await GetProjectPathsAsync(solutionPath, cancellationToken);

        // packageId -> { version -> [projects] }
        var packageMap = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.OrdinalIgnoreCase);
        bool anyCpm = false;

        using var collection = new ProjectCollection();
        collection.IsBuildEnabled = false;

        foreach (var projectPath in projectPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(projectPath))
            {
                result.Errors.Add($"Not found: {projectPath}");
                continue;
            }

            Project? project = null;
            try
            {
                project = new Project(projectPath, null, null, collection,
                    ProjectLoadSettings.IgnoreMissingImports | ProjectLoadSettings.IgnoreInvalidImports);

                string relProject = RelativeTo(projectPath, solutionDir);

                // Check CPM
                if (project.GetPropertyValue("ManagePackageVersionsCentrally")
                        .Equals("true", StringComparison.OrdinalIgnoreCase))
                    anyCpm = true;

                // Collect PackageReferences
                foreach (var item in project.GetItems("PackageReference"))
                {
                    string packageId = item.EvaluatedInclude;
                    if (string.IsNullOrWhiteSpace(packageId)) continue;

                    string version = item.GetMetadataValue("Version");
                    string versionOverride = item.GetMetadataValue("VersionOverride");

                    // Track version override
                    if (!string.IsNullOrEmpty(versionOverride))
                    {
                        result.VersionOverrides.Add(new VersionOverrideInfo
                        {
                            Project = relProject,
                            PackageId = packageId,
                            OverrideVersion = versionOverride
                        });
                    }

                    string effectiveVersion = !string.IsNullOrEmpty(versionOverride) ? versionOverride
                        : !string.IsNullOrEmpty(version) ? version
                        : "(no version)";

                    if (!packageMap.TryGetValue(packageId, out var versionMap))
                    {
                        versionMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                        packageMap[packageId] = versionMap;
                    }

                    if (!versionMap.TryGetValue(effectiveVersion, out var projects))
                    {
                        projects = [];
                        versionMap[effectiveVersion] = projects;
                    }
                    projects.Add(relProject);
                }
            }
            catch (Exception ex) when (ex is InvalidProjectFileException or InvalidOperationException)
            {
                result.Errors.Add($"{Path.GetFileName(projectPath)}: {ex.Message}");
            }
            finally
            {
                if (project != null) collection.UnloadProject(project);
            }
        }

        result.CpmEnabled = anyCpm;
        result.TotalPackages = packageMap.Count;

        // Find inconsistencies (packages with 2+ different versions)
        result.Inconsistencies = packageMap
            .Where(kvp => kvp.Value.Count > 1)
            .OrderByDescending(kvp => kvp.Value.Count)
            .Select(kvp => new PackageInconsistency
            {
                PackageId = kvp.Key,
                VersionToProjects = kvp.Value
            })
            .ToList();

        return JsonSerializer.Serialize(result, JsonOptions);
    }

    private static async Task<List<string>> GetProjectPathsAsync(string solutionPath, CancellationToken ct)
    {
        string ext = Path.GetExtension(solutionPath).ToLowerInvariant();
        if (ext == ".slnx")
        {
            var solutionDir = Path.GetDirectoryName(solutionPath)!;
            var serializer = SolutionSerializers.SlnXml;
            var model = await serializer.OpenAsync(solutionPath, ct);
            return model.SolutionProjects
                .Select(p => Path.IsPathRooted(p.FilePath)
                    ? p.FilePath
                    : Path.GetFullPath(Path.Combine(solutionDir, p.FilePath)))
                .ToList();
        }

        var sln = SolutionFile.Parse(solutionPath);
        return sln.ProjectsInOrder
            .Where(p => p.ProjectType == SolutionProjectType.KnownToBeMSBuildFormat)
            .Select(p => p.AbsolutePath)
            .ToList();
    }
}
