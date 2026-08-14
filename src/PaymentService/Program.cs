using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaymentService.Data;
using PaymentService.Models;
using PaymentService.Contracts;
using PaymentService.BackgroundServices;
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

builder.Services.AddHostedService<RecoveryService>();

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
app.MapPost("/operations", async (CreateOperationRequest req, AppDbContext db) =>
{
    var op = new Operation
    {
        OperationId = req.OperationId,
        Amount = req.Amount,
        Currency = req.Currency,
        Description = req.Description,
        Status = OperationStatus.Created,
        CreatedAt = DateTime.UtcNow
    };

    db.Operations.Add(op);

    var nextEventId = 1;
    var evt = new OperationEvent
    {
        OperationId = op.OperationId,
        EventId = nextEventId,
        Type = "CREATED",
        ToStatus = OperationStatus.Created,
        Message = "Operation created",
        OccurredAt = op.CreatedAt
    };
    db.OperationEvents.Add(evt);

    await db.SaveChangesAsync();

    return Results.Created($"/operations/{op.OperationId}", new OperationResponse(op.OperationId, op.Status, op.ProviderPaymentId));
});

app.MapPost("/operations/{id}/submit", async (string id, AppDbContext db, IHttpClientFactory httpClientFactory) =>
{
    Console.WriteLine($"[SUBMIT] Starting submit for operation: {id}");

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
                Console.WriteLine($"[SUBMIT] Operation {id} not found");
                return Results.NotFound(new { error = "Operation not found" });
            }

            Console.WriteLine($"[SUBMIT] Current status: {op.Status}, providerPaymentId: {op.ProviderPaymentId}");

            if (op.Status != OperationStatus.Created)
            {
                await transaction.CommitAsync();
                Console.WriteLine($"[SUBMIT] Status is {op.Status}, not CREATED, returning existing");
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

            Console.WriteLine($"[SUBMIT] Changed status to PROCESSING, event {nextEventId}, calling provider");

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

            Console.WriteLine($"[SUBMIT] Provider response status: {response.StatusCode}");

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
                Console.WriteLine($"[SUBMIT] Set providerPaymentId to: {op.ProviderPaymentId}");
            }

            return Results.Accepted($"/operations/{id}", new OperationResponse(
                op.OperationId,
                op.Status,
                op.ProviderPaymentId
            ));
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.Message.Contains("busy") && attempt < maxAttempts)
        {
            Console.WriteLine($"[SUBMIT] Database busy, retry {attempt}/{maxAttempts}");
            await Task.Delay(delayMs);
        }
    }

    Console.WriteLine($"[SUBMIT] Max retries exceeded for {id}");
    return Results.StatusCode(503);
});

