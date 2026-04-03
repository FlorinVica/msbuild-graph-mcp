FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/MsBuildGraphMcp/*.csproj ./
RUN dotnet restore
COPY src/MsBuildGraphMcp/ .
RUN dotnet publish -c Release -o /app

# SDK image required — MSBuildLocator needs an SDK installation to resolve MSBuild assemblies
FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS runtime
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "MsBuildGraphMcp.dll"]
