using System;

namespace SourceFlow.Messaging.Commands
{
    /// <summary>
    /// Data transfer object representing a serialized command for storage.
    /// </summary>
    public class CommandData
    {
        public int EntityId { get; set; }
        public int SequenceNo { get; set; }
        public string CommandName { get; set; } = string.Empty;
        public string CommandType { get; set; } = string.Empty;
        public string PayloadType { get; set; } = string.Empty;
        public string PayloadData { get; set; } = string.Empty;
        public string Metadata { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
    }
}
