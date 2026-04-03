# MSBuild Project Graph MCP Server

[![NuGet](https://img.shields.io/nuget/v/MsBuildGraphMcp.svg)](https://www.nuget.org/packages/MsBuildGraphMcp)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8.0%2B-purple.svg)](https://dotnet.microsoft.com/)
[![Tests](https://img.shields.io/badge/tests-269%20passing-brightgreen.svg)](#testing)

> **Pre-build static analysis of MSBuild solutions for LLM-powered coding assistants.**
> Analyze project dependencies, detect build issues, inspect shared imports, and compare configurations — all through natural language, before building.

No existing MCP server provides pre-build project graph analysis. This fills that gap.

## What It Does

Ask your AI assistant natural questions about your .NET/C++ solution:

- *"Show me the dependency graph for this solution"* → Full DAG with topological sort
- *"Are there any TFM mismatches?"* → Finds net6.0 projects referencing net8.0 libraries
- *"What does Directory.Build.props affect?"* → Shows all projects importing each shared file
- *"Compare Debug vs Release"* → Property and package reference differences
- *"Where does LangVersion come from?"* → Traces to Directory.Build.props line 3

## Tools

| Tool | Description |
|------|-------------|
| `analyze_solution` | Parse .sln/.slnx/.slnf — projects, TFMs, output types, configurations |
| `get_project_graph` | Build the full dependency DAG with topological sort and graph metrics |
| `find_shared_imports` | Discover Directory.Build.props/.targets and other shared imports |
| `detect_build_issues` | Find circular deps, TFM mismatches, orphan projects, platform mismatches |
| `analyze_project_properties` | Inspect evaluated properties with source tracking (which file, which line) |
| `compare_configurations` | Diff Debug vs Release (or any two configs) — properties, packages, references |
| `analyze_impact` | "What breaks if I remove project X?" — direct + transitive dependents |
| `check_package_versions` | NuGet package version consistency, CPM detection, VersionOverride tracking |
| `get_build_order` | Lightweight build order (topological sort) with critical path length |
| `list_projects` | Fast project listing without MSBuild evaluation — instant response |

## Quick Start

### Install

```bash
dotnet tool install -g MsBuildGraphMcp
```

### Configure

**Claude Desktop** — add to `%APPDATA%\Claude\claude_desktop_config.json`:
```json
{
  "mcpServers": {
    "msbuild-graph": {
      "command": "msbuild-graph-mcp"
    }
  }
}
```

**Claude Code:**
```bash
claude mcp add msbuild-graph -- msbuild-graph-mcp
```

**VS Code** — add to `.vscode/mcp.json`:
```json
{
  "servers": {
    "msbuild-graph": {
      "type": "stdio",
      "command": "msbuild-graph-mcp"
    }
  }
}
```

**Cursor** — add to `.cursor/mcp.json`:
```json
{
  "mcpServers": {
    "msbuild-graph": {
      "command": "msbuild-graph-mcp"
    }
  }
}
```

## Requirements

- .NET SDK 8.0+ (or Visual Studio 2022+)
- Windows (MSBuildLocator discovers VS/.NET SDK installations)

## Example Output

### `get_project_graph`

```json
{
  "metrics": {
    "nodeCount": 4,
    "edgeCount": 3,
    "uniqueProjectCount": 4,
    "maxDepth": 2,
    "constructionTimeMs": 297
  },
  "projects": [
    {
      "name": "WebApp",
      "targetFrameworks": ["net8.0"],
      "outputType": "Exe",
      "isRoot": true,
      "references": ["DataLib\\DataLib.csproj", "CoreLib\\CoreLib.csproj"]
    }
  ],
  "topologicalOrder": [
    "CoreLib\\CoreLib.csproj",
    "DataLib\\DataLib.csproj",
    "WebApp\\WebApp.csproj"
  ]
}
```

### `detect_build_issues`

```json
{
  "totalIssues": 2,
  "frameworkMismatches": [{
    "project": "OldApp.csproj",
    "projectTfm": "net6.0",
    "dependency": "NewLib.csproj",
    "dependencyTfm": "net8.0",
    "reason": "net6.0 cannot consume net8.0"
  }],
  "orphanProjects": [{
    "name": "UnusedLib",
    "reason": "Library with no referencing projects"
  }]
}
```

## Features

- **Pre-build analysis** — no build required, evaluates MSBuild project files directly
- **.sln / .slnx / .slnf** — supports all solution formats including the new .slnx (VS 17.10+)
- **Multi-targeting aware** — correctly handles TargetFrameworks (plural) with outer/inner build deduplication
- **TFM compatibility** — NuGet.Frameworks-based checking (net8.0-windows vs net8.0, netstandard2.0 vs net48, etc.)
- **Property source tracking** — shows which file and line number defined each MSBuild property
- **Configuration diffing** — compares any two configurations (Debug/Release, custom configs)
- **Circular dependency detection** — catches both MSB4251 and CircularDependencyException
- **Shared import discovery** — identifies Directory.Build.props, Directory.Build.targets, Directory.Packages.props
- **CPM detection** — recognizes Central Package Management (ManagePackageVersionsCentrally)

## Security

All tools are **read-only** — no builds are triggered, no files are modified.

| Measure | Description |
|---------|-------------|
| `IsBuildEnabled = false` | Prevents MSBuild target execution during evaluation |
| `MSBUILDENABLEALLPROPERTYFUNCTIONS` guard | Server refuses to start if this dangerous env var is set |
| Path validation | UNC paths rejected, extension whitelist, existence check |
| Fresh ProjectCollection per call | No state leakage between tool invocations |
| try/finally cleanup | Projects always unloaded, even on exception |

> **Important:** MSBuild property functions execute during evaluation (by design). Functions like `$([System.IO.File]::ReadAllText(...))` run when a project is loaded. Only analyze projects you trust.

## Architecture

```
┌──────────────────┐     stdio (JSON-RPC)    ┌─────────────────────┐
│  Claude / Copilot │◄──────────────────────►│ msbuild-graph-mcp   │
│  VS Code / Cursor │                        │                     │
└──────────────────┘                         │ MSBuildLocator      │
                                             │ ► ProjectGraph      │
                                             │ ► ProjectCollection │
                                             │ ► SolutionPersist   │
                                             └─────────────────────┘
```

Key architectural decisions:
- **MSBuildLocator JIT trap pattern** — RegisterInstance() in Main(), MSBuild types only in [NoInlining] method
- **EvaluationContext.Shared** via ProjectInstanceFactoryFunc — 2-5x speedup for large solutions
- **Parallelism capped at 8** — prevents memory exhaustion on high-core machines
- **Microsoft.Build 17.13.9** with ExcludeAssets="runtime" — avoids 17.14.8 dependency publishing bug

## Testing

269 tests across 28 test files, sourced from 5 rounds of internet research covering 20+ sources:

```bash
dotnet test tests/MsBuildGraphMcp.Tests/
# Passed! - Failed: 0, Passed: 269, Skipped: 0, Total: 269, Duration: 10s
```

Test categories: validation, core tools, TFM compatibility, security (OWASP/CVE), solution formats, production resilience, concurrency, path edge cases, JSON serialization, real-world scenarios.

8 production bugs found and fixed during testing.

## Complementary Tools

This server provides **pre-build** analysis. For post-build analysis, see:
- [baronfel/mcp-binlog-tool](https://github.com/baronfel/mcp-binlog-tool) — MSBuild binary log analysis (build times, errors, targets)

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines.

## License

[MIT](LICENSE) - FlorinVica
