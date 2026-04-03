using System.ComponentModel;
using ModelContextProtocol.Server;

namespace MsBuildGraphMcp.Prompts;

[McpServerPromptType]
public static class HealthCheckPrompt
{
    [McpServerPrompt(Name = "project-health-check")]
    [Description("Guided workflow: comprehensive solution health analysis. Runs all analysis tools in sequence and summarizes findings.")]
    public static string GetPrompt(
        [Description("Absolute path to .sln, .slnx, or .slnf file")] string solutionPath)
    {
        return $"""
            Please analyze the health of the solution at: {solutionPath}

            Run these tools in sequence and summarize the findings:

            1. **list_projects** — Quick overview of all projects in the solution
            2. **analyze_solution** — Detailed project info (TFMs, output types, SDKs)
            3. **get_project_graph** — Dependency graph, build order, depth metrics
            4. **detect_build_issues** — TFM mismatches, orphan projects, circular deps
            5. **find_shared_imports** — Directory.Build.props/.targets and shared files
            6. **check_package_versions** — NuGet version consistency across projects

            After running all tools, provide a structured health report with:
            - **Overview**: project count, graph depth, build complexity
            - **Issues found**: list each issue with severity (critical/warning/info)
            - **Recommendations**: actionable steps to improve solution health
            - **Score**: rate the solution health from 1-10
            """;
    }
}
