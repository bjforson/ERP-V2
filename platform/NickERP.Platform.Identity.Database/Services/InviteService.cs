using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NickERP.Platform.Email;
using NickERP.Platform.Identity.Entities;
using NickERP.Platform.Tenancy;

namespace NickERP.Platform.Identity.Database.Services;

/// <summary>
/// Sprint 21 / Phase B — issue / redeem / revoke one-time invite
/// tokens. Sibling to <c>EdgeNodeApiKeyService</c> (Sprint 13);
/// shares the "operator-driven, plaintext exactly once, hashes
/// at rest" posture.
/// </summary>
/// <remarks>
/// <para>
/// <b>Issuance.</b> <see cref="IssueInviteAsync"/> generates a
/// CSPRNG plaintext token, hashes it via <see cref="InviteTokenHasher"/>,
/// stores the hash + 8-char prefix, then sends the email through
/// <see cref="IEmailSender"/> with the rendered <c>invite</c>
/// template. The plaintext is NEVER logged; the only persisted
/// trace is the row's <c>TokenPrefix</c> + <c>IssuedAt</c>.
/// </para>
/// <para>
/// <b>Redemption.</b> <see cref="RedeemInviteAsync"/> looks up the
/// row by HMAC-hashed token. The lookup happens BEFORE the tenant
/// is known from request context (the caller is anonymous up to
/// this point), so the service flips into
/// <see cref="ITenantContext.SetSystemContext"/> for the read +
/// the redemption-marking write. Concurrent redemptions are
/// race-safe via the unique partial index on the hash column.
/// </para>
/// <para>
/// <b>Revocation.</b> <see cref="RevokeInviteAsync"/> marks the
/// row revoked. Idempotent — already-revoked or already-redeemed
/// rows are no-ops. Operator UI surfaces this via the future
/// invites admin page.
/// </para>
/// </remarks>
public interface IInviteService
{
    /// <summary>
    /// Issue a fresh invite for <paramref name="email"/> on
    /// <paramref name="tenantId"/>. Returns the issuance result; the
    /// plaintext token is included in the result so the caller can
    /// display it (e.g. for filesystem-sender deployments where the
    /// email path is observably side-channeled). For SMTP deployments
    /// the plaintext is in the email body and the caller MAY discard
    /// the result.
    /// </summary>
    Task<InviteIssuance> IssueInviteAsync(
        long tenantId,
        string tenantName,
        string email,
        string intendedRoles = "Tenant.Admin",
        Guid? issuedByUserId = null,
        TimeSpan? expiresIn = null,
        CancellationToken ct = default);

    /// <summary>
    /// Redeem an invite by its plaintext token. Returns the matched
    /// row + an outcome enum. Outcome values other than
    /// <see cref="InviteRedemptionOutcome.Ok"/> mean the caller
    /// should NOT proceed with user creation — surface an error to
    /// the invitee instead.
    /// </summary>
    Task<InviteRedemption> RedeemInviteAsync(string plaintextToken, CancellationToken ct = default);

    /// <summary>
    /// Mark the redemption complete with the given user id. Called
    /// by the portal AFTER the user has successfully signed in or
    /// been created. Separated from <see cref="RedeemInviteAsync"/>
    /// so the validation step happens before any user mutation.
    /// </summary>
    Task<bool> MarkRedeemedAsync(Guid inviteId, Guid redeemedByUserId, CancellationToken ct = default);

    /// <summary>
    /// Revoke a pending invite. Idempotent — already-revoked or
    /// already-redeemed rows return false without mutating state.
    /// </summary>
    Task<bool> RevokeInviteAsync(long tenantId, Guid inviteId, Guid revokingUserId, CancellationToken ct = default);

    /// <summary>List invites for a tenant (admin UI).</summary>
    Task<IReadOnlyList<InviteSummary>> ListAsync(long tenantId, CancellationToken ct = default);
}

/// <summary>Default <see cref="IInviteService"/>.</summary>
public sealed class InviteService : IInviteService
{
    private readonly IdentityDbContext _db;
    private readonly InviteTokenHasher _hasher;
    private readonly IEmailSender _email;
    private readonly IEmailTemplateProvider _templates;
    private readonly ITenantContext _tenant;
    private readonly IOptions<EmailOptions> _emailOptions;
    private readonly ILogger<InviteService> _logger;
    private readonly TimeProvider _clock;

