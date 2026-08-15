using Microsoft.EntityFrameworkCore;
using PaymentService.Data;
using PaymentService.Models;
using PaymentService.Provider;

namespace PaymentService.BackgroundServices;

public class RecoveryService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RecoveryService> _logger;

    public RecoveryService(IServiceScopeFactory scopeFactory, ILogger<RecoveryService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var loggerScope = _logger.BeginScope(new Dictionary<string, object> { ["Service"] = "RecoveryService" });
        _logger.LogInformation("Recovery service starting");

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await ProcessProcessingOperations(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Recovery service canceled gracefully");
        }

        _logger.LogInformation("Recovery service stopped");
    }

    private async Task ProcessProcessingOperations(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var operationIds = await db.Operations
            .Where(o => o.Status == OperationStatus.Processing && o.ProviderPaymentId == null)
            .Select(o => o.OperationId)
            .ToListAsync(cancellationToken);

        if (operationIds.Count == 0)
        {
            return;
        }

        _logger.LogInformation("Found {Count} PROCESSING operations to recover", operationIds.Count);

        foreach (var operationId in operationIds)
        {
            await RecoverOperation(operationId, cancellationToken);
        }
    }

    private async Task RecoverOperation(string operationId, CancellationToken cancellationToken)
    {
        using var loggerScope = _logger.BeginScope(new Dictionary<string, object> { ["OperationId"] = operationId, ["Service"] = "RecoveryService" });

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var httpClient = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>().CreateClient("provider");

            var op = await db.Operations.FirstOrDefaultAsync(o => o.OperationId == operationId, cancellationToken);
            if (op == null)
            {
                _logger.LogWarning("Operation not found in database");
                return;
            }

            // статус мог измениться, пока шёл предыдущий цикл recovery
            if (op.Status != OperationStatus.Processing || op.ProviderPaymentId != null)
            {
                return;
            }

            _logger.LogInformation("Recovering operation");

            var result = await ProviderClient.SubmitPaymentAsync(httpClient, op, cancellationToken);
            if (!result.Accepted)
            {
                _logger.LogWarning("Provider returned {StatusCode}, will retry later", result.StatusCode);
                return;
            }

            if (result.ProviderPaymentId == null)
            {
                _logger.LogInformation("Provider accepted {StatusCode} without providerPaymentId, waiting for callback", result.StatusCode);
                return;
            }

            op.ProviderPaymentId = result.ProviderPaymentId;
            await db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Set providerPaymentId to: {ProviderPaymentId}", op.ProviderPaymentId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error recovering operation");
        }
    }
}
