using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace UsersService.Tests.Api;

[Collection(SqlServerCollection.Name)]
public class UsersApiTests(SqlServerFixture fx)
{
    private readonly HttpClient _client = fx.Api.CreateClient();
    private UsersApiFactory Api => fx.Api;

    private static string NewEmail() => $"api-{Guid.NewGuid():N}@example.com";

    private Task<HttpResponseMessage> PostJson(string url, object? body) =>
        _client.PostAsync(url, body is string raw
            ? new StringContent(raw, System.Text.Encoding.UTF8, "application/json")
            : JsonContent.Create(body));

    private Task<HttpResponseMessage> Register(string email, string password = "Secret123!", string displayName = "Player") =>
        PostJson("/v1/users", new { email, password, displayName });

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task AssertProblem(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(status == response.StatusCode, $"Expected {(int)status} {code}, got {(int)response.StatusCode}: {text}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var json = JsonDocument.Parse(text).RootElement;
        Assert.Equal(code, json.GetProperty("code").GetString());
        Assert.Equal((int)status, json.GetProperty("status").GetInt32());
    }

    private static async Task AssertStatus(HttpResponseMessage response, HttpStatusCode status)
    {
        if (response.StatusCode != status)
            Assert.Fail($"Expected {(int)status}, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    [Fact]
    public async Task Post_users_returns_202_with_user_id_and_sends_link_to_public_base_url()
    {
        var email = NewEmail();

        var response = await Register(email);

        await AssertStatus(response, HttpStatusCode.Accepted);
        var userId = (await Json(response)).GetProperty("userId").GetGuid();
        Assert.Equal(userId, Assert.Single(Api.Identity.IdentitiesFor(email)).Id);
        Assert.StartsWith($"{UsersApiFactory.PublicBaseUrl}/v1/users/verify?token=",
            Assert.Single(Api.Email.SentTo(email)).VerifyUrl);
    }

    [Fact]
    public async Task Post_users_duplicate_email_in_any_case_returns_409()
    {
        var email = NewEmail();
        await AssertStatus(await Register(email), HttpStatusCode.Accepted);

        await AssertProblem(await Register(email), HttpStatusCode.Conflict, "EMAIL_ALREADY_REGISTERED");
        await AssertProblem(await Register(email.ToUpperInvariant()), HttpStatusCode.Conflict, "EMAIL_ALREADY_REGISTERED");
        Assert.Single(Api.Identity.IdentitiesFor(email));
    }

    [Theory]
    [InlineData("""{"email":"nope","password":"Secret123!","displayName":"P"}""")]
    [InlineData("""{"email":"a@example.com","password":"1234567","displayName":"P"}""")]
    [InlineData("""{"email":"a@example.com","password":"Secret123!","displayName":""}""")]
    [InlineData("""{"email":"a@example.com","password":"Secret123!"}""")]
    [InlineData("""{"password":"Secret123!","displayName":"P"}""")]
    [InlineData("""{"email":42,"password":"Secret123!","displayName":"P"}""")]
    [InlineData("""{"email":""")]
    [InlineData("")]
    [InlineData("null")]
    public async Task Post_users_invalid_body_returns_400_validation_failed(string body)
    {
        await AssertProblem(await PostJson("/v1/users", body), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    [Fact]
    public async Task Post_users_display_name_over_100_chars_returns_400()
    {
        await AssertProblem(await Register(NewEmail(), displayName: new string('x', 101)),
            HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    [Fact]
    public async Task Post_verify_returns_204_and_is_idempotent()
    {
        var email = NewEmail();
        var userId = (await Json(await Register(email))).GetProperty("userId").GetGuid();
        var token = Api.Email.TokenFor(email);

        await AssertStatus(await PostJson("/v1/users/verify", new { token }), HttpStatusCode.NoContent);
        await AssertStatus(await PostJson("/v1/users/verify", new { token }), HttpStatusCode.NoContent);

        Assert.True(Api.Identity.Get(userId)!.Enabled);
        await using var db = fx.CreateContext();
        Assert.NotNull((await db.Users.SingleAsync(u => u.Id == userId)).EmailVerifiedAt);
    }

    [Fact]
    public async Task Get_verify_link_from_email_returns_200_verified()
    {
        var email = NewEmail();
        var userId = (await Json(await Register(email))).GetProperty("userId").GetGuid();
        var link = new Uri(Api.Email.SentTo(email).Single().VerifyUrl);

        var response = await _client.GetAsync(link.PathAndQuery);

        await AssertStatus(response, HttpStatusCode.OK);
        Assert.Equal("verified", (await Json(response)).GetProperty("status").GetString());
        Assert.True(Api.Identity.Get(userId)!.Enabled);

        await AssertStatus(await _client.GetAsync(link.PathAndQuery), HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("""{"token":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"}""")]
    [InlineData("""{"token":""}""")]
    [InlineData("""{"token":"%%%"}""")]
    public async Task Post_verify_unknown_token_returns_400_invalid_token(string body)
    {
        await AssertProblem(await PostJson("/v1/users/verify", body), HttpStatusCode.BadRequest, "INVALID_TOKEN");
    }

    [Theory]
    [InlineData("/v1/users/verify?token=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("/v1/users/verify?token=")]
    [InlineData("/v1/users/verify")]
    public async Task Get_verify_unknown_or_missing_token_returns_400_invalid_token(string url)
    {
        await AssertProblem(await _client.GetAsync(url), HttpStatusCode.BadRequest, "INVALID_TOKEN");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("")]
    [InlineData("""{"token":5}""")]
    public async Task Post_verify_missing_or_malformed_body_returns_400_validation_failed(string body)
    {
        await AssertProblem(await PostJson("/v1/users/verify", body), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    [Fact]
    public async Task OpenApi_document_describes_both_endpoints()
    {
        var response = await _client.GetAsync("/openapi/v1.json");

        await AssertStatus(response, HttpStatusCode.OK);
        var paths = (await Json(response)).GetProperty("paths");
        Assert.True(paths.TryGetProperty("/v1/users", out var users));
        Assert.True(users.GetProperty("post").GetProperty("responses").TryGetProperty("409", out _));
        Assert.True(paths.TryGetProperty("/v1/users/verify", out var verify));
        Assert.True(verify.TryGetProperty("post", out _));
        Assert.True(verify.TryGetProperty("get", out _));
    }
}
