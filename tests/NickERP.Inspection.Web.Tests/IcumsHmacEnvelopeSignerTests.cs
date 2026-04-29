using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Inspection.ExternalSystems.Abstractions;
using NickERP.Inspection.Web.Services;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 9 / FU-icums-signing — round-trip + edge-case tests for
/// <see cref="IcumsHmacEnvelopeSigner"/>.
///
/// <para>
/// Uses <see cref="EphemeralDataProtectionProvider"/> so the data-
/// protection key ring is in-process only — no filesystem state, and
/// each test gets a fresh ring. The EF in-memory provider stands in
/// for Postgres; RLS is not exercised here (covered by tenant scoping
/// in the LINQ query and by Postgres-backed tests in a future sprint).
/// </para>
/// </summary>
public sealed class IcumsHmacEnvelopeSignerTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task Sign_then_Verify_round_trips_under_active_key()
    {
        await using var db = NewDb();
        var signer = NewSigner(db, out _);

        const long tenant = 7L;
        SeedActiveKey(db, signer, tenant, "k1");

        var payload = Encoding.UTF8.GetBytes("{\"ref\":\"BOE-1\"}");
        var signed = await signer.SignAsync(tenant.ToString(), payload);

        signed.SignatureHeader.Should().StartWith("icums-hmac-sha256 keyId=k1 sig=");

        var verdict = await signer.VerifyAsync(tenant.ToString(), payload, signed.SignatureHeader);
        verdict.Valid.Should().BeTrue();
        verdict.KeyIdUsed.Should().Be("k1");
        verdict.FailureReason.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Verify_rejects_tampered_payload()
    {
        await using var db = NewDb();
        var signer = NewSigner(db, out _);

        const long tenant = 7L;
        SeedActiveKey(db, signer, tenant, "k1");

        var payload = Encoding.UTF8.GetBytes("{\"ref\":\"BOE-1\"}");
        var signed = await signer.SignAsync(tenant.ToString(), payload);

        var tampered = Encoding.UTF8.GetBytes("{\"ref\":\"BOE-2\"}");
        var verdict = await signer.VerifyAsync(tenant.ToString(), tampered, signed.SignatureHeader);

        verdict.Valid.Should().BeFalse();
        verdict.FailureReason.Should().Contain("mismatch", because: "tampered payload must produce a signature mismatch");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Verify_accepts_retired_key_within_overlap_window()
    {
        await using var db = NewDb();
        var signer = NewSigner(db, out _);

        const long tenant = 7L;
        var k1Bytes = SeedKeyBytes();
        SeedKey(db, signer, tenant, "k1",
            activatedAt: DateTimeOffset.UtcNow.AddDays(-2),
            retiredAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            verificationOnlyUntil: DateTimeOffset.UtcNow.AddDays(7),
            material: k1Bytes);

        // Active key for signing — different from k1.
        SeedActiveKey(db, signer, tenant, "k2");

        var payload = Encoding.UTF8.GetBytes("body");
        var sigUnderK1 = ComputeSigForKey(k1Bytes, payload, "k1");

        var verdict = await signer.VerifyAsync(tenant.ToString(), payload, sigUnderK1);
        verdict.Valid.Should().BeTrue();
        verdict.KeyIdUsed.Should().Be("k1");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Verify_rejects_retired_key_after_window_closed()
    {
        await using var db = NewDb();
        var signer = NewSigner(db, out _);

        const long tenant = 7L;
        var k1Bytes = SeedKeyBytes();
        SeedKey(db, signer, tenant, "k1",
            activatedAt: DateTimeOffset.UtcNow.AddDays(-30),
            retiredAt: DateTimeOffset.UtcNow.AddDays(-15),
            verificationOnlyUntil: DateTimeOffset.UtcNow.AddDays(-8), // window closed yesterday-ish
            material: k1Bytes);

        var payload = Encoding.UTF8.GetBytes("body");
        var sigUnderK1 = ComputeSigForKey(k1Bytes, payload, "k1");

        var verdict = await signer.VerifyAsync(tenant.ToString(), payload, sigUnderK1);
        verdict.Valid.Should().BeFalse();
        verdict.FailureReason.Should().Contain("retired");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Verify_rejects_other_tenants_key()
    {
        await using var db = NewDb();
        var signer = NewSigner(db, out _);

        const long tenantA = 7L;
        const long tenantB = 8L;
        var aBytes = SeedKeyBytes();
        SeedKey(db, signer, tenantA, "k1",
            activatedAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            retiredAt: null,
            verificationOnlyUntil: null,
            material: aBytes);
        SeedActiveKey(db, signer, tenantB, "k1"); // tenant B also uses keyId=k1, different material

        var payload = Encoding.UTF8.GetBytes("body");
        var sigUnderA = ComputeSigForKey(aBytes, payload, "k1");

        // Caller in tenant B presents a sig minted by tenant A's key.
        // Because the sig and tenant B's k1 differ, verify must fail.
        var verdict = await signer.VerifyAsync(tenantB.ToString(), payload, sigUnderA);
        verdict.Valid.Should().BeFalse();
        verdict.FailureReason.Should().Contain("mismatch", because: "tenant B's k1 is different material");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Verify_rejects_unknown_keyId()
    {
        await using var db = NewDb();
        var signer = NewSigner(db, out _);

        const long tenant = 7L;
        SeedActiveKey(db, signer, tenant, "k1");

        var payload = Encoding.UTF8.GetBytes("body");
        var fakeHeader = "icums-hmac-sha256 keyId=k99 sig=" + Convert.ToBase64String(new byte[32]);

        var verdict = await signer.VerifyAsync(tenant.ToString(), payload, fakeHeader);
        verdict.Valid.Should().BeFalse();
        verdict.FailureReason.Should().Contain("Unknown keyId");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Sign_throws_when_no_active_key()
    {
        await using var db = NewDb();
        var signer = NewSigner(db, out _);

        var payload = Encoding.UTF8.GetBytes("body");
        var act = async () => await signer.SignAsync("7", payload);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*No active*signing key*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a sig")]
    [InlineData("icums-hmac-sha256 sig=abc")]      // missing keyId
    [InlineData("icums-hmac-sha256 keyId=k1")]     // missing sig
    [InlineData("hmac-md5 keyId=k1 sig=abc")]      // wrong algorithm
    [InlineData("icums-hmac-sha256 keyId=k1 sig=NOTBASE64!!!")]
    [Trait("Category", "Unit")]
    public async Task Verify_rejects_malformed_header(string header)
    {
        await using var db = NewDb();
        var signer = NewSigner(db, out _);
        SeedActiveKey(db, signer, 7L, "k1");

        var verdict = await signer.VerifyAsync("7", Encoding.UTF8.GetBytes("body"), header);
        verdict.Valid.Should().BeFalse();
        verdict.FailureReason.Should().NotBeNullOrEmpty();
    }

    // -- helpers --------------------------------------------------------

    private static InspectionDbContext NewDb()
    {
        var opts = new DbContextOptionsBuilder<InspectionDbContext>()
            .UseInMemoryDatabase("icums-signer-" + Guid.NewGuid())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new InspectionDbContext(opts);
    }

    private static IcumsHmacEnvelopeSigner NewSigner(InspectionDbContext db, out EphemeralDataProtectionProvider provider)
    {
        provider = new EphemeralDataProtectionProvider();
        return new IcumsHmacEnvelopeSigner(db, provider, NullLogger<IcumsHmacEnvelopeSigner>.Instance);
    }

    private static byte[] SeedKeyBytes()
        => IcumsHmacEnvelopeSigner.GenerateKeyMaterial();

    private static void SeedActiveKey(InspectionDbContext db, IcumsHmacEnvelopeSigner signer, long tenantId, string keyId)
    {
        SeedKey(db, signer, tenantId, keyId,
            activatedAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            retiredAt: null,
            verificationOnlyUntil: null,
            material: IcumsHmacEnvelopeSigner.GenerateKeyMaterial());
    }

    private static void SeedKey(
        InspectionDbContext db,
        IcumsHmacEnvelopeSigner signer,
        long tenantId,
        string keyId,
        DateTimeOffset? activatedAt,
        DateTimeOffset? retiredAt,
        DateTimeOffset? verificationOnlyUntil,
        byte[] material)
    {
        db.IcumsSigningKeys.Add(new IcumsSigningKey
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            KeyId = keyId,
            KeyMaterialEncrypted = signer.WrapKeyForStorage(material),
            CreatedAt = DateTimeOffset.UtcNow,
            ActivatedAt = activatedAt,
            RetiredAt = retiredAt,
            VerificationOnlyUntil = verificationOnlyUntil
        });
        db.SaveChanges();
    }

    private static string ComputeSigForKey(byte[] keyBytes, byte[] payload, string keyId)
    {
        using var hmac = new System.Security.Cryptography.HMACSHA256(keyBytes);
        var sig = hmac.ComputeHash(payload);
        return $"icums-hmac-sha256 keyId={keyId} sig={Convert.ToBase64String(sig)}";
    }
}
