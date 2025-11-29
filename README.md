# <img src="https://github.com/CodeShayk/SourceFlow.Net/blob/master/Images/ninja-icon-16.png" alt="ninja" style="width:30px;"/> SourceFlow.Net 
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
* 🏗️ Domain-Driven Design Support
* ⚡ CQRS Implementation with Command/Query Segregation 
* 📊 Event-First Design with Event Sourcing Foundation  
* 🧱 Clean Architecture

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

**Command Queue**
- A dedicated queue for each bounded context (microservice)
- Routes incoming commands to the appropriate subscribing sagas within the domain

**Event Dispatcher**
- Publishes domain events to cloud-based topics for cross-service communication
- Enables event-driven architecture across distributed systems

**Event Listeners**
- Bootstrap components that listen to subscribed event topics
- Dispatch received events to the appropriate aggregates and views within each domain context
- Enable seamless integration across bounded contexts
   
#### Architecture
<img src="https://github.com/CodeShayk/SourceFlow.Net/blob/master/Images/Architecture-Complete.png" alt="architecture" style="width:1200px; hieght:700px"/>

### RoadMap

| Package | Version | Release Date |Details |.Net Frameworks|
|------|---------|--------------|--------|-----------|
|SourceFlow|v1.0.0 [![NuGet version](https://badge.fury.io/nu/SourceFlow.Net.svg)](https://badge.fury.io/nu/SourceFlow.Net)|29th Nov 2025|Core functionality for event sourcing and CQRS|[![.Net 10](https://img.shields.io/badge/.Net-10-blue)](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) [![.Net 9.0](https://img.shields.io/badge/.Net-9.0-blue)](https://dotnet.microsoft.com/en-us/download/dotnet/9.0) [![.Net Standard 2.1](https://img.shields.io/badge/.NetStandard-2.1-blue)](https://github.com/dotnet/standard/blob/v2.1.0/docs/versions/netstandard2.1.md) [![.Net Standard 2.0](https://img.shields.io/badge/.NetStandard-2.0-blue)](https://github.com/dotnet/standard/blob/v2.0.0/docs/versions/netstandard2.0.md) [![.Net Framework 4.6.2](https://img.shields.io/badge/.Net-4.6.2-blue)](https://dotnet.microsoft.com/en-us/download/dotnet-framework/net46)|
|SourceFlow.Stores.EntityFramework|v1.0.0 [![NuGet version](https://badge.fury.io/nu/SourceFlow.Stores.EntityFramework.svg)](https://badge.fury.io/nu/SourceFlow.Stores.EntityFramework)|29th Nov 2025|Provides store implementation using EF. Can configure different (types of ) databases for each store.|[![.Net 10](https://img.shields.io/badge/.Net-10-blue)](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) [![.Net 9.0](https://img.shields.io/badge/.Net-9.0-blue)](https://dotnet.microsoft.com/en-us/download/dotnet/9.0) [![.Net 8.0](https://img.shields.io/badge/.Net-8.0-blue)](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) |
|SourceFlow.Cloud.AWS|v2.0.0 |(TBC) |Provides support for AWS cloud with cross domain boundary command and Event publishing & subscription.|[![.Net 10](https://img.shields.io/badge/.Net-10-blue)](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) [![.Net 9.0](https://img.shields.io/badge/.Net-9.0-blue)](https://dotnet.microsoft.com/en-us/download/dotnet/9.0) [![.Net 8.0](https://img.shields.io/badge/.Net-8.0-blue)](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)|
|SourceFlow.Cloud.Azure|v2.0.0 |(TBC) |Provides support for Azure cloud with cross domain boundary command and Event publishing & subscription.|[![.Net 10](https://img.shields.io/badge/.Net-10-blue)](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) [![.Net 9.0](https://img.shields.io/badge/.Net-9.0-blue)](https://dotnet.microsoft.com/en-us/download/dotnet/9.0) [![.Net 8.0](https://img.shields.io/badge/.Net-8.0-blue)](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)|

## Getting Started
### Installation
add nuget packages for SourceFlow.Net
> - dotnet add package SourceFlow
> - dotnet add package SourceFlow.Stores.EntityFramework 
> - dotnet add package SourceFlow.Cloud.Aws (to be released)
> - add custom implementation for stores, and extend for your cloud.

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
