# SourceFlow.Net Technology Stack

## Build System
- **Solution**: Visual Studio solution (.sln) with MSBuild
- **Project Format**: SDK-style .csproj files
- **Package Management**: NuGet packages
- **Versioning**: GitVersion for semantic versioning

## Target Frameworks
- **.NET 10.0** - Latest framework support
- **.NET 9.0** - Current LTS support
- **.NET 8.0** - Previous LTS (Entity Framework projects)
- **.NET Standard 2.1** - Cross-platform compatibility
- **.NET Standard 2.0** - Broader compatibility
- **.NET Framework 4.6.2** - Legacy support

## Core Dependencies
- **System.Text.Json** - JSON serialization
- **Microsoft.Extensions.DependencyInjection** - Dependency injection
- **Microsoft.Extensions.Logging** - Logging abstractions
- **OpenTelemetry** - Distributed tracing and metrics
- **Entity Framework Core 9.0** - Data persistence (EF projects)
- **Polly** - Resilience and retry policies

## Cloud Dependencies
- **AWS SDK** - SQS, SNS, KMS integration
- **Azure SDK** - Service Bus, Key Vault integration

## Testing Framework
- **xUnit** - Unit testing framework
- **Moq** - Mocking framework (implied from test structure)

## Common Commands

### Build
```bash
# Build entire solution
dotnet build SourceFlow.Net.sln

# Build specific project
dotnet build src/SourceFlow/SourceFlow.csproj

# Build for specific framework
dotnet build -f net10.0
```

### Test
```bash
# Run all tests
dotnet test

# Run specific test project
dotnet test tests/SourceFlow.Core.Tests/

# Run with coverage
dotnet test --collect:"XPlat Code Coverage"
```

### Package
```bash
# Create NuGet packages
dotnet pack --configuration Release

# Pack specific project
dotnet pack src/SourceFlow/SourceFlow.csproj --configuration Release
```

### Restore
```bash
# Restore all dependencies
dotnet restore

# Clean and restore
dotnet clean && dotnet restore
```

## Development Tools
- **Visual Studio 2022** - Primary IDE
- **GitHub Actions** - CI/CD pipelines
- **CodeQL** - Security analysis
- **GitVersion** - Automatic versioning

## Code Quality
- **.NET Analyzers** - Static code analysis
- **EditorConfig** - Code formatting standards
- **JSCPD** - Copy-paste detection