using Bank.Accounts;
using SourceFlow.Messaging;
using SourceFlow.Runtime;

namespace SourceFlow.Core.Tests.Runtime;

/// <summary>GEN-76..77 (WP-C.17, CR-53, CR-56, CR-52): deterministic ids, UTF-8 id formatting and registration checks.</summary>
[Trait("Category", "Unit")]
public sealed class DeterminismTests
{
    private static readonly MessageId Command = new(Guid.Parse("0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b"));

    [Fact]
    [Trait("Id", "GEN-76")]
    [Trait("Covers", "ADR-003;CR-53;CR-56")]
    public void NewId_IsTheSameOnEveryReplay_DistinctPerPurpose_AndIdsFormatAsUtf8()
    {
        var decision = new DecisionContext(DateTimeOffset.UnixEpoch, StreamVersion.None, Command, CorrelationId.None, new Dictionary<string, string>());
        var again = decision with { Now = DateTimeOffset.UnixEpoch.AddHours(1) };

        AccountId child = decision.NewId<AccountId>("child");
        child.ShouldBe(again.NewId<AccountId>("child"), "a retried decision creates the same id");
        child.ShouldNotBe(decision.NewId<AccountId>("other"));
        decision.NewId<TagId>().IsDefault.ShouldBeFalse("string-backed ids are supported");
        Should.Throw<NotSupportedException>(() => decision.NewId<TicketNumber>()).Message.ShouldContain("TicketNumber");

        var reaction = new ReactionContext<AccountId>(child, DateTimeOffset.UnixEpoch, Command, IsNew: true);
        reaction.NewId<AccountId>("child").ShouldBe(child, "a reaction derives from the message id the same way");

        // IUtf8SpanFormattable: the UTF-8 form is the text form.
        Span<byte> buffer = stackalloc byte[64];
        ((IUtf8SpanFormattable)child).TryFormat(buffer, out int written, default, null).ShouldBeTrue();
        System.Text.Encoding.UTF8.GetString(buffer[..written]).ShouldBe(child.ToString());
        ((IUtf8SpanFormattable)TagId.Parse("tag_7")).TryFormat(buffer, out written, default, null).ShouldBeTrue();
        System.Text.Encoding.UTF8.GetString(buffer[..written]).ShouldBe("tag_7");
        ((IUtf8SpanFormattable)TicketNumber.Parse("42")).TryFormat(buffer, out written, default, null).ShouldBeTrue();
        System.Text.Encoding.UTF8.GetString(buffer[..written]).ShouldBe("42");
    }

    [Fact]
    [Trait("Id", "GEN-77")]
    [Trait("Covers", "ADR-005;CR-56")]
    public void AddMessage_RefusesANameOrVersionTheTypeDoesntDeclare()
    {
        var builder = new SourceFlowModelBuilder();
        builder.AddMessage<AccountOpened>("bank.account.opened", 1);   // as declared: fine

        Should.Throw<InvalidOperationException>(() => new SourceFlowModelBuilder().AddMessage<AccountOpened>("bank.account.created", 1))
            .Message.ShouldContain("declares [MessageType(\"bank.account.opened\", 1)]");
        Should.Throw<InvalidOperationException>(() => new SourceFlowModelBuilder().AddMessage<AccountOpened>("bank.account.opened", 2));
    }
}
