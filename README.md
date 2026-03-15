# <img src="https://github.com/CodeShayk/SourceFlow.Net/blob/master/Images/event-icon.png" alt="event" style="width:50px;"/> SourceFlow.Net v2.0.0
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/CodeShayk/SourceFlow.Net/blob/master/LICENSE.md) 
[![GitHub Release](https://img.shields.io/github/v/release/CodeShayk/SourceFlow.Net?logo=github&sort=semver)](https://github.com/CodeShayk/SourceFlow.Net/releases/latest)
[![master-build](https://github.com/CodeShayk/SourceFlow.Net/actions/workflows/Master-Build.yml/badge.svg)](https://github.com/CodeShayk/SourceFlow.Net/actions/workflows/Master-Build.yml)
[![master-codeql](https://github.com/CodeShayk/SourceFlow.Net/actions/workflows/Master-CodeQL.yml/badge.svg)](https://github.com/CodeShayk/SourceFlow.Net/actions/workflows/Master-CodeQL.yml)
<p align="center"> </p>
<p align="center">
  <strong>A modern, lightweight, and extensible .NET framework for building event-sourced applications using Domain-Driven Design (DDD) principles and Command Query Responsibility Segregation (CQRS) patterns.</strong>
</p>

---

## Overview
SourceFlow.Net empowers developers to build scalable, maintainable applications by providing a complete toolkit for event sourcing, domain modeling, and command/query separation. Built from the ground up for .NET 9.0 with **performance** and **developer experience** as core priorities.

### Key Features
* 🏗️ **Domain-Driven Design Support** - First-class support for aggregates, entities, value objects
* ⚡ **CQRS Implementation** - Complete command/query separation with optimized read models
* 📊 **Event Sourcing Foundation** - Event-first design with full audit trail
* 🧱 **Clean Architecture** - Clear separation of concerns and dependency management
* 💾 **Flexible Persistence** - Multiple storage options including Entity Framework Core
* 🔄 **Event Replay** - Built-in command replay for debugging and state reconstruction
* 🎯 **Type Safety** - Strongly-typed commands, events, and projections
* 📦 **Dependency Injection** - Seamless integration with .NET DI container
* 📈 **OpenTelemetry Integration** - Built-in distributed tracing and metrics for operations at scale
* ⚡ **Memory Optimization** - ArrayPool-based optimization for extreme throughput scenarios
* 🛡️ **Resilience Patterns** - Polly integration for fault tolerance with retry policies and circuit breakers

### Core Concepts

#### v1.0.0 Architecture

**Aggregates**
- An `Aggregate` encapsulates a root domain entity within a bounded context (microservice)
- Changes to aggregates are initiated by publishing commands
- Aggregates subscribe to events to react to external changes from other sagas or workflows that may affect their state

**Sagas**
- A `Saga` represents a long-running transaction that orchestrates complex business processes
- Sagas subscribe to commands and execute the actual updates to aggregate entities
- They manage both success and failure flows to ensure data consistency and preserve aggregate state
- Sagas can publish commands to themselves or other sagas to coordinate multi-step workflows
- Events can be raised by sagas during command handling to notify other components of state changes

**Events**
- Events are published to interested subscribers when state changes occur
- Two primary event subscribers exist in the framework:
  - **Aggregates**: React to events from external workflows that impact their domain state
  - **Views**: Project event data into optimized read models for query operations

**Views**
- Views subscribe to events and transform domain data into denormalized view models
- View models provide optimized read access for consumers such as UIs or reporting systems
- Data in view models follows eventual consistency patterns

#### v2.0.0 Roadmap (Cloud Integration)

**Command Dispatcher**
- Dispatches commands to cloud-based message queues for distributed processing
- Targets specific command queues based on bounded context routing
- Configured using the Bus Configuration System fluent API

**Command Queue**
- A dedicated queue for each bounded context (microservice)
- Routes incoming commands to the appropriate subscribing sagas within the domain

**Event Dispatcher**
- Publishes domain events to cloud-based topics for cross-service communication
- Enables event-driven architecture across distributed systems
- Configured using the Bus Configuration System fluent API

**Event Listeners**
- Bootstrap components that listen to subscribed event topics
- Dispatch received events to the appropriate aggregates and views within each domain context
- Enable seamless integration across bounded contexts

**Bus Configuration System**
- Code-first fluent API for configuring command and event routing
- Automatic resource creation (queues, topics, subscriptions)
- Type-safe configuration with compile-time validation
- Simplified setup using short names instead of full URLs/ARNs
- See [Cloud Configuration Guide](docs/SourceFlow.Net-README.md#-cloud-configuration-with-bus-configuration-system) for details
   
#### Architecture
<img src="https://github.com/CodeShayk/SourceFlow.Net/blob/master/Images/Architecture-Complete.png" alt="architecture" style="width:1200px; hieght:700px"/>

Click on **[Architecture](https://github.com/CodeShayk/SourceFlow.Net/blob/master/docs/Architecture/README.md)** for  more details on how to extend SourceFlow.Net for bespoke requirements.


### RoadMap

| Package | Version | Release Date |Details |.Net Frameworks|
|------|---------|--------------|--------|-----------|
|SourceFlow|v2.0.0 [![NuGet version](https://badge.fury.io/nu/SourceFlow.Net.svg)](https://badge.fury.io/nu/SourceFlow.Net)|15th Mar 2026|v1.0.0 Core functionality with integrated cloud abstractions. Cloud.Core consolidated into main package. Breaking changes: namespace updates from SourceFlow.Cloud.Core.* to SourceFlow.Cloud.*|[![.Net 10](https://img.shields.io/badge/.Net-10-blue)](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) [![.Net 9.0](https://img.shields.io/badge/.Net-9.0-blue)](https://dotnet.microsoft.com/en-us/download/dotnet/9.0) [![.Net Standard 2.1](https://img.shields.io/badge/.NetStandard-2.1-blue)](https://github.com/dotnet/standard/blob/v2.1.0/docs/versions/netstandard2.1.md) [![.Net Standard 2.0](https://img.shields.io/badge/.NetStandard-2.0-blue)](https://github.com/dotnet/standard/blob/v2.0.0/docs/versions/netstandard2.0.md)|
|SourceFlow|v1.0.0|29th Nov 2025|Initial stable release with event sourcing and CQRS|[![.Net 10](https://img.shields.io/badge/.Net-10-blue)](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) [![.Net 9.0](https://img.shields.io/badge/.Net-9.0-blue)](https://dotnet.microsoft.com/en-us/download/dotnet/9.0) [![.Net Standard 2.1](https://img.shields.io/badge/.NetStandard-2.1-blue)](https://github.com/dotnet/standard/blob/v2.1.0/docs/versions/netstandard2.1.md) [![.Net Standard 2.0](https://img.shields.io/badge/.NetStandard-2.0-blue)](https://github.com/dotnet/standard/blob/v2.0.0/docs/versions/netstandard2.0.md) [![.Net Framework 4.6.2](https://img.shields.io/badge/.Net-4.6.2-blue)](https://dotnet.microsoft.com/en-us/download/dotnet-framework/net46)|
|SourceFlow.Stores.EntityFramework|v2.0.0 [![NuGet version](https://badge.fury.io/nu/SourceFlow.Stores.EntityFramework.svg)](https://badge.fury.io/nu/SourceFlow.Stores.EntityFramework)|29th Nov 2025|v1.0.0 Core EF store implementations with new cloud idempotency provider implementation. |[![.Net 10](https://img.shields.io/badge/.Net-10-blue)](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) [![.Net 9.0](https://img.shields.io/badge/.Net-9.0-blue)](https://dotnet.microsoft.com/en-us/download/dotnet/9.0) [![.Net 8.0](https://img.shields.io/badge/.Net-8.0-blue)](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) [![.Net Standard 2.1](https://img.shields.io/badge/.NetStandard-2.1-blue)](https://github.com/dotnet/standard/blob/v2.1.0/docs/versions/netstandard2.1.md) [![.Net Standard 2.0](https://img.shields.io/badge/.NetStandard-2.0-blue)](https://github.com/dotnet/standard/blob/v2.0.0/docs/versions/netstandard2.0.md)|
|SourceFlow.Stores.EntityFramework|v1.0.0 |29th Nov 2025|Provides store implementation using EF. Can configure different (types of ) databases for each store.|[![.Net 10](https://img.shields.io/badge/.Net-10-blue)](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) [![.Net 9.0](https://img.shields.io/badge/.Net-9.0-blue)](https://dotnet.microsoft.com/en-us/download/dotnet/9.0) [![.Net 8.0](https://img.shields.io/badge/.Net-8.0-blue)](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) [![.Net Standard 2.1](https://img.shields.io/badge/.NetStandard-2.1-blue)](https://github.com/dotnet/standard/blob/v2.1.0/docs/versions/netstandard2.1.md) [![.Net Standard 2.0](https://img.shields.io/badge/.NetStandard-2.0-blue)](https://github.com/dotnet/standard/blob/v2.0.0/docs/versions/netstandard2.0.md)|
|SourceFlow.Cloud.AWS|v2.0.0 |15th Mar 2026 |Provides support for AWS cloud with cross domain boundary command and Event publishing & subscription. Includes comprehensive testing framework with LocalStack integration, performance benchmarks, security validation, and resilience testing.|[![.Net 10](https://img.shields.io/badge/.Net-10-blue)](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) [![.Net 9.0](https://img.shields.io/badge/.Net-9.0-blue)](https://dotnet.microsoft.com/en-us/download/dotnet/9.0) [![.Net 8.0](https://img.shields.io/badge/.Net-8.0-blue)](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)|
|SourceFlow.Cloud.Azure|v2.0.0 |(TBC) |Provides support for Azure cloud with cross domain boundary command and Event publishing & subscription. Includes comprehensive testing framework with Azurite integration, performance benchmarks, security validation, and resilience testing.|[![.Net 10](https://img.shields.io/badge/.Net-10-blue)](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) [![.Net 9.0](https://img.shields.io/badge/.Net-9.0-blue)](https://dotnet.microsoft.com/en-us/download/dotnet/9.0) [![.Net 8.0](https://img.shields.io/badge/.Net-8.0-blue)](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)|

## Getting Started
### Installation
add nuget packages for SourceFlow.Net
> - dotnet add package SourceFlow.Net
> - dotnet add package SourceFlow.Stores.EntityFramework 
> - dotnet add package SourceFlow.Cloud.AWS
> - add custom implementation for stores, and extend for your cloud.

### Cloud Integration with Idempotency

When deploying SourceFlow.Net applications to the cloud with AWS or Azure, idempotency is crucial for handling duplicate messages in distributed systems.

#### Single-Instance Deployments (Default)

For single-instance deployments, SourceFlow automatically uses an in-memory idempotency service:

```csharp
services.UseSourceFlow();

services.UseSourceFlowAws(
    options => { options.Region = RegionEndpoint.USEast1; },
    bus => bus
        .Send.Command<CreateOrderCommand>(q => q.Queue("orders.fifo"))
        .Listen.To.CommandQueue("orders.fifo"));
```

#### Multi-Instance Deployments (Recommended for Production)

For multi-instance deployments, use the SQL-based idempotency service to ensure duplicate detection across all instances:

```csharp
services.UseSourceFlow();

// Register Entity Framework stores with SQL-based idempotency
services.AddSourceFlowEfStores(connectionString);
services.AddSourceFlowIdempotency(
    connectionString: connectionString,
    cleanupIntervalMinutes: 60);

// Configure cloud integration (AWS or Azure)
services.UseSourceFlowAws(
    options => { options.Region = RegionEndpoint.USEast1; },
    bus => bus
        .Send.Command<CreateOrderCommand>(q => q.Queue("orders.fifo"))
        .Listen.To.CommandQueue("orders.fifo"));
```

**Benefits of SQL-Based Idempotency:**
- ✅ Distributed duplicate detection across multiple instances
- ✅ Automatic cleanup of expired records
- ✅ Database-backed persistence for reliability
- ✅ Supports SQL Server, PostgreSQL, MySQL, SQLite

For more details, see:
- [AWS Cloud Integration](docs/aws-integration.md.md)
- [SQL-Based Idempotency Service](docs/SQL-Based-Idempotency-Service.md)
- [Cloud Integration Testing guide](docs/Cloud-Integration-Testing.md)

### Developer Guide
This comprehensive guide provides detailed information about the SourceFlow.Net framework, covering everything from basic concepts to advanced implementation patterns and troubleshooting guidelines.

Please click on [Developer Guide](https://github.com/CodeShayk/SourceFlow.Net/wiki) for complete details.

## Support

If you are having problems, please let me know by [raising a new issue](https://github.com/CodeShayk/SourceFlow.Net/issues/new/choose).

## License

This project is licensed with the [MIT license](LICENSE).

## Contributing
We welcome contributions! Please see our Contributing Guide for details.
- 🐛 Bug Reports - Create an [issue](https://github.com/CodeShayk/sourceflow.net/issues/new/choose)
- 💡 Feature Requests - Start a [discussion](https://github.com/CodeShayk/SourceFlow.Net/discussions)
- 📝 Documentation - Help improve our [docs](https://github.com/CodeShayk/SourceFlow.Net/wiki)
- 💻 Code - Submit [pull](https://github.com/CodeShayk/SourceFlow.Net/pulls) requests

## Credits
Thank you for reading. Please fork, explore, contribute and report. Happy Coding !! :)
