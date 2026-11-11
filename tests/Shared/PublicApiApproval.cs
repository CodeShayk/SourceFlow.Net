using System.Reflection;
using System.Runtime.CompilerServices;
using PublicApiGenerator;
using SourceFlow.Testing;

namespace SourceFlow.Tests.Shared;

/// <summary>
/// Public API approval (plan 04 §6): renders an assembly's public surface with PublicApiGenerator and compares it
/// with an approved snapshot. Linked into category test projects that set <c>IncludePublicApiApproval=true</c>.
/// </summary>
internal static class PublicApiApproval
{
    public static void AssertApproved(Assembly assembly, [CallerFilePath] string callerFilePath = "")
    {
        var options = new ApiGeneratorOptions
        {
            ExcludeAttributes =
            [
                "System.Runtime.CompilerServices.InternalsVisibleToAttribute",
                "System.Reflection.AssemblyMetadataAttribute",
                "System.Runtime.Versioning.TargetFrameworkAttribute",
            ],
        };

        string api = assembly.GeneratePublicApi(options);
        Approvals.AssertMatches(api, assembly.GetName().Name + ".PublicApi", callerFilePath);
    }
}
