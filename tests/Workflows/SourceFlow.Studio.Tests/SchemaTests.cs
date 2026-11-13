using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Json.Schema;
using SourceFlow.Studio.Dsl;
using SourceFlow.Studio.Dsl.Diagnostics;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace SourceFlow.Studio.Tests;

/// <summary>The normative schema <c>schemas/workflow-1.json</c> agrees with the reader on every golden file (plan 10 WP-S.3).</summary>
[Trait("Category", "Unit")]
public sealed partial class SchemaTests
{
    private static readonly Lazy<JsonSchema> Schema = new(() =>
        JsonSchema.FromText(File.ReadAllText(Path.Combine(Golden.Root, "..", "..", "schemas", "workflow-1.json"))));

    [Theory]
    [Trait("Id", "UT-WF-12")]
    [Trait("Covers", "WFD-20")]
    [MemberData(nameof(Cases))]
    public void Schema_AndReader_AgreeOnEveryGoldenFile(string @case)
    {
        string yaml = Golden.Workflow(@case);
        WorkflowAnalysisResult analysis = WorkflowAnalysis.Analyze(yaml, Golden.Environment);
        if (analysis.Diagnostics.Any(d => d.Id == WorkflowRules.Syntax))
        {
            return;   // not YAML, or anchors and aliases: a YAML-level rule the JSON form can't express
        }

        bool structural = analysis.Diagnostics.Any(d => d.Id.StartsWith("WF-S", StringComparison.Ordinal) || d.Id == WorkflowRules.NestedCompensation);
        EvaluationResults results = Schema.Value.Evaluate(ToJson(yaml), new EvaluationOptions { OutputFormat = OutputFormat.List });

        results.IsValid.ShouldBe(!structural, string.Join("; ", analysis.Diagnostics.Select(d => d.ToString())));
    }

    [Theory]
    [Trait("Id", "UT-WF-12")]
    [InlineData("PT30S", true)]
    [InlineData("PT1.5S", true)]
    [InlineData("P1DT12H", true)]
    [InlineData("P2W", true)]
    [InlineData("PT0S", true)]
    [InlineData("P1M", false)]
    [InlineData("PT", false)]
    [InlineData("P", false)]
    [InlineData("5m", false)]
    [InlineData("PT1.5M", false)]
    public void Durations_SchemaAndReaderAgree(string duration, bool valid)
    {
        Dsl.Model.IsoDuration.TryParse(duration, out _).ShouldBe(valid);
        Regex pattern = new(((JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(Golden.Root, "..", "..", "schemas", "workflow-1.json")))!["$defs"]!["duration"]!)["pattern"]!.GetValue<string>());
        pattern.IsMatch(duration).ShouldBe(valid);
    }

    public static TheoryData<string> Cases()
    {
        return Golden.Cases();
    }

    // YAML to JSON with the reader's scalar rules (YAML 1.2 core schema; quoted scalars are strings).
    internal static JsonNode? ToJson(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        return Convert(stream.Documents[0].RootNode);
    }

    private static JsonNode? Convert(YamlNode node)
    {
        switch (node)
        {
            case YamlMappingNode mapping:
                var obj = new JsonObject();
                foreach (KeyValuePair<YamlNode, YamlNode> pair in mapping.Children)
                {
                    obj[((YamlScalarNode)pair.Key).Value!] = Convert(pair.Value);
                }

                return obj;
            case YamlSequenceNode sequence:
                var array = new JsonArray();
                foreach (YamlNode child in sequence.Children)
                {
                    array.Add(Convert(child));
                }

                return array;
            case YamlScalarNode scalar:
                string text = scalar.Value ?? string.Empty;
                if (scalar.Style is ScalarStyle.SingleQuoted or ScalarStyle.DoubleQuoted or ScalarStyle.Literal or ScalarStyle.Folded)
                {
                    return JsonValue.Create(text);
                }

                if (text is "" or "~" or "null")
                {
                    return null;
                }

                if (text is "true" or "false")
                {
                    return JsonValue.Create(text == "true");
                }

                if (IntegerPattern().IsMatch(text))
                {
                    return JsonValue.Create(long.Parse(text, CultureInfo.InvariantCulture));
                }

                if (FloatPattern().IsMatch(text))
                {
                    return JsonValue.Create(decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture));
                }

                return JsonValue.Create(text);
            default:
                throw new NotSupportedException(node.GetType().Name);
        }
    }

    [GeneratedRegex("^[-+]?[0-9]+$")]
    private static partial Regex IntegerPattern();

    [GeneratedRegex("^[-+]?(\\.[0-9]+|[0-9]+(\\.[0-9]*)?)([eE][-+]?[0-9]+)?$")]
    private static partial Regex FloatPattern();
}
