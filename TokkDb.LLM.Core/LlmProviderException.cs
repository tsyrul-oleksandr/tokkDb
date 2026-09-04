namespace TokkDb.LLM.Core;

public sealed class LlmProviderException : Exception
{
    public LlmProviderException(string message, int? statusCode = null, string? responseBody = null, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    public int? StatusCode { get; }

    public string? ResponseBody { get; }
}
