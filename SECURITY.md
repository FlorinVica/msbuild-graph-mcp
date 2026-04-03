# Security Policy

## Supported Versions

| Version | Supported |
|---------|-----------|
| 1.0.x   | Yes       |

## Reporting a Vulnerability

Please report security vulnerabilities by opening a GitHub issue with the label `security`.

For critical vulnerabilities (arbitrary code execution, data exfiltration), please email directly rather than opening a public issue.

## Security Model

### What This Server Does

This MCP server performs **read-only MSBuild project evaluation**. It does NOT:
- Execute builds or targets
- Modify any files
- Make network requests
- Execute arbitrary commands

### Known Limitations

#### MSBuild Property Functions Execute During Evaluation

This is an inherent MSBuild design limitation, not a bug in this server. During project evaluation (`new Project()` / `ProjectInstance.FromFile()`), MSBuild property functions execute:

```xml
<!-- These ALL execute during evaluation, not during build -->
<PropertyGroup>
  <Data>$([System.IO.File]::ReadAllText('secret.txt'))</Data>
  <Env>$([System.Environment]::GetEnvironmentVariable('API_KEY'))</Env>
</PropertyGroup>
```

**Mitigation:** Only analyze project files you trust. This is the same trust model as opening a project in Visual Studio.

### Security Measures Implemented

| Measure | Purpose |
|---------|---------|
| `MSBUILDENABLEALLPROPERTYFUNCTIONS` startup guard | Blocks full RCE via property functions (CVE-2025-21172) |
| `IsBuildEnabled = false` | Prevents MSBuild target execution |
| UNC path rejection | Blocks `\\server\share` paths |
| Extension whitelist | Only .sln/.slnx/.slnf/.csproj/.vbproj/.fsproj/.vcxproj |
| File existence validation | Rejects non-existent paths |
| Fresh ProjectCollection per call | No state leakage between invocations |
| Parallelism cap (8) | Prevents memory exhaustion |
| try/finally resource cleanup | Projects always unloaded |

### OWASP MCP Security Alignment

Per the [OWASP MCP Security Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/MCP_Security_Cheat_Sheet.html):

- **Tool schema integrity**: All parameters have strict types and descriptions
- **Input validation**: All paths validated before evaluation
- **Error sanitization**: No raw stack traces in tool responses
- **Principle of least privilege**: Read-only evaluation, no builds
