using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace PaymentService.Models;

[Index(nameof(OperationId), IsUnique = true)]
public class Operation
{
    public long Id { get; set; }

    [Required]
    public string OperationId { get; set; } = default!;

    [Required]
    public string Amount { get; set; } = default!;

    [Required]
    public string Currency { get; set; } = default!;

    public string? Description { get; set; }

    [Required]
    public string Status { get; set; } = OperationStatus.Created;

    public string? ProviderPaymentId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<OperationEvent> Events { get; set; } = [];
}

public static class OperationStatus
{
    public const string Created = "CREATED";
    public const string Processing = "PROCESSING";
    public const string Completed = "COMPLETED";
    public const string Rejected = "REJECTED";
}
