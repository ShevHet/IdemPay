using Microsoft.EntityFrameworkCore;
using PaymentService.Data;
using PaymentService.Models;
using PaymentService.Contracts;
using Polly;
using Polly.Retry;
using Microsoft.Extensions.Http;

var builder = WebApplication.CreateBuilder(args);

var providerUrl = Environment.GetEnvironmentVariable("PROVIDER_URL")
               ?? builder.Configuration["ProviderUrl"]
               ?? "http://localhost:8081";

builder.Services.AddDbContext<AppDbContext>(opt =>
    opt.UseSqlite("Data Source=/data/app.db"));

builder.Services.AddHttpClient("provider", c =>
{
    c.BaseAddress = new Uri(providerUrl);
    c.Timeout = TimeSpan.FromSeconds(30);
}).AddPolicyHandler(AddRetryPolicy());

static IAsyncPolicy<HttpResponseMessage> AddRetryPolicy()
{
    // Создаем политику с обработкой HttpRequestException и TaskCanceledException,
    // а также статуса 503
    var policyBuilder = Policy<HttpResponseMessage>.Handle<HttpRequestException>();

    // Чтобы обрабатывать и TaskCanceledException, и статус 503, оборачиваем в декоратор
    return Policy<HttpResponseMessage>
        .Handle<HttpRequestException>()
        .Or<TaskCanceledException>()
        .OrResult(res => res.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
        .WaitAndRetryAsync(
            retryCount: 3,
            sleepDurationProvider: attempt =>
            {
                var jitter = new Random().Next(0, 501); // 0-500ms
                var delay = TimeSpan.FromMilliseconds(Math.Pow(2, attempt - 1) * 1000 + jitter);
                return delay;
            }
        );
}

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// HACK: костыль, переделать если будет время
app.MapPost("/operations", (CreateOperationRequest req) =>
{
    return Results.Created($"/operations/{req.OperationId}", new OperationResponse(req.OperationId, OperationStatus.Created, null));
});

app.MapPost("/operations/{id}/submit", async (string id, AppDbContext db, IHttpClientFactory httpClientFactory) =>
{
    const int maxAttempts = 3;
    const int delayMs = 100;

    for (int attempt = 1; attempt <= maxAttempts; attempt++)
    {
        try
        {
            using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);

            var op = await db.Operations
                .Include(o => o.Events)
                .FirstOrDefaultAsync(o => o.OperationId == id);

            if (op == null)
            {
                await transaction.CommitAsync();
                return Results.NotFound(new { error = "Operation not found" });
            }

            if (op.Status != OperationStatus.Created)
            {
                await transaction.CommitAsync();
                return Results.Ok(new OperationResponse(
                    op.OperationId,
                    op.Status,
                    op.ProviderPaymentId
                ));
            }

            var fromStatus = op.Status;
            op.Status = OperationStatus.Processing;

            var nextEventId = op.Events.Any() ? op.Events.Max(e => e.EventId) + 1 : 1;

            var evt = new OperationEvent
            {
                OperationId = op.OperationId,
                EventId = nextEventId,
                Type = "STATUS_CHANGED",
                FromStatus = fromStatus,
                ToStatus = OperationStatus.Processing,
                Message = "Operation submitted for processing",
                OccurredAt = DateTime.UtcNow
            };

            db.OperationEvents.Add(evt);
            await db.SaveChangesAsync();

            await transaction.CommitAsync();

            // Вызываем провайдера через именованный клиент с retry-policy
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

            var response = await httpClient.PostAsync("/payments", request);

            if (response.StatusCode == System.Net.HttpStatusCode.Accepted)
            {
                var responseContent = await response.Content.ReadAsStringAsync();
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
                await db.SaveChangesAsync();
            }

            return Results.Accepted($"/operations/{id}", new OperationResponse(
                op.OperationId,
                op.Status,
                op.ProviderPaymentId
            ));
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.Message.Contains("busy") && attempt < maxAttempts)
        {
            await Task.Delay(delayMs);
        }
    }

    return Results.StatusCode(503);
});

app.MapGet("/operations/{id}", (string id) =>
{
    return Results.Ok(new OperationResponse(id, OperationStatus.Created));
});

app.MapGet("/operations/{id}/events", (string id) =>
{
    return Results.Ok(Array.Empty<EventResponse>());
});

app.MapPost("/receipts", async (ReceiptRequest req, AppDbContext db) =>
{
    // 1. Найти операцию по operationId
    var op = await db.Operations
        .Include(o => o.Events)
        .FirstOrDefaultAsync(o => o.OperationId == req.OperationId);
    
    if (op == null) return Results.NotFound(new { error = "Operation not found" });

    // 2. Если providerPaymentId ещё null — установить из dto
    if (op.ProviderPaymentId == null)
    {
        op.ProviderPaymentId = req.ProviderPaymentId;
    }

    // 3. Обновить Status = dto.Result
    op.Status = req.Result == "success" ? OperationStatus.Completed : OperationStatus.Rejected;

    // 4. Добавить OperationEvent
    var nextEventId = op.Events.Any() ? op.Events.Max(e => e.EventId) + 1 : 1;
    var evt = new OperationEvent
    {
        OperationId = op.OperationId,
        EventId = nextEventId,
        Type = "STATUS_CHANGED",
        FromStatus = op.Status == OperationStatus.Completed ? OperationStatus.Completed : OperationStatus.Rejected,
        ToStatus = op.Status,
        Message = req.Message,
        OccurredAt = req.OccurredAt
    };
    db.OperationEvents.Add(evt);

    // 5. SaveChanges
    await db.SaveChangesAsync();

    return Results.NoContent();
});

app.Run();
