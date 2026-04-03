# MSBuild Project Graph MCP Server

## Project Overview
MCP server exposing MSBuild's ProjectGraph API to LLMs. Pre-build static analysis of .NET/C++ solutions.

## Build
```bash
dotnet build src/MsBuildGraphMcp/MsBuildGraphMcp.csproj
```

## Test
```bash
dotnet test tests/MsBuildGraphMcp.Tests/MsBuildGraphMcp.Tests.csproj
```
269 tests, ~10 seconds. All must pass.

## Architecture
- **Program.cs**: MSBuildLocator registration (JIT trap pattern — separate method with [NoInlining])
- **Tools/**: 6 MCP tools, all ReadOnly/non-Destructive/Idempotent
- **Models/**: DTOs for JSON serialization (camelCase, null-omitting)
- **Helpers.cs**: Path validation, JSON options, .slnx entry point resolution

## Critical Constraints
1. **MSBuildLocator MUST register before ANY Microsoft.Build type is JIT-compiled** — the separate-method pattern is intentional
2. **ExcludeAssets="runtime"** on all Microsoft.Build.* packages — MSBuildLocator provides runtime assemblies
3. **Microsoft.Build 17.13.9** — do NOT upgrade to 17.14.8 (dependency publishing bug #11847)
4. **IsBuildEnabled = false** on all ProjectCollection instances — prevents target execution
5. **MSBUILDENABLEALLPROPERTYFUNCTIONS** blocked at startup — prevents arbitrary code execution via property functions
6. **Fresh ProjectCollection per tool call** with try/finally cleanup — prevents memory leaks
7. **EvaluationContext.Shared** wired through ProjectInstanceFactoryFunc — 2-5x speedup for large solutions
8. **degreeOfParallelism capped at 8** — prevents memory exhaustion on high-core machines

## Security Notes
- Property functions execute during evaluation (MSBuild design limitation)
- Only evaluate TRUSTED project files
- UNC paths rejected, extension whitelist enforced
- Error messages may contain file paths — acceptable for local tool

## .slnx Support
.slnx files are pre-parsed with Microsoft.VisualStudio.SolutionPersistence and individual project paths passed as entry points to ProjectGraph (MSBuild's SolutionFile.Parse doesn't support .slnx on .NET 8).
