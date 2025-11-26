using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SourceFlow.Messaging.Commands;
using SourceFlow.Stores.EntityFramework.Models;
using SourceFlow.Stores.EntityFramework.Services;


namespace SourceFlow.Stores.EntityFramework.Stores
{
    public class EfCommandStore : ICommandStore
    {
        private readonly CommandDbContext _context;
        private readonly IDatabaseResiliencePolicy _resiliencePolicy;
        private readonly IDatabaseTelemetryService _telemetryService;

        public EfCommandStore(
            CommandDbContext context,
            IDatabaseResiliencePolicy resiliencePolicy,
            IDatabaseTelemetryService telemetryService)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _resiliencePolicy = resiliencePolicy ?? throw new ArgumentNullException(nameof(resiliencePolicy));
            _telemetryService = telemetryService ?? throw new ArgumentNullException(nameof(telemetryService));
        }

        public async Task Append(CommandData commandData)
        {
            if (commandData == null)
                throw new ArgumentNullException(nameof(commandData));

            await _telemetryService.TraceAsync(
                "sourceflow.ef.command.append",
                async () =>
                {
                    await _resiliencePolicy.ExecuteAsync(async () =>
                    {
                        var commandRecord = new CommandRecord
                        {
                            EntityId = commandData.EntityId,
                            SequenceNo = commandData.SequenceNo,
                            CommandName = commandData.CommandName,
                            CommandType = commandData.CommandType,
                            PayloadType = commandData.PayloadType,
                            PayloadData = commandData.PayloadData,
                            Metadata = commandData.Metadata,
                            Timestamp = commandData.Timestamp,
                            CreatedAt = DateTime.UtcNow,
                            UpdatedAt = DateTime.UtcNow
                        };

                        _context.Commands.Add(commandRecord);
                        await _context.SaveChangesAsync();

                        // Clear change tracker to prevent caching issues
                        _context.ChangeTracker.Clear();
                    });

                    _telemetryService.RecordCommandAppended();
                },
                activity =>
                {
                    activity?.SetTag("sourceflow.entity_id", commandData.EntityId);
                    activity?.SetTag("sourceflow.sequence_no", commandData.SequenceNo);
                    activity?.SetTag("sourceflow.command_type", commandData.CommandName);
                });
        }

        public async Task<IEnumerable<CommandData>> Load(int entityId)
        {
            return await _telemetryService.TraceAsync(
                "sourceflow.ef.command.load",
                async () =>
                {
                    return await _resiliencePolicy.ExecuteAsync(async () =>
                    {
                        var commandRecords = await _context.Commands
                            .AsNoTracking()
                            .Where(c => c.EntityId == entityId)
                            .OrderBy(c => c.SequenceNo)
                            .ToListAsync();

                        var commands = commandRecords.Select(record => new CommandData
                        {
                            EntityId = record.EntityId,
                            SequenceNo = record.SequenceNo,
                            CommandName = record.CommandName,
                            CommandType = record.CommandType,
                            PayloadType = record.PayloadType,
                            PayloadData = record.PayloadData,
                            Metadata = record.Metadata,
                            Timestamp = record.Timestamp
                        }).ToList();

                        _telemetryService.RecordCommandsLoaded(commands.Count);

                        return commands;
                    });
                },
                activity =>
                {
                    activity?.SetTag("sourceflow.entity_id", entityId);
                });
        }
    }
}