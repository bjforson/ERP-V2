using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NickFinance.Ledger;
using NickFinance.PettyCash;
using NickFinance.PettyCash.Disbursement;
using NickFinance.PettyCash.Receipts;
using Xunit;

namespace NickFinance.PettyCash.Tests;

[Collection("PettyCash")]
public class ReceiptAndDisbursementTests
{
    private static Money Ghs(long minor) => new(minor, "GHS");
    private readonly PettyCashFixture _fx;
    public ReceiptAndDisbursementTests(PettyCashFixture fx) => _fx = fx;

    // -----------------------------------------------------------------
    // Receipt service
    // -----------------------------------------------------------------

    [Fact]
    public async Task Receipt_Upload_PersistsHashesAndStoresFile()
    {
        var pc = _fx.CreatePettyCash();
        var lg = _fx.CreateLedger();
        var svc = new PettyCashService(pc, new LedgerWriter(lg));
        var fl = await svc.CreateFloatAsync(Guid.NewGuid(), Guid.NewGuid(), Ghs(10_000_000), Guid.NewGuid());
        var v = await svc.SubmitVoucherAsync(new SubmitVoucherRequest(
            fl.FloatId, Guid.NewGuid(), VoucherCategory.Transport, "Cab",
            Ghs(50_000),
            new[] { new VoucherLineInput("a", Ghs(50_000)) }));

        var storage = new InMemoryReceiptStorage();
        var receipts = new ReceiptService(pc, storage);
        var bytes = Encoding.UTF8.GetBytes("PNG_FAKE_BYTES_payload_a_quick_brown_fox_jumps_over_the_lazy_dog");
        var result = await receipts.UploadAsync(new UploadRequest(
            v.VoucherId, "ride.jpg", "image/jpeg", bytes, Guid.NewGuid(),
            GpsLatitude: 5.6037m, GpsLongitude: -0.187m));

        Assert.NotNull(result.Receipt);
        Assert.Equal(64, result.Receipt.Sha256.Length);
        Assert.Equal(64, result.Receipt.ApproximateHash.Length);
        Assert.Equal(bytes.LongLength, result.Receipt.FileSizeBytes);
        Assert.Null(result.WarnDuplicateOf);

        await pc.DisposeAsync();
        await lg.DisposeAsync();
    }

    [Fact]
    public async Task Receipt_Upload_RejectsDuplicateOnSameVoucher()
    {
        var pc = _fx.CreatePettyCash();
        var lg = _fx.CreateLedger();
        var svc = new PettyCashService(pc, new LedgerWriter(lg));
        var fl = await svc.CreateFloatAsync(Guid.NewGuid(), Guid.NewGuid(), Ghs(10_000_000), Guid.NewGuid());
        var v = await svc.SubmitVoucherAsync(new SubmitVoucherRequest(
            fl.FloatId, Guid.NewGuid(), VoucherCategory.Transport, "Cab",
            Ghs(50_000),
            new[] { new VoucherLineInput("a", Ghs(50_000)) }));

        var receipts = new ReceiptService(pc, new InMemoryReceiptStorage());
        var bytes = Encoding.UTF8.GetBytes("dupe-detection-test-payload-1234567890");
        await receipts.UploadAsync(new UploadRequest(v.VoucherId, "r.jpg", "image/jpeg", bytes, Guid.NewGuid()));
        await Assert.ThrowsAsync<PettyCashException>(() =>
            receipts.UploadAsync(new UploadRequest(v.VoucherId, "r.jpg", "image/jpeg", bytes, Guid.NewGuid())));

        await pc.DisposeAsync();
        await lg.DisposeAsync();
    }

    [Fact]
    public async Task Receipt_Upload_WarnsOnCrossVoucherDuplicate()
    {
        var pc = _fx.CreatePettyCash();
        var lg = _fx.CreateLedger();
        var svc = new PettyCashService(pc, new LedgerWriter(lg));
        var fl = await svc.CreateFloatAsync(Guid.NewGuid(), Guid.NewGuid(), Ghs(10_000_000), Guid.NewGuid());

        var v1 = await svc.SubmitVoucherAsync(new SubmitVoucherRequest(
            fl.FloatId, Guid.NewGuid(), VoucherCategory.Transport, "Cab1",
            Ghs(10_000), new[] { new VoucherLineInput("a", Ghs(10_000)) }));
        var v2 = await svc.SubmitVoucherAsync(new SubmitVoucherRequest(
            fl.FloatId, Guid.NewGuid(), VoucherCategory.Transport, "Cab2",
            Ghs(10_000), new[] { new VoucherLineInput("a", Ghs(10_000)) }));

        var receipts = new ReceiptService(pc, new InMemoryReceiptStorage());
        var bytes = Encoding.UTF8.GetBytes("payload-shared-across-vouchers-very-suspicious-9876");

        await receipts.UploadAsync(new UploadRequest(v1.VoucherId, "r.jpg", "image/jpeg", bytes, Guid.NewGuid()));
        var second = await receipts.UploadAsync(new UploadRequest(v2.VoucherId, "r.jpg", "image/jpeg", bytes, Guid.NewGuid()));

        Assert.NotNull(second.WarnDuplicateOf);
        Assert.Equal(v1.VoucherId, second.WarnDuplicateOf);

        await pc.DisposeAsync();
        await lg.DisposeAsync();
    }

    // -----------------------------------------------------------------
    // Disbursement channels
    // -----------------------------------------------------------------

