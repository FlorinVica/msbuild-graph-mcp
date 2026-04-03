namespace MsBuildGraphMcp.Models;

public sealed class PropertyAnalysis
{
    public required string ProjectPath { get; init; }
    public List<PropertyDetail> Properties { get; set; } = [];
    public List<ImportInfo> ImportChain { get; set; } = [];
}

public sealed class PropertyDetail
{
    public required string Name { get; init; }
    public string? EvaluatedValue { get; init; }
    public string? UnevaluatedValue { get; init; }
    public string? SourceFile { get; init; }
    public int? SourceLine { get; init; }
    public bool IsImported { get; init; }
    public bool IsGlobal { get; init; }
    public bool IsEnvironment { get; init; }
    public bool IsReserved { get; init; }
    public string? PreviousValue { get; init; }
}

public sealed class ImportInfo
{
    public required string ImportedFile { get; init; }
    public required string ImportedBy { get; init; }
    public bool IsSdkImport { get; init; }
}
