using SourceFlow.Studio.Dsl;
using SourceFlow.Studio.Dsl.Diagnostics;
using SourceFlow.Studio.Dsl.Expressions;
using SourceFlow.Studio.Dsl.Manifests;
using SourceFlow.Studio.Dsl.Model;
using SourceFlow.Studio.Dsl.Reading;
using SourceFlow.Studio.Dsl.Validation;

namespace SourceFlow.Studio.Tests;

/// <summary>UT-WF-01..40 (plan 10 §3.1): the DSL reader, guards and the validation rules WF-V01…V16.</summary>
[Trait("Category", "Unit")]
public sealed class WorkflowDslTests
{
    private static WorkflowDefinition Example => WorkflowReader.Read(Golden.Workflow("01-order-fulfilment")).Workflow!;

    // ================================================================ reader (UT-WF-01..11)

    [Fact]
    [Trait("Id", "UT-WF-01")]
    [Trait("Covers", "WFD-20")]
    public void Reader_ReadsEveryElementOfTheDocExample()
    {
        ReadResult read = WorkflowReader.Read(Golden.Workflow("01-order-fulfilment"));
        read.Diagnostics.ShouldBeEmpty();
        WorkflowDefinition w = read.Workflow!;

        (w.SchemaVersion, w.Name, w.Version, w.Owner, w.ProcessName).ShouldBe((1, "order-fulfilment", 1, "orders", "order-fulfilment@1"));
        w.Description.ShouldBe("Reserve stock, take payment, ship.");
        (w.Start.On, w.Start.Correlate, w.Start.When).ShouldBe(("orders.order-placed.v1", "data.orderId", null));
        w.Data.Select(d => (d.Name, d.From)).ShouldBe([("orderId", "start.data.orderId"), ("amount", "start.data.total"), ("lines", "start.data.lines")]);
        w.Steps.Select(s => s.Id).ShouldBe(["reserve-stock", "charge-payment", "review-large-order", "create-shipment", "await-address-fix"]);
        w.PivotIndex.ShouldBe(3);

        WorkflowStep reserve = w.Steps[0];
        reserve.Send!.Command.ShouldBe("inventory.reserve-stock.v1");
        reserve.Send.Map.Select(m => (m.Field, m.Value.Kind, m.Value.Text)).ShouldBe([("orderId", MapValueKind.Path, "$data.orderId"), ("lines", MapValueKind.Path, "$data.lines")]);
        reserve.Await!.Success.On.ShouldBe("inventory.stock-reserved.v1");
        reserve.Await.Failures.Single().Rejected.ShouldBe("inventory.out_of_stock");
        reserve.Await.Failures.Single().Then.Kind.ShouldBe(ThenKind.Fail);
        reserve.Timeout!.After.ShouldBe(TimeSpan.FromMinutes(5));
        reserve.Timeout.Retry!.Max.ShouldBe(2);
        reserve.Compensate!.Send!.Command.ShouldBe("inventory.release-stock.v1");
        reserve.Compensate.Retry!.Backoff.ShouldBe(Backoff.Exponential);
        reserve.Compensate.Retry.Cap.ShouldBe(TimeSpan.FromMinutes(10));
        reserve.Compensate.Timeout!.After.ShouldBe(TimeSpan.FromMinutes(15));

        WorkflowStep charge = w.Steps[1];
        charge.Await!.Success.Capture.Single().ShouldSatisfyAllConditions(c => c.Field.ShouldBe("paymentId"), c => c.Path.ShouldBe("event.data.paymentId"));
        charge.Await.Failures[0].Retry!.Initial.ShouldBe(TimeSpan.FromMinutes(10));
        charge.Await.Failures[1].Rejected.ShouldBe("payments.fraud_suspected");

        w.Steps[2].When.ShouldBe("$data.amount > 5000");
        w.Steps[2].Await!.Failures.Single().On.ShouldBe("orders.order-rejected-by-review.v1");
        w.Steps[2].Timeout!.Then.Kind.ShouldBe(ThenKind.Manual);
        w.Steps[3].Pivot.ShouldBeTrue();
        w.Steps[3].Await!.Failures.Single().Then.ShouldSatisfyAllConditions(t => t.Kind.ShouldBe(ThenKind.Goto), t => t.Target.ShouldBe("await-address-fix"));
        w.Steps[3].Next.ShouldBe(WorkflowStep.CompleteKeyword);
        w.Steps[4].Next.ShouldBe("create-shipment");
        w.On.Completed!.Command.ShouldBe("orders.mark-fulfilled.v1");
        w.On.Compensated!.Map.Single(m => m.Field == "reason").Value.Text.ShouldBe("$failure.code");
        w.On.ManualAlert.ShouldBeTrue();
    }

