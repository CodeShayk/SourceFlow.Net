# SourceFlow Azure Cloud Extension

**Project**: `src/SourceFlow.Cloud.Azure/`  
**Purpose**: Azure cloud integration for distributed command and event processing

**Dependencies**: 
- `SourceFlow` (core framework with integrated cloud functionality)
- Azure SDK packages (Service Bus, Key Vault, Identity)

## Core Functionality

### Azure Services Integration
- **Azure Service Bus** - Unified messaging for commands and events
- **Azure Key Vault** - Message encryption and secret management
- **Azure Monitor** - Telemetry and health monitoring
- **Managed Identity** - Secure authentication without connection strings

### Infrastructure Components
- **`AzureBusBootstrapper`** - Hosted service for automatic resource provisioning
- **`ServiceBusClientFactory`** - Factory for creating configured Service Bus clients
- **`AzureHealthCheck`** - Health check implementation for Azure services

### Dispatcher Implementations
- **`AzureServiceBusCommandDispatcher`** - Routes commands to Service Bus queues
- **`AzureServiceBusEventDispatcher`** - Publishes events to Service Bus topics
- **Enhanced Versions** - Advanced features with encryption and monitoring

### Listener Services
- **`AzureServiceBusCommandListener`** - Background service consuming queue messages
- **`AzureServiceBusEventListener`** - Background service consuming topic subscriptions
- **Hosted Service Integration** - Automatic lifecycle management

### Monitoring & Observability
- **`AzureDeadLetterMonitor`** - Failed message monitoring and analysis
- **`AzureTelemetryExtensions`** - Azure-specific metrics and tracing

## Configuration System

### Fluent Bus Configuration

The Bus Configuration System provides a type-safe, intuitive way to configure Azure Service Bus messaging infrastructure using a fluent API. Unlike AWS, Azure uses short names directly without URL/ARN resolution.

**Complete Configuration Example:**

```csharp
using SourceFlow.Cloud.Azure;

services.UseSourceFlowAzure(
    options => {
        options.FullyQualifiedNamespace = "myservicebus.servicebus.windows.net";
        options.UseManagedIdentity = true;
        options.MaxConcurrentCalls = 10;
        options.AutoCompleteMessages = true;
    },
    bus => bus
        .Send
            .Command<CreateOrderCommand>(q => q.Queue("orders"))
            .Command<UpdateOrderCommand>(q => q.Queue("orders"))
            .Command<CancelOrderCommand>(q => q.Queue("orders"))
            .Command<AdjustInventoryCommand>(q => q.Queue("inventory"))
            .Command<ProcessPaymentCommand>(q => q.Queue("payments"))
        .Raise
            .Event<OrderCreatedEvent>(t => t.Topic("order-events"))
            .Event<OrderUpdatedEvent>(t => t.Topic("order-events"))
            .Event<OrderCancelledEvent>(t => t.Topic("order-events"))
            .Event<InventoryAdjustedEvent>(t => t.Topic("inventory-events"))
            .Event<PaymentProcessedEvent>(t => t.Topic("payment-events"))
        .Listen.To
            .CommandQueue("orders")
            .CommandQueue("inventory")
            .CommandQueue("payments")
        .Subscribe.To
            .Topic("order-events")
            .Topic("payment-events")
            .Topic("inventory-events"));
```

### Azure-Specific Bus Configuration Details

#### Service Bus Queue Name Usage

Azure Service Bus uses short queue names directly without URL resolution:

**Configuration:** `"orders"`  
**Used As:** `"orders"` (no transformation)

**How it works:**
1. Bootstrapper uses queue name directly with ServiceBusClient
2. No account ID or namespace resolution needed
3. Namespace is configured once in options
4. All queue operations use the configured namespace

**Benefits:**
- Simpler configuration (no URL construction)
- Consistent naming across environments
- Easier to read and maintain

#### Service Bus Topic Name Usage

Azure Service Bus uses short topic names directly:

