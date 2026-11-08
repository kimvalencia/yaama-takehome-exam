using System.Net;
using WalletService.Domain;

namespace WalletService.Tests.Api;

[Collection(SqlServerCollection.Name)]
public class TransactionsApiTests(SqlServerFixture fx) : ApiTestBase(fx)
{
    [Fact]
    public async Task Provider_post_returns_201_then_200_with_Replayed_true()
    {
        var player = await SeedPlayer(usd: 100m);
        var body = Tx(player, "BET", "12.50", reference: "bet-1");

        var first = await PostTransaction(Provider, body);
        var second = await PostTransaction(Provider, body);

        await AssertStatus(first, HttpStatusCode.Created);
        await AssertStatus(second, HttpStatusCode.OK);
        var a = await Json(first);
        var b = await Json(second);
        Assert.False(a.GetProperty("replayed").GetBoolean());
        Assert.True(b.GetProperty("replayed").GetBoolean());
        Assert.Equal(a.GetProperty("id").GetString(), b.GetProperty("id").GetString());
        Assert.Equal("bet-1", a.GetProperty("reference").GetString());
        Assert.Equal("BET", a.GetProperty("type").GetString());
        Assert.Equal("USD", a.GetProperty("currency").GetString());
        Assert.Equal("12.50", a.GetProperty("amount").GetString());
        Assert.Equal("87.50", a.GetProperty("balanceAfter").GetString());
        Assert.Equal("87.50", b.GetProperty("balanceAfter").GetString());
        Assert.Equal(87.50m, await BalanceOf(player));
    }

    [Fact]
    public async Task Win_settles_a_bet_and_echoes_betReference()
    {
        var player = await SeedPlayer(usd: 100m);
        await AssertStatus(await PostTransaction(Provider, Tx(player, "BET", "10", reference: "bet-9")), HttpStatusCode.Created);

        var win = await PostTransaction(Provider, Tx(player, "WIN", "25.00", betReference: "bet-9"));

        await AssertStatus(win, HttpStatusCode.Created);
        var json = await Json(win);
        Assert.Equal("bet-9", json.GetProperty("betReference").GetString());
        Assert.Equal("115.00", json.GetProperty("balanceAfter").GetString());
    }

