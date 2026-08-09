using Microsoft.EntityFrameworkCore;
using PaymentService.Data;
using PaymentService.Models;

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

app.MapPost("/operations", (CreateOperationRequest req) =>
{
    // TODO: validate + save to DB
    return Results.Created($"/operations/{req.OperationId}", new
    {
        req.OperationId,
        req.Amount,
        req.Currency,
        req.Description,
        Status = OperationStatus.Created,
        ProviderPaymentId = (string?)null
    });
});

app.MapPost("/operations/{id}/submit", (string id) =>
{
    // TODO: atomically move to PROCESSING and schedule provider call
    return Results.Accepted($"/operations/{id}", new
    {
        OperationId = id,
        Status = OperationStatus.Processing
    });
});

app.MapGet("/operations/{id}", (string id) =>
{
    // TODO: fetch from DB
    return Results.Ok(new
    {
        OperationId = id,
        Status = OperationStatus.Created,
        ProviderPaymentId = (string?)null
    });
});

app.MapGet("/operations/{id}/events", (string id) =>
{
    // TODO: fetch events from DB
    return Results.Ok(Array.Empty<object>());
});

app.MapPost("/receipts", (ReceiptRequest req) =>
{
    // TODO: atomically process receipt and transition to final status
    return Results.NoContent();
});

app.Run();

record CreateOperationRequest(
    string OperationId,
    string Amount,
    string Currency,
    string? Description
);

record ReceiptRequest(
    string ProviderPaymentId,
    string OperationId,
    string Result,
    string? Message,
    DateTime OccurredAt
);