    [Theory]
    [Trait("Id", "UT-WF-02")]
    [InlineData("PT30S", 30)]
    [InlineData("PT5M", 300)]
    [InlineData("PT1H", 3600)]
    [InlineData("P1D", 86400)]
    [InlineData("P1DT2H3M4S", 93784)]
    [InlineData("P2W", 1209600)]
    [InlineData("PT0.5S", 0.5)]
    public void Durations_AreIso8601_AndRoundTrip(string text, double seconds)
    {
        IsoDuration.TryParse(text, out TimeSpan duration).ShouldBeTrue();
        duration.ShouldBe(TimeSpan.FromSeconds(seconds));
        IsoDuration.TryParse(IsoDuration.Format(duration), out TimeSpan again).ShouldBeTrue();
        again.ShouldBe(duration);
        IsoDuration.Format(TimeSpan.Zero).ShouldBe("PT0S");
        Should.Throw<ArgumentOutOfRangeException>(() => IsoDuration.Format(TimeSpan.FromSeconds(-1)));
    }

    [Theory]
    [Trait("Id", "UT-WF-02")]
    [InlineData("P1Y")]
    [InlineData("P1M")]
    [InlineData("PT")]
    [InlineData("PT1M2H")]
    [InlineData("P1.5D")]
    [InlineData("PTT1M")]
    [InlineData("1H")]
    [InlineData("")]
    [InlineData(null)]
    public void Durations_RejectUnsupportedForms(string? text)
    {
        IsoDuration.TryParse(text, out _).ShouldBeFalse();
    }

    [Fact]
    [Trait("Id", "UT-WF-03")]
    [Trait("Covers", "WFD-28")]
    public void Reader_KeepsSourcePositions()
    {
        WorkflowDefinition w = Example;

        w.At("owner").ShouldBe(new SourceLocation(4, 8));
        w.Start.At("on").ShouldBe(new SourceLocation(8, 7));
        w.Steps[1].Location.Line.ShouldBe(30);
        w.Steps[1].Send!.At("command").ShouldBe(new SourceLocation(31, 23));
        w.Steps[1].Send!.Map[1].At("value").Line.ShouldBe(31);
        w.Steps[3].At("pivot").ShouldBe(new SourceLocation(59, 12));
        w.Steps[3].At("missing").ShouldBe(w.Steps[3].Location);
        SourceLocation.None.Line.ShouldBe(0);
        new SourceLocation(2, 3).ToString().ShouldBe("(2,3)");
    }

    [Fact]
    [Trait("Id", "UT-WF-04")]
    [Trait("Covers", "WFD-20")]
    public void Reader_ReportsEveryUnknownKey_AndKeepsReading()
    {
        ReadResult read = WorkflowReader.Read(Golden.Workflow("04-s02-unknown-keys"));

        read.Diagnostics.Select(d => (d.Id, d.Location.Line)).ShouldBe([(WorkflowRules.UnknownKey, 5), (WorkflowRules.UnknownKey, 14)]);
        read.Diagnostics[1].Message.ShouldContain("'retries'");
        read.Workflow!.Steps.Single().Id.ShouldBe("approve");
        read.HasErrors.ShouldBeTrue();
    }

    [Theory]
    [Trait("Id", "UT-WF-05")]
    [InlineData(2, "Update SourceFlow Studio")]
    [InlineData(0, "version 1 is the first DSL version")]
    public void Reader_SchemaVersionMismatch_GivesAMigrationHint(int version, string hint)
    {
        ReadResult read = WorkflowReader.Read(Golden.Workflow("02-minimal").Replace("sourceflow.workflow: 1", "sourceflow.workflow: " + version, StringComparison.Ordinal));

        read.Diagnostics.Single().ShouldSatisfyAllConditions(d => d.Id.ShouldBe(WorkflowRules.SchemaVersion), d => d.Message.ShouldContain(hint));
    }

