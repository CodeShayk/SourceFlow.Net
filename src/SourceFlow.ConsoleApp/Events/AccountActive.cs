namespace SourceFlow.ConsoleApp.Events
{
    public class AccountActive : AccountEvent
    {
        public AccountActive(Guid aggregateId) : base(aggregateId)
        {
        }

        public DateTime DateOpened { get; set; } = DateTime.UtcNow;
    }
}