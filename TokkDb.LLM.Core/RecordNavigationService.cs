using Microsoft.Extensions.Logging;

namespace TokkDb.LLM.Core;

/// <summary>
/// Broker for record navigation requests.
///
/// Deliberately free of any UI type: it validates and publishes the request, and
/// subscribers decide what to do with it (switch page, select the record). That
/// keeps the chat decoupled from the Database page.
/// </summary>
public sealed class RecordNavigationService : IRecordNavigationService
{
    private readonly ILogger<RecordNavigationService> _logger;

    public RecordNavigationService(ILogger<RecordNavigationService> logger)
    {
        _logger = logger;
    }

    public event EventHandler<OpenRecordRequest>? RecordNavigationRequested;

    public void OpenRecord(OpenRecordRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.CollectionName) ||
            string.IsNullOrWhiteSpace(request.RecordId))
        {
            _logger.LogWarning(
                "Record navigation request ignored, incomplete. CollectionName: {CollectionName}, RecordId: {RecordId}",
                request.CollectionName,
                request.RecordId);
            return;
        }

        _logger.LogInformation(
            "Record navigation requested. CollectionName: {CollectionName}, RecordId: {RecordId}",
            request.CollectionName,
            request.RecordId);

        RecordNavigationRequested?.Invoke(this, request);
    }
}