    [Fact]
    [Trait("Id", "UT-WF-06")]
    public void Reader_TypesScalars_AndMapValues()
    {
        const string Yaml = """
            sourceflow.workflow: 1
            name: typed
            version: 1
            owner: orders
            start: { on: orders.order-placed.v1, correlate: data.orderId }
            data:
              count: { type: integer, default: 3 }
              ratio: { type: number, default: 1.5 }
              flag: { type: boolean, default: true }
              label: { type: string, default: '42' }
              none: { type: string, default: ~ }
            steps:
              - id: one
                send:
                  command: orders.note-audit.v1
                  map: { note: "text", n: 7, d: -2.25, b: false, z: null, q: '$data.count', p: $data.count, h: { $hook: Compute } }
                await: { success: orders.audit-noted.v1 }
                timeout: { after: PT1M, then: manual }
            """;

        WorkflowDefinition w = WorkflowReader.Read(Yaml).Workflow!;

        w.Data.Select(d => d.Default!.Kind).ShouldBe([ScalarKind.Integer, ScalarKind.Number, ScalarKind.Boolean, ScalarKind.String, ScalarKind.Null]);
        w.Data[3].Default!.ToString().ShouldBe("'42'");
        w.Data[4].Default!.ToString().ShouldBe("null");
        IReadOnlyList<MapEntry> map = w.Steps[0].Send!.Map;
        map.Select(m => m.Value.Kind).ShouldBe([MapValueKind.Literal, MapValueKind.Literal, MapValueKind.Literal, MapValueKind.Literal, MapValueKind.Literal, MapValueKind.Literal, MapValueKind.Path, MapValueKind.Hook]);
        map.Take(6).Select(m => m.Value.Literal!.Kind).ShouldBe([ScalarKind.String, ScalarKind.Integer, ScalarKind.Number, ScalarKind.Boolean, ScalarKind.Null, ScalarKind.String]);
        map[2].Value.Literal!.Text.ShouldBe("-2.25");
        map[5].Value.Literal!.Text.ShouldBe("$data.count", "quoted: a literal, not a path");
        map[7].Value.Text.ShouldBe("Compute");
        map[5].Value.Literal!.ShouldBe(WorkflowReader.Read(Yaml).Workflow!.Steps[0].Send!.Map[5].Value.Literal, "values compare by kind and text");
        map[5].Value.Literal!.ShouldNotBe(map[0].Value.Literal);
    }

    [Fact]
    [Trait("Id", "UT-WF-07")]
    public void Reader_ReadsThenTimeoutAndCompensateForms()
    {
        WorkflowDefinition local = WorkflowReader.Read(Golden.Workflow("27-local-steps")).Workflow!;
        local.Steps[0].Timeout!.ShouldSatisfyAllConditions(t => t.None.ShouldBeTrue(), t => t.Reason.ShouldBe("the order service always answers"), t => t.After.ShouldBeNull());
        local.Steps[0].Compensate!.ShouldSatisfyAllConditions(c => c.None.ShouldBeTrue(), c => c.Reason.ShouldBe("a rejection is final"));
        local.Steps[1].Await!.Failures.Single().Then.Kind.ShouldBe(ThenKind.Complete);

        WorkflowDefinition retries = WorkflowReader.Read(Golden.Workflow("28-retries-and-backoff")).Workflow!;
        RetrySpec retry = retries.Steps[0].Timeout!.Retry!;
        (retry.Max, retry.Backoff, retry.Initial, retry.Cap, retry.Jitter).ShouldBe((20, Backoff.Exponential, TimeSpan.FromSeconds(1), (TimeSpan?)TimeSpan.FromHours(1), false));
        retry.Delay(1).ShouldBe(TimeSpan.FromSeconds(1));
        retry.Delay(4).ShouldBe(TimeSpan.FromSeconds(8));
        retry.Delay(20).ShouldBe(TimeSpan.FromHours(1), "capped");
        retries.Steps[0].Timeout!.Then.ShouldSatisfyAllConditions(t => t.Kind.ShouldBe(ThenKind.Goto), t => t.ToString().ShouldBe("goto approve"));

        ReadResult scalars = WorkflowReader.Read(Golden.Workflow("02-minimal").Replace("timeout: { after: PT1H, then: manual }", "timeout: none\n    compensate: none", StringComparison.Ordinal));
        scalars.Workflow!.Steps[0].Timeout!.None.ShouldBeTrue();
        scalars.Workflow.Steps[0].Compensate!.None.ShouldBeTrue();
    }

    [Theory]
    [Trait("Id", "UT-WF-08")]
    [InlineData("a: [1", 2)]
    [InlineData("- just\n- a list\n", 1)]
    [InlineData("sourceflow.workflow: 1\n---\nsourceflow.workflow: 1\n", 1)]
    [InlineData("sourceflow.workflow: 1\nname: x\nname: y\n", 3)]
    public void Reader_InvalidYaml_IsReportedWithAPosition(string yaml, int line)
    {
        ReadResult read = WorkflowReader.Read(yaml);

        read.Workflow.ShouldBeNull();
        read.Diagnostics.Single().ShouldSatisfyAllConditions(d => d.Id.ShouldBe(WorkflowRules.Syntax), d => d.Location.Line.ShouldBe(line));
        Should.Throw<ArgumentNullException>(() => WorkflowReader.Read(null!));
    }

