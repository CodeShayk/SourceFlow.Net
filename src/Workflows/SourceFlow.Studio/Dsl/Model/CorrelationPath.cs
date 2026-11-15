using System;
using System.Collections.Generic;

namespace SourceFlow.Studio.Dsl.Model
{
    /// <summary>
    /// The one reading of <c>start.correlate</c> (doc 15 §5.4, ADR-024 §3): a path into the start message's data, such as
    /// <c>data.orderId</c> or <c>data.customer.id</c>. The reader, the validator, the simulator, the path explorer, the
    /// spec generator and the source generator all use it.
    /// </summary>
    /// <remarks>
    /// The start message is read along the whole path, so nested paths work. Awaited events that the workflow didn't send a
    /// command for carry the instance key, the path's last segment, as a member of their own.
    /// </remarks>
    public static class CorrelationPath
    {
        /// <summary>The prefix every path starts with.</summary>
        public const string Prefix = "data.";

        /// <summary>True when <paramref name="path"/> is <c>data.</c> followed by one or more identifiers joined by dots.</summary>
        public static bool IsValid(string? path)
        {
            if (path is null || !path.StartsWith(Prefix, StringComparison.Ordinal) || path.Length == Prefix.Length)
            {
                return false;
            }

            foreach (string segment in path.Substring(Prefix.Length).Split('.'))
            {
                if (segment.Length == 0 || !(char.IsLetter(segment[0]) || segment[0] == '_'))
                {
                    return false;
                }

                foreach (char c in segment)
                {
                    if (!(char.IsLetterOrDigit(c) || c == '_') || c > 127)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>The path within the start message's data: <c>customer.id</c> for <c>data.customer.id</c>.</summary>
        public static string Field(string path)
        {
            if (path is null)
            {
                throw new ArgumentNullException(nameof(path));
            }

            return path.StartsWith(Prefix, StringComparison.Ordinal) ? path.Substring(Prefix.Length) : path;
        }

        /// <summary>The members along the path: <c>customer</c>, <c>id</c> for <c>data.customer.id</c>.</summary>
        public static IReadOnlyList<string> Segments(string path)
        {
            string field = Field(path);
            return field.Length == 0 ? Array.Empty<string>() : field.Split('.');
        }

        /// <summary>
        /// The instance key, the last segment: <c>id</c> for <c>data.customer.id</c>. An awaited event that isn't a reply
        /// correlates through its own member of that name.
        /// </summary>
        public static string InstanceKey(string path)
        {
            string field = Field(path);
            return field.Substring(field.LastIndexOf('.') + 1);
        }

        /// <summary>The value at the path in the start message's data (nested maps), or null.</summary>
        public static object? Read(IReadOnlyDictionary<string, object?> data, string path)
        {
            object? current = data;
            foreach (string segment in Segments(path))
            {
                if (current is IReadOnlyDictionary<string, object?> map && map.TryGetValue(segment, out object? next))
                {
                    current = next;
                }
                else if (current is IDictionary<string, object?> mutable && mutable.TryGetValue(segment, out object? next2))
                {
                    current = next2;
                }
                else
                {
                    return null;
                }
            }

            return current;
        }

        /// <summary>Start data holding only <paramref name="value"/> at the path, nested as the path says.</summary>
        public static Dictionary<string, object?> Data(string path, object? value)
        {
            var root = new Dictionary<string, object?>(StringComparer.Ordinal);
            Set(root, path, value);
            return root;
        }

        /// <summary>Sets <paramref name="value"/> at the path in <paramref name="data"/>, adding the nested maps it needs.</summary>
        public static void Set(IDictionary<string, object?> data, string path, object? value)
        {
            if (data is null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            IReadOnlyList<string> segments = Segments(path);
            IDictionary<string, object?> current = data;
            for (int i = 0; i < segments.Count - 1; i++)
            {
                if (!(current.TryGetValue(segments[i], out object? next) && next is IDictionary<string, object?> map))
                {
                    map = new Dictionary<string, object?>(StringComparer.Ordinal);
                    current[segments[i]] = map;
                }

                current = map;
            }

            if (segments.Count > 0)
            {
                current[segments[segments.Count - 1]] = value;
            }
        }
    }
}
