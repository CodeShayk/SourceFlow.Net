using System;
using System.Security.Cryptography;
using System.Text;

namespace SourceFlow.Studio.Dsl.Simulation
{
    /// <summary>
    /// The deterministic message ids of workflow sends (doc 15 §5.3): UUIDv5 of
    /// <c>workflow/{process}/{instance}/{step}/{attempt}</c> (compensations use <c>compensate:{step}</c>) in the SourceFlow
    /// namespace, the same as <c>SourceFlow.Messaging.MessageIds.CreateVersion5</c>. A re-send of the same attempt
    /// therefore has the same id and is deduplicated by the owner's inbox.
    /// </summary>
    public static class WorkflowIds
    {
        /// <summary>The SourceFlow namespace UUID (<c>MessageIds.SourceFlowNamespace</c>).</summary>
        public static readonly Guid Namespace = new Guid("5f1c8e2a-6d3b-4c71-9a0e-7b2d4f6a8c13");

        /// <summary>The id of a step's command.</summary>
        public static Guid Send(string process, string instance, string step, int attempt)
        {
            return Version5(Namespace, Name(process, instance, step, attempt));
        }

        /// <summary>The id of a compensation's command.</summary>
        public static Guid Compensation(string process, string instance, string step, int attempt)
        {
            return Version5(Namespace, Name(process, instance, "compensate:" + step, attempt));
        }

        /// <summary>The id of a terminal action's command (<c>on.completed</c> / <c>on.compensated</c>).</summary>
        public static Guid Terminal(string process, string instance, string status)
        {
            return Version5(Namespace, Name(process, instance, "on:" + status, 1));
        }

        /// <summary>The UUIDv5 name for a workflow send.</summary>
        public static string Name(string process, string instance, string step, int attempt)
        {
            return "workflow/" + process + "/" + instance + "/" + step + "/" + attempt.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>An RFC 9562 version 5 UUID (SHA-1, name-based).</summary>
        public static Guid Version5(Guid namespaceId, string name)
        {
            byte[] ns = namespaceId.ToByteArray();
            SwapByteOrder(ns);   // to network order
            byte[] nameBytes = Encoding.UTF8.GetBytes(name);
            byte[] input = new byte[ns.Length + nameBytes.Length];
            Buffer.BlockCopy(ns, 0, input, 0, ns.Length);
            Buffer.BlockCopy(nameBytes, 0, input, ns.Length, nameBytes.Length);
            byte[] hash;
#pragma warning disable CA5350 // RFC 9562 version 5 is defined with SHA-1; it is not used for security here.
            using (SHA1 sha = SHA1.Create())
            {
                hash = sha.ComputeHash(input);
            }
#pragma warning restore CA5350

            byte[] uuid = new byte[16];
            Buffer.BlockCopy(hash, 0, uuid, 0, 16);
            uuid[6] = (byte)((uuid[6] & 0x0F) | 0x50);
            uuid[8] = (byte)((uuid[8] & 0x3F) | 0x80);
            SwapByteOrder(uuid);   // back to Guid's little-endian fields
            return new Guid(uuid);
        }

        private static void SwapByteOrder(byte[] guid)
        {
            Swap(guid, 0, 3);
            Swap(guid, 1, 2);
            Swap(guid, 4, 5);
            Swap(guid, 6, 7);
        }

        private static void Swap(byte[] bytes, int a, int b)
        {
            byte t = bytes[a];
            bytes[a] = bytes[b];
            bytes[b] = t;
        }
    }
}
