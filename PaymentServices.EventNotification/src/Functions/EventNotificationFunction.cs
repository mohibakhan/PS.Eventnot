using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using PaymentServices.EventNotification.Models;
using PaymentServices.EventNotification.Repositories;
using PaymentServices.EventNotification.Services;
using PaymentServices.Shared.Enums;
using PaymentServices.Shared.Infrastructure;
using PaymentServices.Shared.Messages;

namespace PaymentServices.EventNotification.Functions;

/// <summary>
/// Service Bus Trigger — subscribed to event-notification subscription.
///
/// Handles the following states:
///   TMS:      TmsCompleted, TmsComplianceAlert, TmsFailed
///   Terminal: TransferCompleted, TransferFailed
///   Failures: AccountResolutionFailed, KycFailed, KycManualReview
///
/// Maps each state to a valid ledgerWebhookFunctions event type:
///   tmscompleted, transactioncompleted, transactionfailed,
///   kycfailed, actionrequired, tmsactionrequired, accountactionrequired
///
/// If the state does not map to a valid event type the message is
/// completed silently — no webhook call is made.
/// </summary>
public sealed class EventNotificationFunction
{
    private readonly IRtpSendWebhookService _webhookService;
    private readonly ITransactionStateRepository _transactionStateRepository;
    private readonly ILogger<EventNotificationFunction> _logger;

    public EventNotificationFunction(
        IRtpSendWebhookService webhookService,
        ITransactionStateRepository transactionStateRepository,
        ILogger<EventNotificationFunction> logger)
    {
        _webhookService = webhookService;
        _transactionStateRepository = transactionStateRepository;
        _logger = logger;
    }

    [Function(nameof(EventNotificationFunction))]
    public async Task RunAsync(
        [ServiceBusTrigger(
            topicName: "%app:AppSettings:SERVICE_BUS_TOPIC%",
            subscriptionName: "%app:AppSettings:SERVICE_BUS_NOTIFICATION_SUBSCRIPTION%",
            Connection = "app:AppSettings:SERVICE_BUS_CONNSTRING")]
        ServiceBusReceivedMessage serviceBusMessage,
        ServiceBusMessageActions messageActions,
        CancellationToken cancellationToken)
    {
        PaymentMessage? message = null;

        try
        {
            message = ServiceBusPublisher.Deserialize(serviceBusMessage);

            _logger.LogInformation(
                "EventNotification received. EvolveId={EvolveId} State={State} CorrelationId={CorrelationId}",
                message.EvolveId, message.State, message.CorrelationId);

            // -------------------------------------------------------------------------
            // Map state to ledgerWebhookFunctions event type
            // Returns null if state is not in the valid event list
            // -------------------------------------------------------------------------
            var payload = LedgerWebhookPayloadMapper.FromMessage(message);

            if (payload is null)
            {
                _logger.LogInformation(
                    "State {State} does not map to a valid webhook event. EvolveId={EvolveId} — completing silently.",
                    message.State, message.EvolveId);

                await messageActions.CompleteMessageAsync(serviceBusMessage, cancellationToken);
                return;
            }

            _logger.LogInformation(
                "Sending event to ledgerWebhookFunctions. EvolveId={EvolveId} Event={Event}",
                message.EvolveId, payload.Event);

            // -------------------------------------------------------------------------
            // POST to ledgerWebhookFunctions with retry + exponential backoff
            // -------------------------------------------------------------------------
            await _webhookService.NotifyAsync(payload, cancellationToken);

            // -------------------------------------------------------------------------
            // Update Cosmos transaction state to NotificationSent
            // Only update for terminal states — TMS events are not terminal
            // -------------------------------------------------------------------------
            var isTerminalState = message.State is
                TransactionState.TransferCompleted or
                TransactionState.TransferFailed or
                TransactionState.AccountResolutionFailed or
                TransactionState.KycFailed or
                TransactionState.KycManualReview or
                TransactionState.TmsComplianceAlert;

            if (isTerminalState)
            {
                await _transactionStateRepository.UpdateStateAsync(
                    message.EvolveId,
                    TransactionState.NotificationSent,
                    cancellationToken: cancellationToken);
            }

            _logger.LogInformation(
                "EventNotification sent successfully. EvolveId={EvolveId} Event={Event} IsTerminal={IsTerminal}",
                message.EvolveId, payload.Event, isTerminalState);

            await messageActions.CompleteMessageAsync(serviceBusMessage, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "EventNotification cancelled. EvolveId={EvolveId}",
                message?.EvolveId ?? "unknown");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "EventNotification failed after all retries. EvolveId={EvolveId} CorrelationId={CorrelationId}",
                message?.EvolveId ?? "unknown", message?.CorrelationId ?? "unknown");

            if (message is not null)
            {
                try
                {
                    await _transactionStateRepository.UpdateStateAsync(
                        message.EvolveId,
                        TransactionState.NotificationFailed,
                        tx => tx.FailureReason = $"Webhook delivery failed: {ex.Message}",
                        cancellationToken);
                }
                catch (Exception innerEx)
                {
                    _logger.LogError(innerEx,
                        "Failed to update NotificationFailed state. EvolveId={EvolveId}",
                        message.EvolveId);
                }
            }

            await messageActions.DeadLetterMessageAsync(
                serviceBusMessage,
                deadLetterReason: "WebhookDeliveryFailed",
                deadLetterErrorDescription: ex.Message,
                cancellationToken: cancellationToken);
        }
    }
}
