using System.Text.Json.Serialization;
using PaymentServices.Shared.Enums;
using PaymentServices.Shared.Messages;

namespace PaymentServices.EventNotification.Models;

// ---------------------------------------------------------------------------
// ledgerWebhookFunctions EventNotification contract
// Matches the IEventNotification / EventNotification class exactly
// ---------------------------------------------------------------------------

/// <summary>
/// Payload sent to ledgerWebhookFunctions POST endpoint.
/// Maps to IEventNotification contract.
/// </summary>
public sealed class LedgerWebhookPayload
{
    [JsonPropertyName("event")]
    public required string Event { get; init; }

    [JsonPropertyName("subject")]
    public required string Subject { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("code")]
    public int? Code { get; init; }

    [JsonPropertyName("metadata")]
    public required EvolveMetadata Metadata { get; init; }

    [JsonPropertyName("from")]
    public TransactionParty? From { get; init; }

    [JsonPropertyName("to")]
    public TransactionParty? To { get; init; }

    [JsonPropertyName("data")]
    public object? Data { get; init; }
}

public sealed class EvolveMetadata
{
    [JsonPropertyName("evolveId")]
    public required string EvolveId { get; init; }

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; init; }
}

public sealed class TransactionParty
{
    [JsonPropertyName("accountId")]
    public string? AccountId { get; init; }

    [JsonPropertyName("entityId")]
    public string? EntityId { get; init; }

    [JsonPropertyName("accountNumber")]
    public string? AccountNumber { get; init; }
}

// ---------------------------------------------------------------------------
// Event data objects — contents differ per event type
// ---------------------------------------------------------------------------

public sealed class TransactionEventData
{
    [JsonPropertyName("eveTransactionId")]
    public string? EveTransactionId { get; init; }

    [JsonPropertyName("gluId_s")]
    public string? GluIdSource { get; init; }

    [JsonPropertyName("gluId_d")]
    public string? GluIdDestination { get; init; }

    [JsonPropertyName("transactionFlags")]
    public IReadOnlyList<string> TransactionFlags { get; init; } = [];

    [JsonPropertyName("fintechId")]
    public string? FintechId { get; init; }
}

public sealed class ComplianceEventData
{
    [JsonPropertyName("transactionFlags")]
    public IReadOnlyList<string> TransactionFlags { get; init; } = [];

    [JsonPropertyName("failureReason")]
    public string? FailureReason { get; init; }

    [JsonPropertyName("fintechId")]
    public string? FintechId { get; init; }
}

// ---------------------------------------------------------------------------
// Valid event types matching ledgerWebhookFunctions validation list
// ---------------------------------------------------------------------------
public static class LedgerWebhookEventTypes
{
    public const string TmsCompleted = "tmscompleted";
    public const string TransactionCompleted = "transactioncompleted";
    public const string TransactionFailed = "transactionfailed";
    public const string KycFailed = "kycfailed";
    public const string ActionRequired = "actionrequired";
    public const string TmsActionRequired = "tmsactionrequired";
    public const string AccountActionRequired = "accountactionrequired";
}

// ---------------------------------------------------------------------------
// Mapper — TransactionState → LedgerWebhookPayload
// ---------------------------------------------------------------------------
public static class LedgerWebhookPayloadMapper
{
    /// <summary>
    /// Maps a PaymentMessage to the ledgerWebhookFunctions payload.
    /// Returns null if the state does not map to a valid event type
    /// (e.g. TmsPending is not in the valid list).
    /// </summary>
    public static LedgerWebhookPayload? FromMessage(PaymentMessage message)
    {
        var (eventType, description, code) = message.State switch
        {
            TransactionState.TmsCompleted =>
                (LedgerWebhookEventTypes.TmsCompleted, "TMS Screening Cleared", (int?)null),

            TransactionState.TransferCompleted =>
                (LedgerWebhookEventTypes.TransactionCompleted, "Transaction Completed Successfully", (int?)null),

            TransactionState.TransferFailed =>
                (LedgerWebhookEventTypes.TransactionFailed, "Transaction Failed", (int?)500),

            TransactionState.KycFailed =>
                (LedgerWebhookEventTypes.KycFailed, "KYC Failed", (int?)400),

            TransactionState.KycManualReview =>
                (LedgerWebhookEventTypes.ActionRequired, "KYC Requires Manual Review", (int?)202),

            TransactionState.TmsComplianceAlert =>
                (LedgerWebhookEventTypes.TmsActionRequired, "TMS Compliance Alert", (int?)202),

            TransactionState.TmsFailed =>
                (LedgerWebhookEventTypes.TmsActionRequired, "TMS Screening Failed", (int?)500),

            TransactionState.AccountResolutionFailed =>
                (LedgerWebhookEventTypes.AccountActionRequired, "Account Resolution Failed", (int?)400),

            // TmsPending and other intermediate states not in valid list — skip
            _ => (null, null, (int?)null)
        };

        // State not in valid event type list — do not send
        if (eventType is null)
            return null;

        // Use destination entity as subject (matches original webhook handler pattern)
        var subject = message.Destination.EntityId
            ?? message.Source.EntityId
            ?? message.EvolveId;

        // Build data object — differs per event type
        object? data = message.State switch
        {
            TransactionState.TransferCompleted =>
                new TransactionEventData
                {
                    EveTransactionId = message.EveTransactionId,
                    GluIdSource = message.GluIdSource,
                    GluIdDestination = message.GluIdDestination,
                    TransactionFlags = message.TransactionFlags,
                    FintechId = message.FintechId
                },

            TransactionState.TmsCompleted or
            TransactionState.TmsComplianceAlert or
            TransactionState.TmsFailed =>
                new ComplianceEventData
                {
                    TransactionFlags = message.TransactionFlags,
                    FailureReason = message.FailureReason,
                    FintechId = message.FintechId
                },

            _ => new ComplianceEventData
            {
                FailureReason = message.FailureReason,
                FintechId = message.FintechId
            }
        };

        return new LedgerWebhookPayload
        {
            Event = eventType,
            Subject = subject,
            Description = description!,
            Code = code,
            Metadata = new EvolveMetadata
            {
                EvolveId = message.EvolveId,
                CorrelationId = message.CorrelationId
            },
            From = new TransactionParty
            {
                AccountId = message.Source.AccountId,
                EntityId = message.Source.EntityId,
                AccountNumber = message.Source.AccountNumber
            },
            To = new TransactionParty
            {
                AccountId = message.Destination.AccountId,
                EntityId = message.Destination.EntityId,
                AccountNumber = message.Destination.AccountNumber
            },
            Data = data
        };
    }
}
