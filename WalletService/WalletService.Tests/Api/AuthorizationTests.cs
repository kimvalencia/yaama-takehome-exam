using System.Net;
using WalletService.Domain;

namespace WalletService.Tests.Api;

[Collection(SqlServerCollection.Name)]
public class AuthorizationTests(SqlServerFixture fx) : ApiTestBase(fx)
{
    [Fact]
    public async Task Anonymous_requests_are_401()
    {
        var player = await SeedPlayer();

        await AssertStatus(await PostTransaction(null, Tx(player, "BET", "1")), HttpStatusCode.Unauthorized);
        await AssertStatus(await Get(null, "/v1/players/me/wallets"), HttpStatusCode.Unauthorized);
        await AssertStatus(await Get(null, "/v1/players/me/transactions"), HttpStatusCode.Unauthorized);
        await AssertStatus(await Topup(null, new { currency = "USD", amount = "1" }, Key), HttpStatusCode.Unauthorized);
        await AssertStatus(await Get(null, $"/v1/players/{player}/wallets"), HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Player_token_on_provider_endpoint_is_403()
    {
        var player = await SeedPlayer();

        await AssertStatus(await PostTransaction(TestCaller.Player(player), Tx(player, "WIN", "1000", betReference: "x")),
            HttpStatusCode.Forbidden);
        Assert.Equal(100m, await BalanceOf(player));
    }

    [Fact]
    public async Task Provider_token_on_player_routes_is_403()
    {
        var player = await SeedPlayer();

        await AssertStatus(await Get(Provider, "/v1/players/me/wallets"), HttpStatusCode.Forbidden);
        await AssertStatus(await Get(Provider, "/v1/players/me/transactions"), HttpStatusCode.Forbidden);
        await AssertStatus(await Topup(Provider, new { currency = "USD", amount = "1" }, Key), HttpStatusCode.Forbidden);
        await AssertStatus(await Get(Provider, $"/v1/players/{player}/wallets"), HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Provider_token_without_azp_is_403()
    {
        var player = await SeedPlayer();

        await AssertStatus(await PostTransaction(TestCaller.ProviderWithoutAzp(), Tx(player, "BET", "1")), HttpStatusCode.Forbidden);
        Assert.Equal(100m, await BalanceOf(player));
    }

    [Fact]
    public async Task Other_players_id_on_admin_route_is_403_for_player_and_200_for_admin()
    {
        var me = await SeedPlayer();
        var other = await SeedPlayer(usd: 7m);
        await PostTransaction(Provider, Tx(other, "BET", "2", reference: "other-bet"));

        await AssertStatus(await Get(TestCaller.Player(me), $"/v1/players/{other}/wallets"), HttpStatusCode.Forbidden);
        await AssertStatus(await Get(TestCaller.Player(me), $"/v1/players/{other}/transactions"), HttpStatusCode.Forbidden);
        // The admin-style route is admin-only, even for the caller's own id.
        await AssertStatus(await Get(TestCaller.Player(me), $"/v1/players/{me}/wallets"), HttpStatusCode.Forbidden);

        var wallets = await Get(TestCaller.Admin(), $"/v1/players/{other}/wallets");
        await AssertStatus(wallets, HttpStatusCode.OK);
        var usd = (await Json(wallets)).EnumerateArray().Single(w => w.GetProperty("currency").GetString() == "USD");
        Assert.Equal("5.00", usd.GetProperty("balance").GetString());

        var history = await Get(TestCaller.Admin(), $"/v1/players/{other}/transactions?type=bet");
        await AssertStatus(history, HttpStatusCode.OK);
        var item = Assert.Single((await Json(history)).GetProperty("items").EnumerateArray());
        Assert.Equal("other-bet", item.GetProperty("reference").GetString());
    }

    [Fact]
    public async Task Admin_route_for_unknown_player_is_404()
    {
        await AssertProblem(await Get(TestCaller.Admin(), $"/v1/players/{Guid.NewGuid()}/wallets"),
            HttpStatusCode.NotFound, "UNKNOWN_PLAYER");
    }

    [Fact]
    public async Task Admin_route_with_bad_query_is_400()
    {
        var other = await SeedPlayer();

        await AssertProblem(await Get(TestCaller.Admin(), $"/v1/players/{other}/transactions?cursor=%%%"),
            HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    [Fact]
    public async Task Admin_without_player_role_cannot_use_me_routes()
    {
        await AssertStatus(await Get(TestCaller.Admin(), "/v1/players/me/wallets"), HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Role_check_tolerates_extra_keycloak_roles_and_requires_membership()
    {
        var me = await SeedPlayer();
        var onlyDefaultRoles = new TestCaller(Sub: me.ToString(), Roles: "default-roles-gaming,offline_access,uma_authorization", EmailVerified: "true");

        await AssertStatus(await Get(onlyDefaultRoles, "/v1/players/me/wallets"), HttpStatusCode.Forbidden);
        await AssertStatus(await Get(TestCaller.Player(me), "/v1/players/me/wallets"), HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("false")]
    [InlineData(null)]
    public async Task Topup_without_verified_email_is_403_EMAIL_NOT_VERIFIED(string? emailVerified)
    {
        var me = await SeedPlayer();
        var caller = new TestCaller(Sub: me.ToString(), Roles: "player,default-roles-gaming", Azp: "wallet-web", EmailVerified: emailVerified);

        await AssertProblem(await Topup(caller, new { currency = "USD", amount = "5" }, Key),
            HttpStatusCode.Forbidden, "EMAIL_NOT_VERIFIED");
        Assert.Equal(100m, await BalanceOf(me));
        // Reading is still allowed; only money-in requires a verified email.
        await AssertStatus(await Get(caller, "/v1/players/me/wallets"), HttpStatusCode.OK);
    }

    [Fact]
    public async Task Topup_with_non_guid_sub_is_403()
    {
        var caller = new TestCaller(Sub: "service-account-xyz", Roles: "player", EmailVerified: "true");

        await AssertStatus(await Topup(caller, new { currency = "USD", amount = "5" }, Key), HttpStatusCode.Forbidden);
    }
}
