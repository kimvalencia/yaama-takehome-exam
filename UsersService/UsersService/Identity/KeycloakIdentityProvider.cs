using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace UsersService.Identity;

public sealed class KeycloakOptions
{
    public const string Section = "Keycloak";

    public string BaseUrl { get; set; } = "http://keycloak:8080";
    public string Realm { get; set; } = "gaming";
    public string ClientId { get; set; } = "users-api";
    public string ClientSecret { get; set; } = "users-api-secret";
    public string PlayerRole { get; set; } = "player";
}

/// <summary>
/// Keycloak admin REST API client. Authenticates as the confidential client <c>users-api</c>
/// (client credentials; its service account has realm-management <c>manage-users</c> and <c>view-realm</c>).
/// </summary>
public sealed class KeycloakIdentityProvider(HttpClient http, IOptions<KeycloakOptions> options) : IIdentityProvider
{
    private readonly KeycloakOptions _o = options.Value;

    private string Admin => $"{_o.BaseUrl.TrimEnd('/')}/admin/realms/{Uri.EscapeDataString(_o.Realm)}";

    public async Task<Guid> CreateDisabledUserAsync(string email, string password, string displayName, CancellationToken ct)
    {
        var token = await GetAdminTokenAsync(ct);
        var username = email.ToLowerInvariant();

        var representation = new JsonObject
        {
            ["username"] = username,
            ["email"] = username,
            ["enabled"] = false,
            ["emailVerified"] = false,
            // Keycloak's default user profile wants first/last name; without them the first login is
            // blocked by the "verify profile" required action. The display name fills both.
            ["firstName"] = displayName,
            ["lastName"] = displayName,
            ["credentials"] = new JsonArray(new JsonObject
            {
                ["type"] = "password",
                ["value"] = password,
                ["temporary"] = false,
            }),
        };

        Guid userId;
        using (var create = await SendAsync(HttpMethod.Post, $"{Admin}/users", token, representation, ct))
        {
            if (create.StatusCode == HttpStatusCode.Created)
            {
                userId = IdFromLocation(create)
                         ?? await FindUserIdByEmailAsync(username, token, ct)
                         ?? throw new InvalidOperationException("Keycloak created the user but its id could not be read.");
            }
            else if (create.StatusCode == HttpStatusCode.Conflict)
            {
                // A previous attempt already created this identity: reuse it.
                userId = await FindUserIdByEmailAsync(username, token, ct)
                         ?? throw new InvalidOperationException($"Keycloak reported a conflict for {username} but no such user was found.");
            }
            else if (create.StatusCode == HttpStatusCode.BadRequest)
            {
                // e.g. a realm password policy violation.
                throw new IdentityRejectedException(await ErrorMessageAsync(create, ct));
            }
            else
            {
                await EnsureSuccessAsync(create, "create user", ct);
                throw new InvalidOperationException("unreachable");
            }
        }

        await AssignPlayerRoleAsync(userId, token, ct);
        return userId;
    }

    public async Task EnableUserAsync(Guid userId, CancellationToken ct)
    {
        var token = await GetAdminTokenAsync(ct);
        var url = $"{Admin}/users/{userId:D}";

        // Read-modify-write the full representation so the PUT does not drop profile attributes.
        JsonObject user;
        using (var get = await SendAsync(HttpMethod.Get, url, token, null, ct))
        {
            await EnsureSuccessAsync(get, "read user", ct);
            user = (await get.Content.ReadFromJsonAsync<JsonObject>(ct))!;
        }

        if (user["enabled"]?.GetValue<bool>() == true && user["emailVerified"]?.GetValue<bool>() == true)
            return;

        user["enabled"] = true;
        user["emailVerified"] = true;
        using var put = await SendAsync(HttpMethod.Put, url, token, user, ct);
        await EnsureSuccessAsync(put, "enable user", ct);
    }

    private async Task AssignPlayerRoleAsync(Guid userId, string token, CancellationToken ct)
    {
        JsonObject role;
        using (var get = await SendAsync(HttpMethod.Get, $"{Admin}/roles/{Uri.EscapeDataString(_o.PlayerRole)}", token, null, ct))
        {
            await EnsureSuccessAsync(get, "read the player role", ct);
            role = (await get.Content.ReadFromJsonAsync<JsonObject>(ct))!;
        }

        // Adding a mapping the user already has is a no-op in Keycloak, so this is safe on the reuse path.
        using var map = await SendAsync(HttpMethod.Post, $"{Admin}/users/{userId:D}/role-mappings/realm", token,
            new JsonArray(role), ct);
        await EnsureSuccessAsync(map, "assign the player role", ct);
    }

    private async Task<Guid?> FindUserIdByEmailAsync(string email, string token, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get,
            $"{Admin}/users?email={Uri.EscapeDataString(email)}&exact=true&briefRepresentation=true", token, null, ct);
        await EnsureSuccessAsync(response, "search users", ct);
        var users = await response.Content.ReadFromJsonAsync<JsonArray>(ct) ?? [];
        var match = users.OfType<JsonObject>().FirstOrDefault(u =>
            string.Equals(u["email"]?.GetValue<string>(), email, StringComparison.OrdinalIgnoreCase)
            || string.Equals(u["username"]?.GetValue<string>(), email, StringComparison.OrdinalIgnoreCase));
        return match?["id"]?.GetValue<string>() is { } id ? Guid.Parse(id) : null;
    }

    private async Task<string> GetAdminTokenAsync(CancellationToken ct)
    {
        var url = $"{_o.BaseUrl.TrimEnd('/')}/realms/{Uri.EscapeDataString(_o.Realm)}/protocol/openid-connect/token";
        using var response = await http.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = _o.ClientId,
            ["client_secret"] = _o.ClientSecret,
        }), ct);
        await EnsureSuccessAsync(response, "get an admin token", ct);
        var body = await response.Content.ReadFromJsonAsync<JsonObject>(ct);
        return body?["access_token"]?.GetValue<string>()
               ?? throw new InvalidOperationException("Keycloak token response had no access_token.");
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string token, JsonNode? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await http.SendAsync(request, ct);
    }

    private static Guid? IdFromLocation(HttpResponseMessage response) =>
        response.Headers.Location is { } location && Guid.TryParse(location.Segments[^1].TrimEnd('/'), out var id)
            ? id
            : null;

    private static async Task<string> ErrorMessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        try
        {
            var json = JsonNode.Parse(text);
            return json?["error_description"]?.GetValue<string>() ?? json?["errorMessage"]?.GetValue<string>()
                   ?? json?["error"]?.GetValue<string>() ?? "The identity provider rejected the registration.";
        }
        catch (JsonException)
        {
            return "The identity provider rejected the registration.";
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string action, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var text = await response.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException(
            $"Keycloak failed to {action}: {(int)response.StatusCode} {response.ReasonPhrase} {text}", null, response.StatusCode);
    }
}
