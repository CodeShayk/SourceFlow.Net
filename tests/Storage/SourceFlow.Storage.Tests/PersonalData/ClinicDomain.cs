using System.Text.Json.Serialization;

namespace SourceFlow.Storage.Tests.PersonalData;

// A small domain for crypto-shredding on real databases (doc 10 §4, E2E-29..31): a client belongs to a customer, the
// subject of its personal data.
[StronglyTypedId]
public readonly partial record struct ClientId(Guid Value);

[MessageType("clinic.client.register", 1)]
public sealed record RegisterClient(ClientId ClientId, string Customer, string Name, decimal Balance) : ICommand<Client>;

[MessageType("clinic.client.registered", 1)]
public sealed record ClientRegistered(
    ClientId ClientId,
    string Customer,
    [property: PersonalData(Subject = nameof(ClientRegistered.Customer))] string Name,
    decimal Balance) : IEvent;

[Aggregate("clinic-client", SnapshotEvery = 1)]
public sealed partial record Client : IAggregate<Client, ClientId>
{
    public static Client Initial { get; } = new();

    public string Customer { get; init; } = string.Empty;

    [PersonalData(Subject = nameof(Customer))]
    public string Name { get; init; } = string.Empty;

    public Decision Decide(RegisterClient c, DecisionContext ctx)
    {
        return Decision.Accept(new ClientRegistered(c.ClientId, c.Customer, c.Name, c.Balance));
    }

    private Client When(ClientRegistered e)
    {
        return this with { Customer = e.Customer, Name = e.Name };
    }
}

public sealed record ClientCard(ClientId Id, string Customer, [property: PersonalData(Subject = nameof(ClientCard.Customer))] string Name, decimal Balance);

// Hand-registered (no [Projection], no [SourceFlowModule]): this assembly's generated ReportingModule registers every
// annotated type, and must not get this projection.
public sealed class ClientCardsProjection : IDocumentProjectionRuntime<ClientCardsProjection, ClientCard, ClientId>
{
    private static readonly Type[] Handled = [typeof(ClientRegistered)];

    public static string ProjectionName => "clinic-client-cards";

    public static int ProjectionVersion => 1;

    public static IReadOnlyList<Type> HandledEvents => Handled;

    public static bool TryGetKey(IEvent @event, in EventContext context, out ClientId key)
    {
        key = @event is ClientRegistered e ? e.ClientId : default;
        return @event is ClientRegistered;
    }

    public static string FormatKey(ClientId key)
    {
        return key.ToStreamSegment();
    }

    public ClientCard? Apply(IEvent @event, ClientCard? current, in EventContext context)
    {
        return @event is ClientRegistered e ? new ClientCard(e.ClientId, e.Customer, e.Name, e.Balance) : current;
    }
}

public sealed class ClinicModule : ISourceFlowModule
{
    public static string ModuleName => "ClinicModule";

    public static void Register(ISourceFlowModuleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddJsonTypeInfoResolver(ClinicJsonContext.Default);
        builder.AddMessage<RegisterClient>("clinic.client.register", 1);
        builder.AddMessage<ClientRegistered>("clinic.client.registered", 1);
        builder.AddAggregate<Client, ClientId>();
        builder.AddProjection<ClientCardsProjection, ClientCard, ClientId>();
    }
}

[JsonSerializable(typeof(RegisterClient))]
[JsonSerializable(typeof(ClientRegistered))]
[JsonSerializable(typeof(Client))]
[JsonSerializable(typeof(ClientCard))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public sealed partial class ClinicJsonContext : JsonSerializerContext;
