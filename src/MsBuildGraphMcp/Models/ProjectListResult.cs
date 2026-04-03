namespace MsBuildGraphMcp.Models;

public sealed class ProjectListResult
{
    public required string SolutionPath { get; init; }
    public string Format { get; set; } = "sln";
    public List<ProjectEntry> Projects { get; set; } = [];
    public int Count => Projects.Count;
    public List<string> Errors { get; set; } = [];
}

public sealed class ProjectEntry
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required string Extension { get; init; }
    public bool ExistsOnDisk { get; set; }
}
