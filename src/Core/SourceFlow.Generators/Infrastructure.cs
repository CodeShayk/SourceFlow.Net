using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace System.Runtime.CompilerServices
{
    // Polyfill so records and init accessors compile on netstandard2.0.
    internal static class IsExternalInit
    {
    }
}

namespace SourceFlow.Generators
{
    /// <summary>An immutable array with value equality, so incremental pipeline steps cache correctly (GEN-38).</summary>
    internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IEnumerable<T>
        where T : IEquatable<T>
    {
        private readonly T[]? _items;

        public EquatableArray(IEnumerable<T> items)
        {
            _items = items.ToArray();
        }

        public static EquatableArray<T> Empty => new(Array.Empty<T>());

        public int Count => _items?.Length ?? 0;

        public T this[int index] => _items![index];

        public bool Equals(EquatableArray<T> other)
        {
            T[] left = _items ?? Array.Empty<T>();
            T[] right = other._items ?? Array.Empty<T>();
            if (left.Length != right.Length)
            {
                return false;
            }

            for (int i = 0; i < left.Length; i++)
            {
                if (!left[i].Equals(right[i]))
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object? obj)
        {
            return obj is EquatableArray<T> other && Equals(other);
        }

        public override int GetHashCode()
        {
            int hash = 17;
            foreach (T item in _items ?? Array.Empty<T>())
            {
                hash = (hash * 31) + item.GetHashCode();
            }

            return hash;
        }

        public IEnumerator<T> GetEnumerator()
        {
            return ((IEnumerable<T>)(_items ?? Array.Empty<T>())).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }

    internal static class EquatableArrayExtensions
    {
        public static EquatableArray<T> ToEquatableArray<T>(this IEnumerable<T> items)
            where T : IEquatable<T>
        {
            return new EquatableArray<T>(items);
        }
    }

    /// <summary>Well-known metadata names of the SourceFlow contracts the generator looks for.</summary>
    internal static class KnownNames
    {
        public const string MessageTypeAttribute = "SourceFlow.MessageTypeAttribute";
        public const string AggregateAttribute = "SourceFlow.AggregateAttribute";
        public const string ProjectionAttribute = "SourceFlow.ProjectionAttribute";
        public const string ProcessManagerAttribute = "SourceFlow.ProcessManagerAttribute";
        public const string ModuleAttribute = "SourceFlow.SourceFlowModuleAttribute";
        public const string StronglyTypedIdAttribute = "SourceFlow.StronglyTypedIdAttribute";
        public const string AggregateIdAttribute = "SourceFlow.AggregateIdAttribute";
        public const string Message = "SourceFlow.IMessage";
        public const string Command = "SourceFlow.ICommand";
        public const string CommandOf = "SourceFlow.ICommand`1";
        public const string Event = "SourceFlow.IEvent";
        public const string IntegrationEvent = "SourceFlow.IIntegrationEvent";
        public const string Timeout = "SourceFlow.ITimeout";
        public const string Aggregate = "SourceFlow.IAggregate`2";
        public const string ProcessManager = "SourceFlow.IProcessManager`2";
        public const string DocumentProjection = "SourceFlow.IDocumentProjection`2";
        public const string SessionProjection = "SourceFlow.ISessionProjection`1";
        public const string CancellationToken = "System.Threading.CancellationToken";
        public const string Task = "System.Threading.Tasks.Task";
        public const string ValueTask = "System.Threading.Tasks.ValueTask";
        public const string CommandHandler = "SourceFlow.ICommandHandler`2";
        public const string EventHandler = "SourceFlow.IEventHandler`1";
        public const string Upcaster = "SourceFlow.IUpcaster`2";
        public const string StronglyTypedId = "SourceFlow.IStronglyTypedId`1";
        public const string Decision = "SourceFlow.Decision";
        public const string DecisionContext = "SourceFlow.DecisionContext";
        public const string Reaction = "SourceFlow.Reaction";
        public const string ReactionContext = "SourceFlow.ReactionContext`1";
        public const string EventContext = "SourceFlow.EventContext";
    }

    internal static class SymbolExtensions
    {
        public static readonly SymbolDisplayFormat FullyQualified = SymbolDisplayFormat.FullyQualifiedFormat
            .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

        public static string FullName(this ITypeSymbol symbol)
        {
            return symbol.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString(FullyQualified);
        }

        public static string Namespace(this INamedTypeSymbol symbol)
        {
            return symbol.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : string.Empty;
        }

        public static bool Is(this ITypeSymbol? symbol, string metadataName)
        {
            return symbol is not null && MetadataName(symbol) == metadataName;
        }

        public static string MetadataName(ITypeSymbol symbol)
        {
            ITypeSymbol original = symbol.OriginalDefinition;
            string ns = original.ContainingNamespace is { IsGlobalNamespace: false } n ? n.ToDisplayString() + "." : string.Empty;
            return ns + original.MetadataName;
        }

        public static INamedTypeSymbol? FindInterface(this ITypeSymbol symbol, string metadataName)
        {
            foreach (INamedTypeSymbol candidate in symbol.AllInterfaces)
            {
                if (MetadataName(candidate) == metadataName)
                {
                    return candidate;
                }
            }

            return null;
        }

        public static bool Implements(this ITypeSymbol symbol, string metadataName)
        {
            return symbol.FindInterface(metadataName) is not null || MetadataName(symbol) == metadataName;
        }

        public static AttributeData? FindAttribute(this ISymbol symbol, string metadataName)
        {
            foreach (AttributeData attribute in symbol.GetAttributes())
            {
                if (attribute.AttributeClass is { } cls && MetadataName(cls) == metadataName)
                {
                    return attribute;
                }
            }

            return null;
        }

        public static T Named<T>(this AttributeData attribute, string name, T fallback)
        {
            foreach (KeyValuePair<string, TypedConstant> pair in attribute.NamedArguments)
            {
                if (pair.Key == name && pair.Value.Value is T value)
                {
                    return value;
                }
            }

            return fallback;
        }

        public static bool IsNested(this INamedTypeSymbol symbol)
        {
            return symbol.ContainingType is not null;
        }

        public static string Keyword(this INamedTypeSymbol symbol)
        {
            if (symbol.IsRecord)
            {
                return symbol.IsValueType ? "record struct" : "record";
            }

            return symbol.IsValueType ? "struct" : "class";
        }

        public static bool IsPartial(this INamedTypeSymbol symbol)
        {
            foreach (SyntaxReference reference in symbol.DeclaringSyntaxReferences)
            {
                if (reference.GetSyntax() is Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax declaration
                    && declaration.Modifiers.Any(m => m.Text == "partial"))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Builds generated source text with consistent headers and namespace blocks.</summary>
    internal sealed class SourceWriter
    {
        private readonly StringBuilder _builder = new();
        private int _indent;

        public SourceWriter()
        {
            Line("// <auto-generated/>");
            Line("// Generated by SourceFlow.Generators. Do not edit.");
            Line("#nullable enable");
            Line("#pragma warning disable CS1591 // generated members carry no XML docs");
            Line();
        }

        public SourceWriter Line(string text = "")
        {
            if (text.Length == 0)
            {
                _builder.Append('\n');
                return this;
            }

            _builder.Append(' ', _indent * 4).Append(text).Append('\n');
            return this;
        }

        public SourceWriter Open(string text)
        {
            Line(text);
            Line("{");
            _indent++;
            return this;
        }

        public SourceWriter Close(string suffix = "")
        {
            _indent--;
            Line("}" + suffix);
            return this;
        }

        public void OpenNamespace(string ns)
        {
            if (ns.Length > 0)
            {
                Open("namespace " + ns);
            }
        }

        public void CloseNamespace(string ns)
        {
            if (ns.Length > 0)
            {
                Close();
            }
        }

        public override string ToString()
        {
            return _builder.ToString();
        }
    }

    internal static class Literal
    {
        public static string Of(string value)
        {
            var builder = new StringBuilder(value.Length + 2).Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    default: builder.Append(c); break;
                }
            }

            return builder.Append('"').ToString();
        }

        public static string HintName(string fullName, string suffix)
        {
            var builder = new StringBuilder();
            foreach (char c in fullName.Replace("global::", string.Empty))
            {
                builder.Append(char.IsLetterOrDigit(c) || c == '.' ? c : '_');
            }

            return builder.Append('.').Append(suffix).Append(".g.cs").ToString();
        }

        public static ImmutableArray<T> OrEmpty<T>(this ImmutableArray<T> array)
        {
            return array.IsDefault ? ImmutableArray<T>.Empty : array;
        }
    }
}
