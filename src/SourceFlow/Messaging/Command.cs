namespace SourceFlow.Messaging
{
    /// <summary>
    /// Base class for command in the command-driven architecture.
    /// </summary>
    public abstract class Command<TPayload> : ICommand
        where TPayload : class, IPayload, new()
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="Command{TPayload}"/> class with a new payload.
        /// </summary>
        /// <param name="payload"></param>
        public Command(TPayload payload)
        {
            Metadata = new Metadata();
            Name = GetType().Name;
            Payload = payload;
        }

        /// <summary>
        /// Metadata associated with the command, which includes information such as event ID, occurrence time, and sequence number.
        /// </summary>
        public Metadata Metadata { get; set; } = new Metadata();

        /// <summary>
        /// Name of the command, typically the class name.
        /// </summary>
        public string Name { get; set; }

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