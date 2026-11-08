namespace WalletService.Domain;

public class Wallet
{
    public Guid Id { get; set; }
    public Guid PlayerId { get; set; }
    public Currency Currency { get; set; }
    public decimal Balance { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
