using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SourceFlow.Messaging.Commands;
using SourceFlow.Stores.EntityFramework.Models;


namespace SourceFlow.Stores.EntityFramework.Stores
{
    public class EfCommandStore : ICommandStore
    {
        private readonly CommandDbContext _context;

        public EfCommandStore(CommandDbContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public async Task Append(CommandData commandData)
        {
            if (commandData == null)
                throw new ArgumentNullException(nameof(commandData));

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
        }

        public async Task<IEnumerable<CommandData>> Load(int entityId)
        {
            var commandRecords = await _context.Commands
                .AsNoTracking()
                .Where(c => c.EntityId == entityId)
                .OrderBy(c => c.SequenceNo)
                .ToListAsync();

            return commandRecords.Select(record => new CommandData
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
        }
    }
}