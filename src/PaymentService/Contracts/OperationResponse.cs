namespace PaymentService.Contracts;

public record OperationResponse(
    string OperationId,
    string Status,
    string? ProviderPaymentId = null,
    string? Description = null,
    string? Amount = null,
    string? Currency = null,
    DateTime CreatedAt = default,
    List<EventResponse>? Events = null
);
