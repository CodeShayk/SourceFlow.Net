using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SourceFlow.Messaging;
using SourceFlow.Messaging.Commands;
using SourceFlow.Observability;
using SourceFlow.Performance;

namespace SourceFlow.Impl
{
    internal class CommandStoreAdapter : ICommandStoreAdapter
    {
        private readonly ICommandStore store;
        private readonly IDomainTelemetryService _telemetryService;

        public CommandStoreAdapter(ICommandStore store, IDomainTelemetryService telemetryService = null)
        {
            this.store = store;
            _telemetryService = telemetryService;
        }

        public async Task Append(ICommand command)
        {
            if (_telemetryService != null)
            {
                await _telemetryService.TraceAsync(
                    "sourceflow.domain.command.append",
                    async () =>
                    {
                        var commandData = SerializeCommand(command);
                        await store.Append(commandData);
                        _telemetryService.RecordCommandExecuted(command.GetType().Name, command.Entity.Id);
                    },
                    activity =>
                    {
                        activity?.SetTag("sourceflow.command_type", command.GetType().Name);
                        activity?.SetTag("sourceflow.entity_id", command.Entity.Id);
                        activity?.SetTag("sourceflow.sequence_no", command.Metadata.SequenceNo);
                    });
            }
            else
            {
                var commandData = SerializeCommand(command);
                await store.Append(commandData);
            }
        }

        public async Task<IEnumerable<ICommand>> Load(int entityId)
        {
            if (_telemetryService != null)
            {
                return await _telemetryService.TraceAsync(
                    "sourceflow.domain.command.load",
                    async () =>
                    {
                        var commandDataList = await store.Load(entityId);
                        var commands = new List<ICommand>();

                        foreach (var commandData in commandDataList)
                        {
                            try
                            {
                                var command = DeserializeCommand(commandData);
                                if (command != null)
                                    commands.Add(command);
                            }
                            catch
                            {
                                // Skip commands that can't be deserialized
                                continue;
                            }
                        }

                        return commands;
                    },
                    activity =>
                    {
                        activity?.SetTag("sourceflow.entity_id", entityId);
                    });
            }
            else
            {
                var commandDataList = await store.Load(entityId);
                var commands = new List<ICommand>();

                foreach (var commandData in commandDataList)
                {
                    try
                    {
                        var command = DeserializeCommand(commandData);
                        if (command != null)
                            commands.Add(command);
                    }
                    catch
                    {
                        // Skip commands that can't be deserialized
                        continue;
                    }
                }

                return commands;
            }
        }

        public async Task<int> GetNextSequenceNo(int entityId)
        {
            var events = await Load(entityId);

            if (events != null && events.Any())
                return events.Max<ICommand, int>(c => ((IMetadata)c).Metadata.SequenceNo) + 1;

            return 1;
        }

        private CommandData SerializeCommand(ICommand command)
        {
            if (_telemetryService != null)
            {
                return _telemetryService.TraceSerialization(
                    "serialize",
                    () =>
                    {
                        // Serialize using concrete type, not interface type, to capture all properties
                        // Use ByteArrayPool for optimized serialization with reduced allocations
                        var payloadJson = command.Payload != null
                            ? ByteArrayPool.Serialize(command.Payload, command.Payload.GetType())
                            : string.Empty;

                        return new CommandData
                        {
                            EntityId = command.Entity.Id,
                            SequenceNo = command.Metadata.SequenceNo,
                            CommandName = command.Name ?? string.Empty,
                            CommandType = command.GetType().AssemblyQualifiedName ?? string.Empty,
                            PayloadType = command.Payload?.GetType().AssemblyQualifiedName ?? string.Empty,
                            PayloadData = payloadJson,
                            Metadata = ByteArrayPool.Serialize(command.Metadata),
                            Timestamp = command.Metadata.OccurredOn
                        };
                    },
                    activity =>
                    {
                        activity?.SetTag("sourceflow.command_type", command.GetType().Name);
                    });
            }
            else
            {
                // Serialize using concrete type, not interface type, to capture all properties
                // Use ByteArrayPool for optimized serialization with reduced allocations
                var payloadJson = command.Payload != null
                    ? ByteArrayPool.Serialize(command.Payload, command.Payload.GetType())
                    : string.Empty;

                return new CommandData
                {
                    EntityId = command.Entity.Id,
                    SequenceNo = command.Metadata.SequenceNo,
                    CommandName = command.Name ?? string.Empty,
                    CommandType = command.GetType().AssemblyQualifiedName ?? string.Empty,
                    PayloadType = command.Payload?.GetType().AssemblyQualifiedName ?? string.Empty,
                    PayloadData = payloadJson,
                    Metadata = ByteArrayPool.Serialize(command.Metadata),
                    Timestamp = command.Metadata.OccurredOn
                };
            }
        }

        private ICommand DeserializeCommand(CommandData commandData)
        {
            // Use ByteArrayPool for optimized deserialization with reduced allocations
            var metadata = ByteArrayPool.Deserialize<Metadata>(commandData.Metadata);

            // Get the command type
            var commandType = Type.GetType(commandData.CommandType);
            if (commandType == null)
                return null;

            // Create an instance of the command
            var command = Activator.CreateInstance(commandType) as ICommand;
            if (command == null)
                return null;

            // Restore the metadata
            command.Metadata = metadata ?? new Metadata();

            // Restore the entity reference
            command.Entity = new EntityRef { Id = commandData.EntityId };

            // Deserialize and restore the payload if it exists
            if (!string.IsNullOrEmpty(commandData.PayloadType) && !string.IsNullOrEmpty(commandData.PayloadData))
            {
                var payloadType = Type.GetType(commandData.PayloadType);
                if (payloadType != null)
                {
                    var payload = ByteArrayPool.Deserialize(commandData.PayloadData, payloadType);

                    // Set the payload using reflection
                    var payloadProperty = commandType.GetProperty("Payload");
                    if (payloadProperty != null && payload != null)
                    {
                        payloadProperty.SetValue(command, payload);
                    }
                }
            }

            return command;
        }
    }
}
