using System.Net;
using System.Text.Json;
using WalletService.Domain;

namespace WalletService.Tests.Api;

[Collection(SqlServerCollection.Name)]
public class PlayerApiTests(SqlServerFixture fx) : ApiTestBase(fx)
{
    [Fact]
    public async Task Me_wallets_returns_only_the_callers_three_wallets()
    {
        var me = await SeedPlayer(usd: 12.5m, eur: 3m, btc: 0.00000001m);
        await SeedPlayer(usd: 999m);

        var response = await Get(TestCaller.Player(me), "/v1/players/me/wallets");

        await AssertStatus(response, HttpStatusCode.OK);
        var wallets = (await Json(response)).EnumerateArray()
            .ToDictionary(w => w.GetProperty("currency").GetString()!, w => w.GetProperty("balance").GetString());
        Assert.Equal(3, wallets.Count);
        Assert.Equal("12.50", wallets["USD"]);
        Assert.Equal("3.00", wallets["EUR"]);
        Assert.Equal("0.00000001", wallets["BTC"]);
    }

    [Fact]
    public async Task Topup_credits_and_appears_in_history_as_DEPOSIT()
    {
        var me = await SeedPlayer(usd: 0m, eur: 1m);
        var caller = TestCaller.Player(me);

        var first = await Topup(caller, new { currency = "eur", amount = "20.00" }, Key);
        var replay = await Topup(caller, new { currency = "EUR", amount = "20.00" }, Key);

        await AssertStatus(first, HttpStatusCode.Created);
        await AssertStatus(replay, HttpStatusCode.OK);
        var tx = await Json(first);
        Assert.Equal("DEPOSIT", tx.GetProperty("type").GetString());
        Assert.Equal(Key, tx.GetProperty("reference").GetString());
        Assert.Equal("21.00", tx.GetProperty("balanceAfter").GetString());
        Assert.True((await Json(replay)).GetProperty("replayed").GetBoolean());
        Assert.Equal(21m, await BalanceOf(me, Currency.EUR));

        var history = await Json(await Get(caller, "/v1/players/me/transactions"));
        var item = Assert.Single(history.GetProperty("items").EnumerateArray());
        Assert.Equal("DEPOSIT", item.GetProperty("type").GetString());
        Assert.Equal("EUR", item.GetProperty("currency").GetString());
        Assert.Equal("20.00", item.GetProperty("amount").GetString());
    }

