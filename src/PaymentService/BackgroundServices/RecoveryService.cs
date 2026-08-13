using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PaymentService.Data;
using PaymentService.Models;
using static PaymentService.Program;

namespace PaymentService.BackgroundServices;

public class RecoveryService : BackgroundService
{
    private readonly ILogger<RecoveryService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    public RecoveryService(ILogger<RecoveryService> logger, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[RECOVERY] Recovery service starting");

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
            _logger.LogInformation("[RECOVERY] Recovery service canceled");
        }
    }

    private async Task ProcessProcessingOperations(CancellationToken cancellationToken)
    {
        using var scope = Program.ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var processingOps = await db.Operations
            .Where(o => o.Status == OperationStatus.Processing && o.ProviderPaymentId == null)
            .ToListAsync(cancellationToken);

        if (!processingOps.Any())
        {
            _logger.LogInformation("[RECOVERY] No PROCESSING operations without ProviderPaymentId");
            return;
        }

        _logger.LogInformation("[RECOVERY] Found {Count} PROCESSING operations to recover", processingOps.Count);

        foreach (var op in processingOps)
        {
            await RecoverOperation(op, cancellationToken);
        }
    }

    private async Task RecoverOperation(Operation op, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("[RECOVERY] Recovering operation {OperationId}", op.OperationId);

            // Вызываем провайдера — тот же код, что в /operations/{id}/submit
            var httpClient = _httpClientFactory.CreateClient("provider");
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

            _logger.LogInformation("[RECOVERY] Provider response status: {StatusCode}", response.StatusCode);

            if (response.StatusCode == System.Net.HttpStatusCode.Accepted)
            {
                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
                var providerPaymentId = "pending";

                if (!string.IsNullOrWhiteSpace(responseContent))
                {
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(responseContent);
                        if (doc.RootElement.TryGetProperty("providerPaymentId", out var prop))
                        {
                            providerPaymentId = prop.GetString() ?? "pending";
                        }
                    }
                    catch
                    {
                        // если не парсится — просто оставим pending
                    }
                }

                op.ProviderPaymentId = providerPaymentId;
                await Program.ServiceProvider.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>().SaveChangesAsync(cancellationToken);
                _logger.LogInformation("[RECOVERY] Set providerPaymentId to: {ProviderPaymentId}", op.ProviderPaymentId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[RECOVERY] Error recovering operation {OperationId}", op.OperationId);
        }
    }
}