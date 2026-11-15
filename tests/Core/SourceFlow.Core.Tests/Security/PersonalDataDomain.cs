namespace SourceFlow.Core.Tests.Security;

// A test domain for crypto-shredding (doc 10 §4): a patient belongs to a customer (the subject). Name, age and street are
// personal data of the customer; the city is not.
[StronglyTypedId]
public readonly partial record struct PatientId(Guid Value);

public sealed record PatientAddress([property: PersonalData(Subject = "Customer")] string Street, string City);

[MessageType("test.patient.register", 1)]
public sealed record RegisterPatient(PatientId PatientId, string Customer, string Name, int Age, PatientAddress Home) : ICommand<Patient>;

[MessageType("test.patient.rename", 1)]
public sealed record RenamePatient(PatientId PatientId, string Name) : ICommand<Patient>;

[MessageType("test.patient.registered", 1)]
public sealed record PatientRegistered(
    PatientId PatientId,
    string Customer,
    [property: PersonalData(Subject = nameof(PatientRegistered.Customer))] string Name,
    [property: PersonalData(Subject = nameof(PatientRegistered.Customer))] int Age,
    PatientAddress Home) : IEvent;

[MessageType("test.patient.renamed", 1)]
public sealed record PatientRenamed(PatientId PatientId, string Customer, [property: PersonalData(Subject = nameof(PatientRenamed.Customer))] string Name) : IEvent;

[Aggregate("test-patient", SnapshotEvery = 2)]
public sealed partial record Patient : IAggregate<Patient, PatientId>
{
    public static Patient Initial { get; } = new();

    public string Customer { get; init; } = string.Empty;

    [PersonalData(Subject = nameof(Customer))]
    public string Name { get; init; } = string.Empty;

    public int Age { get; init; }

    public Decision Decide(RegisterPatient c, DecisionContext ctx)
    {
        return Decision.Accept(new PatientRegistered(c.PatientId, c.Customer, c.Name, c.Age, c.Home));
    }

    public Decision Decide(RenamePatient c, DecisionContext ctx)
    {
        return Decision.Accept(new PatientRenamed(c.PatientId, Customer, c.Name));
    }

    private Patient When(PatientRegistered e)
    {
        return this with { Customer = e.Customer, Name = e.Name, Age = e.Age };
    }

    private Patient When(PatientRenamed e)
    {
        return this with { Name = e.Name };
    }
}

public sealed record PatientCard(PatientId Id, string Customer, [property: PersonalData(Subject = nameof(PatientCard.Customer))] string Name, string City);

// Registered by hand (no [Projection]) in its own module, so hosts of CoreTestModule do not get an extra subscription.
public sealed class PatientCardsProjection : IDocumentProjectionRuntime<PatientCardsProjection, PatientCard, PatientId>
{
    private static readonly Type[] Handled = [typeof(PatientRegistered), typeof(PatientRenamed)];

    public static string ProjectionName => "test-patient-cards";

    public static int ProjectionVersion => 1;

    public static IReadOnlyList<Type> HandledEvents => Handled;

    public static bool TryGetKey(IEvent @event, in EventContext context, out PatientId key)
    {
        switch (@event)
        {
            case PatientRegistered e:
                key = e.PatientId;
                return true;
            case PatientRenamed e:
                key = e.PatientId;
                return true;
            default:
                key = default;
                return false;
        }
    }

    public static string FormatKey(PatientId key)
    {
        return key.ToStreamSegment();
    }

    public PatientCard? Apply(IEvent @event, PatientCard? current, in EventContext context)
    {
        return @event switch
        {
            PatientRegistered e => new PatientCard(e.PatientId, e.Customer, e.Name, e.Home.City),
            PatientRenamed e when current is not null => current with { Name = e.Name },
            _ => current,
        };
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(PatientCard))]
[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
public sealed partial class PatientCardsJsonContext : System.Text.Json.Serialization.JsonSerializerContext;

public sealed class PatientCardsModule : ISourceFlowModule
{
    public static string ModuleName => "PatientCardsModule";

    public static void Register(ISourceFlowModuleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddJsonTypeInfoResolver(PatientCardsJsonContext.Default);
        builder.AddProjection<PatientCardsProjection, PatientCard, PatientId>();
    }
}
