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

### Concept
**v1.0.0**
- `Aggregate` wraps the root aggregate entity that you wish to manage within a domain context (microservice). Changes are applied to aggregate entity by publishing commands. 
- `Saga` is a long running transaction that subscribes to `commands` to apply actual updates to aggregate entity. Saga basically `orchestrates` the success and failure flows to `preserve` the `state` of `root aggregate` accordingly. Saga can also publish `commands` to `itself` or `other` saga's. Saga can be defined to raise `events` when handling commands.
- `Events` are published to `subscribers`. There are two subscribers to event - ie. i. `Aggregates` ii. `Views`
- `Aggregate` subscribes to `events` to publish `changes` to root aggregate based upon `external` stimulus. ie. potential changes from any other saga workflow that could affect the state of Aggregate in context.
- `View` subscribes to `events` to `write` data to `view model`, view sources `transformed` data for interested viewers. ie. `UI` (viewer) could read data from view model (with eventual consistency).

**v2.0.0**
- `Command Dispatcher` will dispatch commands to `Cloud`. It basically targets a specific command queue.
- `Command Queue` is the queue designated for the whole of domain context (microservice). When command is sent to this queue it gets dispatched to subscribing Saga's in the domain context.
- Event Disptacher` will dispatch events to the `Cloud`. It basically publishes to certain topic.
- `Event Listeners` is a bootstrap component listening to subscribed topics. When it receives the event for the topic then it dispatches to subscribing Aggregates and Views within the domain context.
   
#### Architecture
<img src="https://github.com/CodeShayk/SourceFlow.Net/blob/v1.0.0/Images/Architecture-Complete.png" alt="arcitecture" style="width:1200px; hieght:700px"/>

### RoadMap

| Package | Version | Release Date |Details |.Net Frameworks|
|------|---------|--------------|--------|-----------|
|SourceFlow|v1.0.0 [![NuGet version](https://badge.fury.io/nu/SourceFlow.svg)](https://badge.fury.io/nu/SourceFlow)|29th Oct 2025|Core functionality for event sourcing and CQRS|[![.Net 10](https://img.shields.io/badge/.Net-10-blue)](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) [![.Net 9.0](https://img.shields.io/badge/.Net-9.0-blue)](https://dotnet.microsoft.com/en-us/download/dotnet/9.0) [![.Net Standard 2.1](https://img.shields.io/badge/.NetStandard-2.1-blue)](https://github.com/dotnet/standard/blob/v2.1.0/docs/versions/netstandard2.1.md) [![.Net Standard 2.0](https://img.shields.io/badge/.NetStandard-2.0-blue)](https://github.com/dotnet/standard/blob/v2.0.0/docs/versions/netstandard2.0.md) [![.Net Framework 4.6.2](https://img.shields.io/badge/.Net-4.6.2-blue)](https://dotnet.microsoft.com/en-us/download/dotnet-framework/net46)|
|SourceFlow.Stores.EntityFramework|v1.0.0 [![NuGet version](https://badge.fury.io/nu/SourceFlow.Stores.EntityFramework.svg)](https://badge.fury.io/nu/SourceFlow.Stores.EntityFramework)|29th Oct 2025|Provides store implementation using EF. Can configure different (types of ) databases for each store.|[![.Net 10](https://img.shields.io/badge/.Net-10-blue)](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) [![.Net 9.0](https://img.shields.io/badge/.Net-9.0-blue)](https://dotnet.microsoft.com/en-us/download/dotnet/9.0) [![.Net 8.0](https://img.shields.io/badge/.Net-8.0-blue)](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) |
|SourceFlow.Cloud.AWS|v2.0.0 |(TBC) |Provides support for AWS cloud with cross domain boundary command and Event publishing & subscription.|[![.Net 10](https://img.shields.io/badge/.Net-10-blue)](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) [![.Net 9.0](https://img.shields.io/badge/.Net-9.0-blue)](https://dotnet.microsoft.com/en-us/download/dotnet/9.0) [![.Net 8.0](https://img.shields.io/badge/.Net-8.0-blue)](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)|
|SourceFlow.Cloud.Azure|v2.0.0 |(TBC) |Provides support for Azure cloud with cross domain boundary command and Event publishing & subscription.|[![.Net 10](https://img.shields.io/badge/.Net-10-blue)](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) [![.Net 9.0](https://img.shields.io/badge/.Net-9.0-blue)](https://dotnet.microsoft.com/en-us/download/dotnet/9.0) [![.Net 8.0](https://img.shields.io/badge/.Net-8.0-blue)](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)|
## Getting Started
### Installation
nuget add package SourceFlow.Net
> - dotnet add package SourceFlow.Net
> - dotnet add package SourceFlow.Net.SqlServer (to be released)
> - or your preferred storage
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
