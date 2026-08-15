using System.Data;
using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PaymentService.BackgroundServices;
using PaymentService.Contracts;
using PaymentService.Data;
using PaymentService.Models;
using PaymentService.Provider;
using Polly;

var builder = WebApplication.CreateBuilder(args);

// ProviderUrl в appsettings пустая строка, поэтому одного ?? недостаточно
var providerUrl = FirstNonEmpty(Environment.GetEnvironmentVariable("PROVIDER_URL"), builder.Configuration["ProviderUrl"], "http://localhost:8081");

builder.Services.AddDbContext<AppDbContext>(opt =>
    opt.UseSqlite("Data Source=/data/app.db"));

builder.Services.AddHttpClient("provider", c =>
{
    c.BaseAddress = new Uri(providerUrl);
    c.Timeout = TimeSpan.FromSeconds(30);
}).AddPolicyHandler(BuildRetryPolicy());

builder.Services.AddHostedService<RecoveryService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/operations", async (CreateOperationRequest req, AppDbContext db) =>
{
    // Проверка на дубликат operationId
    var existing = await db.Operations.AnyAsync(o => o.OperationId == req.OperationId);
    if (existing)
    {
        return Results.Conflict(new { error = "Operation already exists" });
    }

    if (string.IsNullOrWhiteSpace(req.OperationId) || string.IsNullOrWhiteSpace(req.Amount) || string.IsNullOrWhiteSpace(req.Currency))
    {
        return Results.BadRequest(new { error = "operationId, amount and currency are required" });
    }

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
    db.OperationEvents.Add(new OperationEvent
    {
        OperationId = op.OperationId,
        EventId = 1,
        Type = "CREATED",
        ToStatus = OperationStatus.Created,
        Message = "Operation created",
        OccurredAt = op.CreatedAt
    });
    await db.SaveChangesAsync();

    return Results.Created($"/operations/{op.OperationId}", new OperationResponse(op.OperationId, op.Status, op.ProviderPaymentId));
});

