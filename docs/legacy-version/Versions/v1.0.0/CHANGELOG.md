# Changelog

All notable changes to SourceFlow.Net will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] - 2025-01-28

### Added

#### Core Framework (SourceFlow.Net)
- Complete event sourcing implementation with Domain-Driven Design (DDD) principles
- CQRS pattern implementation with command/query segregation
- Aggregate pattern for managing root entities within bounded contexts
- Saga orchestration for long-running transactions and workflow management
- Event-first design with comprehensive event sourcing foundation
- Command and event publishing/subscription infrastructure
- View model projection system for read-optimized data models
- Support for multiple .NET frameworks:
  - .NET 10.0
  - .NET 9.0
  - .NET Standard 2.1
  - .NET Standard 2.0
  - .NET Framework 4.6.2
- OpenTelemetry integration for observability and tracing
- Dependency injection support via Microsoft.Extensions.DependencyInjection
- Structured logging support via Microsoft.Extensions.Logging

#### Entity Framework Store Provider (SourceFlow.Stores.EntityFramework)
- `ICommandStore` implementation using Entity Framework Core
- `IEntityStore` implementation using Entity Framework Core
- `IViewModelStore` implementation using Entity Framework Core
- Configurable connection strings per store type (separate or shared databases)
- Support for .NET 10.0, .NET 9.0, and .NET 8.0
- SQL Server database provider support
- Polly-based resilience and retry policies
- OpenTelemetry instrumentation for Entity Framework Core operations

#### Architecture & Patterns
- Clean architecture principles
- Separation of concerns between read and write models
- Event-driven communication between aggregates
- State preservation and consistency guarantees
- Extensible framework design for custom implementations

### Documentation
- Comprehensive README with architecture diagrams
- Developer guide available on GitHub Wiki
- Package documentation and XML comments
- Architecture diagram showing complete system design
- Roadmap for future cloud provider support (v2.0.0)

### Infrastructure
- NuGet package generation on build
- GitHub Actions CI/CD pipeline integration
- CodeQL security analysis
- Symbol packages for debugging support
- MIT License

[1.0.0]: https://github.com/CodeShayk/SourceFlow.Net/releases/tag/v1.0.0
