using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using PaymentServices.Shared.Enums;
using PaymentServices.Shared.Models;

namespace PaymentServices.EventNotification.Repositories;

public interface ITransactionStateRepository
{
    Task UpdateStateAsync(
        string evolveId,
        TransactionState state,
        Action<CosmosTransaction>? update = null,
        CancellationToken cancellationToken = default);
}

public sealed class TransactionStateRepository : ITransactionStateRepository
{
    private readonly Container _container;
    private readonly ILogger<TransactionStateRepository> _logger;

    public TransactionStateRepository(
        [FromKeyedServices("transactions")] Container container,
        ILogger<TransactionStateRepository> logger)
    {
        _container = container;
        _logger = logger;
    }

    public async Task UpdateStateAsync(
        string evolveId,
        TransactionState state,
        Action<CosmosTransaction>? update = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _container.ReadItemAsync<CosmosTransaction>(
                evolveId, new PartitionKey(evolveId),
                cancellationToken: cancellationToken);

            var transaction = response.Resource;
            transaction.State = state;
            transaction.LastUpdatedAt = DateTimeOffset.UtcNow;
            update?.Invoke(transaction);

            await _container.ReplaceItemAsync(
                transaction, evolveId,
                new PartitionKey(evolveId),
                cancellationToken: cancellationToken);

            _logger.LogInformation(
                "Transaction state updated. EvolveId={EvolveId} State={State}",
                evolveId, state);
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogError("Transaction not found. EvolveId={EvolveId}", evolveId);
            throw;
        }
    }
}