**Configuration:** `"order-events"`  
**Used As:** `"order-events"` (no transformation)

**How it works:**
1. Bootstrapper uses topic name directly with ServiceBusClient
2. Namespace is configured once in options
3. All topic operations use the configured namespace

#### Session-Enabled Queue Configuration

Use the `.fifo` suffix to enable session-based ordering:

```csharp
.Send
    .Command<CreateOrderCommand>(q => q.Queue("orders.fifo"))
```

**Automatic Session Attributes:**
- `RequiresSession = true` - Enables session handling
- `SessionId` - Set to entity ID for ordering per entity
- `MaxDeliveryCount = 10` - Maximum delivery attempts
- `LockDuration = 5 minutes` - Message lock duration

**When to use session-enabled queues:**
- Commands must be processed in order per entity
- Stateful message processing is required
- Message grouping by entity is needed

**Standard Queue Alternative:**
```csharp
.Send
    .Command<SendEmailCommand>(q => q.Queue("notifications"))
```
- Higher throughput (no session overhead)
- Concurrent processing across all messages
- Best for independent operations

#### Bootstrapper Resource Creation

The `AzureBusBootstrapper` automatically creates missing Azure Service Bus resources at application startup:

**Service Bus Queue Creation:**
```csharp
using Azure.Messaging.ServiceBus.Administration;

// For session-enabled queues (detected by .fifo suffix)
var queueOptions = new CreateQueueOptions("orders.fifo")
{
    RequiresSession = true,
    MaxDeliveryCount = 10,
    LockDuration = TimeSpan.FromMinutes(5),
    DefaultMessageTimeToLive = TimeSpan.FromDays(14),
    EnableDeadLetteringOnMessageExpiration = true,
    EnableBatchedOperations = true
};

// For standard queues
var queueOptions = new CreateQueueOptions("notifications")
{
    RequiresSession = false,
    MaxDeliveryCount = 10,
    LockDuration = TimeSpan.FromMinutes(5),
    DefaultMessageTimeToLive = TimeSpan.FromDays(14),
    EnableDeadLetteringOnMessageExpiration = true,
    EnableBatchedOperations = true
};
```

**Service Bus Topic Creation:**
```csharp
var topicOptions = new CreateTopicOptions("order-events")
{
    DefaultMessageTimeToLive = TimeSpan.FromDays(14),
    EnableBatchedOperations = true,
    MaxSizeInMegabytes = 1024
};
```

**Service Bus Subscription Creation with Forwarding:**

The bootstrapper automatically creates subscriptions that forward topic messages to command queues:

```csharp
// For each topic in Subscribe.To configuration
// And each queue in Listen.To configuration
var subscriptionOptions = new CreateSubscriptionOptions("order-events", "fwd-to-orders")
{
    ForwardTo = "orders", // Forward to command queue
    MaxDeliveryCount = 10,
    LockDuration = TimeSpan.FromMinutes(5),
    EnableDeadLetteringOnMessageExpiration = true,
    EnableBatchedOperations = true
};
```

**Subscription Naming Convention:**
- Pattern: `fwd-to-{queueName}`
- Example: Topic "order-events" → Subscription "fwd-to-orders" → Queue "orders"

**Resource Creation Behavior:**
- Idempotent operations (safe to run multiple times)
- Skips creation if resource already exists
- Logs resource creation for audit trail
- Fails fast if permissions are insufficient

#### Managed Identity Integration

**Recommended Authentication Approach:**

```csharp
services.UseSourceFlowAzure(options => {
    options.FullyQualifiedNamespace = "myservicebus.servicebus.windows.net";
    options.UseManagedIdentity = true;
});
```

**How Managed Identity Works:**
1. Application runs on Azure resource (VM, App Service, Container Instance, etc.)
2. Azure automatically provides identity credentials
3. ServiceBusClient uses DefaultAzureCredential
4. No connection strings or secrets needed