    [Fact]
    [Trait("Id", "UT-WF-09")]
    public void Reader_RejectsAnchorsAndAliases()
    {
        ReadResult read = WorkflowReader.Read(Golden.Workflow("29-s01-anchors"));

        read.Diagnostics.Single().ShouldSatisfyAllConditions(d => d.Id.ShouldBe(WorkflowRules.Syntax), d => d.Message.ShouldContain("Anchors and aliases"));
        read.Workflow.ShouldNotBeNull();
    }

    [Theory]
    [Trait("Id", "UT-WF-10")]
    [InlineData("    await: { success: orders.order-approved.v1 }", "    await: { success: orders.order-approved.v1, failure: [ { rejected: x.y, on: orders.order-approved.v1 } ] }", "either 'rejected")]
    [InlineData("  orderId: { from: start.data.orderId }", "  orderId: { from: start.data.orderId, type: uuid }", "'type' and 'default' are for fields without one")]
    [InlineData("  orderId: { from: start.data.orderId }", "  orderId: { personal: true }", "needs 'from' or 'type'")]
    [InlineData("  orderId: { from: start.data.orderId }", "  orderId: { type: money }", "Unknown data type 'money'")]
    [InlineData("    timeout: { after: PT1H, then: manual }", "    timeout: { after: PT1H, none: true, then: manual }", "'after' or 'none: true', not both")]
    [InlineData("    timeout: { after: PT1H, then: manual }", "    timeout: { then: manual }", "needs 'after'")]
    [InlineData("    timeout: { after: PT1H, then: manual }", "    timeout: { after: PT1H, retry: { backoff: linear }, then: manual }", "needs 'max'")]
    [InlineData("    timeout: { after: PT1H, then: manual }", "    timeout: { after: PT1H, retry: { max: 1, backoff: linear }, then: manual }", "'fixed' or 'exponential'")]
    [InlineData("steps:\n", "steps: []\nignored:\n", "lists no step")]
    [InlineData("    send: { command: orders.approve-order.v1, map: { orderId: $data.orderId } }", "    send: { command: orders.approve-order.v1, map: { orderId: [1, 2] } }", "a path ($data.x")]
    public void Reader_ReportsShapeErrors(string find, string replace, string expected)
    {
        string yaml = Golden.Workflow("02-minimal").Replace(find, replace, StringComparison.Ordinal);
        if (find == "steps:\n")
        {
            yaml = yaml[..yaml.IndexOf("steps:", StringComparison.Ordinal)] + "steps: []\n";
        }

        WorkflowReader.Read(yaml).Diagnostics.ShouldContain(d => (d.Id == WorkflowRules.Shape || d.Id == WorkflowRules.UnknownKey) && d.Message.Contains(expected), string.Join("; ", WorkflowReader.Read(yaml).Diagnostics));
    }

    [Theory]
    [Trait("Id", "UT-WF-11")]
    [InlineData("  - id: approve", "  - id: complete", "'complete' is reserved")]
    [InlineData("correlate: data.orderId", "correlate: orderId", "path into the start message")]
    [InlineData("  orderId: { from: start.data.orderId }", "  orderId: { from: data.orderId }", "path into the start message")]
    [InlineData("  orderId: { from: start.data.orderId }", "  order-id: { from: start.data.orderId }", "is not an identifier")]
    [InlineData("orderId: $data.orderId", "orderId: $data.order-id", "is not a valid path")]
    [InlineData("orderId: $data.orderId", "orderId: { $hook: compute }", "PascalCase")]
    [InlineData("owner: orders", "owner: Orders", "is not a service name")]
    public void Reader_ReportsFormatErrors(string find, string replace, string expected)
    {
        WorkflowReader.Read(Golden.Workflow("02-minimal").Replace(find, replace, StringComparison.Ordinal)).Diagnostics
            .ShouldContain(d => d.Id == WorkflowRules.Format && d.Message.Contains(expected));
    }

    // ================================================================ guards (UT-WF-13..15)

