using System.Net;
using System.Net.Mail;
using System.Text;

namespace HorusAPI.Services;

public interface IEmailSender
{
    /// <summary>Sends the 6-digit registration code. Never throws — returns false when the message could not be queued.</summary>
    Task<bool> SendVerificationCodeAsync(string email, string username, string code, TimeSpan validFor, CancellationToken ct = default);

    /// <summary>Sends the password-reset link. Never throws — returns false when the message could not be queued.</summary>
    Task<bool> SendPasswordResetAsync(string email, string username, string link, TimeSpan validFor, CancellationToken ct = default);
}

/// <summary>
/// Relays through the Postfix container on the internal Docker network
/// (no auth, no TLS — the hop never leaves the compose network). Set
/// Mail__Enabled=false to log codes/links instead of sending, which is what
/// local `dotnet run` does when there is no mail server around.
/// </summary>
public class SmtpEmailSender(IConfiguration cfg, ILogger<SmtpEmailSender> log) : IEmailSender
{
    private const string BrandName = "Horus Ping Booster";

    private bool Enabled => cfg.GetValue("Mail:Enabled", true);
    private string Host => cfg["Mail:Host"] ?? "mailserver";
    private int Port => cfg.GetValue("Mail:Port", 587);
    private bool UseStartTls => cfg.GetValue("Mail:UseStartTls", false);
    private string FromName => cfg["Mail:FromName"] ?? BrandName;

    private string From => cfg["Mail:From"]
        ?? (string.IsNullOrWhiteSpace(cfg["DOMAIN"]) ? "no-reply@localhost" : $"no-reply@mail.{cfg["DOMAIN"]}");

    public Task<bool> SendVerificationCodeAsync(
        string email, string username, string code, TimeSpan validFor, CancellationToken ct = default)
    {
        int minutes = (int)validFor.TotalMinutes;

        string text = $"""
            Здравствуйте, {username}!

            Код подтверждения регистрации в {BrandName}: {code}

            Код действует {minutes} минут. Введите его в приложении, чтобы активировать аккаунт.
            Если вы не регистрировались — просто проигнорируйте это письмо.
            """;

        string html = Layout(
            title: "Подтвердите e-mail",
            intro: $"Здравствуйте, <b>{Escape(username)}</b>! Вот код для активации аккаунта {BrandName}.",
            body: $"""
                <div style="margin:26px 0;padding:20px;border-radius:16px;background:rgba(240,196,106,.09);border:1px solid rgba(240,196,106,.32);text-align:center">
                  <div style="font-family:'Courier New',monospace;font-size:34px;font-weight:700;letter-spacing:.28em;color:#F0C46A">{Escape(code)}</div>
                </div>
                <p style="margin:0;color:rgba(239,234,246,.62);font-size:14px">Код действует {minutes} минут.</p>
                """,
            footer: "Если вы не создавали аккаунт, просто проигнорируйте это письмо.");

        return SendAsync(email, $"{code} — код подтверждения {BrandName}", text, html, ct);
    }

    public Task<bool> SendPasswordResetAsync(
        string email, string username, string link, TimeSpan validFor, CancellationToken ct = default)
    {
        int minutes = (int)validFor.TotalMinutes;

        string text = $"""
            Здравствуйте, {username}!

            Вы запросили сброс пароля в {BrandName}. Откройте ссылку, чтобы задать новый пароль:

            {link}

            Ссылка действует {minutes} минут и срабатывает один раз.
            Если вы не запрашивали сброс — просто проигнорируйте это письмо, пароль останется прежним.
            """;

        string html = Layout(
            title: "Сброс пароля",
            intro: $"Здравствуйте, <b>{Escape(username)}</b>! Вы запросили сброс пароля {BrandName}.",
            body: $"""
                <div style="margin:26px 0;text-align:center">
                  <a href="{Escape(link)}" style="display:inline-block;padding:15px 34px;border-radius:14px;background:#F0C46A;color:#1A0C26;font-weight:700;font-size:16px;text-decoration:none">Задать новый пароль</a>
                </div>
                <p style="margin:0 0 10px;color:rgba(239,234,246,.62);font-size:14px">Ссылка действует {minutes} минут и срабатывает один раз.</p>
                <p style="margin:0;color:rgba(239,234,246,.45);font-size:12.5px;word-break:break-all">{Escape(link)}</p>
                """,
            footer: "Если вы не запрашивали сброс, проигнорируйте это письмо — пароль останется прежним.");

        return SendAsync(email, $"Сброс пароля — {BrandName}", text, html, ct);
    }

    private async Task<bool> SendAsync(string to, string subject, string text, string html, CancellationToken ct)
    {
        if (!Enabled)
        {
            // Dev fallback: the whole point is to see the code without a mail server.
            log.LogWarning("Mail disabled — would send to {To}: {Subject}\n{Body}", to, subject, text);
            return true;
        }

        try
        {
            using var message = new MailMessage
            {
                From            = new MailAddress(From, FromName, Encoding.UTF8),
                Subject         = subject,
                SubjectEncoding = Encoding.UTF8,
                Body            = text,
                BodyEncoding    = Encoding.UTF8
            };
            message.To.Add(new MailAddress(to));
            message.AlternateViews.Add(
                AlternateView.CreateAlternateViewFromString(html, Encoding.UTF8, "text/html"));

            using var client = new SmtpClient(Host, Port)
            {
                DeliveryMethod = SmtpDeliveryMethod.Network,
                EnableSsl      = UseStartTls,
                Timeout        = 10_000
            };

            string? user = cfg["Mail:User"];
            if (!string.IsNullOrEmpty(user))
                client.Credentials = new NetworkCredential(user, cfg["Mail:Password"]);

            await client.SendMailAsync(message, ct);

            log.LogInformation("Mail sent to {To}: {Subject}", to, subject);
            return true;
        }
        catch (Exception ex)
        {
            // Callers answer 202 regardless: a dead mail server must not tell an
            // attacker whether the address exists, and must not fail registration.
            log.LogError(ex, "Failed to send mail to {To} via {Host}:{Port}", to, Host, Port);
            return false;
        }
    }

    private static string Layout(string title, string intro, string body, string footer) => $"""
        <!doctype html>
        <html lang="ru"><body style="margin:0;padding:0;background:#0B0512">
          <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background:#0B0512;padding:32px 12px">
            <tr><td align="center">
              <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="max-width:520px;background:#160A22;border:1px solid rgba(255,255,255,.08);border-radius:22px">
                <tr><td style="padding:34px 32px;font-family:'Segoe UI',Roboto,Helvetica,Arial,sans-serif;color:#EFEAF6">
                  <div style="font-size:13px;font-weight:700;letter-spacing:.22em;color:#F3D48E;margin-bottom:18px">HORUS</div>
                  <h1 style="margin:0 0 14px;font-size:23px;line-height:1.25;color:#FFFFFF">{title}</h1>
                  <p style="margin:0;color:rgba(239,234,246,.75);font-size:15px;line-height:1.6">{intro}</p>
                  {body}
                  <hr style="border:none;border-top:1px solid rgba(255,255,255,.08);margin:26px 0 16px">
                  <p style="margin:0;color:rgba(239,234,246,.45);font-size:12.5px;line-height:1.6">{footer}</p>
                </td></tr>
              </table>
            </td></tr>
          </table>
        </body></html>
        """;

    private static string Escape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