    [Fact]
    public async Task Topup_same_key_different_amount_is_409()
    {
        var me = await SeedPlayer();
        var caller = TestCaller.Player(me);
        await AssertStatus(await Topup(caller, new { currency = "USD", amount = "5" }, Key), HttpStatusCode.Created);

        await AssertProblem(await Topup(caller, new { currency = "USD", amount = "6" }, Key),
            HttpStatusCode.Conflict, "DUPLICATE_REFERENCE_MISMATCH");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Topup_without_Idempotency_Key_is_400(string? key)
    {
        var me = await SeedPlayer();

        await AssertProblem(await Topup(TestCaller.Player(me), new { currency = "USD", amount = "5" }, key),
            HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.Equal(100m, await BalanceOf(me));
    }

    [Fact]
    public async Task Topup_with_Idempotency_Key_longer_than_100_is_400()
    {
        var me = await SeedPlayer();

        await AssertProblem(await Topup(TestCaller.Player(me), new { currency = "USD", amount = "5" }, new string('k', 101)),
            HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    [Theory]
    [InlineData("0", "USD")]
    [InlineData("-5", "USD")]
    [InlineData("abc", "USD")]
    [InlineData("1.005", "USD")]
    [InlineData("0.123456789", "BTC")]
    [InlineData("5", "DOGE")]
    [InlineData("5", "")]
    public async Task Topup_invalid_amount_or_currency_is_400(string amount, string currency)
    {
        var me = await SeedPlayer();

        await AssertProblem(await Topup(TestCaller.Player(me), new { currency, amount }, Guid.NewGuid().ToString()),
            HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    [Fact]
    public async Task Topup_that_would_overflow_the_balance_is_400_not_500()
    {
        var me = await SeedPlayer(usd: 99999999999999999999.99m);

        await AssertProblem(await Topup(TestCaller.Player(me), new { currency = "USD", amount = "1" }, Key),
            HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.Equal(99999999999999999999.99m, await BalanceOf(me));
    }

    [Fact]
    public async Task Topup_for_player_without_wallets_is_404()
    {
        await AssertProblem(await Topup(TestCaller.Player(Guid.NewGuid()), new { currency = "USD", amount = "5" }, Key),
            HttpStatusCode.NotFound, "UNKNOWN_PLAYER");
    }

    [Fact]
    public async Task History_is_newest_first_pages_via_NextCursor_and_filters()
    {
        var me = await SeedPlayer(usd: 100m, eur: 100m);
        var refs = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            var r = $"usd-{i}";
            refs.Add(r);
            await AssertStatus(await PostTransaction(Provider, Tx(me, "BET", "1", "USD", r)), HttpStatusCode.Created);
        }
        await AssertStatus(await PostTransaction(Provider, Tx(me, "BET", "1", "EUR", "eur-0")), HttpStatusCode.Created);
        await AssertStatus(await PostTransaction(Provider, Tx(me, "WIN", "3", "USD", "win-0", betReference: "usd-0")), HttpStatusCode.Created);
        // Someone else's activity never shows up.
        var other = await SeedPlayer();
        await PostTransaction(Provider, Tx(other, "BET", "1"));

        var caller = TestCaller.Player(me);
        var all = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var url = "/v1/players/me/transactions?limit=3" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
            var response = await Get(caller, url);
            await AssertStatus(response, HttpStatusCode.OK);
            var page = await Json(response);
            var items = page.GetProperty("items").EnumerateArray().ToList();
            Assert.True(items.Count <= 3);
            all.AddRange(items.Select(x => x.GetProperty("reference").GetString()!));
            cursor = page.GetProperty("nextCursor").ValueKind == JsonValueKind.Null ? null : page.GetProperty("nextCursor").GetString();
            pages++;
        } while (cursor is not null && pages < 10);

        Assert.Equal(["win-0", "eur-0", "usd-4", "usd-3", "usd-2", "usd-1", "usd-0"], all);
        Assert.Equal(3, pages);

        var eurOnly = await Json(await Get(caller, "/v1/players/me/transactions?currency=eur"));
        Assert.Equal(["eur-0"], eurOnly.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("reference").GetString()));

        var wins = await Json(await Get(caller, "/v1/players/me/transactions?type=win&currency=USD"));
        var win = Assert.Single(wins.GetProperty("items").EnumerateArray());
        Assert.Equal("WIN", win.GetProperty("type").GetString());
        Assert.Equal("usd-0", win.GetProperty("betReference").GetString());
        Assert.Equal(JsonValueKind.Null, wins.GetProperty("nextCursor").ValueKind);
    }

    [Theory]
    [InlineData("cursor=garbage!!")]
    [InlineData("cursor=bm90LWEtbnVtYmVy")] // base64("not-a-number")
    [InlineData("cursor=LTU=")]             // base64("-5")
    [InlineData("cursor=MA==")]             // base64("0")
    [InlineData("limit=abc")]
    [InlineData("currency=DOGE")]
    [InlineData("type=JACKPOT")]
    public async Task History_bad_query_is_400(string query)
    {
        var me = await SeedPlayer();

        await AssertProblem(await Get(TestCaller.Player(me), "/v1/players/me/transactions?" + query),
            HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    [Theory]
    [InlineData("limit=0", 1)]
    [InlineData("limit=-3", 1)]
    [InlineData("limit=100000", 25)]
    [InlineData("", 20)]
    public async Task History_limit_is_clamped_to_1_100_default_20(string query, int expectedCount)
    {
        var me = await SeedPlayer(usd: 1000m);
        for (var i = 0; i < 25; i++)
            await PostTransaction(Provider, Tx(me, "BET", "1"));

        var page = await Json(await Get(TestCaller.Player(me), "/v1/players/me/transactions?" + query));

        Assert.Equal(expectedCount, page.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task History_limit_upper_clamp_is_100()
    {
        var me = await SeedPlayer(usd: 1000m);
        await using (var db = Fx.CreateContext())
        {
            var walletId = db.Wallets.Single(w => w.PlayerId == me && w.Currency == Currency.USD).Id;
            for (var i = 0; i < 105; i++)
                db.Transactions.Add(new WalletTransaction
                {
                    Id = Guid.NewGuid(), WalletId = walletId, ProviderId = ProviderId, Reference = $"seed-{i}",
                    Type = "BET", Amount = 1m, BalanceAfter = 1000m - i - 1, CreatedAt = DateTimeOffset.UtcNow,
                });
            await db.SaveChangesAsync();
        }

        var page = await Json(await Get(TestCaller.Player(me), "/v1/players/me/transactions?limit=100000"));

        Assert.Equal(100, page.GetProperty("items").GetArrayLength());
        Assert.NotEqual(JsonValueKind.Null, page.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task OpenApi_document_is_served_and_lists_the_v1_paths()
    {
        var response = await Get(null, "/openapi/v1.json");

        await AssertStatus(response, HttpStatusCode.OK);
        var doc = await Json(response);
        var paths = doc.GetProperty("paths");
        foreach (var path in new[] { "/v1/transactions", "/v1/wallet/topup", "/v1/players/me/wallets",
                     "/v1/players/me/transactions", "/v1/players/{playerId}/wallets", "/v1/players/{playerId}/transactions" })
            Assert.True(paths.TryGetProperty(path, out _), $"missing {path}");
        var schemes = doc.GetProperty("components").GetProperty("securitySchemes");
        Assert.True(schemes.TryGetProperty("provider", out _));
        Assert.True(schemes.TryGetProperty("player", out _));
        Assert.Contains("How to authenticate", doc.GetProperty("info").GetProperty("description").GetString());
    }

    [Fact]
    public async Task Scalar_ui_is_served()
    {
        await AssertStatus(await Get(null, "/scalar"), HttpStatusCode.OK);
    }
}