    [Theory]
    [Trait("Id", "UT-WF-13")]
    [Trait("Covers", "WFD-26")]
    [InlineData("$data.amount > 5000")]
    [InlineData("(event.data.total >= 1.5 OR NOT $data.flag) and $data.name like 'a%'")]
    [InlineData("$data.code NOT IN ('x', 'y') AND EXISTS $data.reviewer")]
    [InlineData("-$data.amount + 2 * 3 / 4 % 5 < 0 or $data.name <> \"it's\"")]
    [InlineData("$data.name = 'it''s' AND $data.x != null AND TRUE")]
    public void Guards_Parse(string text)
    {
        GuardParseResult parsed = GuardParser.Parse(text);

        parsed.Error.ShouldBeNull();
        parsed.Expression.ShouldNotBeNull();
    }

    [Theory]
    [Trait("Id", "UT-WF-13")]
    [InlineData("$data.amount >", "ends early", 14)]
    [InlineData("$data.amount > 1)", "unexpected ')'", 16)]
    [InlineData("amount > 1", "unknown name 'amount'", 0)]
    [InlineData("$data > 1", "is not a path", 0)]
    [InlineData("$data.a = 'open", "unterminated string", 10)]
    [InlineData("$data.a NOT 1", "expected LIKE or IN", 12)]
    [InlineData("$data.a IN 1", "expected '('", 11)]
    [InlineData("$data.a # 1", "unexpected character '#'", 8)]
    [InlineData("EXISTS 1", "EXISTS needs a path", 7)]
    [InlineData("1.2.3 = 1", "is not a number", 0)]
    public void Guards_ReportParseErrors(string text, string expected, int position)
    {
        GuardParseResult parsed = GuardParser.Parse(text);

        parsed.Expression.ShouldBeNull();
        parsed.Error!.ShouldContain(expected);
        parsed.ErrorPosition.ShouldBe(position);
    }

    [Theory]
    [Trait("Id", "UT-WF-14")]
    [InlineData("$data.amount > 5000", null)]
    [InlineData("$data.id = 'abc' AND $data.at < '2026-01-01'", null)]
    [InlineData("$data.flag", null)]
    [InlineData("$data.amount", "not true or false")]
    [InlineData("$data.amount > 'big'", "compares a number with text")]
    [InlineData("$data.flag AND $data.amount", "'AND' needs true/false")]
    [InlineData("$data.name LIKE 1", "'LIKE' needs text")]
    [InlineData("$data.name + 1 > 2", "'+' needs a number")]
    [InlineData("$data.flag < true", "doesn't order true and false")]
    [InlineData("$data.items = $data.items", "can't compare array values")]
    [InlineData("$data.amount IN (1, 'x')", "compares a number with text")]
    [InlineData("NOT $data.amount", "'NOT' needs true/false")]
    [InlineData("-$data.name > 1", "'-' needs a number")]
    [InlineData("$data.missing = 1", "'$data.missing' is not in scope here ($data.*)")]
    public void Guards_BindAgainstTypes(string text, string? error)
    {
        var types = new Dictionary<string, string>
        {
            ["$data.amount"] = "number", ["$data.id"] = "uuid", ["$data.at"] = "date-time", ["$data.flag"] = "boolean",
            ["$data.name"] = "string", ["$data.items"] = "array",
        };

        string? result = GuardBinder.Bind(GuardParser.Parse(text).Expression!, p => types.GetValueOrDefault(p), "$data.*");

        if (error is null)
        {
            result.ShouldBeNull();
        }
        else
        {
            result.ShouldNotBeNull().ShouldContain(error);
        }
    }

    [Theory]
    [Trait("Id", "UT-WF-15")]
    [InlineData("$data.amount > 5000", true)]
    [InlineData("$data.amount > 9000", false)]
    [InlineData("$data.amount / 0 > 1", false)]
    [InlineData("$data.amount % 7 = 0 AND -$data.amount < 0", true)]
    [InlineData("$data.missing > 1", false)]
    [InlineData("$data.missing = null", true)]
    [InlineData("$data.missing != 1", false)]
    [InlineData("NOT ($data.missing > 1)", false)]
    [InlineData("NOT ($data.name = null) AND $data.missing = null AND ($data.missing != null) = false", true)]
    [InlineData("$data.missing > 1 OR $data.amount > 1", true)]
    [InlineData("$data.missing > 1 AND $data.amount > 1", false)]
    [InlineData("NOT ($data.missing > 1 OR $data.amount < 1)", false)]
    [InlineData("$data.missing IN (1)", false)]
    [InlineData("EXISTS $data.missing", false)]
    [InlineData("EXISTS $data.name", true)]
    [InlineData("$data.name LIKE 'Ad_%' AND $data.name NOT LIKE '%z'", true)]
    [InlineData("$data.name LIKE 'Ada' AND 'a%b' LIKE 'a\\%b'", true)]
    [InlineData("$data.code IN ('x', 'y') AND $data.code NOT IN ('z')", true)]
    [InlineData("$data.flag = true OR $data.flag = false", true)]
    [InlineData("$data.flag > false", true)]
    [InlineData("$data.count = 7 AND $data.ratio < 0.6 AND $data.id = '7d6f0f8a-1b2c-4d3e-8f9a-0b1c2d3e4f5a'", true)]
    [InlineData("$data.amount - 1 * 2 + 1 = 7000 - 1", true)]
    [InlineData("$data.name < $data.code", true)]
    [InlineData("$data.amount = 'x'", false)]
    public void Guards_Evaluate_WithCesqlNullSemantics(string text, bool expected)
    {
        var values = new Dictionary<string, object?>
        {
            ["$data.amount"] = 7000m, ["$data.name"] = "Ada", ["$data.code"] = "x", ["$data.flag"] = true, ["$data.count"] = 7,
            ["$data.ratio"] = 0.5, ["$data.id"] = Guid.Parse("7d6f0f8a-1b2c-4d3e-8f9a-0b1c2d3e4f5a"),
        };

        GuardEvaluator.Evaluate(GuardParser.Parse(text).Expression!, p => values.GetValueOrDefault(p)).ShouldBe(expected);
    }

