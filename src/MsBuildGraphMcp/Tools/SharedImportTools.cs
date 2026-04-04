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
public static class SharedImportTools
{
    private static readonly HashSet<string> SpecialFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Directory.Build.props",
        "Directory.Build.targets",
        "Directory.Packages.props",
        "Packages.props"
    };

    [McpServerTool(Name = "find_shared_imports", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description(
        "Discovers .props and .targets files shared across multiple projects in a solution. " +
        "Identifies Directory.Build.props, Directory.Build.targets, and Directory.Packages.props. " +
        "Shows which projects import each shared file. " +
        "Example: find_shared_imports(\"C:\\src\\MyApp.sln\")")]
    public static async Task<string> FindSharedImports(
        [Description("Absolute path to .sln, .slnx, or .slnf file")] string solutionPath,
        CancellationToken cancellationToken = default)
    {
        solutionPath = ValidateSolutionPath(solutionPath);
        var solutionDir = Path.GetDirectoryName(solutionPath)!;

        var result = new SharedImportAnalysis { SolutionPath = solutionPath };

        // Collect project paths from solution (.slnx needs SolutionPersistence)
        var projectPaths = await GetProjectPathsAsync(solutionPath, cancellationToken);

        // Map: import file path -> set of projects that import it (HashSet avoids duplicates from multi-targeting)
        var importMap = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var specialFiles = new Dictionary<string, (string type, HashSet<string> projects)>(StringComparer.OrdinalIgnoreCase);

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

            try
            {
                var project = new Project(
                    projectPath, null, null, collection,
                    ProjectLoadSettings.IgnoreMissingImports | ProjectLoadSettings.IgnoreInvalidImports);

                string relProject = RelativeTo(projectPath, solutionDir);

                foreach (var import in project.Imports)
                {
                    string importedPath = import.ImportedProject.FullPath;
                    if (string.IsNullOrEmpty(importedPath))
                        continue;

                    if (!importMap.TryGetValue(importedPath, out var consumers))
                    {
                        consumers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        importMap[importedPath] = consumers;
                    }
                    consumers.Add(relProject);

                    // Track special build customization files
                    string fileName = Path.GetFileName(importedPath);
                    if (SpecialFileNames.Contains(fileName))
                    {
                        if (!specialFiles.ContainsKey(importedPath))
                        {
                            string fileType = fileName switch
                            {
                                _ when fileName.Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase) => "DirectoryBuildProps",
                                _ when fileName.Equals("Directory.Build.targets", StringComparison.OrdinalIgnoreCase) => "DirectoryBuildTargets",
                                _ when fileName.Contains("Packages", StringComparison.OrdinalIgnoreCase) => "CentralPackageManagement",
                                _ => "SharedProps"
                            };
                            specialFiles[importedPath] = (fileType, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                        }
                        specialFiles[importedPath].projects.Add(relProject);
                    }
                }

                result.TotalImportsAnalyzed++;
                collection.UnloadProject(project);
            }
            catch (Exception ex) when (ex is InvalidProjectFileException or InvalidOperationException)
            {
                result.Errors.Add($"{Path.GetFileName(projectPath)}: {ex.Message}");
            }
        }

        // Only report imports shared by 2+ projects
        result.SharedImports = importMap
            .Where(kvp => kvp.Value.Count > 1)
            .OrderByDescending(kvp => kvp.Value.Count)
            .Select(kvp => new SharedImport
            {
                ImportPath = RelativeTo(kvp.Key, solutionDir),
                ImportedBy = kvp.Value.ToList()
            })
            .ToList();

        result.SpecialFiles = specialFiles
            .Select(kvp => new SpecialImport
            {
                FilePath = RelativeTo(kvp.Key, solutionDir),
                FileType = kvp.Value.type,
                AffectedProjects = kvp.Value.projects.ToList()
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

        if (ext == ".slnf")
        {
            var slnfPaths = await TryGetSlnfProjectPathsAsync(solutionPath, ct);
            if (slnfPaths != null)
                return slnfPaths;
        }

        var sln = SolutionFile.Parse(solutionPath);
        return sln.ProjectsInOrder
            .Where(p => p.ProjectType == SolutionProjectType.KnownToBeMSBuildFormat)
            .Select(p => p.AbsolutePath)
            .ToList();
    }
}
