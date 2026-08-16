namespace PaymentService.Contracts;

public record EventResponse(
    int EventId,
    string Type,
    string? FromStatus,
    string ToStatus,
    string? Message,
    DateTime OccurredAt
);
