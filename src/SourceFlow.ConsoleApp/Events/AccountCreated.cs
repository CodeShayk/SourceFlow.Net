namespace SourceFlow.ConsoleApp.Events
{
    public class AccountCreated : AccountEvent
    {
        public AccountCreated(Source source) : base(source)
        {
        }

        public string AccountName { get; set; } = string.Empty;
        public decimal InitialBalance { get; set; }
    }
}