    public InviteService(
        IdentityDbContext db,
        InviteTokenHasher hasher,
        IEmailSender email,
        IEmailTemplateProvider templates,
        ITenantContext tenant,
        IOptions<EmailOptions> emailOptions,
        ILogger<InviteService> logger,
        TimeProvider? clock = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _hasher = hasher ?? throw new ArgumentNullException(nameof(hasher));
        _email = email ?? throw new ArgumentNullException(nameof(email));
        _templates = templates ?? throw new ArgumentNullException(nameof(templates));
        _tenant = tenant ?? throw new ArgumentNullException(nameof(tenant));
        _emailOptions = emailOptions ?? throw new ArgumentNullException(nameof(emailOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<InviteIssuance> IssueInviteAsync(
        long tenantId,
        string tenantName,
        string email,
        string intendedRoles = "Tenant.Admin",
        Guid? issuedByUserId = null,
        TimeSpan? expiresIn = null,
        CancellationToken ct = default)
    {
        if (tenantId <= 0)
            throw new ArgumentOutOfRangeException(nameof(tenantId), "TenantId must be positive.");
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantName);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(intendedRoles);

        var emailNormalized = email.Trim().ToLowerInvariant();
        if (emailNormalized.Length > 320)
        {
            throw new ArgumentException("Email must be at most 320 chars (RFC 5321).", nameof(email));
        }

        var now = _clock.GetUtcNow();
        var ttl = expiresIn ?? TimeSpan.FromHours(_emailOptions.Value.Invite.DefaultExpiryHours);
        if (ttl <= TimeSpan.Zero)
        {
            throw new ArgumentException("ExpiresIn must be positive.", nameof(expiresIn));
        }
        var expiresAt = now + ttl;

        var plaintext = InviteTokenHasher.GenerateToken();
        var hash = _hasher.ComputeHash(plaintext);
        var prefix = InviteTokenHasher.ComputePrefix(plaintext);

        var row = new InviteToken
        {
            TenantId = tenantId,
            Email = emailNormalized,
            TokenHash = hash,
            TokenPrefix = prefix,
            IntendedRoles = intendedRoles.Trim(),
            IssuedAt = now,
            ExpiresAt = expiresAt,
            IssuedByUserId = issuedByUserId
        };

        _db.InviteTokens.Add(row);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Invite issued: tenant={TenantId} email={Email} prefix={Prefix} expires={Expires} issuedBy={UserId}.",
            tenantId, emailNormalized, prefix, expiresAt, issuedByUserId);

        // Render + send the invite email. We do this after the row is
        // saved so a failed send leaves the invite in the DB (operator
        // can re-send via the admin UI by re-issuing). If we sent first
        // and the DB write failed, we'd have a usable token in someone's
        // inbox with no matching row — that fails open.
        var portalBase = _emailOptions.Value.Invite.PortalBaseUrl;
        if (string.IsNullOrWhiteSpace(portalBase))
        {
            // Don't crash the issue if the portal base isn't configured;
            // log loudly so ops can fix and re-send. The row is still
            // valid — they can manually paste the link.
            _logger.LogWarning(
                "Invite issued but Email:Invite:PortalBaseUrl is not configured; skipping email send. "
                + "Token prefix={Prefix} email={Email} tenant={TenantId}. Manual send required.",
                prefix, emailNormalized, tenantId);
            return new InviteIssuance(row.Id, plaintext, prefix, row.IssuedAt, row.ExpiresAt, EmailSendStatus.Skipped);
        }

        var inviteLink = BuildInviteLink(portalBase, plaintext);
        var sendStatus = EmailSendStatus.Sent;
        try
        {
            var rendered = await _templates.RenderAsync("invite", new Dictionary<string, string>
            {
                ["TenantName"] = tenantName,
                ["IntendedRoles"] = intendedRoles,
                ["InviteLink"] = inviteLink,
                ["ExpiresAt"] = row.ExpiresAt.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture),
            }, ct);

            var message = new EmailMessage(
                to: emailNormalized,
                subject: rendered.Subject.Trim(),
                bodyText: rendered.BodyText,
                bodyHtml: rendered.BodyHtml,
                from: _emailOptions.Value.DefaultFrom);

            await _email.SendAsync(message, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Don't surface email-send failures as a tenant-create
            // blocker — log + mark the issuance result so the caller
            // UI can show a "saved but email send failed; resend?"
            // hint.
            _logger.LogWarning(ex,
                "Invite saved (id={InviteId}) but email send failed via {Provider}. Operator can re-issue.",
                row.Id, _email.ProviderName);
            sendStatus = EmailSendStatus.Failed;
        }

        return new InviteIssuance(row.Id, plaintext, prefix, row.IssuedAt, row.ExpiresAt, sendStatus);
    }

    /// <inheritdoc />
    public async Task<InviteRedemption> RedeemInviteAsync(string plaintextToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(plaintextToken))
        {
            return new InviteRedemption(InviteRedemptionOutcome.UnknownToken, null);
        }

        // Same posture as EdgeAuthHandler.TryAuthenticatePerNodeAsync —
        // the redemption is pre-tenant-resolution, so we flip into
        // SetSystemContext for the lookup. The migration's RLS policy
        // admits this via the `OR app.tenant_id = '-1'` clause.
        // Registered in docs/system-context-audit-register.md.
        _tenant.SetSystemContext();

        byte[] hash;
        try
        {
            hash = _hasher.ComputeHash(plaintextToken);
        }
        catch (ArgumentException)
        {
            // Empty / null already caught above; keep this branch as a
            // belt-and-braces against future hasher exceptions.
            return new InviteRedemption(InviteRedemptionOutcome.UnknownToken, null);
        }

        InviteToken? row;
        try
        {
            row = await _db.InviteTokens.AsNoTracking()
                .Where(x => x.TokenHash == hash)
                .FirstOrDefaultAsync(ct);
        }
        catch (DbException ex)
        {
            _logger.LogError(ex, "Invite redeem lookup failed.");
            return new InviteRedemption(InviteRedemptionOutcome.LookupError, null);
        }

        if (row is null)
        {
            return new InviteRedemption(InviteRedemptionOutcome.UnknownToken, null);
        }

        // Defence-in-depth constant-time comparison (same as
        // EdgeAuthHandler). The DB already did a bytea = match via
        // the index but FixedTimeEquals guarantees timing safety
        // across future query-shape changes.
        if (!CryptographicOperations.FixedTimeEquals(row.TokenHash, hash))
        {
            return new InviteRedemption(InviteRedemptionOutcome.UnknownToken, null);
        }

        if (row.RevokedAt is not null)
        {
            return new InviteRedemption(InviteRedemptionOutcome.Revoked, row);
        }
        if (row.RedeemedAt is not null)
        {
            return new InviteRedemption(InviteRedemptionOutcome.AlreadyRedeemed, row);
        }
        var now = _clock.GetUtcNow();
        if (row.ExpiresAt <= now)
        {
            return new InviteRedemption(InviteRedemptionOutcome.Expired, row);
        }

        return new InviteRedemption(InviteRedemptionOutcome.Ok, row);
    }

