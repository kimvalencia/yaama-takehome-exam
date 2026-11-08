namespace WalletService.Domain;

public class WalletTransaction
{
    public Guid Id { get; set; }

    /// <summary>Database identity; gives a stable insertion order for history and cursor pagination.</summary>
    public long Seq { get; set; }

    public Guid WalletId { get; set; }
    public string ProviderId { get; set; } = string.Empty;
    public string Reference { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;

    /// <summary>Always positive; the direction comes from the type.</summary>
    public decimal Amount { get; set; }

    public decimal BalanceAfter { get; set; }
    public Guid? RelatedTransactionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
