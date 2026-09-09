using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SNM.Master.Tests;

public class AuthApiTests(MasterFactory factory) : IClassFixture<MasterFactory>
{
    [Fact]
    public async Task LoginRefreshAndUserDetailFollowTheTemplateContract()
    {
        var client = factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = MasterFactory.AdminPassword });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("code").GetInt32());
        var data = body.GetProperty("data");
        Assert.Equal("Bearer", data.GetProperty("tokenType").GetString());
        Assert.True(data.GetProperty("expiresIn").GetInt32() > 0);
        var refresh = data.GetProperty("refreshToken").GetString()!;

        // refresh rotates the token; the old one is rejected afterwards
        var r2 = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = refresh });
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        var r3 = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = refresh });
        Assert.Equal(HttpStatusCode.Unauthorized, r3.StatusCode);
        Assert.Equal(10011, (await r3.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetInt32());

        var auth = await factory.LoginAsync();
        var detail = await MasterFactory.DataAsync(await auth.GetAsync("/api/user/detail"));
        Assert.Equal("admin", detail.GetProperty("username").GetString());
        Assert.Equal("SUPER_ADMIN", detail.GetProperty("currentRole").GetProperty("code").GetString());
        Assert.True(detail.GetProperty("profile").GetProperty("avatar").GetString()!.Length > 0);

        var tree = await MasterFactory.DataAsync(await auth.GetAsync("/api/role/permissions/tree"));
        Assert.Contains(tree.EnumerateArray(), m => m.GetProperty("code").GetString() == "Home" && m.GetProperty("path").GetString() == "/");
        Assert.True((await MasterFactory.DataAsync(await auth.GetAsync("/api/permission/menu/validate?path=/nodes/12"))).GetBoolean());
        Assert.False((await MasterFactory.DataAsync(await auth.GetAsync("/api/permission/menu/validate?path=/nope"))).GetBoolean());
    }

    [Fact]
    public async Task WrongPasswordAndMissingTokenUseTheEnvelope()
    {
        var client = factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "nope-nope-nope" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(10001, (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetInt32());

        var unauth = await client.GetAsync("/api/nodes");
        Assert.Equal(HttpStatusCode.Unauthorized, unauth.StatusCode);
        var env = await unauth.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(401, env.GetProperty("code").GetInt32());

        var missing = await client.GetAsync("/api/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(404, (await missing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task PasswordChangeRequiresOldPassword()
    {
        var auth = await factory.LoginAsync();
        var bad = await auth.PostAsJsonAsync("/api/auth/password", new { oldPassword = "wrong", newPassword = "another-good-pass" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(10010, (await bad.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetInt32());
        var tooShort = await auth.PostAsJsonAsync("/api/auth/password", new { oldPassword = MasterFactory.AdminPassword, newPassword = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);
        var errors = (await tooShort.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("errors");
        Assert.True(errors.TryGetProperty("newPassword", out _));
    }
}
