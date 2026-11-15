using Bank.Accounts;
using SourceFlow.Runtime;

namespace SourceFlow.Core.Tests;

/// <summary>The Bank sample's runtime model, built once per test run.</summary>
internal static class BankModel
{
    private static readonly Lazy<RuntimeModel> Model = new(() => new SourceFlowModelBuilder().AddModule<AccountsModule>().Build());

    public static RuntimeModel Instance => Model.Value;

    public static readonly AccountId Account = AccountId.Parse("0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b");
}