**Required Azure RBAC Roles:**
- **Azure Service Bus Data Owner** - Full access for bootstrapper (development)
- **Azure Service Bus Data Sender** - Send messages to queues/topics
- **Azure Service Bus Data Receiver** - Receive messages from queues/subscriptions

**Assigning Roles:**
```bash
# Get the managed identity principal ID
PRINCIPAL_ID=$(az webapp identity show --name myapp --resource-group mygroup --query principalId -o tsv)

# Assign Service Bus Data Owner role
az role assignment create \
  --role "Azure Service Bus Data Owner" \
  --assignee $PRINCIPAL_ID \
  --scope /subscriptions/{subscription-id}/resourceGroups/{resource-group}/providers/Microsoft.ServiceBus/namespaces/{namespace}
```

**Connection String Alternative (Not Recommended for Production):**
```csharp
services.UseSourceFlowAzure(options => {
    options.ServiceBusConnectionString = "Endpoint=sb://myservicebus.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=...";
});
```

**Production Best Practices:**
- Always use Managed Identity in production
- Use connection strings only for local development
- Rotate connection strings regularly if used
- Store connection strings in Azure Key Vault
- Use separate identities for different environments

### Bus Bootstrapper
- **Automatic Resource Creation** - Creates missing queues, topics, and subscriptions at startup
- **Name Resolution** - Uses short names directly (no URL/ARN translation needed)
- **FIFO Queue Detection** - Automatically enables sessions for .fifo queues
- **Topic Forwarding** - Creates subscriptions that forward to command queues
- **Validation** - Ensures at least one command queue exists when subscribing to topics
- **Hosted Service** - Runs before listeners to ensure routing is ready

### Connection Options
```csharp
// Connection string approach
services.UseSourceFlowAzure(options => {
    options.ServiceBusConnectionString = connectionString;
});

// Managed identity approach (recommended)
services.UseSourceFlowAzure(options => {
    options.FullyQualifiedNamespace = "myservicebus.servicebus.windows.net";
    options.UseManagedIdentity = true;
});
```

### Azure Options
```csharp
services.UseSourceFlowAzure(options => {
    options.EnableCommandRouting = true;
    options.EnableEventRouting = true;
    options.EnableCommandListener = true;
    options.EnableEventListener = true;
    options.MaxConcurrentCalls = 10;
    options.AutoCompleteMessages = true;
});
```

## Service Registration

### Core Pattern
```csharp
services.UseSourceFlowAzure(
    options => { /* Azure settings */ },
    bus => { /* Bus configuration */ });
// Automatically registers:
// - ServiceBusClient with retry policies
// - ServiceBusAdministrationClient for resource management
// - Command and event dispatchers
// - AzureBusBootstrapper as hosted service
// - Background listeners
// - BusConfiguration with routing
// - Health checks
// - Telemetry services
```

### Service Lifetimes
- **Singleton**: ServiceBusClient, event dispatchers, bus configuration, listeners, bootstrapper
- **Scoped**: Command dispatchers (matches core framework pattern)

### Registration Order
1. Service Bus clients (messaging and administration)
2. BusConfiguration from fluent API
3. AzureBusBootstrapper (must run before listeners)
4. Command and event dispatchers
5. Background listeners
6. Health checks and telemetry

## Service Bus Features

### Message Properties
- **SessionId** - Entity-based message ordering
- **MessageId** - Unique message identification
- **CorrelationId** - Request/response correlation
- **Custom Properties** - Command/event metadata

### Advanced Messaging
- **Sessions** - Ordered message processing per entity
- **Duplicate Detection** - Automatic deduplication
- **Dead Letter Queues** - Failed message handling
- **Scheduled Messages** - Delayed message delivery

## Routing Configuration

### Fluent Configuration (Recommended)
```csharp
services.UseSourceFlowAzure(
    options => { /* Azure settings */ },
    bus => bus
        .Send.Command<CreateOrderCommand>(q => q.Queue("orders"))
        .Raise.Event<OrderCreatedEvent>(t => t.Topic("order-events")));
```

