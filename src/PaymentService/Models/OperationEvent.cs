using System.ComponentModel.DataAnnotations;

namespace PaymentService.Models;

public class OperationEvent
{
    public long Id { get; set; }

    [Required]
    public string OperationId { get; set; } = default!;

    public int EventId { get; set; }

    [Required]
    public string Type { get; set; } = default!;

    public string? FromStatus { get; set; }

    [Required]
    public string ToStatus { get; set; } = default!;

    public string? Message { get; set; }

    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    public Operation Operation { get; set; } = default!;
}
