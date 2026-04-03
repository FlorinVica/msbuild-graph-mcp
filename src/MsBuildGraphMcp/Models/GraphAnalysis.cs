namespace MsBuildGraphMcp.Models;

public sealed class GraphAnalysis
{
    public GraphMetrics Metrics { get; set; } = new();
    public List<GraphProjectNode> Projects { get; set; } = [];
    public List<string> TopologicalOrder { get; set; } = [];
    public string? CircularDependencyError { get; set; }
    public List<string> Errors { get; set; } = [];
}

public sealed class GraphMetrics
{
    public int NodeCount { get; set; }
    public int EdgeCount { get; set; }
    public int UniqueProjectCount { get; set; }
    public int MaxDepth { get; set; }
    public double ConstructionTimeMs { get; set; }
}

public sealed class GraphProjectNode
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public List<string> TargetFrameworks { get; set; } = [];
    public bool IsMultiTargeting { get; set; }
    public string? OutputType { get; init; }
    public bool IsRoot { get; set; }
    public bool IsLeaf { get; set; }
    public List<string> References { get; set; } = [];
    public List<string> ReferencedBy { get; set; } = [];
}
