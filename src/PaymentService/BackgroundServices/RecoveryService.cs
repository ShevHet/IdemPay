using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PaymentService.Data;
using PaymentService.Models;

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

        var processingOps = await db.Operations
            .Where(o => o.Status == OperationStatus.Processing && o.ProviderPaymentId == null)
            .ToListAsync(cancellationToken);

        if (!processingOps.Any())
        {
            _logger.LogInformation("No PROCESSING operations without ProviderPaymentId");
            return;
        }

        _logger.LogInformation("Found {Count} PROCESSING operations to recover", processingOps.Count);

        foreach (var op in processingOps)
        {
            await RecoverOperation(op, cancellationToken);
        }
    }

    private async Task RecoverOperation(Operation op, CancellationToken cancellationToken)
    {
        using var loggerScope = _logger.BeginScope(new Dictionary<string, object> { ["OperationId"] = op.OperationId, ["Service"] = "RecoveryService" });

        try
        {
            _logger.LogInformation("Recovering operation");

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Повторный вызов провайдера, если ProviderPaymentId == null
            var (response, providerPaymentId) = await CallProviderAsync(op, cancellationToken);

            if (response.IsSuccessStatusCode && providerPaymentId != null)
            {
                op.ProviderPaymentId = providerPaymentId;
                await db.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Set providerPaymentId to: {ProviderPaymentId}", op.ProviderPaymentId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error recovering operation");
        }
    }

    private async Task<(bool IsSuccess, string? ProviderPaymentId)> CallProviderAsync(Operation op, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var httpClientFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
        var httpClient = httpClientFactory.CreateClient("provider");

        var payload = new
        {
            operationId = op.OperationId,
            amount = op.Amount,
            currency = op.Currency
        };

        var json = System.Text.Json.JsonSerializer.Serialize(payload);
        using var request = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        request.Headers.Add("Idempotency-Key", op.OperationId);
        request.Headers.Add("X-Correlation-ID", op.OperationId);

        var response = await httpClient.PostAsync("/payments", request, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!string.IsNullOrWhiteSpace(responseContent))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(responseContent);
                    if (doc.RootElement.TryGetProperty("providerPaymentId", out var prop))
                    {
                        var providerPaymentId = prop.GetString();
                        if (!string.IsNullOrEmpty(providerPaymentId))
                        {
                            return (true, providerPaymentId);
                        }
                    }
                }
                catch
                {
                    // если не парсится — возвращаем null, будет повторный вызов
                }
            }

            return (true, "pending");
        }

        return (false, null);
    }
}