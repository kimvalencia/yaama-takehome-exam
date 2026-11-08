using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using WalletService.Domain;

namespace WalletService.Api;

[Description("A provider transaction. The provider id is taken from the access token (azp), never from the body.")]
public record TransactionRequest(
    [property: Description("Provider's unique id for this transaction (max 100 chars). Retrying with the same reference and payload is safe.")]
    string Reference,
    [property: Description("BET (debit) or WIN (credit). Case-insensitive.")]
    string Type,
    [property: Description("The player's id (Keycloak user id).")]
    Guid PlayerId,
    [property: Description("USD, EUR or BTC. Case-insensitive.")]
    string Currency,
    [property: Description("Positive decimal as a string. USD/EUR: at most 2 decimals, BTC: at most 8.")]
    string Amount,
    [property: Description("WIN only: the reference of the BET being settled (same provider, player and currency).")]
    string? BetReference);

[Description("A recorded wallet transaction.")]
public record TransactionResponse(
    Guid Id,
    string Reference,
    string Type,
    string Currency,
    [property: Description("Always positive; the direction follows from the type.")]
    string Amount,
    [property: Description("Wallet balance right after this transaction.")]
    string BalanceAfter,
    string? BetReference,
    DateTimeOffset CreatedAt,
    [property: Description("True when this response replays an earlier identical request (HTTP 200).")]
    bool Replayed);

public record WalletResponse(string Currency, string Balance);

public record TransactionPage(
    IReadOnlyList<TransactionResponse> Items,
    [property: Description("Opaque cursor for the next (older) page; null when there are no more items.")]
    string? NextCursor);

[Description("Simulated top-up. Requires a verified email and an Idempotency-Key header.")]
public record TopupRequest(
    [property: Description("USD, EUR or BTC. Case-insensitive.")]
    string Currency,
    [property: Description("Positive decimal as a string, within the currency's precision.")]
    string Amount);

/// <summary>Parsing and formatting of the contract's string-typed money, currencies and cursors.</summary>
public static partial class Contract
{
    public const int MaxReferenceLength = 100;
    public const int DefaultLimit = 20;
    public const int MaxLimit = 100;

    // decimal(28,8) holds at most 20 integer digits and 8 fraction digits. Plain digits only:
    // no sign, exponent, thousands separator or whitespace, so "-5", "+5", "1e3", "1,5" are all rejected.
    [GeneratedRegex(@"^[0-9]{1,20}(\.[0-9]{1,8})?$", RegexOptions.CultureInvariant)]
    private static partial Regex AmountPattern();

    public static string SupportedCurrencies => string.Join(", ", Enum.GetNames<Currency>());

    public static bool TryParseCurrency(string? value, out Currency currency, out string? error)
    {
        error = null;
        if (CurrencyRules.TryParse(value, out currency)) return true;
        error = $"Unknown currency '{value}'. Supported: {SupportedCurrencies}.";
        return false;
    }

    public static bool TryParseAmount(string? value, Currency currency, out decimal amount, out string? error)
    {
        amount = 0;
        error = null;
        var decimals = CurrencyRules.Decimals(currency);
        if (value is null || !AmountPattern().IsMatch(value)
            || !decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount))
        {
            error = $"Amount '{value}' is not a valid positive decimal string such as \"12.50\".";
            return false;
        }
        if (!CurrencyRules.IsValidAmount(currency, amount))
        {
            error = amount <= 0
                ? "Amount must be greater than zero."
                : $"{currency} amounts allow at most {decimals} decimal places.";
            return false;
        }
        return true;
    }

    public static string FormatMoney(decimal value, Currency currency) =>
        value.ToString("F" + CurrencyRules.Decimals(currency), CultureInfo.InvariantCulture);

    /// <summary>Upper-cases a transaction type and checks it against the allowed set.</summary>
    public static bool TryNormaliseType(string? value, IReadOnlyCollection<string> allowed, out string type, out string? error)
    {
        type = (value ?? string.Empty).Trim().ToUpperInvariant();
        error = null;
        if (type.Length > 0 && allowed.Contains(type)) return true;
        error = $"Unknown transaction type '{value}'. Supported: {string.Join(", ", allowed)}.";
        return false;
    }

    public static string EncodeCursor(long seq) => Convert.ToBase64String(Encoding.UTF8.GetBytes(seq.ToString(CultureInfo.InvariantCulture)));

    public static bool TryDecodeCursor(string value, out long seq)
    {
        seq = 0;
        try
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(value));
            return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out seq) && seq > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static int ClampLimit(int? limit) => Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
}
