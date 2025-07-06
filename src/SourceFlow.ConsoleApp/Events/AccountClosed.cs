namespace SourceFlow.ConsoleApp.Events
{
    public class AccountClosed : AccountEvent
    {
        public AccountClosed(Source source) : base(source)
        {
        }

        public string Reason { get; set; } = string.Empty;
    }
}