### Key Features
- **Short Names Only** - Provide queue/topic names directly
- **Automatic Resolution** - Names used as-is (no URL/ARN translation)
- **Resource Creation** - Missing queues/topics/subscriptions created automatically
- **Session Support** - .fifo suffix automatically enables sessions
- **Type Safety** - Compile-time validation of command/event types
- **Topic Forwarding** - Subscriptions automatically forward to command queues

## Security Features

### Managed Identity Integration
- **DefaultAzureCredential** - Automatic credential resolution
- **System-Assigned Identity** - VM/App Service identity
- **User-Assigned Identity** - Shared identity across resources
- **Local Development** - Azure CLI/Visual Studio credentials

### Message Encryption
- **`AzureKeyVaultMessageEncryption`** - Key Vault-based encryption
- **Sensitive Data Masking** - `[SensitiveData]` attribute support
- **Key Rotation** - Automatic Key Vault key rotation

### Access Control
- **RBAC Integration** - Role-based access control
- **Namespace-Level Security** - Service Bus access policies
- **Queue/Topic Permissions** - Granular access control

## Monitoring & Observability

### Health Checks
- **`AzureServiceBusHealthCheck`** - Service Bus connectivity validation
- **Queue/Topic Existence** - Resource availability checks
- **Permission Validation** - Access rights verification

### Telemetry Integration
- **`AzureTelemetryExtensions`** - Azure-specific metrics and tracing
- **Azure Monitor Integration** - Native Azure telemetry
- **Application Insights** - Detailed application monitoring

### Dead Letter Monitoring
- **`AzureDeadLetterMonitor`** - Failed message analysis
- **Automatic Retry** - Configurable retry policies
- **Error Classification** - Failure pattern analysis

## Performance Optimizations

### Connection Management
- **ServiceBusClient Singleton** - Shared client instance
- **Connection Pooling** - Efficient connection reuse
- **Retry Policies** - Exponential backoff with jitter

### Message Processing
- **Concurrent Processing** - Configurable parallelism
- **Prefetch Count** - Optimized message batching
- **Auto-Complete** - Automatic message completion
- **Session Handling** - Ordered processing per entity

## Development Guidelines

### Bus Configuration Best Practices
- Use fluent API for type-safe configuration
- Provide short queue/topic names only
- Use .fifo suffix for queues requiring sessions
- Group related commands to the same queue
- Let bootstrapper create resources in development
- Use ARM templates/Bicep for production infrastructure
- Configure at least one command queue when subscribing to topics

### Bootstrapper Behavior
- Runs once at application startup as hosted service
- Creates missing queues with appropriate settings
- Creates missing topics
- Creates subscriptions that forward to command queues
- Subscription naming: "fwd-to-{queueName}"
- Must complete before listeners start polling
- Uses ServiceBusAdministrationClient for management operations

### Message Design
- Use sessions for ordered processing
- Include correlation IDs for tracing
- Design for at-least-once delivery
- Implement idempotent message handlers
- Use duplicate detection for deduplication

### Error Handling
- Configure appropriate retry policies
- Use dead letter queues for poison messages
- Implement circuit breaker patterns
- Monitor message processing metrics
- Handle `CircuitBreakerOpenException` gracefully

### Security Best Practices
- Use managed identity over connection strings
- Encrypt sensitive message content with Key Vault
- Implement message validation
- Use least privilege access principles
- Use RBAC for granular access control

### Testing Strategies
- Use Service Bus emulator for local development
- Mock Service Bus clients in unit tests
- Integration tests with real Service Bus
- Load testing for throughput validation
- Test session-based ordering guarantees

### Deployment Considerations
- Configure Service Bus namespaces per environment
- Use ARM templates or Bicep for infrastructure
- Implement proper monitoring and alerting
- Plan for disaster recovery scenarios
- Consider geo-replication for high availability