using System.Text.Json.Serialization;
using SourceFlow;

namespace Bank.Contracts;

/// <summary>Published by Accounts when an account is opened (no PII by design, doc 03 §7).</summary>
[MessageType("accounts.account-opened", 1)]
public sealed record AccountOpenedV1(Guid AccountId, DateTimeOffset OpenedAt) : IIntegrationEvent;

/// <summary>The contracts module: registers the integration events for every service that uses them.</summary>
[SourceFlowModule(typeof(ContractsJsonContext))]
public sealed partial class ContractsModule;

/// <summary>JSON metadata for the contracts (DC-1).</summary>
[JsonSerializable(typeof(AccountOpenedV1))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public sealed partial class ContractsJsonContext : JsonSerializerContext;
