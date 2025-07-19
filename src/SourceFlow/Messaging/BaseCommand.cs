using System;

namespace SourceFlow.Messaging
{
    /// <summary>
    /// Base class for command in the command-driven architecture.
    /// </summary>
    public class BaseCommand<TPayload> : ICommand<TPayload>
        where TPayload : class, IPayload, new()
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="BaseCommand{TPayload}"/> class with a new payload.
        /// </summary>
        /// <param name="payload"></param>
        public BaseCommand(TPayload payload)
        {
            EventId = Guid.NewGuid();
            OccurredOn = DateTime.UtcNow;
            Payload = payload;
        }

        /// <summary>
        /// Unique identifier for the command.
        /// </summary>
        public Guid EventId { get; }

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

        /// <summary>
        /// Payload of the command, containing the data associated with the command.
        /// </summary>
        IPayload ICommand.Payload
        {
            get { return Payload; }
            set
            {
                Payload = (TPayload)value;
            }
        }
    }
}