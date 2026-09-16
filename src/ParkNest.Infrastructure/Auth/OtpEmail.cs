namespace ParkNest.Infrastructure.Auth;

internal static class OtpEmail
{
    // The code leads the subject so it can be read off the notification without opening the mail,
    // and so iOS and Android offer it as a one-tap autofill.
    public static string Subject(string code) => $"{code} is your ParkNest sign-in code";

    public static string Text(string code, int lifetimeMinutes) =>
        $"Your ParkNest sign-in code is {code}.\n\n" +
        $"It expires in {lifetimeMinutes} minutes. If you did not ask for it, ignore this email - " +
        "nobody can sign in without the code.";

    public static string Html(string code, int lifetimeMinutes) => $$"""
        <div style="font-family:-apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif;max-width:420px;margin:0 auto;padding:24px;color:#1f2933">
          <p style="margin:0 0 16px">Your ParkNest sign-in code is</p>
          <p style="margin:0 0 16px;font-size:32px;font-weight:700;letter-spacing:6px">{{code}}</p>
          <p style="margin:0;color:#52606d;font-size:14px">It expires in {{lifetimeMinutes}} minutes. If you did not ask for it, ignore this email &mdash; nobody can sign in without the code.</p>
        </div>
        """;
}
