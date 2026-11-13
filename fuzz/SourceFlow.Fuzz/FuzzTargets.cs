using System.Text;
using Bank.Accounts;
using Bank.Contracts;
using Bank.Transfers;
using PubsubMessage = Google.Cloud.PubSub.V1.PubsubMessage;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using SourceFlow.Cloud.Aws;
using SourceFlow.Cloud.Azure;
using SourceFlow.Cloud.Gcp;
using SourceFlow.Hosting;
using SourceFlow.Messaging;
using SourceFlow.Migration;
using SourceFlow.Query;
using SourceFlow.Runtime;
using SourceFlow.Security;

namespace SourceFlow.Fuzz;

/// <summary>
/// The fuzz targets (doc 10 §7, WP-5.5): every parser that reads untrusted bytes. Each target states the exceptions its
/// contract allows (malformed input is rejected with them); any other exception, a hang or a crash is a finding.
/// </summary>
public static class FuzzTargets
{
    private static readonly Lazy<Fixture> State = new(() => new Fixture());

    /// <summary>The targets by name.</summary>
    public static IReadOnlyDictionary<string, FuzzTarget> All { get; } = new Dictionary<string, FuzzTarget>(StringComparer.Ordinal)
    {
        ["cloudevents-binary"] = new(CloudEventsBinary, [typeof(FormatException)], "CloudEvents binary mode: attribute map and body (CloudEvents.FromAttributes)"),
        ["cloudevents-structured"] = new(CloudEventsStructured, [typeof(FormatException)], "CloudEvents structured mode (CloudEvents.FromStructured)"),
        ["aws-wire"] = new(AwsWireDecode, [typeof(FormatException)], "SQS/SNS message attributes and body (AwsWire.Decode), and the v2 bridge"),
        ["gcp-wire"] = new(GcpWireDecode, [typeof(FormatException)], "Pub/Sub attributes and data (GcpWire.Decode)"),
        ["azure-wire"] = new(AzureWireDecode, [typeof(FormatException)], "Service Bus application properties and body (AzureWire.Decode)"),
        ["registry"] = new(RegistryLookup, [typeof(System.Text.Json.JsonException)], "Type lookup in the registry allow-list, then deserialization of the registered type"),
        ["protected-payload"] = new(ProtectedPayload, [typeof(DecryptionFailedException)], "The protected-payload decoder (PayloadProtector.UnprotectAsync)"),
        ["personal-data"] = new(PersonalData, [typeof(DecryptionFailedException), typeof(System.Text.Json.JsonException)], "Personal-data markers in stored JSON (PersonalDataProtector.UnprotectAsync)"),
        ["sfql"] = new(Sfql, [], "The SFQL request guard, lexer and parser (pipe syntax)"),
        ["sql"] = new(Sql, [], "The SFQL request guard, lexer and parser (SQL syntax)"),
    };

    /// <summary>Runs one input; rethrows only what the target's contract doesn't allow.</summary>
    public static void Execute(string target, ReadOnlySpan<byte> input)
    {
        FuzzTarget fuzz = All[target];
        try
        {
            fuzz.Run(input);
        }
        catch (Exception e) when (fuzz.Allowed.Any(a => a.IsInstanceOfType(e)))
        {
            // Rejected as the contract says.
        }
    }

    /// <summary>Valid inputs for each target, built with the real encoders, to start fuzzing from.</summary>
    public static IReadOnlyList<byte[]> Seeds(string target)
    {
        return target switch
        {
            "cloudevents-binary" or "aws-wire" or "gcp-wire" or "azure-wire" => [.. State.Value.Envelopes.Select(e => Lines(CloudEvents.ToAttributes(e), e.Body.Span)), .. V2Seeds()],
            "cloudevents-structured" => [.. State.Value.Envelopes.Select(CloudEvents.ToStructured)],
            "registry" => State.Value.RegistrySeeds,
            "protected-payload" => State.Value.ProtectedSeeds,
            "personal-data" => State.Value.PersonalSeeds,
            "sfql" => [.. Queries.Sfql.Select(Encoding.UTF8.GetBytes)],
            "sql" => [.. Queries.Sql.Select(Encoding.UTF8.GetBytes)],
            _ => throw new ArgumentException($"Unknown target '{target}'.", nameof(target)),
        };
    }

