namespace PaymentService.Contracts;

public record OperationResponse(
    string OperationId,
    string Status,
    string? ProviderPaymentId = null
);
