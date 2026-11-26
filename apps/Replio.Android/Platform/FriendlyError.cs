namespace Replio.Android.Platform;

/// <summary>
/// What went wrong, in plain words: an exception text from the network or a server («HttpRequestException …»,
/// «Response status code does not indicate success: 429») becomes «нет связи», «войдите в аккаунт», «лимит» or
/// «попробуйте ещё раз». The phone shows only these; the original text goes to the log.
/// </summary>
public static class FriendlyError
{
    public enum Kind
    {
        /// <summary>Already a message for people (a settings hint): shown as it is.</summary>
        None,
        Network,
        SignIn,
        Limit,
        Other
    }

    private static readonly string[] LimitMarks = ["402", "429", "too many requests", "limit", "лимит", "quota", "insufficient", "баланс"];
    private static readonly string[] SignInMarks = ["401", "403", "unauthorized", "forbidden", "войдите", "sign in", "не авторизован"];
    private static readonly string[] NetworkMarks =
    [
        "no such host", "name or service not known", "unable to resolve", "network", "connection", "timed out", "timeout",
        "ssl", "socket", "sending the request", "operation was canceled", "task was canceled", "eof", "host", "unreachable",
        "502", "503", "504", "bad gateway", "service unavailable", "сеть", "соединени"
    ];
    private static readonly string[] TechnicalMarks = ["exception", "status code", "http", "{", "error", "ошибка", "failed"];

    public static Kind Classify(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Kind.Other;
        }

        string lower = text.ToLowerInvariant();
        if (LimitMarks.Any(lower.Contains))
        {
            return Kind.Limit;
        }

        if (SignInMarks.Any(lower.Contains))
        {
            return Kind.SignIn;
        }

        if (NetworkMarks.Any(lower.Contains))
        {
            return Kind.Network;
        }

        return TechnicalMarks.Any(lower.Contains) ? Kind.Other : Kind.None;
    }

    /// <summary>One sentence for a dialog or a notice: «Нет связи с сервером. Проверьте интернет и нажмите ещё раз.»</summary>
    public static string Describe(Kind kind) => kind switch
    {
        Kind.Network => MobileText.Get("ErrNetwork"),
        Kind.SignIn => MobileText.Get("ErrSignIn"),
        Kind.Limit => MobileText.Get("ErrLimit"),
        _ => MobileText.Get("ErrOther")
    };
}
