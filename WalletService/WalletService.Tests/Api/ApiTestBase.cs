using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WalletService.Domain;

namespace WalletService.Tests.Api;

/// <summary>Shared helpers for API tests: per-request callers, wallet seeding and problem-body assertions.</summary>
public abstract class ApiTestBase(SqlServerFixture fx)
{
    protected readonly SqlServerFixture Fx = fx;
    private readonly HttpClient _client = fx.Api.CreateClient();

    // Unique per test instance so tests sharing the database never collide on (ProviderId, Reference).
    protected readonly string ProviderId = "provider-" + Guid.NewGuid().ToString("N");

    protected TestCaller Provider => TestCaller.Provider(ProviderId);

    // A fresh Idempotency-Key per test instance (keys are scoped per player, so this is just tidiness).
    protected readonly string Key = "key-" + Guid.NewGuid().ToString("N");

    protected async Task<HttpResponseMessage> Send(
        TestCaller? caller, HttpMethod method, string url, object? body = null,
        IDictionary<string, string>? headers = null)
    {
        using var request = new HttpRequestMessage(method, url);
        caller?.Apply(request);
        if (body is string raw)
            request.Content = new StringContent(raw, System.Text.Encoding.UTF8, "application/json");
        else if (body is not null)
            request.Content = JsonContent.Create(body);
        foreach (var (k, v) in headers ?? new Dictionary<string, string>())
            request.Headers.Add(k, v);
        return await _client.SendAsync(request);
    }

    protected Task<HttpResponseMessage> Get(TestCaller? caller, string url) => Send(caller, HttpMethod.Get, url);

    protected Task<HttpResponseMessage> PostTransaction(TestCaller? caller, object body) =>
        Send(caller, HttpMethod.Post, "/v1/transactions", body);

    protected Task<HttpResponseMessage> Topup(TestCaller? caller, object body, string? idempotencyKey) =>
        Send(caller, HttpMethod.Post, "/v1/wallet/topup", body,
            idempotencyKey is null ? null : new Dictionary<string, string> { ["Idempotency-Key"] = idempotencyKey });

    protected static object Tx(Guid player, string type, string amount, string currency = "USD",
        string? reference = null, string? betReference = null) =>
        new { reference = reference ?? Guid.NewGuid().ToString("N"), type, playerId = player, currency, amount, betReference };

    /// <summary>Seeds the three wallets a registered player has.</summary>
    protected async Task<Guid> SeedPlayer(decimal usd = 100m, decimal eur = 0m, decimal btc = 0m)
    {
        var player = Guid.NewGuid();
        await using var db = Fx.CreateContext();
        db.Wallets.AddRange(
            new Wallet { Id = Guid.NewGuid(), PlayerId = player, Currency = Currency.USD, Balance = usd },
            new Wallet { Id = Guid.NewGuid(), PlayerId = player, Currency = Currency.EUR, Balance = eur },
            new Wallet { Id = Guid.NewGuid(), PlayerId = player, Currency = Currency.BTC, Balance = btc });
        await db.SaveChangesAsync();
        return player;
    }

    protected async Task<decimal> BalanceOf(Guid player, Currency currency = Currency.USD)
    {
        await using var db = Fx.CreateContext();
        return await db.Wallets.Where(w => w.PlayerId == player && w.Currency == currency)
            .Select(w => w.Balance).SingleAsync();
    }

    protected static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>Asserts an RFC 7807 body with the given status and stable <c>code</c>.</summary>
    protected static async Task<JsonElement> AssertProblem(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(status == response.StatusCode, $"Expected {(int)status} {code}, got {(int)response.StatusCode}: {text}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var json = JsonDocument.Parse(text).RootElement.Clone();
        Assert.Equal(code, json.GetProperty("code").GetString());
        Assert.Equal((int)status, json.GetProperty("status").GetInt32());
        return json;
    }

    protected static async Task AssertStatus(HttpResponseMessage response, HttpStatusCode status)
    {
        if (response.StatusCode != status)
            Assert.Fail($"Expected {(int)status}, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }
}