    /// <inheritdoc />
    public async Task<bool> MarkRedeemedAsync(Guid inviteId, Guid redeemedByUserId, CancellationToken ct = default)
    {
        if (inviteId == Guid.Empty)
            throw new ArgumentException("InviteId is required.", nameof(inviteId));
        if (redeemedByUserId == Guid.Empty)
            throw new ArgumentException("RedeemedByUserId is required.", nameof(redeemedByUserId));

        // Mark-redeemed runs AFTER the validation pass and is the
        // race-critical step. We stay in system context (the caller
        // hasn't yet established a tenant context — they're being
        // bootstrapped right now). The unique partial index on
        // TokenHash filtered to active rows is what makes this
        // race-safe: two parallel mark-redeemed calls both pass the
        // tracked SELECT, but only one UPDATE succeeds in flipping
        // the active-row index entry — the loser hits a unique
        // violation, returns false.
        if (!_tenant.IsSystem)
        {
            _tenant.SetSystemContext();
        }

        var row = await _db.InviteTokens.FirstOrDefaultAsync(x => x.Id == inviteId, ct);
        if (row is null)
        {
            return false;
        }
        if (row.RedeemedAt is not null || row.RevokedAt is not null)
        {
            return false;
        }
        row.RedeemedAt = _clock.GetUtcNow();
        row.RedeemedByUserId = redeemedByUserId;
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is DbException)
        {
            // Concurrent redemption raced us to the punch. Treat as
            // already-redeemed; caller surfaces "this invite was
            // already used" without a 500.
            _logger.LogWarning(
                "Invite {InviteId} redemption raced; another caller marked it redeemed first.",
                inviteId);
            return false;
        }

