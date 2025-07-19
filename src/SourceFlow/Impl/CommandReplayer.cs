using System.Threading.Tasks;
using SourceFlow.Messaging.Bus;

namespace SourceFlow.Impl
{
    /// <summary>
    /// Interface for replaying commands in the event-driven architecture.
    /// </summary>
    internal class CommandReplayer : ICommandReplayer
    {
        /// <summary>
        /// The command bus used to replay commands for a given aggregate.
        /// </summary>
        private readonly ICommandBus commandBus;

        /// <summary>
        /// Initializes a new instance of the <see cref="CommandReplayer"/> class.
        /// </summary>
        /// <param name="commandBus"></param>
        public CommandReplayer(ICommandBus commandBus)
        {
            this.commandBus = commandBus;
        }

        /// <summary>
        /// Replays stream of commands for a given aggregate.
        /// </summary>
        /// <param name="aggregateId">Unique aggregate entity id.</param>
        /// <returns></returns>
        async Task ICommandReplayer.Replay(int aggregateId)
        {
            await commandBus.Replay(aggregateId);
        }
    }
}