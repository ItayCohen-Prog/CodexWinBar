using System.Net;

namespace CodexWinBar.Providers;

/// <summary>Minimal HTML pages the loopback listener serves after an OAuth sign-in attempt.</summary>
public static class SignInPages
{
    /// <summary>Page shown after a successful sign-in; tells the user to return to the app.</summary>
    public static string Success(string providerName) => Page(
        $"{providerName} sign-in complete",
        "You can close this tab and return to CodexWinBar.");

    /// <summary>Page shown when the sign-in failed or was rejected.</summary>
    public static string Failure(string providerName, string reason) => Page(
        $"{providerName} sign-in failed",
        WebUtility.HtmlEncode(reason));

    private static string Page(string title, string body) =>
        "<!doctype html><html><head><meta charset=\"utf-8\"><title>" + WebUtility.HtmlEncode(title) +
        "</title><style>body{font-family:'Segoe UI',sans-serif;display:flex;align-items:center;" +
        "justify-content:center;height:100vh;margin:0;background:#1f1f1f;color:#eee}" +
        "div{text-align:center}h1{font-size:20px;font-weight:600}p{color:#aaa}</style></head>" +
        $"<body><div><h1>{WebUtility.HtmlEncode(title)}</h1><p>{body}</p></div></body></html>";
}
