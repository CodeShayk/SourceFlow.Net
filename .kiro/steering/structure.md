# SourceFlow.Net Project Structure

## Solution Organization

```
SourceFlow.Net/
├── src/                          # Source code projects
├── tests/                        # Test projects
├── docs/                         # Documentation
├── Images/                       # Diagrams and assets
├── .github/                      # GitHub workflows
└── .kiro/                        # Kiro configuration
```

## Source Projects (`src/`)

### Core Framework
- **`SourceFlow/`** - Main framework library
  - `Aggregate/` - Aggregate pattern implementation
  - `Messaging/` - Commands, events, and messaging infrastructure
  - `Projections/` - View model projections
  - `Saga/` - Saga pattern for long-running transactions
  - `Observability/` - OpenTelemetry integration
  - `Performance/` - Memory optimization utilities
  - `Cloud/` - Shared cloud functionality (Configuration, Resilience, Security, Observability)

### Persistence Layer
- **`SourceFlow.Stores.EntityFramework/`** - EF Core persistence
  - `Stores/` - Store implementations (Command, Entity, ViewModel)
  - `Models/` - Data models
  - `Extensions/` - Service registration extensions
  - `Options/` - Configuration options

### Cloud Extensions
- **`SourceFlow.Cloud.AWS/`** - AWS integration
  - `Messaging/` - SQS/SNS dispatchers
  - `Configuration/` - Routing configuration
  - `Security/` - KMS encryption

- **`SourceFlow.Cloud.Azure/`** - Azure integration
  - `Messaging/` - Service Bus dispatchers
  - `Security/` - Key Vault encryption

**Note**: Cloud core functionality (resilience, security, observability) is now integrated into the main `SourceFlow` project under the `Cloud/` namespace, eliminating the need for a separate `SourceFlow.Cloud.Core` package.

## Test Projects (`tests/`)

### Test Structure Pattern
Each source project has a corresponding test project:
- `SourceFlow.Core.Tests/` - Core framework tests
- `SourceFlow.Cloud.AWS.Tests/` - AWS extension tests
- `SourceFlow.Cloud.Azure.Tests/` - Azure extension tests
- `SourceFlow.Stores.EntityFramework.Tests/` - EF persistence tests

### Test Organization
```
TestProject/
├── Unit/                         # Unit tests
├── Integration/                  # Integration tests
├── E2E/                         # End-to-end scenarios
├── TestHelpers/                 # Test utilities
└── TestModels/                  # Test data models
```

## Documentation (`docs/`)

### Architecture Documentation
- `Architecture/` - Detailed architecture analysis
  - `01-Architecture-Overview.md`
  - `02-Command-Flow-Analysis.md`
  - `03-Event-Flow-Analysis.md`
  - `04-Current-Dispatching-Patterns.md`
  - `05-Store-Persistence-Architecture.md`

### Package Documentation
- `SourceFlow.Net-README.md` - Core package documentation
- `SourceFlow.Stores.EntityFramework-README.md` - EF package docs

## Naming Conventions

### Projects
- **Core**: `SourceFlow`
- **Extensions**: `SourceFlow.{Category}.{Provider}` (e.g., `SourceFlow.Cloud.AWS`)
- **Tests**: `{ProjectName}.Tests`

### Namespaces
- Follow project structure: `SourceFlow.Messaging.Commands`
- Cloud extensions: `SourceFlow.Cloud.AWS.Messaging`

### Files
- **Interfaces**: `I{Name}.cs` (e.g., `ICommandBus.cs`)
- **Implementations**: `{Name}.cs` (e.g., `CommandBus.cs`)
- **Tests**: `{ClassName}Tests.cs`

## Key Architectural Folders

### Messaging Infrastructure
```
Messaging/
├── Commands/           # Command pattern implementation
├── Events/            # Event pattern implementation
├── Bus/              # Command bus orchestration
└── Impl/             # Concrete implementations
```

### Extension Points
```
{Feature}/
├── I{Feature}.cs           # Interface definition
├── {Feature}.cs           # Default implementation
└── Impl/                  # Alternative implementations
```

## Configuration Files
- **`.editorconfig`** - Code formatting rules
- **`.gitignore`** - Git exclusions
- **`GitVersion.yml`** - Versioning configuration
- **`.jscpd.json`** - Copy-paste detection settings

## Build Artifacts
- `bin/` - Compiled binaries (gitignored)
- `obj/` - Build intermediates (gitignored)
- Generated NuGet packages in project output directories