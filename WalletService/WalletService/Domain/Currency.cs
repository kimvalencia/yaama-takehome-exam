namespace WalletService.Domain;

public enum Currency
{
    USD,
    EUR,
    BTC,
}

public static class CurrencyRules
{
    /// <summary>Number of decimal places an amount in this currency may carry.</summary>
    public static int Decimals(Currency currency) => currency switch
    {
        Currency.USD => 2,
        Currency.EUR => 2,
        Currency.BTC => 8,
        _ => throw new ArgumentOutOfRangeException(nameof(currency), currency, "Unsupported currency."),
    };

    /// <summary>Case-insensitive parse that only accepts the defined names (no numeric values).</summary>
    public static bool TryParse(string? s, out Currency currency)
    {
        currency = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        foreach (var value in Enum.GetValues<Currency>())
        {
            if (string.Equals(value.ToString(), s.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                currency = value;
                return true;
            }
        }
        return false;
    }

    /// <summary>True when the amount is positive and has no more decimals than the currency allows.</summary>
    public static bool IsValidAmount(Currency currency, decimal amount) =>
        amount > 0 && decimal.Round(amount, Decimals(currency)) == amount;
}
