using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaymentServices.EventNotification.Models;

namespace PaymentServices.EventNotification.Services;

public interface IRtpSendWebhookService
{
    /// <summary>
    /// Posts the event payload to ledgerWebhookFunctions POST endpoint.
    /// Retries with exponential backoff on failure.
    /// Throws if all retries exhausted.
    /// </summary>
    Task NotifyAsync(
        LedgerWebhookPayload payload,
        CancellationToken cancellationToken = default);
}

public sealed class RtpSendWebhookService : IRtpSendWebhookService
{
    private readonly HttpClient _httpClient;
    private readonly EventNotificationSettings _settings;
    private readonly ILogger<RtpSendWebhookService> _logger;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public RtpSendWebhookService(
        HttpClient httpClient,
        IOptions<EventNotificationSettings> settings,
        ILogger<RtpSendWebhookService> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _logger = logger;

        _httpClient.Timeout = TimeSpan.FromSeconds(
            _settings.RTP_SEND_WEBHOOK_TIMEOUT_SECONDS > 0
                ? _settings.RTP_SEND_WEBHOOK_TIMEOUT_SECONDS
                : 30);
    }

    public async Task NotifyAsync(
        LedgerWebhookPayload payload,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.RTP_SEND_WEBHOOK_URL))
            throw new InvalidOperationException("RTP_SEND_WEBHOOK_URL is not configured.");

        var maxRetries = _settings.RTP_SEND_WEBHOOK_MAX_RETRIES > 0
            ? _settings.RTP_SEND_WEBHOOK_MAX_RETRIES
            : 3;

        var body = JsonSerializer.Serialize(payload, _jsonOptions);
        Exception? lastException = null;

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                _logger.LogInformation(
                    "Calling ledgerWebhookFunctions. EvolveId={EvolveId} Event={Event} Attempt={Attempt}/{MaxRetries}",
                    payload.Metadata.EvolveId, payload.Event, attempt, maxRetries);

                using var request = new HttpRequestMessage(
                    HttpMethod.Post, _settings.RTP_SEND_WEBHOOK_URL);

                request.Content = new StringContent(body, Encoding.UTF8, "application/json");

                if (!string.IsNullOrWhiteSpace(_settings.RTP_SEND_WEBHOOK_API_KEY))
                {
                    request.Headers.Add(
                        _settings.RTP_SEND_WEBHOOK_API_KEY_HEADER,
                        _settings.RTP_SEND_WEBHOOK_API_KEY);
                }

                var response = await _httpClient.SendAsync(request, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation(
                        "ledgerWebhookFunctions succeeded. EvolveId={EvolveId} Event={Event} StatusCode={StatusCode}",
                        payload.Metadata.EvolveId, payload.Event, (int)response.StatusCode);
                    return;
                }

                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning(
                    "ledgerWebhookFunctions returned non-success. EvolveId={EvolveId} Event={Event} StatusCode={StatusCode} Body={Body} Attempt={Attempt}/{MaxRetries}",
                    payload.Metadata.EvolveId, payload.Event, (int)response.StatusCode,
                    responseBody, attempt, maxRetries);

                lastException = new HttpRequestException(
                    $"Webhook returned {(int)response.StatusCode}: {responseBody}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastException = ex;
                _logger.LogWarning(ex,
                    "ledgerWebhookFunctions call failed. EvolveId={EvolveId} Event={Event} Attempt={Attempt}/{MaxRetries}",
                    payload.Metadata.EvolveId, payload.Event, attempt, maxRetries);
            }

            if (attempt < maxRetries)
            {
                var delay = TimeSpan.FromMilliseconds(
                    200 * Math.Pow(2, attempt) * (0.5 + Random.Shared.NextDouble() * 0.5));

                _logger.LogInformation(
                    "Retrying webhook in {DelayMs}ms. EvolveId={EvolveId} Event={Event}",
                    delay.TotalMilliseconds, payload.Metadata.EvolveId, payload.Event);

                await Task.Delay(delay, cancellationToken);
            }
        }

        throw new InvalidOperationException(
            $"ledgerWebhookFunctions failed after {maxRetries} attempts. EvolveId={payload.Metadata.EvolveId} Event={payload.Event}",
            lastException);
    }
}
