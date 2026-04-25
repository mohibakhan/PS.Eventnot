using PaymentServices.Shared.Models;

namespace PaymentServices.EventNotification.Models;

/// <summary>
/// EventNotification-specific settings bound from <c>app:AppSettings</c>.
/// </summary>
public sealed class EventNotificationSettings : AppSettings
{
    // -------------------------------------------------------------------------
    // RTPSend webhook
    // -------------------------------------------------------------------------

    /// <summary>RTPSend webhook URL — receives final payment state.</summary>
    public string RTP_SEND_WEBHOOK_URL { get; set; } = string.Empty;

    /// <summary>Optional API key header value for RTPSend webhook auth.</summary>
    public string RTP_SEND_WEBHOOK_API_KEY { get; set; } = string.Empty;

    /// <summary>Optional API key header name. Default: x-api-key.</summary>
    public string RTP_SEND_WEBHOOK_API_KEY_HEADER { get; set; } = "x-api-key";

    /// <summary>Timeout in seconds for webhook HTTP call. Default: 30.</summary>
    public int RTP_SEND_WEBHOOK_TIMEOUT_SECONDS { get; set; } = 30;

    /// <summary>Maximum retry attempts before dead lettering. Default: 3.</summary>
    public int RTP_SEND_WEBHOOK_MAX_RETRIES { get; set; } = 3;

    // -------------------------------------------------------------------------
    // Cosmos
    // -------------------------------------------------------------------------
    public string COSMOS_TRANSACTIONS_CONTAINER { get; set; } = "tchSendTransactions";

    // -------------------------------------------------------------------------
    // Service Bus
    // -------------------------------------------------------------------------
    public string SERVICE_BUS_NOTIFICATION_SUBSCRIPTION { get; set; } = "event-notification";
}
