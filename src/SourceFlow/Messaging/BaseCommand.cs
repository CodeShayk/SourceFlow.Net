using System;

namespace SourceFlow.Messaging
{
    /// <summary>
    /// Base class for command in the command-driven architecture.
    /// </summary>
    public class BaseCommand<TPayload> : ICommand<TPayload> where TPayload : class, IPayload, new()
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="BaseEvent"/> class with a specified aggregate id.
        /// </summary>
        public BaseCommand()
        {
            EventId = Guid.NewGuid();
            OccurredOn = DateTime.UtcNow;
            Payload = new TPayload();
        }

        /// <summary>
        /// Unique identifier for the command.
        /// </summary>
        public Guid EventId { get; }

        /// <summary>
        /// Entity entity of the command, indicating where it originated from.
        /// </summary>
        public Source Entity { get; set; }

        /// <summary>
        /// Indicates whether the command is a replay of an existing command.
        /// </summary>
        public DateTime OccurredOn { get; }

        /// <summary>
        /// Indicates whether the command is a replay of an existing command.
        /// </summary>
        bool ICommand.IsReplay { get; set; }

        /// <summary>
        /// Sequence number of the command within the aggregate's command stream.
        /// </summary>
        public int SequenceNo { get; set; }

        /// <summary>
        /// Payload of the command, containing the data associated with the command.
        /// </summary>
        public TPayload Payload { get; set; }
    }
}