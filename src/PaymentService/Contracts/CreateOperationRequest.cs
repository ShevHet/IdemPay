namespace PaymentService.Contracts;

public record CreateOperationRequest(
    string OperationId,
    string Amount,
    string Currency,
    string? Description
);
