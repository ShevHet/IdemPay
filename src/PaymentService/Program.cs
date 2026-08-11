using Microsoft.EntityFrameworkCore;
using PaymentService.Data;
using PaymentService.Models;
using PaymentService.Contracts;

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
});

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

app.MapPost("/operations/{id}/submit", async (string id, AppDbContext db) =>
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
    // NOTE: проверить на конкурентных вызовах
    var op = await db.Operations.FirstOrDefaultAsync(o => o.OperationId == req.OperationId);
    if (op == null) return Results.NotFound();

    op.Status = req.Result == "success" ? OperationStatus.Completed : OperationStatus.Rejected;
    op.ProviderPaymentId = req.ProviderPaymentId;
    await db.SaveChangesAsync();

    return Results.NoContent();
});

app.Run();
