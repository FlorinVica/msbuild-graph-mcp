# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/).

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
