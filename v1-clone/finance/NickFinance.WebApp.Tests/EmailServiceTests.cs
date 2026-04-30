using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NickFinance.WebApp.Services;
using Xunit;

namespace NickFinance.WebApp.Tests;

/// <summary>
/// Tests for <see cref="SmtpEmailService"/> and <see cref="NoopEmailService"/>
/// that don't require a real SMTP server. We exercise the MIME-construction
/// path (the on-wire shape the SMTP client would send) directly via the
/// <c>BuildMime</c> helper, plus the env-var routing in
/// <see cref="EmailServiceFactory"/>.
/// </summary>
public sealed class EmailServiceTests
{
    private static SmtpOptions DefaultOpts() => new(
        Host: "smtp.example.com",
        Port: 587,
        Username: "user@example.com",
        Password: "secret",
        FromAddress: "finance@example.com",
        FromDisplayName: "Finance",
        UseStartTls: true);

    [Fact]
    public void BuildMime_sets_to_subject_html_and_text_alt()
    {
        var svc = new SmtpEmailService(DefaultOpts(), NullLogger<SmtpEmailService>.Instance);
        var mime = svc.BuildMime(new EmailMessage(
            To: "alice@customer.test",
            Subject: "Statement attached",
            BodyHtml: "<p>Closing balance <b>1,234.56</b> GHS</p>",
            BodyText: "Closing balance 1,234.56 GHS"));

        Assert.Equal("Statement attached", mime.Subject);
        Assert.Single(mime.To.Mailboxes);
        Assert.Equal("alice@customer.test", mime.To.Mailboxes.Single().Address);
        Assert.Equal("Finance", mime.From.Mailboxes.Single().Name);
        Assert.Equal("finance@example.com", mime.From.Mailboxes.Single().Address);

        var html = mime.HtmlBody ?? string.Empty;
        var text = mime.TextBody ?? string.Empty;
        Assert.Contains("Closing balance", html);
        Assert.Contains("Closing balance 1,234.56 GHS", text);
    }

    [Fact]
    public void BuildMime_includes_attachment_when_supplied()
    {
        var svc = new SmtpEmailService(DefaultOpts(), NullLogger<SmtpEmailService>.Instance);
        var pdfBytes = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x37 }; // "%PDF-1.7"
        var mime = svc.BuildMime(new EmailMessage(
            To: "alice@customer.test",
            Subject: "Statement",
            BodyHtml: "<p>See attached.</p>",
            Attachments: new[]
            {
                new EmailAttachment(
                    Filename: "statement.pdf",
                    ContentType: "application/pdf",
                    Content: pdfBytes)
            }));

        var attachments = mime.Attachments.OfType<MimeKit.MimePart>().ToList();
        Assert.Single(attachments);
        Assert.Equal("statement.pdf", attachments[0].FileName);
        Assert.Equal("application", attachments[0].ContentType.MediaType);
        Assert.Equal("pdf", attachments[0].ContentType.MediaSubtype);
    }

    [Fact]
    public async Task NoopEmailService_returns_false_and_does_not_throw()
    {
        var svc = new NoopEmailService(NullLogger<NoopEmailService>.Instance);
        var ok = await svc.SendAsync(new EmailMessage("a@b.test", "X", "<p>x</p>"));
        Assert.False(ok);
    }

    [Theory]
    [InlineData("Finance <finance@example.com>", "Finance", "finance@example.com")]
    [InlineData("plain@example.com", "Nick TC-Scan Ltd", "plain@example.com")]
    [InlineData("  Spaced Name <addr@e.test>  ", "Spaced Name", "addr@e.test")]
    public void ParseFrom_splits_display_name_and_address(string input, string display, string addr)
    {
        var (d, a) = EmailServiceFactory.ParseFrom(input);
        Assert.Equal(display, d);
        Assert.Equal(addr, a);
    }

    [Fact]
    public void ReadOptionsFromEnv_returns_null_when_required_vars_missing()
    {
        // When NICKFINANCE_SMTP_HOST isn't set, factory returns null and
        // the DI registration falls back to NoopEmailService. We snapshot
        // and restore the existing values to be safe.
        var oldHost = Environment.GetEnvironmentVariable("NICKFINANCE_SMTP_HOST");
        Environment.SetEnvironmentVariable("NICKFINANCE_SMTP_HOST", null);
        try
        {
            var opts = EmailServiceFactory.ReadOptionsFromEnv();
            Assert.Null(opts);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NICKFINANCE_SMTP_HOST", oldHost);
        }
    }
}