    [Fact]
    public async Task Insufficient_funds_is_422_problem_with_code()
    {
        var player = await SeedPlayer(usd: 10m);

        var response = await PostTransaction(Provider, Tx(player, "BET", "10.01"));

        var problem = await AssertProblem(response, (HttpStatusCode)422, "INSUFFICIENT_FUNDS");
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("title").GetString()));
        Assert.Equal(10m, await BalanceOf(player));
    }

    [Fact]
    public async Task Rejections_map_to_their_statuses()
    {
        var player = await SeedPlayer(usd: 100m);
        await PostTransaction(Provider, Tx(player, "BET", "10", reference: "bet-1"));
        await PostTransaction(Provider, Tx(player, "WIN", "5", reference: "win-1", betReference: "bet-1"));

        await AssertProblem(await PostTransaction(Provider, Tx(Guid.NewGuid(), "BET", "1")), HttpStatusCode.NotFound, "UNKNOWN_PLAYER");
        await AssertProblem(await PostTransaction(Provider, Tx(player, "BET", "11", reference: "bet-1")), HttpStatusCode.Conflict, "DUPLICATE_REFERENCE_MISMATCH");
        await AssertProblem(await PostTransaction(Provider, Tx(player, "WIN", "1", betReference: "nope")), (HttpStatusCode)422, "UNKNOWN_BET");
        await AssertProblem(await PostTransaction(Provider, Tx(player, "WIN", "1", betReference: "bet-1")), (HttpStatusCode)422, "BET_ALREADY_SETTLED");
        await AssertProblem(await PostTransaction(Provider, Tx(player, "WIN", "1")), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    [Fact]
    public async Task Provider_id_comes_from_azp_not_body()
    {
        var player = await SeedPlayer(usd: 100m);
        var otherProvider = TestCaller.Provider("provider-" + Guid.NewGuid().ToString("N"));
        var body = new { reference = "shared-ref", type = "BET", playerId = player, currency = "USD", amount = "1", providerId = "evil" };

        // Same reference from two providers is two distinct transactions.
        await AssertStatus(await PostTransaction(Provider, body), HttpStatusCode.Created);
        await AssertStatus(await PostTransaction(otherProvider, body), HttpStatusCode.Created);
        Assert.Equal(98m, await BalanceOf(player));
    }

    [Theory]
    [InlineData("0", "USD")]
    [InlineData("-5", "USD")]
    [InlineData("abc", "USD")]
    [InlineData("", "USD")]
    [InlineData(" ", "USD")]
    [InlineData("1.005", "USD")]
    [InlineData("1e3", "USD")]
    [InlineData("1,5", "EUR")]
    [InlineData("+5", "USD")]
    [InlineData("0.123456789", "BTC")]
    [InlineData("100000000000000000000", "USD")]       // 21 integer digits: does not fit decimal(28,8)
    [InlineData("99999999999999999999999999999999", "USD")] // beyond System.Decimal
    [InlineData("NaN", "USD")]
    public async Task Invalid_amount_is_400_VALIDATION_FAILED(string amount, string currency)
    {
        var player = await SeedPlayer(usd: 100m, btc: 1m);

        var response = await PostTransaction(Provider, Tx(player, "BET", amount, currency));

        await AssertProblem(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.Equal(100m, await BalanceOf(player));
    }

    [Fact]
    public async Task Amount_sent_as_json_number_is_400()
    {
        var player = await SeedPlayer();
        var raw = $$"""{"reference":"r1","type":"BET","playerId":"{{player}}","currency":"USD","amount":12.5}""";

        await AssertProblem(await Send(Provider, HttpMethod.Post, "/v1/transactions", raw), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    [Theory]
    [InlineData("""{"type":"BET","playerId":"00000000-0000-0000-0000-000000000001","currency":"USD","amount":"1"}""")]
    [InlineData("""{"reference":"r","type":"BET","playerId":"not-a-guid","currency":"USD","amount":"1"}""")]
    [InlineData("""{"reference":"r","type":"BET","playerId":"00000000-0000-0000-0000-000000000000","currency":"USD","amount":"1"}""")]
    [InlineData("""{"reference":"r","type":"BET","currency":"USD","amount":"1"}""")]
    [InlineData("""{"reference":"r","playerId":"00000000-0000-0000-0000-000000000001","currency":"USD","amount":"1"}""")]
    [InlineData("""not json""")]
    [InlineData("")]
    public async Task Malformed_body_is_400_VALIDATION_FAILED(string raw)
    {
        await AssertProblem(await Send(Provider, HttpMethod.Post, "/v1/transactions", raw), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    [Fact]
    public async Task Currency_and_type_are_case_insensitive_and_normalised()
    {
        var player = await SeedPlayer(usd: 100m);

        var response = await PostTransaction(Provider, Tx(player, "bet", "5", "usd"));

        await AssertStatus(response, HttpStatusCode.Created);
        var json = await Json(response);
        Assert.Equal("BET", json.GetProperty("type").GetString());
        Assert.Equal("USD", json.GetProperty("currency").GetString());
        Assert.Equal(95m, await BalanceOf(player));
    }

    [Theory]
    [InlineData("BET", "DOGE")]
    [InlineData("BET", "")]
    [InlineData("BET", "1")]
    [InlineData("REFUND", "USD")]
    [InlineData("JACKPOT", "USD")]
    [InlineData("DEPOSIT", "USD")] // DEPOSIT is the player top-up type, not a provider type
    [InlineData("", "USD")]
    public async Task Unknown_currency_or_type_is_400(string type, string currency)
    {
        var player = await SeedPlayer(usd: 100m);

        var problem = await AssertProblem(await PostTransaction(Provider, Tx(player, type, "5", currency)),
            HttpStatusCode.BadRequest, "VALIDATION_FAILED");

        Assert.False(string.IsNullOrEmpty(problem.GetProperty("detail").GetString()));
        Assert.Equal(100m, await BalanceOf(player));
    }

    [Fact]
    public async Task Reference_or_betReference_longer_than_100_is_400()
    {
        var player = await SeedPlayer(usd: 100m);

        await AssertProblem(await PostTransaction(Provider, Tx(player, "BET", "1", reference: new string('r', 101))),
            HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        await AssertProblem(await PostTransaction(Provider, Tx(player, "WIN", "1", betReference: new string('b', 101))),
            HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        await AssertStatus(await PostTransaction(Provider, Tx(player, "BET", "1", reference: new string('r', 100))),
            HttpStatusCode.Created);
    }

    [Fact]
    public async Task Provider_id_longer_than_100_is_400()
    {
        var player = await SeedPlayer(usd: 100m);

        await AssertProblem(await PostTransaction(TestCaller.Provider(new string('p', 101)), Tx(player, "BET", "1")),
            HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.Equal(100m, await BalanceOf(player));
    }

    [Fact]
    public async Task Credit_that_would_overflow_the_balance_is_400_not_500()
    {
        var player = await SeedPlayer(usd: 99999999999999999999.99m);
        await AssertStatus(await PostTransaction(Provider, Tx(player, "BET", "1", reference: "big-bet")), HttpStatusCode.Created);

        var response = await PostTransaction(Provider, Tx(player, "WIN", "99999999999999999999", betReference: "big-bet"));

        await AssertProblem(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.Equal(99999999999999999998.99m, await BalanceOf(player));
    }
}
