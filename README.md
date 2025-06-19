# <img src="https://github.com/CodeShayk/SourceFlow.Net/blob/master/ninja-icon-16.png" alt="ninja" style="width:30px;"/> SourceFlow.Net

[![.Net 9.0](https://img.shields.io/badge/.Net-9.0-blue)](https://dotnet.microsoft.com/en-us/download/dotnet/9.0)
[![.Net Standard 2.1](https://img.shields.io/badge/.NetStandard-2.1-blue)](https://github.com/dotnet/standard/blob/v2.1.0/docs/versions/netstandard2.1.md)
[![NuGet version](https://badge.fury.io/nu/SourceFlow.Net.svg)](https://badge.fury.io/nu/SourceFlow.Net) 
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/CodeShayk/SourceFlow.Net/blob/master/LICENSE.md) 
[![GitHub Release](https://img.shields.io/github/v/release/CodeShayk/SourceFlow.Net?logo=github&sort=semver)](https://github.com/CodeShayk/SourceFlow.Net/releases/latest)
[![master-build](https://github.com/CodeShayk/SourceFlow.Net/actions/workflows/Master-Build.yml/badge.svg)](https://github.com/CodeShayk/SourceFlow.Net/actions/workflows/Master-Build.yml)
[![master-codeql](https://github.com/CodeShayk/SourceFlow.Net/actions/workflows/Master-CodeQL.yml/badge.svg)](https://github.com/CodeShayk/SourceFlow.Net/actions/workflows/Master-CodeQL.yml)

<p align="center"> </p>
<p align="center">
  <strong>A modern, lightweight, and extensible .NET framework for building event-sourced applications using Domain-Driven Design (DDD) principles and Command Query Responsibility Segregation (CQRS) patterns.</strong>
</p>

---

## 🚀 Overview

SourceFlow.Net empowers developers to build scalable, maintainable applications by providing a complete toolkit for event sourcing, domain modeling, and command/query separation. Built from the ground up for .NET 8+ with **performance** and **developer experience** as core priorities.

## ✨ Key Features

### 📊 Event Sourcing Foundation
- **Event Store Abstraction** - Pluggable storage backends (SQL Server, PostgreSQL, MongoDB, EventStore DB)
- **Event Serialization** - JSON, MessagePack, and custom serializers
- **Event Versioning** - Built-in support for event schema evolution
- **Snapshots** - Automatic snapshot management for performance optimization
- **Event Correlation** - Track causation and correlation across event streams

### 🏗️ Domain-Driven Design Support
- **Aggregate Root Framework** - Base classes and interfaces for DDD aggregates
- **Value Objects** - Immutable value type helpers and generators
- **Domain Events** - Rich domain event publishing and handling
- **Bounded Context Isolation** - Tools for maintaining clean architectural boundaries
- **Specification Pattern** - Reusable business rule implementations

### ⚡ CQRS Implementation
- **Command Pipeline** - Validation, authorization, and middleware support
- **Query Optimization** - Dedicated read models with eventual consistency
- **Mediator Pattern** - Built-in command/query dispatching
- **Projection Engine** - Real-time and batch projection processing
- **Read Model Synchronization** - Automated view materialization

### 👨‍💻 Developer Experience
- **Minimal Configuration** - Convention-over-configuration approach
- **ASP.NET Core Integration** - Seamless web API and MVC support
- **Dependency Injection** - Native DI container integration
- **Health Checks** - Built-in monitoring and diagnostics
- **OpenTelemetry Support** - Distributed tracing and metrics
- **Code Generation** - T4 templates and source generators for boilerplate reduction

### 🏢 Enterprise Features
- **Multi-tenancy** - Tenant isolation at the event store level
- **Eventual Consistency** - Saga pattern implementation for long-running processes
- **Event Replay** - Complete system state reconstruction capabilities
- **Audit Trail** - Immutable audit log for compliance requirements
- **Performance Monitoring** - Built-in metrics and performance counters
- **Horizontal Scaling** - Support for distributed event processing

## 🏛️ Architecture Principles

<table>
  <tr>
    <td><strong>🧱 Clean Architecture</strong></td>
    <td>Enforces dependency inversion and separation of concerns</td>
  </tr>
  <tr>
    <td><strong>📝 Event-First Design</strong></td>
    <td>Events as the source of truth for all state changes</td>
  </tr>
  <tr>
    <td><strong>🔒 Immutable Data</strong></td>
    <td>Promotes functional programming concepts where applicable</td>
  </tr>
  <tr>
    <td><strong>🧪 Testability</strong></td>
    <td>Easy unit and integration testing with in-memory implementations</td>
  </tr>
  <tr>
    <td><strong>👁️ Observability</strong></td>
    <td>Comprehensive logging, metrics, and tracing out of the box</td>
  </tr>
</table>

## 💼 Use Cases

| Industry | Applications |
|----------|-------------|
| **💰 Financial** | Trading platforms, payment processing, accounting systems |
| **🛒 E-commerce** | Order management, inventory tracking, customer journeys |
| **🌐 IoT** | Device state management, sensor data processing |
| **👥 Collaboration** | Document versioning, user activity tracking |
| **📋 Compliance** | Audit trails, regulatory re