    /// <summary>Tokens worth inserting when mutating a target's inputs (a libFuzzer dictionary).</summary>
    public static IReadOnlyList<string> Dictionary(string target)
    {
        return target switch
        {
            "sfql" => ["from", "events", "readmodel", "|", "where", "select", "project", "sort", "by", "take", "skip", "summarize", "count()", "bin(", "ago(", "and", "or", "not", "==", "!=", "=~", "in", "between", "contains", "'", "\"", "(", ")", "[", "]", ",", "1d", "null", "true", "@p", "--", "/*", "*/", "​"],
            "sql" => ["SELECT", "FROM", "WHERE", "GROUP BY", "ORDER BY", "LIMIT", "OFFSET", "AND", "OR", "NOT", "IN", "LIKE", "BETWEEN", "IS NULL", "COUNT(*)", "'", "\"", "(", ")", ",", "*", "=", "<>", "@p", "--", "/*", "*/", ";"],
            "personal-data" or "registry" or "cloudevents-structured" => ["{", "}", "[", "]", "\"", ":", ",", "\"$pd\"", "\"v1:", "null", "true", "1e309", "-0", "\\u0000", "\\ud800"],
            "protected-payload" => [PayloadProtector.Scheme, ";", "local-master", "\n", "\u0001"],
            _ => ["=", "\n", "ce_", "ce_id", "ce_type", "ce_source", "ce_specversion", "ce_time", "ce_sfext", "sf_body", "base64", "empty", "CommandType", "EventType", "{", "}", "\"", "1.0", ".v1", "Notification"],
        };
    }

    // ================================================================ targets

    // Input: "name=value" lines, an empty line, then the body.
    private static void CloudEventsBinary(ReadOnlySpan<byte> input)
    {
        (Dictionary<string, string> attributes, byte[] body) = SplitLines(input);
        var stripped = attributes.Where(a => a.Key.StartsWith(AwsWire.CloudEventsPrefix, StringComparison.Ordinal))
            .ToDictionary(a => a.Key[AwsWire.CloudEventsPrefix.Length..], a => a.Value, StringComparer.Ordinal);
        foreach ((string key, string value) in attributes.Where(a => !a.Key.StartsWith(AwsWire.CloudEventsPrefix, StringComparison.Ordinal)))
        {
            stripped.TryAdd(key, value);
        }

        CloudEvents.FromAttributes(stripped, body);
    }

    private static void CloudEventsStructured(ReadOnlySpan<byte> input)
    {
        CloudEvents.FromStructured(input.ToArray());
    }

    private static void AwsWireDecode(ReadOnlySpan<byte> input)
    {
        (Dictionary<string, string> attributes, byte[] body) = SplitLines(input);
        string text = Encoding.UTF8.GetString(body);
        try
        {
            AwsWire.Decode(attributes, text);
        }
        catch (FormatException)
        {
            // Not a CloudEvent: the v2 bridge reads it next, and must reject garbage with false or JsonException only.
            if (AwsV2Bridge.TryRead(attributes, text, "fuzz-message", out V2Message? v2))
            {
                try
                {
                    State.Value.V2Map.Translate(v2, TimeProvider.System);
                }
                catch (System.Text.Json.JsonException)
                {
                    // A mapped type with a body that isn't JSON: the receiver leaves it for the redrive policy.
                }
            }

            throw;
        }
    }

    private static void GcpWireDecode(ReadOnlySpan<byte> input)
    {
        (Dictionary<string, string> attributes, byte[] body) = SplitLines(input);
        var message = new PubsubMessage { Data = ByteString.CopyFrom(body), MessageId = "fuzz" };
        foreach ((string key, string value) in attributes)
        {
            message.Attributes[key] = value;
        }

        GcpWire.Decode(message);
    }