app.MapPost("/operations/{id}/submit", async (string id, AppDbContext db, IHttpClientFactory httpClientFactory) =>
{
    Console.WriteLine($"[SUBMIT] Starting submit for operation: {id}");

    if (string.IsNullOrWhiteSpace(id))
    {
        return Results.BadRequest(new { error = "operationId is required" });
    }

    return await WithSqliteBusyRetryAsync("SUBMIT", id, async () =>
    {
        using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

        var op = await FindOperationAsync(db, id);
        if (op == null)
        {
            await transaction.CommitAsync();
            Console.WriteLine($"[SUBMIT] Operation {id} not found");
            return Results.NotFound(new { error = "Operation not found" });
        }

        if (op.Status != OperationStatus.Created)
        {
            await transaction.CommitAsync();
            Console.WriteLine($"[SUBMIT] Status is {op.Status}, not CREATED, returning existing");
            return Results.Ok(new OperationResponse(op.OperationId, op.Status, op.ProviderPaymentId));
        }

        op.Status = OperationStatus.Processing;
        db.OperationEvents.Add(new OperationEvent
        {
            OperationId = op.OperationId,
            EventId = NextEventId(op),
            Type = "STATUS_CHANGED",
            FromStatus = OperationStatus.Created,
            ToStatus = OperationStatus.Processing,
            Message = "Operation submitted for processing",
            OccurredAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();

        Console.WriteLine($"[SUBMIT] Changed status to PROCESSING, calling provider");

        var result = await ProviderClient.SubmitPaymentAsync(httpClientFactory.CreateClient("provider"), op);
        Console.WriteLine($"[SUBMIT] Provider response status: {result.StatusCode}");

        // если id не пришёл, ProviderPaymentId остаётся null и операцию добьёт recovery
        if (result.Accepted && result.ProviderPaymentId != null)
        {
            op.ProviderPaymentId = result.ProviderPaymentId;
            await db.SaveChangesAsync();
            Console.WriteLine($"[SUBMIT] Set providerPaymentId to: {op.ProviderPaymentId}");
        }

        return Results.Accepted($"/operations/{id}", new OperationResponse(op.OperationId, op.Status, op.ProviderPaymentId));
    });
});

app.MapPost("/receipts", async (ReceiptRequest req, AppDbContext db) =>
{
    Console.WriteLine($"[RECEIPT CALLBACK] Received for operation: {req.OperationId}, result: {req.Result}, providerPaymentId: {req.ProviderPaymentId}");

    if (string.IsNullOrWhiteSpace(req.OperationId))
    {
        return Results.BadRequest(new { error = "operationId is required" });
    }

    var newStatus = MapCallbackResult(req.Result);
    if (newStatus == null)
    {
        return Results.BadRequest(new { error = "Invalid result value. Expected 'COMPLETED' or 'REJECTED'." });
    }

    return await WithSqliteBusyRetryAsync("RECEIPT CALLBACK", req.OperationId, async () =>
    {
        using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

        var op = await FindOperationAsync(db, req.OperationId);
        if (op == null)
        {
            await transaction.CommitAsync();
            Console.WriteLine($"[RECEIPT CALLBACK] Operation {req.OperationId} not found");
            return Results.NotFound(new { error = "Operation not found" });
        }

        if (!string.IsNullOrEmpty(op.ProviderPaymentId) && !string.IsNullOrEmpty(req.ProviderPaymentId) && op.ProviderPaymentId != req.ProviderPaymentId)
        {
            await transaction.CommitAsync();
            Console.WriteLine($"[RECEIPT CALLBACK] ProviderPaymentId conflict: existing={op.ProviderPaymentId}, received={req.ProviderPaymentId}");
            return Results.Conflict(new { error = "ProviderPaymentId conflict", existing = op.ProviderPaymentId, received = req.ProviderPaymentId });
        }

        // терминальный статус и CREATED ведут себя одинаково: повторный или преждевременный callback просто подтверждаем
        if (op.Status != OperationStatus.Processing)
        {
            await transaction.CommitAsync();
            Console.WriteLine($"[RECEIPT CALLBACK] Status {op.Status} is not PROCESSING, ignoring");
            return Results.NoContent();
        }

        if (string.IsNullOrEmpty(op.ProviderPaymentId) && !string.IsNullOrEmpty(req.ProviderPaymentId))
        {
            op.ProviderPaymentId = req.ProviderPaymentId;
        }

        op.Status = newStatus;
        db.OperationEvents.Add(new OperationEvent
        {
            OperationId = op.OperationId,
            EventId = NextEventId(op),
            Type = "STATUS_CHANGED",
            FromStatus = OperationStatus.Processing,
            ToStatus = newStatus,
            Message = req.Message,
            OccurredAt = req.OccurredAt
        });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();

        Console.WriteLine($"[RECEIPT CALLBACK] Changed status to {newStatus} for {req.OperationId}");
        return Results.NoContent();
    });
});

app.MapGet("/operations/{id}", async (string id, AppDbContext db) =>
{
    var op = await FindOperationAsync(db, id);
    if (op == null)
    {
        return Results.NotFound(new { error = "Operation not found" });
    }

    return Results.Ok(new OperationResponse(op.OperationId, op.Status, op.ProviderPaymentId, op.Description, op.Amount, op.Currency, op.CreatedAt, MapEvents(op)));
});

app.MapGet("/operations/{id}/events", async (string id, AppDbContext db) =>
{
    var op = await FindOperationAsync(db, id);
    if (op == null)
    {
        return Results.NotFound(new { error = "Operation not found" });
    }

    return Results.Ok(MapEvents(op));
});

app.Run();

static string FirstNonEmpty(params string?[] values)
{
    return values.First(v => !string.IsNullOrWhiteSpace(v))!;
}

static IAsyncPolicy<HttpResponseMessage> BuildRetryPolicy()
{
    return Policy<HttpResponseMessage>
        .Handle<HttpRequestException>()
        .Or<TaskCanceledException>()
        .OrResult(res => res.StatusCode == HttpStatusCode.ServiceUnavailable)
        .WaitAndRetryAsync(
            retryCount: 3,
            sleepDurationProvider: attempt => TimeSpan.FromMilliseconds(Math.Pow(2, attempt - 1) * 1000 + Random.Shared.Next(0, 501))
        );
}

// SQLite отдаёт "database is busy" на конкурентных транзакциях, поэтому повторяем всю транзакцию целиком
static async Task<IResult> WithSqliteBusyRetryAsync(string tag, string operationId, Func<Task<IResult>> action)
{
    const int maxAttempts = 3;
    const int delayMs = 100;

    for (var attempt = 1; attempt <= maxAttempts; attempt++)
    {
        try
        {
            return await action();
        }
        catch (SqliteException ex) when (ex.Message.Contains("busy") && attempt < maxAttempts)
        {
            Console.WriteLine($"[{tag}] Database busy, retry {attempt}/{maxAttempts}");
            await Task.Delay(delayMs);
        }
    }

    Console.WriteLine($"[{tag}] Max retries exceeded for {operationId}");
    return Results.StatusCode(503);
}

static Task<Operation?> FindOperationAsync(AppDbContext db, string id)
{
    return db.Operations.Include(o => o.Events).FirstOrDefaultAsync(o => o.OperationId == id);
}

static int NextEventId(Operation op)
{
    return op.Events.Count > 0 ? op.Events.Max(e => e.EventId) + 1 : 1;
}

static string? MapCallbackResult(string? result)
{
    if (string.Equals(result, "COMPLETED", StringComparison.OrdinalIgnoreCase) || string.Equals(result, "success", StringComparison.OrdinalIgnoreCase))
    {
        return OperationStatus.Completed;
    }

    if (string.Equals(result, "REJECTED", StringComparison.OrdinalIgnoreCase) || string.Equals(result, "rejected", StringComparison.OrdinalIgnoreCase))
    {
        return OperationStatus.Rejected;
    }

    return null;
}

static List<EventResponse> MapEvents(Operation op)
{
    return op.Events.OrderBy(e => e.EventId).Select(e => new EventResponse(e.Type, e.FromStatus, e.ToStatus, e.Message, e.OccurredAt)).ToList();
}