    // ================================================================ rules (UT-WF-16..31: one negative and the positive per rule)

    [Theory]
    [Trait("Id", "UT-WF-16..31")]
    [Trait("Covers", "WFD-28")]
    [InlineData("WF-V01", "09-v01-unknown-type", 2)]
    [InlineData("WF-V02", "10-v02-command-owner", 3)]
    [InlineData("WF-V03", "11-v03-unreachable-event", 2)]
    [InlineData("WF-V04", "12-v04-correlation", 2)]
    [InlineData("WF-V05", "13-v05-unreachable-step", 1)]
    [InlineData("WF-V06", "15-v06-timeouts", 3)]
    [InlineData("WF-V07", "16-v07-pivot", 4)]
    [InlineData("WF-V08", "17-v08-rejections", 4)]
    [InlineData("WF-V09", "18-v09-compensation", 2)]
    [InlineData("WF-V10", "19-v10-retries", 4)]
    [InlineData("WF-V11", "20-v11-goto", 2)]
    [InlineData("WF-V12", "21-v12-map", 6)]
    [InlineData("WF-V13", "22-v13-guards", 3)]
    [InlineData("WF-V14", "23-v14-personal-data", 2)]
    [InlineData("WF-V15", "24-v15-published-version", 1)]
    [InlineData("WF-V16", "25-v16-nested-compensation", 2)]
    public void EveryRule_HasAPositiveAndANegativeCase(string rule, string negative, int count)
    {
        foreach (string positive in new[] { "01-order-fulfilment", "02-minimal", "26-guards-and-hooks", "27-local-steps", "28-retries-and-backoff" })
        {
            WorkflowAnalysis.Analyze(Golden.Workflow(positive), Golden.Environment).Diagnostics.ShouldNotContain(d => d.Id == rule, positive);
        }

        WorkflowAnalysisResult result = WorkflowAnalysis.Analyze(Golden.Workflow(negative), Golden.Environment);
        result.Diagnostics.Count(d => d.Id == rule).ShouldBe(count, string.Join("\n", result.Diagnostics));
        result.Diagnostics.Where(d => d.Id == rule).ShouldAllBe(d => d.Severity == WorkflowRules.SeverityOf(rule) && d.Location.Line > 0);
    }

    [Fact]
    [Trait("Id", "UT-WF-32")]
    public void PivotAndCompensation_Combined()
    {
        WorkflowAnalysisResult result = WorkflowAnalysis.Analyze(Golden.Workflow("16-v07-pivot"), Golden.Environment);

        result.Diagnostics.Where(d => d.Id == WorkflowRules.Pivot).Select(d => d.Message).ShouldBe(
        [
            "Step 'create-shipment' is at or after the pivot, so a failure can't 'fail' (no compensation after the point of no return); use manual, complete or goto.",
            "Step 'create-shipment' is at or after the pivot, so it can't be compensated; recover forward instead.",
            "Step 'await-address-fix' is at or after the pivot, so its timeout can't 'fail' (no compensation after the point of no return); use manual or goto.",
            "A workflow has one pivot; 'create-shipment' is already the pivot.",
        ]);
        result.Diagnostics.ShouldNotContain(d => d.Id == WorkflowRules.MissingCompensation, "steps after the pivot need no compensation");
    }

