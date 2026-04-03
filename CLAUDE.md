# MSBuild Project Graph MCP Server

## Project Overview
MCP server exposing MSBuild's ProjectGraph API to LLMs. Pre-build static analysis of .NET/C++ solutions.
10 tools + 2 prompts. Published on NuGet as `MsBuildGraphMcp`.

## Build
```bash
dotnet build src/MsBuildGraphMcp/MsBuildGraphMcp.csproj
```

## Test
```bash
dotnet test tests/MsBuildGraphMcp.Tests/MsBuildGraphMcp.Tests.csproj
```
333 tests, ~12 seconds. All must pass before any commit.

## Architecture
- **Program.cs**: MSBuildLocator registration (JIT trap pattern — separate method with [NoInlining])
- **Tools/**: 10 MCP tools, all ReadOnly/non-Destructive/Idempotent
- **Prompts/**: 2 MCP prompts (project-health-check, migration-readiness)
- **Models/**: DTOs for JSON serialization (camelCase, null-omitting)
- **Tools/Helpers.cs**: Path validation, JSON options, .slnx entry point resolution, timeout config
- **Tools/SecurityScanner.cs**: Pre-evaluation XML scan, allowed paths, error sanitization

## Tools (10)
1. `analyze_solution` — parse .sln/.slnx/.slnf
2. `get_project_graph` — dependency DAG with topological sort
3. `find_shared_imports` — Directory.Build.props/.targets discovery
4. `detect_build_issues` — TFM mismatches, orphans, circular deps
5. `analyze_project_properties` — property source tracking
6. `compare_configurations` — Debug vs Release diff
7. `analyze_impact` — what breaks if I remove project X?
8. `check_package_versions` — NuGet version consistency, CPM
9. `get_build_order` — lightweight topological sort
10. `list_projects` — fast listing, no MSBuild evaluation

## Critical Constraints
1. **MSBuildLocator MUST register before ANY Microsoft.Build type is JIT-compiled** — separate-method pattern with [MethodImpl(NoInlining)] is intentional, do NOT refactor into Main()
2. **ExcludeAssets="runtime"** on all Microsoft.Build.* AND NuGet.Frameworks packages — MSBuildLocator provides runtime assemblies
3. **Microsoft.Build 17.13.9** — do NOT upgrade to 17.14.8 (dependency publishing bug, GitHub #11847)
4. **IsBuildEnabled = false** on all ProjectCollection instances — prevents target execution
5. **MSBUILDENABLEALLPROPERTYFUNCTIONS** blocked at startup in Program.cs — prevents RCE via property functions
6. **Fresh ProjectCollection per tool call** with try/finally cleanup — prevents memory leaks
7. **EvaluationContext.Shared** wired through ProjectInstanceFactoryFunc — 2-5x speedup
8. **degreeOfParallelism capped at 8** — prevents memory exhaustion on high-core machines
9. **Symlink/junction detection** via FileAttributes.ReparsePoint + LinkTarget on all validators
10. **Input length cap** (500 chars) on all path parameters

## Security Layers (15 total)
- Startup: MSBUILDENABLEALLPROPERTYFUNCTIONS block
- Pre-eval: SecurityScanner.ScanProjectFile() detects dangerous property functions, CodeTaskFactory, Exec tasks
- Path validation: UNC rejection, extension whitelist, symlink/junction detection, input length cap, allowed directories (MSBUILD_MCP_ALLOWED_PATHS env var)
- Evaluation: IsBuildEnabled=false, fresh ProjectCollection, try/finally, parallelism cap, timeout (MSBUILD_MCP_TIMEOUT_SECONDS)
- Output: Error message sanitization (SecurityScanner.SanitizeErrorMessage)

## .slnx Support
.slnx files are pre-parsed with Microsoft.VisualStudio.SolutionPersistence and individual project paths passed as entry points to ProjectGraph. MSBuild's SolutionFile.Parse doesn't support .slnx on .NET 8 SDK. This applies to: ProjectGraphTools, BuildIssueTools, BuildOrderTools, ImpactTools (via Helpers.ResolveEntryPointsAsync) and SharedImportTools, PackageVersionTools (via their own GetProjectPathsAsync).

## Adding a New Tool
1. Create file in `Tools/`, add `[McpServerToolType]` + `[McpServerTool(ReadOnly=true, Destructive=false)]`
2. Use `Helpers.ValidateEntryPath()` or `ValidateSolutionPath()` for input
3. Set `collection.IsBuildEnabled = false` on any ProjectCollection
4. Use try/finally for project unload
5. Return JSON via `JsonSerializer.Serialize(result, Helpers.JsonOptions)`
6. Add model in `Models/`
7. Add tests covering happy path + edge cases + error handling