        _logger.LogInformation(
            "Invite redeemed: id={InviteId} tenant={TenantId} email={Email} prefix={Prefix} byUser={UserId}.",
            row.Id, row.TenantId, row.Email, row.TokenPrefix, redeemedByUserId);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> RevokeInviteAsync(long tenantId, Guid inviteId, Guid revokingUserId, CancellationToken ct = default)
    {
        if (tenantId <= 0)
            throw new ArgumentOutOfRangeException(nameof(tenantId), "TenantId must be positive.");

        var row = await _db.InviteTokens
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == inviteId, ct);
        if (row is null)
        {
            return false;
        }
        if (row.RedeemedAt is not null || row.RevokedAt is not null)
        {
            return false;
        }
        row.RevokedAt = _clock.GetUtcNow();
        row.RevokedByUserId = revokingUserId;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Invite revoked: id={InviteId} tenant={TenantId} prefix={Prefix} byUser={UserId}.",
            row.Id, tenantId, row.TokenPrefix, revokingUserId);
        return true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InviteSummary>> ListAsync(long tenantId, CancellationToken ct = default)
    {
        if (tenantId <= 0)
            throw new ArgumentOutOfRangeException(nameof(tenantId), "TenantId must be positive.");

        return await _db.InviteTokens.AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .OrderByDescending(x => x.IssuedAt)
            .Select(x => new InviteSummary(
                x.Id,
                x.Email,
                x.TokenPrefix,
                x.IntendedRoles,
                x.IssuedAt,
                x.ExpiresAt,
                x.RedeemedAt,
                x.RevokedAt,
                x.IssuedByUserId))
            .ToListAsync(ct);
    }

    private static string BuildInviteLink(string portalBaseUrl, string plaintextToken)
    {
        // The plaintext token is URL-safe base64 (no padding, '+' / '/' replaced with '-' / '_'),
        // so it slots into the path segment without escaping. Belt-and-braces: trim trailing
        // slash on the base before concatenating.
        var trimmed = portalBaseUrl.TrimEnd('/');
        return $"{trimmed}/invite/accept/{plaintextToken}";
    }
}

/// <summary>Outcome of <see cref="InviteService.IssueInviteAsync"/>.</summary>
public sealed record InviteIssuance(
    Guid InviteId,
    string Plaintext,
    string Prefix,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    EmailSendStatus EmailStatus);

/// <summary>What happened when the invite issuance tried to send the email.</summary>
public enum EmailSendStatus
{
    /// <summary>Email handed off to the provider successfully.</summary>
    Sent = 0,
    /// <summary>Email send failed but the invite row was saved; operator can resend.</summary>
    Failed = 1,
    /// <summary>No email was sent because <c>Email:Invite:PortalBaseUrl</c> isn't configured.</summary>
    Skipped = 2,
}

/// <summary>Outcome of <see cref="InviteService.RedeemInviteAsync"/>.</summary>
public sealed record InviteRedemption(InviteRedemptionOutcome Outcome, InviteToken? MatchedRow);

/// <summary>Outcome categories from <see cref="InviteService.RedeemInviteAsync"/>.</summary>
public enum InviteRedemptionOutcome
{
    /// <summary>Token presented + valid + not yet redeemed/revoked + not expired.</summary>
    Ok = 0,
    /// <summary>No row matched the presented hash. May indicate a typo, a tampered URL, or an attacker.</summary>
    UnknownToken = 1,
    /// <summary>Matching row found but <see cref="InviteToken.RedeemedAt"/> is set.</summary>
    AlreadyRedeemed = 2,
    /// <summary>Matching row found but <see cref="InviteToken.RevokedAt"/> is set.</summary>
    Revoked = 3,
    /// <summary>Matching row found but <see cref="InviteToken.ExpiresAt"/> has passed.</summary>
    Expired = 4,
    /// <summary>DB error during lookup. Treated as redeem failure (fail-closed).</summary>
    LookupError = 5,
}

/// <summary>Operator-UI summary of one invite row (no plaintext, no hash).</summary>
public sealed record InviteSummary(
    Guid InviteId,
    string Email,
    string TokenPrefix,
    string IntendedRoles,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RedeemedAt,
    DateTimeOffset? RevokedAt,
    Guid? IssuedByUserId);
