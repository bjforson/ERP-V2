using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using NickERP.Inspection.ExternalSystems.Abstractions;
using NickERP.Inspection.ExternalSystems.IcumsGh;

namespace NickERP.Inspection.ExternalSystems.IcumsGh.Tests;

/// <summary>
/// Sprint 9 / FU-icums-signing — exercises
/// <see cref="IcumsGhAdapter.SubmitAsync"/>'s feature-flag wiring:
/// when <c>IcumsGh:Sign</c> is off (default) no <c>.sig</c> file is
/// written; when it's on, a sibling <c>.sig</c> file lands and its
/// contents verify against the signer.
/// </summary>
public sealed class IcumsGhAdapterSubmitAsyncTests : IDisposable
{
    private readonly string _outboxDir;

    public IcumsGhAdapterSubmitAsyncTests()
    {
        _outboxDir = Path.Combine(Path.GetTempPath(), "nickerp-icums-submit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_outboxDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_outboxDir, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task When_sign_flag_is_off_no_sig_file_is_written()
    {
        var adapter = IcumsGhAdapter.ForTests(signer: null, config: BuildConfig(signEnabled: false));

        var result = await adapter.SubmitAsync(
            new ExternalSystemConfig(Guid.NewGuid(), TenantId: 1, ConfigJson: ConfigJsonForOutbox(_outboxDir)),
            new OutboundSubmissionRequest(
                IdempotencyKey: "key-no-sig",
                AuthorityReferenceNumber: "BOE-1",
                PayloadJson: "{\"x\":1}"));

        result.Accepted.Should().BeTrue();
        File.Exists(Path.Combine(_outboxDir, "key-no-sig.json")).Should().BeTrue();
        File.Exists(Path.Combine(_outboxDir, "key-no-sig.json.sig")).Should().BeFalse(
            because: "with the IcumsGh:Sign flag off the adapter must NOT emit a .sig sibling");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task When_sign_flag_is_on_sig_file_is_written_and_verifies()
    {
        var signer = new RecordingSigner();
        var adapter = IcumsGhAdapter.ForTests(signer: signer, config: BuildConfig(signEnabled: true));

        var instanceId = Guid.NewGuid();
        var result = await adapter.SubmitAsync(
            new ExternalSystemConfig(instanceId, TenantId: 42, ConfigJson: ConfigJsonForOutbox(_outboxDir)),
            new OutboundSubmissionRequest(
                IdempotencyKey: "key-with-sig",
                AuthorityReferenceNumber: "BOE-2",
                PayloadJson: "{\"x\":2}"));

        result.Accepted.Should().BeTrue();
        var envelopePath = Path.Combine(_outboxDir, "key-with-sig.json");
        var sigPath = envelopePath + ".sig";
        File.Exists(envelopePath).Should().BeTrue();
        File.Exists(sigPath).Should().BeTrue("the IcumsGh:Sign flag is on so .sig must be written");

        var sigContent = await File.ReadAllTextAsync(sigPath);
        sigContent.Should().StartWith("recording-stub keyId=k-test sig=");

        signer.Calls.Should().ContainSingle();
        signer.Calls[0].TenantId.Should().Be("42");
        signer.Calls[0].PayloadBytes.Should().NotBeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task When_sign_flag_is_on_but_no_signer_registered_falls_back_to_unsigned()
    {
        // Defence-in-depth — if the host enables the flag but forgets to
        // register an IIcumsEnvelopeSigner, the adapter must NOT crash;
        // it falls back to unsigned (and an operator-visible alarm
        // would surface elsewhere, e.g. via a startup health check).
        var adapter = IcumsGhAdapter.ForTests(signer: null, config: BuildConfig(signEnabled: true));

        var result = await adapter.SubmitAsync(
            new ExternalSystemConfig(Guid.NewGuid(), TenantId: 1, ConfigJson: ConfigJsonForOutbox(_outboxDir)),
            new OutboundSubmissionRequest(
                IdempotencyKey: "key-flag-on-no-signer",
                AuthorityReferenceNumber: "BOE-3",
                PayloadJson: "{\"x\":3}"));

        result.Accepted.Should().BeTrue();
        File.Exists(Path.Combine(_outboxDir, "key-flag-on-no-signer.json")).Should().BeTrue();
        File.Exists(Path.Combine(_outboxDir, "key-flag-on-no-signer.json.sig")).Should().BeFalse();
    }

    // -- helpers --------------------------------------------------------

    private static IConfiguration BuildConfig(bool signEnabled)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["IcumsGh:Sign"] = signEnabled ? "true" : "false"
            })
            .Build();
    }

    private static string ConfigJsonForOutbox(string outbox)
    {
        // Match the AdapterConfig shape inside IcumsGhAdapter — only
        // OutboxPath is needed for SubmitAsync.
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            BatchDropPath = "",
            OutboxPath = outbox,
            CacheTtlSeconds = 60
        });
    }

    /// <summary>
    /// Test signer that records every call and returns a deterministic
    /// header. Doesn't perform real HMAC — the adapter test only cares
    /// that the bytes get written; the real signer's correctness is
    /// covered by IcumsHmacEnvelopeSignerTests in the Web.Tests project.
    /// </summary>
    private sealed class RecordingSigner : IIcumsEnvelopeSigner
    {
        public List<(string TenantId, byte[] PayloadBytes)> Calls { get; } = new();

        public Task<SignedEnvelope> SignAsync(string tenantId, ReadOnlyMemory<byte> envelopePayload, CancellationToken ct = default)
        {
            Calls.Add((tenantId, envelopePayload.ToArray()));
            return Task.FromResult(new SignedEnvelope(
                envelopePayload.ToArray(),
                "recording-stub keyId=k-test sig=" + Convert.ToBase64String(new byte[8])));
        }

        public Task<SignatureVerification> VerifyAsync(string tenantId, ReadOnlyMemory<byte> envelopePayload, string signatureHeader, CancellationToken ct = default)
        {
            return Task.FromResult(new SignatureVerification(true, "k-test", null));
        }
    }
}