app.MapPost("/receipts", async (ReceiptRequest req, AppDbContext db) =>
{
    Console.WriteLine($"[RECEIPT CALLBACK] Received for operation: {req.OperationId}, result: {req.Result}, providerPaymentId: {req.ProviderPaymentId}");

    const int maxAttempts = 3;
    const int delayMs = 100;

    // Валидация result: только "success" или "rejected"
    if (req.Result != "success" && req.Result != "rejected")
    {
        return Results.BadRequest(new { error = "Invalid result value. Expected 'success' or 'rejected'." });
    }

    var newStatus = req.Result == "success" ? OperationStatus.Completed : OperationStatus.Rejected;

    for (int attempt = 1; attempt <= maxAttempts; attempt++)
    {
        try
        {
            using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);

            var op = await db.Operations
                .Include(o => o.Events)
                .FirstOrDefaultAsync(o => o.OperationId == req.OperationId);

            if (op == null)
            {
                await transaction.CommitAsync();
                Console.WriteLine($"[RECEIPT CALLBACK] Operation {req.OperationId} not found");
                return Results.NotFound(new { error = "Operation not found" });
            }

            Console.WriteLine($"[RECEIPT CALLBACK] Current status: {op.Status}, providerPaymentId: {op.ProviderPaymentId}");

            // Конфликт providerPaymentId — 409
            if (!string.IsNullOrEmpty(op.ProviderPaymentId) && 
                !string.IsNullOrEmpty(req.ProviderPaymentId) &&
                op.ProviderPaymentId != req.ProviderPaymentId)
            {
                await transaction.CommitAsync();
                Console.WriteLine($"[RECEIPT CALLBACK] ProviderPaymentId conflict: existing={op.ProviderPaymentId}, received={req.ProviderPaymentId}");
                return Results.Conflict(new { error = "ProviderPaymentId conflict", existing = op.ProviderPaymentId, received = req.ProviderPaymentId });
            }

            // Установить providerPaymentId, если был null
            if (string.IsNullOrEmpty(op.ProviderPaymentId) && !string.IsNullOrEmpty(req.ProviderPaymentId))
            {
                op.ProviderPaymentId = req.ProviderPaymentId;
                Console.WriteLine($"[RECEIPT CALLBACK] Set providerPaymentId to: {op.ProviderPaymentId}");
            }

            var oldStatus = op.Status;

            // Если статус уже COMPLETED или REJECTED
            if (oldStatus == OperationStatus.Completed || oldStatus == OperationStatus.Rejected)
            {
                if (oldStatus == newStatus)
                {
                    // тот же result → 204, без нового event
                    await transaction.CommitAsync();
                    Console.WriteLine($"[RECEIPT CALLBACK] Status already {oldStatus}, same result, ignoring");
                    return Results.NoContent();
                }
                else
                {
                    // другой result → 204, игнорировать, без нового event
                    await transaction.CommitAsync();
                    Console.WriteLine($"[RECEIPT CALLBACK] Status already {oldStatus}, different result, ignoring (old={oldStatus}, new={newStatus})");
                    return Results.NoContent();
                }
            }

            // Если PROCESSING → переведи, добавь event
            if (oldStatus == OperationStatus.Processing)
            {
                op.Status = newStatus;

                var nextEventId = op.Events.Any() ? op.Events.Max(e => e.EventId) + 1 : 1;
                var evt = new OperationEvent
                {
                    OperationId = op.OperationId,
                    EventId = nextEventId,
                    Type = "STATUS_CHANGED",
                    FromStatus = oldStatus,
                    ToStatus = newStatus,
                    Message = req.Message,
                    OccurredAt = req.OccurredAt
                };
                db.OperationEvents.Add(evt);

                Console.WriteLine($"[RECEIPT CALLBACK] Changed status from {oldStatus} to {newStatus}, added event {nextEventId}");

                await db.SaveChangesAsync();
                await transaction.CommitAsync();
                Console.WriteLine($"[RECEIPT CALLBACK] Successfully processed callback for {req.OperationId}");
                return Results.NoContent();
            }

            // Если CREATED — неожиданное состояние для callback
            await transaction.CommitAsync();
            Console.WriteLine($"[RECEIPT CALLBACK] Unexpected status {oldStatus} for callback, ignoring");
            return Results.NoContent();
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.Message.Contains("busy") && attempt < maxAttempts)
        {
            Console.WriteLine($"[RECEIPT CALLBACK] Database busy, retry {attempt}/{maxAttempts}");
            await Task.Delay(delayMs);
        }
    }

    Console.WriteLine($"[RECEIPT CALLBACK] Max retries exceeded for {req.OperationId}");
    return Results.StatusCode(503);
});

app.MapGet("/operations/{id}", async (string id, AppDbContext db) =>
{
    var op = await db.Operations
        .Include(o => o.Events)
        .FirstOrDefaultAsync(o => o.OperationId == id);

    if (op == null)
    {
        return Results.NotFound(new { error = "Operation not found" });
    }

    return Results.Ok(new OperationResponse(op.OperationId, op.Status, op.ProviderPaymentId, op.Description, op.Amount, op.Currency, op.CreatedAt, op.Events.Select(e => new EventResponse(e.Type, e.FromStatus, e.ToStatus, e.Message, e.OccurredAt)).ToList()));
});

app.MapGet("/operations/{id}/events", async (string id, AppDbContext db) =>
{
    var op = await db.Operations
        .Include(o => o.Events)
        .FirstOrDefaultAsync(o => o.OperationId == id);

    if (op == null)
    {
        return Results.NotFound(new { error = "Operation not found" });
    }

    var events = op.Events
        .OrderBy(e => e.EventId)
        .Select(e => new EventResponse(e.Type, e.FromStatus, e.ToStatus, e.Message, e.OccurredAt))
        .ToList();

    return Results.Ok(events);
});
