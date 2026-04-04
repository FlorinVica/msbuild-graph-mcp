# Contributing

Thanks for your interest in contributing to MSBuild Project Graph MCP Server!

## Getting Started

```bash
git clone https://github.com/FlorinVica/msbuild-graph-mcp.git
cd msbuild-graph-mcp
dotnet build src/MsBuildGraphMcp/MsBuildGraphMcp.csproj
dotnet test tests/MsBuildGraphMcp.Tests/
```

### Requirements

- .NET SDK 8.0+ (9.0 or 10.0 also work)
- Visual Studio 2022+ or standalone .NET SDK
- Windows (MSBuildLocator requires VS/.NET SDK installation)

## Development Workflow

1. Create a feature branch
2. Make your changes
3. Run the full test suite: `dotnet test` (must pass all 333 tests)
4. Submit a pull request

## Adding a New Tool

1. Create a new file in `src/MsBuildGraphMcp/Tools/`
2. Add `[McpServerToolType]` class attribute
3. Add `[McpServerTool]` method attribute with `ReadOnly = true, Destructive = false`
4. Write a detailed `[Description]` — this is what the LLM reads to decide when to call your tool
5. Use `Helpers.ValidateEntryPath()` or `ValidateSolutionPath()` for input validation
6. Return JSON via `JsonSerializer.Serialize(result, Helpers.JsonOptions)`
7. Add corresponding model in `Models/`
8. Add tests covering happy path, edge cases, and error handling
9. Use `ProjectCollection` with `IsBuildEnabled = false` and `using` disposal

## Test Guidelines

- Every tool must have tests for: valid input, invalid input, edge cases, error handling
- Use `TempSolutionBuilder` fixture to create real .sln/.slnx/.csproj files
- Tests run against real MSBuild APIs (not mocked) — this catches real issues
- Target <15 seconds total test run time

## Architecture Constraints

See [CLAUDE.md](CLAUDE.md) for critical constraints (MSBuildLocator JIT trap, ExcludeAssets, etc.)

## Security

- Never set `MSBUILDENABLEALLPROPERTYFUNCTIONS`
- Always set `IsBuildEnabled = false` on ProjectCollection
- Validate all input paths (no UNC, extension whitelist)
- Use try/finally for ProjectCollection cleanup
- Don't expose raw exception stack traces in tool responses

## Code Style

- Follow existing patterns in the codebase
- Use `sealed` classes for models
- Use `required` properties where appropriate
- camelCase in JSON output (via `Helpers.JsonOptions`)
- snake_case for MCP tool names

## Reporting Issues

Please include:
- .NET SDK version (`dotnet --version`)
- Solution structure (project count, TFMs, multi-targeting)
- Full error message
- Steps to reproduce
