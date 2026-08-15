using System.Net;
using System.Text;
using System.Text.Json;
using PaymentService.Models;

namespace PaymentService.Provider;

internal record ProviderSubmitResult(bool Accepted, HttpStatusCode StatusCode, string? ProviderPaymentId);

internal static class ProviderClient
{
    internal static async Task<ProviderSubmitResult> SubmitPaymentAsync(HttpClient client, Operation op, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(new { operationId = op.OperationId, amount = op.Amount, currency = op.Currency });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/payments")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Idempotency-Key", op.OperationId);
        request.Headers.Add("X-Correlation-ID", op.OperationId);

        using var response = await client.SendAsync(request, cancellationToken);

        // провайдер по контракту отвечает 202, но 200 тоже значит "принято"
        if (!response.IsSuccessStatusCode)
        {
            return new ProviderSubmitResult(false, response.StatusCode, null);
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return new ProviderSubmitResult(true, response.StatusCode, ReadProviderPaymentId(body));
    }

    private static string? ReadProviderPaymentId(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("providerPaymentId", out var prop) || prop.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var providerPaymentId = prop.GetString();
            return string.IsNullOrWhiteSpace(providerPaymentId) ? null : providerPaymentId;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
