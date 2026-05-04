using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NickERP.Platform.Email;

namespace NickERP.Platform.Tests;

/// <summary>
/// Sprint 21 / Phase A — coverage for the email abstraction. Focus
/// on the filesystem sender (the dev-default) and the constructor
/// validation rules on <see cref="EmailMessage"/>; SMTP requires a
/// live relay so it's not unit-testable here.
/// </summary>
public sealed class EmailSenderTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void EmailMessage_RequiresAtLeastOneBody()
    {
        var act = () => new EmailMessage("a@example.com", "subj");
        act.Should().Throw<ArgumentException>().WithMessage("*body*");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void EmailMessage_RequiresRecipient()
    {
        var act = () => new EmailMessage(string.Empty, "subj", bodyText: "hi");
        act.Should().Throw<ArgumentException>().WithMessage("*Recipient*");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void EmailMessage_RequiresSubject()
    {
        var act = () => new EmailMessage("a@example.com", string.Empty, bodyText: "hi");
        act.Should().Throw<ArgumentException>().WithMessage("*Subject*");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FileSystemEmailSender_WritesEmlToConfiguredOutbox()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nickerp-email-tests-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var opts = Options.Create(new EmailOptions
            {
                Provider = FileSystemEmailSender.ProviderTag,
                DefaultFrom = "noreply@nickerp.local",
                FileSystem = new EmailOptions.FileSystemSenderOptions { OutboxDirectory = dir },
            });
            var sender = new FileSystemEmailSender(opts, NullLogger<FileSystemEmailSender>.Instance, TimeProvider.System);
            var msg = new EmailMessage(
                to: "alice@example.com",
                subject: "Welcome",
                bodyText: "hello world",
                bodyHtml: "<b>hello world</b>");

            await sender.SendAsync(msg);

            var files = Directory.GetFiles(dir, "*.eml");
            files.Should().HaveCount(1);
            var contents = await File.ReadAllTextAsync(files[0]);
            contents.Should().Contain("To: alice@example.com");
            contents.Should().Contain("From: noreply@nickerp.local");
            contents.Should().Contain("Subject: Welcome");
            contents.Should().Contain("multipart/alternative");
            contents.Should().Contain("hello world");
            contents.Should().Contain("<b>hello world</b>");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FileSystemEmailSender_RejectsHeaderInjection()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nickerp-email-tests-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var opts = Options.Create(new EmailOptions
            {
                FileSystem = new EmailOptions.FileSystemSenderOptions { OutboxDirectory = dir },
            });
            var sender = new FileSystemEmailSender(opts, NullLogger<FileSystemEmailSender>.Instance, TimeProvider.System);
            var msg = new EmailMessage(
                to: "alice@example.com",
                subject: "x",
                bodyText: "y",
                headers: new Dictionary<string, string>
                {
                    ["X-Evil"] = "value\r\nBcc: attacker@example.com"
                });

            var act = async () => await sender.SendAsync(msg);
            await act.Should().ThrowAsync<ArgumentException>().WithMessage("*header injection*");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task NoOpEmailSender_LogsAndDoesNotThrow()
    {
        var sender = new NoOpEmailSender(NullLogger<NoOpEmailSender>.Instance);
        var msg = new EmailMessage("alice@example.com", "x", bodyText: "y");
        await sender.SendAsync(msg);
        sender.ProviderName.Should().Be("noop");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void EmbeddedTemplateProvider_SubstitutesPlaceholders()
    {
        var template = "Hello {{Name}}, your role is {{Role}}.";
        var rendered = EmbeddedResourceEmailTemplateProvider.Substitute(
            template,
            new Dictionary<string, string> { ["Name"] = "Alice", ["Role"] = "Admin" });
        rendered.Should().Be("Hello Alice, your role is Admin.");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void EmbeddedTemplateProvider_ThrowsOnMissingPlaceholder()
    {
        var template = "Hello {{Name}}.";
        var act = () => EmbeddedResourceEmailTemplateProvider.Substitute(
            template,
            new Dictionary<string, string> { ["Other"] = "x" });
        act.Should().Throw<InvalidOperationException>().WithMessage("*Name*");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task EmbeddedTemplateProvider_RendersInviteTemplate()
    {
        // The invite.{subject.txt,txt,html} files ship as embedded
        // resources in NickERP.Platform.Email. Verify they render with
        // the placeholders the InviteService passes.
        var provider = new EmbeddedResourceEmailTemplateProvider(
            NullLogger<EmbeddedResourceEmailTemplateProvider>.Instance);

        var rendered = await provider.RenderAsync("invite", new Dictionary<string, string>
        {
            ["TenantName"] = "Acme Co",
            ["IntendedRoles"] = "Tenant.Admin",
            ["InviteLink"] = "https://portal.example/invite/accept/abc",
            ["ExpiresAt"] = "2026-05-07 14:00 UTC",
        });

        rendered.Subject.Should().Contain("Acme Co");
        rendered.BodyText.Should().Contain("Acme Co");
        rendered.BodyText.Should().Contain("Tenant.Admin");
        rendered.BodyText.Should().Contain("https://portal.example/invite/accept/abc");
        rendered.BodyHtml.Should().Contain("Acme Co");
        rendered.BodyHtml.Should().Contain("https://portal.example/invite/accept/abc");
    }
}
