# <img src="https://github.com/CodeShayk/SourceFlow.Net/blob/master/Images/ninja-icon-16.png" alt="ninja" style="width:30px;"/> SourceFlow.Net 
[![NuGet version](https://badge.fury.io/nu/SourceFlow.Net.svg)](https://badge.fury.io/nu/SourceFlow.Net) 
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/CodeShayk/SourceFlow.Net/blob/master/LICENSE.md) 
[![GitHub Release](https://img.shields.io/github/v/release/CodeShayk/SourceFlow.Net?logo=github&sort=semver)](https://github.com/CodeShayk/SourceFlow.Net/releases/latest)
[![master-build](https://github.com/CodeShayk/SourceFlow.Net/actions/workflows/Master-Build.yml/badge.svg)](https://github.com/CodeShayk/SourceFlow.Net/actions/workflows/Master-Build.yml)
[![master-codeql](https://github.com/CodeShayk/SourceFlow.Net/actions/workflows/Master-CodeQL.yml/badge.svg)](https://github.com/CodeShayk/SourceFlow.Net/actions/workflows/Master-CodeQL.yml)
[![.Net 9.0](https://img.shields.io/badge/.Net-9.0-blue)](https://dotnet.microsoft.com/en-us/download/dotnet/9.0)
[![.Net Standard 2.1](https://img.shields.io/badge/.NetStandard-2.1-blue)](https://github.com/dotnet/standard/blob/v2.1.0/docs/versions/netstandard2.1.md)
[![.Net Standard 2.0](https://img.shields.io/badge/.NetStandard-2.0-blue)](https://github.com/dotnet/standard/blob/v2.0.0/docs/versions/netstandard2.0.md)
[![.Net Framework 4.6.2](https://img.shields.io/badge/.Net-4.6.2-blue)](https://dotnet.microsoft.com/en-us/download/dotnet-framework/net46)

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
- `Aggregate` wraps the root entity to manage changes to the root aggregate by publishing commands. 
- `Saga` is a long running transaction and subscribes to `commands` to manage the actual updates to root aggregate within the domain bounded context.
- Saga basically `orchestrates` the success and failure flows to `update` the `state` of `root aggregate` accordingly.
- Saga can also publish `commands` to `itself` or `other` saga's.
- Saga can be defined to raise `events` when handling commands.
- `Events` are published to `subscribers`.
- There are two subscribers to event - ie. i. `Aggregates` ii. `Views`
- `Aggregate` subscribe to `events` to manage `changes` to root aggregate based upon `external` stimulus. ie. changes in any saga workflow
- `View` subscribe to `events` to `write` data to `view model`, views source `transformed` data for viewers. ie. `UI` could read data from view model (with eventual consistency).  

<img src="https://github.com/CodeShayk/SourceFlow.Net/blob/v1.0.0/Images/Architecture.png" alt="arcitecture" style="width:700px; hieght:1000px"/>

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
