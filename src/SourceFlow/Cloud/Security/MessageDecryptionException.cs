using System;

namespace SourceFlow.Cloud.Security;

public class MessageDecryptionException : Exception
{
    public MessageDecryptionException(string message, Exception innerException)
        : base(message, innerException) { }
}
