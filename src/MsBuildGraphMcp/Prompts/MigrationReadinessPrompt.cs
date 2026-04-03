using System.ComponentModel;
using ModelContextProtocol.Server;

namespace MsBuildGraphMcp.Prompts;

[McpServerPromptType]
public static class MigrationReadinessPrompt
{
    [McpServerPrompt(Name = "migration-readiness")]
    [Description("Guided workflow: assess readiness for .NET version migration. Analyzes TFM compatibility, shared imports, and potential blockers.")]
    public static string GetPrompt(
        [Description("Absolute path to .sln, .slnx, or .slnf file")] string solutionPath,
        [Description("Target .NET version to migrate to (e.g. net9.0, net10.0)")] string targetFramework = "net9.0")
    {
        return $"""
            Please assess the migration readiness of the solution at: {solutionPath}
            Target framework: {targetFramework}

            Run these tools and analyze migration readiness:

            1. **analyze_solution** — Current TFMs for all projects
            2. **get_project_graph** — Dependency structure (migrate leaves first)
            3. **detect_build_issues** — Existing issues to fix before migration
            4. **find_shared_imports** — Directory.Build.props that may need TFM updates
            5. **check_package_versions** — Packages that may need updates for {targetFramework}
            6. **get_build_order** — Correct migration order (dependencies before dependents)

            Provide a migration plan with:
            - **Current state**: TFM distribution across projects
            - **Migration order**: which projects to migrate first (leaves → roots)
            - **Blockers**: packages or references that don't support {targetFramework}
            - **Shared files to update**: Directory.Build.props, Directory.Packages.props
            - **Estimated effort**: per project (trivial/moderate/complex)
            - **Risk assessment**: low/medium/high with reasoning
            """;
    }
}
