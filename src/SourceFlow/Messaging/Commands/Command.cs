namespace SourceFlow.Messaging.Commands
{
    /// <summary>
    /// Base class for command in the command-driven architecture.
    /// </summary>
    public abstract class Command<TPayload> : ICommand
        where TPayload : class, IPayload, new()
    {
        /// <summary>
        /// Parameterless constructor for deserialization.
        /// </summary>
        protected Command()
        {
            Metadata = new Metadata();
            Name = GetType().Name;
            Payload = new TPayload();
            Entity = new EntityRef();
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Command{TPayload}"/> class with a new payload.
        /// </summary>
        /// <param name="payload"></param>
        public Command(int entityId, TPayload payload)
        {
            Metadata = new Metadata();
            Name = GetType().Name;
            Payload = payload;
            Entity = new EntityRef { Id = entityId };
        }

        /// <summary>
        /// Initializes a new instance of the Command class with the specified entity state and payload.
        /// </summary>
        /// <param name="newEntity">true to indicate that the associated entity is new or to be created; otherwise, false.</param>
        /// <param name="payload">The payload data to associate with the command.</param>
        public Command(bool newEntity, TPayload payload)
        {
            Metadata = new Metadata();
            Name = GetType().Name;
            Payload = payload;
            Entity = new EntityRef { Id = 0, IsNew = newEntity };
        }

        /// <summary>
        /// Initializes a new instance of the Command class with the specified entity ID, new entity state, and payload.
        /// </summary>
        /// <param name="entityId">The ID of the entity associated with the command.</param>
        /// <param name="newEntity">true to indicate that the associated entity is new or to be created; otherwise, false.</param>
        /// <param name="payload">The payload data to associate with the command.</param>
        public Command(int entityId, bool newEntity, TPayload payload)
        {
            Metadata = new Metadata();
            Name = GetType().Name;
            Payload = payload;
            Entity = new EntityRef { Id = entityId, IsNew = newEntity };
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
        /// Entity reference associated with the command.
        /// </summary>
        public EntityRef Entity { get; set; }

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