    private static void AzureWireDecode(ReadOnlySpan<byte> input)
    {
        (Dictionary<string, string> attributes, byte[] body) = SplitLines(input);
        var properties = attributes.ToDictionary(a => a.Key, a => (object)a.Value, StringComparer.Ordinal);
        AzureWire.Decode(properties, BinaryData.FromBytes(body));
    }

    // Input: "{name}.v{version}" line, then the JSON body of that type.
    private static void RegistryLookup(ReadOnlySpan<byte> input)
    {
        int newline = input.IndexOf((byte)'\n');
        string type = Encoding.UTF8.GetString(newline < 0 ? input : input[..newline]);
        byte[] body = newline < 0 ? [] : input[(newline + 1)..].ToArray();
        if (!CloudEvents.TryParseType(type, out string name, out int version))
        {
            return;
        }

        SourceFlowRuntime runtime = State.Value.Runtime;
        if (runtime.Model.Registry.TryGet(name, version, out MessageTypeInfo? info))
        {
            runtime.Serializer.Deserialize(body, info);
        }
    }

    // Input: the sfenc header value, a newline, then the protected body.
    private static void ProtectedPayload(ReadOnlySpan<byte> input)
    {
        int newline = input.IndexOf((byte)'\n');
        if (newline < 0)
        {
            return;
        }

        MessageEnvelope envelope = State.Value.ProtectedTemplate with
        {
            Protection = Encoding.UTF8.GetString(input[..newline]),
            Body = input[(newline + 1)..].ToArray(),
        };
        State.Value.Payloads.UnprotectAsync(envelope, CancellationToken.None).AsTask().GetAwaiter().GetResult();
    }

    private static void PersonalData(ReadOnlySpan<byte> input)
    {
        State.Value.Personal.UnprotectAsync(input.ToArray(), CancellationToken.None).AsTask().GetAwaiter().GetResult();
    }

    private static void Sfql(ReadOnlySpan<byte> input)
    {
        SfqlParser.Parse(Encoding.UTF8.GetString(input), QueryLanguage.Sfql);
    }

    private static void Sql(ReadOnlySpan<byte> input)
    {
        SfqlParser.Parse(Encoding.UTF8.GetString(input), QueryLanguage.Sql);
    }

    // ================================================================ input formats

