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

app.MapPost("/operations", (CreateOperationRequest req) =>
{
    // TODO: валидировать и сохранить в БД
    return Results.Created($"/operations/{req.OperationId}", new OperationResponse(req.OperationId, OperationStatus.Created, null));
});

app.MapPost("/operations/{id}/submit", (string id) =>
{
    // TODO: атомарно перевести в PROCESSING и запланировать вызов провайдера
    // TODO: вынести retry count в конфиг
    return Results.Accepted($"/operations/{id}", new OperationResponse(id, OperationStatus.Processing));
});

app.MapGet("/operations/{id}", (string id) =>
{
    // TODO: получить из БД
    return Results.Ok(new OperationResponse(id, OperationStatus.Created));
});

app.MapGet("/operations/{id}/events", (string id) =>
{
    // TODO: получить события из БД
    return Results.Ok(Array.Empty<EventResponse>());
});

app.MapPost("/receipts", (ReceiptRequest req) =>
{
    // TODO: атомарно обработать квитанцию и перевести в финальный статус
    // TODO: добавить метрики для failed submits
    return Results.NoContent();
});

app.Run();
