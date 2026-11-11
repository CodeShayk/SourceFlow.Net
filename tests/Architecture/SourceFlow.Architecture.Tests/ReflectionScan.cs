using Mono.Cecil;
using Mono.Cecil.Cil;

namespace SourceFlow.Architecture.Tests;

/// <summary>Finds calls to reflection-activation APIs in an assembly's IL (ARCH-05).</summary>
internal static class ReflectionScan
{
    private static readonly (string Type, string Method)[] Banned =
    [
        ("System.Activator", "CreateInstance"),
        ("System.Reflection.MethodInfo", "MakeGenericMethod"),
        ("System.Type", "MakeGenericType"),
        ("System.Type", "GetType"),
        ("System.Reflection.Assembly", "Load"),
        ("System.Reflection.Assembly", "LoadFrom"),
        ("System.Reflection.Assembly", "LoadFile"),
    ];

    public static IEnumerable<string> FindBannedCalls(string assemblyPath)
    {
        using AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(assemblyPath);
        foreach (TypeDefinition type in assembly.MainModule.GetTypes())
        {
            foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody))
            {
                foreach (Instruction instruction in method.Body.Instructions)
                {
                    if (instruction.Operand is not MethodReference called)
                    {
                        continue;
                    }

                    bool genericActivation = called.Name == "CreateInstance" && called is GenericInstanceMethod && !called.HasParameters;
                    bool parameterlessGetType = called.Name == "GetType" && !called.HasParameters; // object.GetType()
                    if (genericActivation || parameterlessGetType)
                    {
                        continue;
                    }

                    if (Banned.Any(b => b.Type == called.DeclaringType.FullName && b.Method == called.Name))
                    {
                        yield return $"{type.FullName}.{method.Name} calls {called.DeclaringType.Name}.{called.Name}";
                    }
                }
            }
        }
    }
}