    private static (Dictionary<string, string> Attributes, byte[] Body) SplitLines(ReadOnlySpan<byte> input)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        while (true)
        {
            int newline = input.IndexOf((byte)'\n');
            if (newline <= 0)
            {
                return (attributes, (newline == 0 ? input[1..] : input).ToArray());
            }

            string line = Encoding.UTF8.GetString(input[..newline]);
            input = input[(newline + 1)..];
            int equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0)
            {
                attributes[line[..equals]] = line[(equals + 1)..];
            }
        }
    }

    private static byte[] Lines(IReadOnlyDictionary<string, string> attributes, ReadOnlySpan<byte> body)
    {
        var text = new StringBuilder();
        foreach ((string key, string value) in attributes)
        {
            text.Append(AwsWire.CloudEventsPrefix).Append(key).Append('=').Append(value.Replace('\n', ' ')).Append('\n');
        }

        text.Append('\n');
        return [.. Encoding.UTF8.GetBytes(text.ToString()), .. body];
    }

    private static IEnumerable<byte[]> V2Seeds()
    {
        yield return Encoding.UTF8.GetBytes("CommandType=MyApp.Commands.CreateAccount, MyApp, Version=1.0.0.0\nEntityId=7\nSequenceNo=1\n\n{\"name\":\"CreateAccount\",\"payload\":{\"id\":7,\"accountName\":\"Ada\"},\"entity\":{\"id\":7}}");
        yield return Encoding.UTF8.GetBytes("\n{\"Type\":\"Notification\",\"MessageId\":\"m-1\",\"Message\":\"{\\\"payload\\\":{\\\"id\\\":7}}\",\"MessageAttributes\":{\"EventType\":{\"Type\":\"String\",\"Value\":\"MyApp.Events.AccountUpdated, MyApp\"}}}");
    }

    private static class Queries
    {
        public static readonly string[] Sfql =
        [
            "from events | where type == 'bank.account.opened' | take 10",
            "from readmodel \"bank-account-summary\" | where balance > 100 and status in ('Open', 'Frozen') | sort by balance desc | take 5",
            "from events | where time > ago(1d) | summarize count() by bin(time, 1h)",
            "from readmodel \"bank-account-summary\" | project holder, balance | where holder contains 'a' // comment",
        ];

        public static readonly string[] Sql =
        [
            "SELECT * FROM events WHERE type = 'bank.account.opened' LIMIT 10",
            "SELECT holder, balance FROM \"bank-account-summary\" WHERE balance BETWEEN 1 AND 100 ORDER BY balance DESC",
            "SELECT COUNT(*) FROM events WHERE stream LIKE 'bank-account-%' GROUP BY type",
        ];
    }

    // ================================================================ shared state

    private sealed class Fixture
    {
        public Fixture()
        {
            var services = new ServiceCollection();
            Keys = new LocalKeyProvider();
            services.AddSourceFlow(sf =>
            {
                sf.ServiceName = "fuzz";
                sf.AddModule<ContractsModule>();
                sf.AddModule<AccountsModule>();
                sf.AddModule<TransfersModule>();
                sf.Security(s => s.UseKeyProvider(Keys).EncryptPersonalData());
                sf.UseInMemoryStorage();
            });
            Provider = services.BuildServiceProvider();
            Runtime = Provider.GetRequiredService<SourceFlowRuntime>();
            Personal = Provider.GetRequiredService<PersonalDataProtector>();
            Payloads = new PayloadProtector(Keys, new PayloadProtectionOptions().Protect("fuzz"));

            var account = AccountId.Parse("0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b");
            IMessage[] messages =
            [
                new OpenAccount(account, "Ada", 100m),
                new AccountOpened(account, "Ada", 100m, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
                new MoneyDeposited(account, 5m, 105m),
                new CloseAccount(account, "done"),
            ];
            Envelopes = [.. messages.Select(m => Runtime.CreateEnvelope(m, subject: "bank-account-x", partitionKey: "k") with { Actor = "user:1", Headers = new Dictionary<string, string> { ["h"] = "v" } })];
            RegistrySeeds = [.. Envelopes.Select(e => (byte[])[.. Encoding.UTF8.GetBytes(CloudEvents.FormatType(e.Type, e.TypeVersion) + "\n"), .. e.Body.Span])];

            MessageEnvelope protectedEnvelope = Payloads.ProtectAsync(Envelopes[1], CancellationToken.None).AsTask().GetAwaiter().GetResult();
            ProtectedTemplate = protectedEnvelope;
            ProtectedSeeds = [[.. Encoding.UTF8.GetBytes(protectedEnvelope.Protection + "\n"), .. protectedEnvelope.Body.Span]];

            (ReadOnlyMemory<byte> personal, _) = Personal.ProtectAsync(typeof(AccountOpened), "bank-account-x", Envelopes[1].Body, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            PersonalSeeds = [personal.ToArray(), Envelopes[1].Body.ToArray()];
            V2Map = new V2MessageMap().Type("MyApp.Commands.CreateAccount, MyApp", "bank.account.open", 1);
        }

        public LocalKeyProvider Keys { get; }

        public ServiceProvider Provider { get; }

        public SourceFlowRuntime Runtime { get; }

        public PersonalDataProtector Personal { get; }

        public PayloadProtector Payloads { get; }

        public MessageEnvelope[] Envelopes { get; }

        public byte[][] RegistrySeeds { get; }

        public MessageEnvelope ProtectedTemplate { get; }

        public byte[][] ProtectedSeeds { get; }

        public byte[][] PersonalSeeds { get; }

        public V2MessageMap V2Map { get; }
    }
}

/// <summary>A fuzz target.</summary>
/// <param name="Run">Parses one input.</param>
/// <param name="Allowed">The exceptions the parser's contract allows for malformed input.</param>
/// <param name="Description">What is fuzzed.</param>
public sealed record FuzzTarget(FuzzTarget.Parse Run, IReadOnlyList<Type> Allowed, string Description)
{
    /// <summary>Parses one input.</summary>
    public delegate void Parse(ReadOnlySpan<byte> input);
}
