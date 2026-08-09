namespace PaymentService.Contracts;

public record ReceiptRequest(
    string ProviderPaymentId,
    string OperationId,
    string Result,
    string? Message,
    DateTime OccurredAt
);
