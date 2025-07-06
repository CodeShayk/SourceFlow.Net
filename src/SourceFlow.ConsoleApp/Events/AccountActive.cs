namespace SourceFlow.ConsoleApp.Events
{
    public class AccountActive : AccountEvent
    {
        public AccountActive(Source source) : base(source)
        {
        }

        public DateTime DateOpened { get; set; } = DateTime.UtcNow;
    }
}