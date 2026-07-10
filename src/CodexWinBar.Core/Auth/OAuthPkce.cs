using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace CodexWinBar.Core.Auth;

/// <summary>RFC 7636 PKCE and OAuth state helpers shared by the provider sign-in flows.</summary>
public static class OAuthPkce
{
    /// <summary>Creates a 64-byte code verifier encoded as unpadded base64url (86 chars).</summary>
    public static string CreateVerifier() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(64));

    /// <summary>Computes the S256 code challenge for <paramref name="verifier"/>.</summary>
    public static string ChallengeS256(string verifier) =>
        Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>Creates a 32-byte CSRF state value encoded as unpadded base64url.</summary>
    public static string CreateState() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
}