    [Fact]
    [Trait("Id", "UT-WF-33")]
    public void NestedGotoLoops_WithWaits_AreValid()
    {
        string yaml = Golden.Workflow("02-minimal").Replace("    timeout: { after: PT1H, then: manual }\n", """
                timeout: { after: PT1H, then: { goto: second } }
              - id: second
                send: { command: orders.approve-order.v1, map: { orderId: $data.orderId } }
                await:
                  success: orders.order-approved.v1
                  failure:
                    - on: orders.order-rejected-by-review.v1
                      then: { goto: approve }
                timeout: { after: PT1H, then: { goto: second } }
                next: complete

            """, StringComparison.Ordinal);

        WorkflowAnalysis.Analyze(yaml, Golden.Environment).Diagnostics.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Id", "UT-WF-34")]
    public void GuardBindingErrors_InEveryScope()
    {
        WorkflowAnalysisResult result = WorkflowAnalysis.Analyze(Golden.Workflow("22-v13-guards"), Golden.Environment);

        string[] messages = [.. result.Diagnostics.Where(d => d.Id == WorkflowRules.Guard).Select(d => d.Message)];
        messages[0].ShouldContain("doesn't parse");                              // start.when
        messages[1].ShouldContain("'event.data.sku' is not in scope");           // await.success.when
        messages[2].ShouldContain("compares a number with text");                // step.when
    }

    [Fact]
    [Trait("Id", "UT-WF-35")]
    [Trait("Covers", "WFD-29")]
    public void PartialMode_SkipsRulesNeedingUnknownServices()
    {
        // Compile time: only the owner's static manifest (no service name, no routes).
        ServiceManifest owner = ServiceManifest.Parse(File.ReadAllText(Path.Combine(Golden.Root, "domain", "orders.manifest.json"))
            .Replace("\"service\": \"orders\"", "\"assembly\": \"Orders\"", StringComparison.Ordinal)
            .Replace("\"endpoint\": \"orders\",", string.Empty, StringComparison.Ordinal));
        var environment = new WorkflowEnvironment([owner], compilingService: "orders", partial: true);

        WorkflowAnalysisResult result = WorkflowAnalysis.Analyze(Golden.Workflow("01-order-fulfilment"), environment);

        result.Diagnostics.ShouldBeEmpty(string.Join("\n", result.Diagnostics));
        WorkflowAnalysis.Analyze(Golden.Workflow("09-v01-unknown-type"), environment).Diagnostics.Count(d => d.Id == WorkflowRules.UnknownType).ShouldBe(2, "unknown types are still errors");
        WorkflowAnalysis.Analyze(Golden.Workflow("09-v01-unknown-type"), environment).Diagnostics.First(d => d.Id == WorkflowRules.UnknownType).Message.ShouldContain("contract package");
    }

    [Fact]
    [Trait("Id", "UT-WF-36")]
    public void Owner_MustBeTheCompilingService_AndAWorkspaceService()
    {
        var compiled = new WorkflowEnvironment(Golden.Environment.Manifests, compilingService: "payments");
        WorkflowAnalysis.Analyze(Golden.Workflow("02-minimal"), compiled).Diagnostics
            .ShouldContain(d => d.Id == WorkflowRules.CommandOwner && d.Message.Contains("compiled by 'payments'"));

        WorkflowAnalysis.Analyze(Golden.Workflow("02-minimal").Replace("owner: orders", "owner: billing", StringComparison.Ordinal), Golden.Environment).Diagnostics
            .ShouldContain(d => d.Id == WorkflowRules.CommandOwner && d.Message.Contains("'billing' is not a service of the workspace"));
    }

    [Fact]
    [Trait("Id", "UT-WF-37")]
    public void DuplicateNameAndVersion_InTheOwner_IsAnError()
    {
        WorkflowDefinition first = WorkflowReader.Read(Golden.Workflow("02-minimal")).Workflow!;
        WorkflowDefinition second = WorkflowReader.Read(Golden.Workflow("02-minimal").Replace("id: approve", "id: approve-again", StringComparison.Ordinal)).Workflow!;
        WorkflowDefinition other = WorkflowReader.Read(Golden.Workflow("02-minimal").Replace("version: 1", "version: 2", StringComparison.Ordinal)).Workflow!;
        var environment = new WorkflowEnvironment(Golden.Environment.Manifests, siblings: [first, second, other]);

        WorkflowValidator.Validate(first, environment).ShouldContain(d => d.Id == WorkflowRules.Version && d.Message == "approve-order@1 is declared twice in orders.");
        WorkflowValidator.Validate(other, environment).ShouldNotContain(d => d.Id == WorkflowRules.Version);
        Should.Throw<ArgumentNullException>(() => WorkflowValidator.Validate(null!, environment));
        Should.Throw<ArgumentNullException>(() => WorkflowValidator.Validate(first, null!));
    }

    [Fact]
    [Trait("Id", "UT-WF-38")]
    [Trait("Covers", "WFD-35")]
    public void Fingerprint_IgnoresLayoutAndComments_ButNotStructure()
    {
        string text = Golden.Workflow("01-order-fulfilment");
        string fingerprint = WorkflowFingerprint.Compute(WorkflowReader.Read(text).Workflow!);

        fingerprint.ShouldStartWith("sha256:");
        fingerprint.Length.ShouldBe(23);
        string relaid = text.Replace("# point of no return", "# the point of no return", StringComparison.Ordinal)
            .Replace("description: Reserve stock, take payment, ship.", "description: Something else.", StringComparison.Ordinal)
            .Replace("map: { orderId: $data.orderId, amount: $data.amount }", "map: { amount: $data.amount,   orderId: $data.orderId }", StringComparison.Ordinal);
        WorkflowFingerprint.Compute(WorkflowReader.Read(relaid).Workflow!).ShouldBe(fingerprint);
        WorkflowFingerprint.Compute(WorkflowReader.Read(text.Replace("after: PT2M", "after: PT3M", StringComparison.Ordinal)).Workflow!).ShouldNotBe(fingerprint);
        WorkflowFingerprint.Compute(WorkflowReader.Read(text.Replace("max: 3, backoff: fixed", "max: 4, backoff: fixed", StringComparison.Ordinal)).Workflow!).ShouldNotBe(fingerprint);

        // Published with this fingerprint: no WF-V15.
        WorkflowDefinition w = WorkflowReader.Read(Golden.Workflow("24-v15-published-version")).Workflow!;
        ServiceManifest orders = ServiceManifest.Parse(File.ReadAllText(Path.Combine(Golden.Root, "domain", "orders.manifest.json"))
            .Replace("sha256:0000000000000000", WorkflowFingerprint.Compute(w), StringComparison.Ordinal));
        var environment = new WorkflowEnvironment(Golden.Environment.Manifests.Where(m => m.Service != "orders").Append(orders));
        WorkflowValidator.Validate(w, environment).ShouldNotContain(d => d.Id == WorkflowRules.Version);
    }

    [Fact]
    [Trait("Id", "UT-WF-39")]
    public void RuleIds_AreStable_AndSeveritiesMatchTheDoc()
    {
        WorkflowRules.All.Count.ShouldBe(22);
        WorkflowRules.All.Distinct().Count().ShouldBe(22);
        WorkflowRules.All.Where(id => WorkflowRules.SeverityOf(id) == WorkflowSeverity.Warning).ShouldBe(["WF-V09", "WF-V14"]);
        Enumerable.Range(1, 16).Select(i => $"WF-V{i:00}").ShouldAllBe(id => WorkflowRules.All.Contains(id));

        var diagnostic = new WorkflowDiagnostic("WF-V06", WorkflowSeverity.Error, "message", new SourceLocation(3, 4));
        diagnostic.ToString().ShouldBe("(3,4): error WF-V06: message");
        diagnostic.ShouldBe(new WorkflowDiagnostic("WF-V06", WorkflowSeverity.Error, "message", new SourceLocation(3, 4)));
        new WorkflowDiagnostic("WF-V09", WorkflowSeverity.Warning, "m", SourceLocation.None).ToString().ShouldContain("warning");
    }

    [Fact]
    [Trait("Id", "UT-WF-40")]
    public void Analysis_SortsAndDeduplicates_AndStopsAtUnreadableYaml()
    {
        WorkflowAnalysisResult result = WorkflowAnalysis.Analyze(Golden.Workflow("21-v12-map"), Golden.Environment);

        result.Diagnostics.Select(d => (d.Location.Line, d.Location.Column)).ShouldBeInOrder();
        result.Diagnostics.Distinct().Count().ShouldBe(result.Diagnostics.Count);
        result.HasErrors.ShouldBeTrue();

        WorkflowAnalysisResult broken = WorkflowAnalysis.Analyze("a: [", Golden.Environment);
        broken.Workflow.ShouldBeNull();
        broken.Diagnostics.Single().Id.ShouldBe(WorkflowRules.Syntax);
        WorkflowAnalysis.Analyze(Golden.Workflow("01-order-fulfilment"), Golden.Environment).HasErrors.ShouldBeFalse();
        Should.Throw<ArgumentNullException>(() => WorkflowAnalysis.Analyze("x: 1", null!));
    }
}
