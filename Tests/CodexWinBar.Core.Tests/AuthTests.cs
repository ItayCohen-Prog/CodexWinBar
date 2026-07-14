using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using CodexWinBar.Core.Auth;
using Xunit;

namespace CodexWinBar.Core.Tests;

public sealed class OAuthPkceTests
{
    [Fact]
    public void Verifier_is_unpadded_base64url_and_unique()
    {
        var first = OAuthPkce.CreateVerifier();
        var second = OAuthPkce.CreateVerifier();

        Assert.NotEqual(first, second);
        Assert.DoesNotContain('=', first);
        Assert.DoesNotContain('+', first);
        Assert.DoesNotContain('/', first);
        // 64 random bytes → 86 base64url chars.
        Assert.Equal(86, first.Length);
    }

    [Fact]
    public void ChallengeS256_matches_sha256_of_verifier()
    {
        var verifier = OAuthPkce.CreateVerifier();

        var expected = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.Equal(expected, OAuthPkce.ChallengeS256(verifier));
    }

    [Fact]
    public void State_is_unpadded_and_unique()
    {
        var first = OAuthPkce.CreateState();
        var second = OAuthPkce.CreateState();

        Assert.NotEqual(first, second);
        Assert.DoesNotContain('=', first);
    }
}

public sealed class AppCredentialStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "codexwinbar-tests-" + Guid.NewGuid().ToString("N"));

    private AppCredentialStore CreateStore() => new(name =>
        name == "CODEXWINBAR_CREDENTIALS_DIR" ? this.directory : null);

    [Fact]
    public void Save_then_load_round_trips_the_document()
    {
        var store = this.CreateStore();
        const string json = """{"tokens":{"access_token":"abc"}}""";

        store.Save("codex", json);

        Assert.True(store.Exists("codex"));
        Assert.Equal(json, store.Load("codex"));
    }

    [Fact]
    public void Load_returns_null_when_missing()
    {
        Assert.Null(this.CreateStore().Load("nope"));
        Assert.False(this.CreateStore().Exists("nope"));
    }

    [Fact]
    public void Save_overwrites_existing_document()
    {
        var store = this.CreateStore();
        store.Save("claude", "first");
        store.Save("claude", "second");

        Assert.Equal("second", store.Load("claude"));
    }

    [Fact]
    public void Delete_removes_the_document()
    {
        var store = this.CreateStore();
        store.Save("gemini", "x");
        store.Delete("gemini");

        Assert.False(store.Exists("gemini"));
        Assert.Null(store.Load("gemini"));
    }

    [Fact]
    public void Stored_bytes_are_encrypted_not_plaintext()
    {
        var store = this.CreateStore();
        store.Save("codex", "super-secret-token-value");

        var onDisk = File.ReadAllBytes(Path.Combine(this.directory, "codex.dat"));
        var asText = Encoding.UTF8.GetString(onDisk);
        Assert.DoesNotContain("super-secret-token-value", asText);
    }

    [Fact]
    public void Default_directory_migrates_credentials_out_of_legacy_install_root()
    {
        var localAppData = Path.Combine(this.directory, "local");
        var legacy = Path.Combine(localAppData, "CodexWinBar", "credentials");
        Directory.CreateDirectory(legacy);
        File.WriteAllBytes(Path.Combine(legacy, "codex.dat"), [1, 2, 3]);
        var store = new AppCredentialStore(name => name switch
        {
            "LOCALAPPDATA" => localAppData,
            _ => null,
        });

        var resolved = store.ResolveDirectory();

        Assert.Equal(Path.Combine(localAppData, "CodexWinBarData", "credentials"), resolved);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(resolved, "codex.dat")));
    }

    public void Dispose()
    {
        if (Directory.Exists(this.directory))
        {
            Directory.Delete(this.directory, recursive: true);
        }
    }
}

public sealed class LoopbackHttpListenerTests
{
    [Fact]
    public async Task Invalid_callback_is_answered_but_the_wait_continues_until_a_valid_one()
    {
        using var listener = new LoopbackHttpListener(0, "/cb");
        var port = listener.Port;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Complete only when a code is present; otherwise keep waiting (a stray/racing request).
        var waitTask = listener.WaitForCallbackAsync(
            query => query.ContainsKey("code")
                ? CallbackResult.Done(LoopbackResponse.Html(200, "ok"))
                : CallbackResult.KeepWaiting(LoopbackResponse.Html(400, "bad")),
            cts.Token);

        // A stray hit with no code is answered (400) and must NOT complete the wait.
        var strayResponse = await SendGetAsync(port, "/cb?state=abc");
        Assert.Contains("400", strayResponse);
        Assert.False(waitTask.IsCompleted);

        // A request to another path 404s and must NOT complete the wait either.
        var otherPathResponse = await SendGetAsync(port, "/nope");
        Assert.Contains("404", otherPathResponse);
        Assert.False(waitTask.IsCompleted);

        // The genuine callback completes the wait and its query is returned.
        var goodResponse = await SendGetAsync(port, "/cb?code=the-code&state=abc");
        Assert.Contains("200", goodResponse);

        var result = await waitTask;
        Assert.Equal("the-code", result["code"]);
    }

    private static async Task<string> SendGetAsync(int port, string target)
    {
        using var client = new TcpClient { ReceiveTimeout = 5000 };
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        var request = Encoding.ASCII.GetBytes($"GET {target} HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(request);
        await stream.FlushAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }
}