    [Fact]
    public async Task Disburse_OfflineChannel_StampsRailReferenceAsVoucherNo()
    {
        var pc = _fx.CreatePettyCash();
        var lg = _fx.CreateLedger();
        var period = await new PeriodService(lg).CreateAsync(2026, 4);
        var svc = new PettyCashService(pc, new LedgerWriter(lg));

        var custodian = Guid.NewGuid();
        var fl = await svc.CreateFloatAsync(Guid.NewGuid(), custodian, Ghs(1_000_000), Guid.NewGuid());
        var v = await svc.SubmitVoucherAsync(new SubmitVoucherRequest(
            fl.FloatId, Guid.NewGuid(), VoucherCategory.Transport, "Cab",
            Ghs(20_000), new[] { new VoucherLineInput("a", Ghs(20_000)) }));
        await svc.ApproveVoucherAsync(v.VoucherId, Guid.NewGuid(), null, null);

        v = await svc.DisburseVoucherAsync(v.VoucherId, custodian, new DateOnly(2026, 4, 12), period.PeriodId);
        Assert.Equal("cash", v.DisbursementChannel);
        Assert.Equal(v.VoucherNo, v.DisbursementReference);

        await pc.DisposeAsync(); await lg.DisposeAsync();
    }

    [Fact]
    public async Task Disburse_MomoChannel_StampsRailReferenceFromGatewayResponse()
    {
        var pc = _fx.CreatePettyCash();
        var lg = _fx.CreateLedger();
        var period = await new PeriodService(lg).CreateAsync(2026, 4);
        var svc = new PettyCashService(pc, new LedgerWriter(lg));

        var custodian = Guid.NewGuid();
        var fl = await svc.CreateFloatAsync(Guid.NewGuid(), custodian, Ghs(1_000_000), Guid.NewGuid());
        var v = await svc.SubmitVoucherAsync(new SubmitVoucherRequest(
            fl.FloatId, Guid.NewGuid(), VoucherCategory.OfficeSupplies, "stationery via momo",
            Ghs(30_000),
            new[] { new VoucherLineInput("paper", Ghs(30_000)) },
            PayeeName: "Acme Stationery"));
        // Stamp MoMo target on the voucher.
        v.PayeeMomoNumber = "0241234567";
        v.PayeeMomoNetwork = "MTN";
        await pc.SaveChangesAsync();
        await svc.ApproveVoucherAsync(v.VoucherId, Guid.NewGuid(), null, null);

        // Fake gateway responding 200 OK with a transaction id.
        var stub = new StubHandler((req) =>
        {
            Assert.Equal("/api/disburse/momo", req.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { transactionId = "HUBTEL-TX-9876", status = "accepted" })
            };
        });
        var http = new HttpClient(stub) { BaseAddress = new Uri("http://comms.test/") };
        var channel = new NickCommsMomoChannel(http);

        v = await svc.DisburseVoucherAsync(v.VoucherId, custodian, new DateOnly(2026, 4, 12), period.PeriodId, channel);
        Assert.Equal("momo:hubtel", v.DisbursementChannel);
        Assert.Equal("HUBTEL-TX-9876", v.DisbursementReference);

        await pc.DisposeAsync(); await lg.DisposeAsync();
    }

    [Fact]
    public async Task Disburse_MomoChannel_GatewayReject_LeavesVoucherApprovedAndNoJournal()
    {
        var pc = _fx.CreatePettyCash();
        var lg = _fx.CreateLedger();
        var period = await new PeriodService(lg).CreateAsync(2026, 4);
        var svc = new PettyCashService(pc, new LedgerWriter(lg));

        var custodian = Guid.NewGuid();
        var fl = await svc.CreateFloatAsync(Guid.NewGuid(), custodian, Ghs(1_000_000), Guid.NewGuid());
        var v = await svc.SubmitVoucherAsync(new SubmitVoucherRequest(
            fl.FloatId, Guid.NewGuid(), VoucherCategory.OfficeSupplies, "supplies",
            Ghs(30_000), new[] { new VoucherLineInput("a", Ghs(30_000)) }));
        v.PayeeMomoNumber = "0241234567"; v.PayeeMomoNetwork = "MTN";
        await pc.SaveChangesAsync();
        await svc.ApproveVoucherAsync(v.VoucherId, Guid.NewGuid(), null, null);

        var stub = new StubHandler(req => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("Hubtel timeout")
        });
        var http = new HttpClient(stub) { BaseAddress = new Uri("http://comms.test/") };
        var channel = new NickCommsMomoChannel(http);

        await Assert.ThrowsAsync<PettyCashException>(() =>
            svc.DisburseVoucherAsync(v.VoucherId, custodian, new DateOnly(2026, 4, 12), period.PeriodId, channel));

        await using var pc2 = _fx.CreatePettyCash();
        var stillApproved = await pc2.Vouchers.FirstAsync(x => x.VoucherId == v.VoucherId);
        Assert.Equal(VoucherStatus.Approved, stillApproved.Status);
        Assert.Null(stillApproved.LedgerEventId);
        Assert.Null(stillApproved.DisbursementChannel);

        await pc.DisposeAsync(); await lg.DisposeAsync();
    }

    // -----------------------------------------------------------------
    // Test stub for HttpClient
    // -----------------------------------------------------------------

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _impl;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> impl) => _impl = impl;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(_impl(request));
    }
}
