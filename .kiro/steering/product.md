# SourceFlow.Net Product Overview

SourceFlow.Net is a modern, lightweight .NET framework for building event-sourced applications using Domain-Driven Design (DDD) principles and Command Query Responsibility Segregation (CQRS) patterns.

## Core Purpose
Build scalable, maintainable applications with complete event sourcing, CQRS implementation, and saga orchestration for complex business workflows.

## Key Features
- **Event Sourcing Foundation** - Event-first design with complete audit trail and state reconstruction
- **CQRS Implementation** - Separate command/query models with optimized read/write paths
- **Saga Pattern** - Long-running transaction orchestration across multiple aggregates
- **Domain-Driven Design** - First-class support for aggregates, entities, and value objects
- **Clean Architecture** - Clear separation of concerns and dependency management
- **Multi-Framework Support** - .NET Framework 4.6.2, .NET Standard 2.0/2.1, .NET 9.0, .NET 10.0
- **Cloud Integration** - AWS and Azure extensions for distributed messaging
- **Performance Optimized** - ArrayPool-based optimization and parallel processing
- **Observable** - Built-in OpenTelemetry integration for distributed tracing

## Architecture Patterns
- **Command Processing**: Command → CommandBus → Saga → Events → CommandStore
- **Event Processing**: Event → EventQueue → View → ViewModel → ViewModelStore
- **Extensible Dispatchers** - Plugin architecture for cloud messaging without core modifications

## Target Use Cases
- Event-driven microservices architectures
- Complex business workflow orchestration
- Applications requiring complete audit trails
- Systems needing independent read/write scaling
- Cloud-native distributed applications