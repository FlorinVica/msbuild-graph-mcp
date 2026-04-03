using System.ComponentModel;
using System.Text.Json;
using Microsoft.Build.Construction;
using Microsoft.Build.Exceptions;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;
using ModelContextProtocol.Server;
using MsBuildGraphMcp.Models;
using static MsBuildGraphMcp.Tools.Helpers;

namespace MsBuildGraphMcp.Tools;

[McpServerToolType]
public static class ProjectListTools
{
    [McpServerTool(Name = "list_projects", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description(
        "Lists all projects in a solution file — fast, no MSBuild evaluation. " +
        "Returns project names, paths, file extensions, and whether each exists on disk. " +
        "Use this for a quick overview before running heavier analysis tools. " +
        "Example: list_projects(\"C:\\src\\MyApp.sln\")")]
    public static async Task<string> ListProjects(
        [Description("Absolute path to .sln, .slnx, or .slnf file")] string solutionPath,
        CancellationToken cancellationToken = default)
    {
        solutionPath = ValidateSolutionPath(solutionPath);
        var solutionDir = Path.GetDirectoryName(solutionPath)!;

        var result = new ProjectListResult
        {
            SolutionPath = solutionPath,
            Format = Path.GetExtension(solutionPath).ToLowerInvariant().TrimStart('.')
        };

        string ext = Path.GetExtension(solutionPath).ToLowerInvariant();

        if (ext == ".slnx")
        {
            var serializer = SolutionSerializers.SlnXml;
            var model = await serializer.OpenAsync(solutionPath, cancellationToken);
            foreach (var p in model.SolutionProjects)
            {
                var absPath = Path.IsPathRooted(p.FilePath)
                    ? p.FilePath
                    : Path.GetFullPath(Path.Combine(solutionDir, p.FilePath));

                result.Projects.Add(new ProjectEntry
                {
                    Name = p.DisplayName ?? Path.GetFileNameWithoutExtension(p.FilePath),
                    Path = absPath,
                    Extension = Path.GetExtension(absPath).ToLowerInvariant(),
                    ExistsOnDisk = File.Exists(absPath)
                });
            }
        }
        else
        {
            try
            {
                var sln = SolutionFile.Parse(solutionPath);
                foreach (var proj in sln.ProjectsInOrder)
                {
                    if (proj.ProjectType == SolutionProjectType.SolutionFolder)
                        continue;

                    result.Projects.Add(new ProjectEntry
                    {
                        Name = proj.ProjectName,
                        Path = proj.AbsolutePath,
                        Extension = Path.GetExtension(proj.AbsolutePath).ToLowerInvariant(),
                        ExistsOnDisk = File.Exists(proj.AbsolutePath)
                    });
                }
            }
            catch (Exception ex) when (ex is InvalidProjectFileException)
            {
                result.Errors.Add(ex.Message);
            }
        }

        return JsonSerializer.Serialize(result, JsonOptions);
    }
}
