namespace MsBuildGraphMcp.Models;

public sealed class SharedImportAnalysis
{
    public required string SolutionPath { get; init; }
    public int TotalImportsAnalyzed { get; set; }
    public List<SharedImport> SharedImports { get; set; } = [];
    public List<SpecialImport> SpecialFiles { get; set; } = [];
    public List<string> Errors { get; set; } = [];
}

public sealed class SharedImport
{
    public required string ImportPath { get; init; }
    public List<string> ImportedBy { get; set; } = [];
    public int ConsumerCount => ImportedBy.Count;
}

public sealed class SpecialImport
{
    public required string FilePath { get; init; }
    public required string FileType { get; init; }
    public List<string> AffectedProjects { get; set; } = [];
}
