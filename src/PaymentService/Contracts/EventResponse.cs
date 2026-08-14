namespace PaymentService.Contracts;

public record EventResponse(
    string Type,
    string? FromStatus,
    string ToStatus,
    string? Message,
    DateTime OccurredAt
);
