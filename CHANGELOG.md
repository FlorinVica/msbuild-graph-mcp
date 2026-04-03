# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/).

## [1.1.0] - 2026-04-03

### Added
- 4 new MCP tools (10 total):
  - `analyze_impact` — "what breaks if I remove project X?" with direct + transitive dependents
  - `check_package_versions` — NuGet package version consistency across projects, CPM/VersionOverride detection
  - `get_build_order` — lightweight topological sort output with critical path length
  - `list_projects` — fast project listing without MSBuild evaluation
- Output truncation: `maxProjects` parameter on `get_project_graph` (default 200, sorted by dependency count)
- 2 MCP Prompts (guided workflows):
  - `project-health-check` — runs all tools in sequence, produces health report with score
  - `migration-readiness` — assesses .NET version migration readiness with ordered plan
- Symlink/junction detection — rejects symbolic links and junctions for security
- Input length validation — max 500 chars for all path parameters

### Security
- **Pre-evaluation XML scanner** — detects dangerous property functions (`System.IO.File`, `System.Net`, `System.Diagnostics`, etc.) BEFORE MSBuild evaluation executes them. Warnings logged to stderr.
- **Allowed directories** — `MSBUILD_MCP_ALLOWED_PATHS` env var restricts evaluation to specific directories (semicolon-separated). When unset, all paths are allowed.
- **Graph construction timeout** — configurable via `MSBUILD_MCP_TIMEOUT_SECONDS` (default 120s). Prevents hangs on corrupt solutions.
- **Error message sanitization** — `SecurityScanner.SanitizeErrorMessage()` strips user profile paths and stack traces from responses.
- Symlink and junction detection via `FileInfo.LinkTarget` + `FileAttributes.ReparsePoint`
- Input length cap (500 chars) prevents potential DoS via extremely long paths
- CodeTaskFactory and Exec task detection in pre-scan (informational warnings)

## [1.0.0] - 2026-04-03

### Added
- 6 MCP tools for pre-build MSBuild project analysis
  - `analyze_solution` — parse .sln/.slnx/.slnf with project details
  - `get_project_graph` — full dependency DAG with topological sort
  - `find_shared_imports` — Directory.Build.props/.targets discovery
  - `detect_build_issues` — TFM mismatches, orphans, circular deps, platform mismatches
  - `analyze_project_properties` — property source tracking with file/line info
  - `compare_configurations` — Debug vs Release configuration diff
- .slnx format support on .NET 8 via SolutionPersistence pre-parsing
- Multi-targeting project deduplication (outer/inner build nodes)
- NuGet.Frameworks-based TFM compatibility checking
- Central Package Management (CPM) detection
- Security hardening
  - `MSBUILDENABLEALLPROPERTYFUNCTIONS` startup guard
  - `IsBuildEnabled = false` on all ProjectCollection instances
  - UNC path rejection and extension whitelist
  - try/finally resource cleanup
- 269 automated tests across 28 test files
- CLAUDE.md for AI-assisted development
- Dockerfile for container deployment
- smithery.yaml for Smithery marketplace
- Client configurations for Claude Desktop, Claude Code, VS Code, Cursor
