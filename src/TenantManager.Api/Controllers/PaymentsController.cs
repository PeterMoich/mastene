using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TenantManager.Api.Infrastructure;
using TenantManager.Application.Common;
using TenantManager.Application.Files;
using TenantManager.Application.Payments;

namespace TenantManager.Api.Controllers;

/// <summary>Multipart form for a proof-of-payment upload.</summary>
public sealed class SubmitPaymentForm
{
    public Guid LeaseId { get; set; }
    public decimal Amount { get; set; }
    public DateOnly PaidOn { get; set; }

    /// <summary>Optional month this payment is for, e.g. 2026-03.</summary>
    public string? Period { get; set; }

    public string? Reference { get; set; }

    /// <summary>PDF, JPG or PNG, up to 5 MB.</summary>
    public IFormFile? File { get; set; }
}

/// <summary>
/// Proof of payment (BR10 to BR12). Tenants submit their own; landlords see all of theirs and verify
/// or reject them. Policies are per action because tenants and landlords share some endpoints.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/payments")]
public sealed class PaymentsController(PaymentService payments) : ControllerBase
{
    /// <summary>Lists payments, newest first. Filter by status, leaseId, propertyId, period and a paidOn from/to range.</summary>
    [HttpGet]
    [Authorize(Policy = Policies.LandlordOrTenant)]
    public Task<PagedResult<PaymentDto>> List([FromQuery] PaymentListQuery query, CancellationToken ct) =>
        payments.ListAsync(query, ct);

    [HttpGet("{id:guid}")]
    [Authorize(Policy = Policies.LandlordOrTenant)]
    public Task<PaymentDto> Get(Guid id, CancellationToken ct) => payments.GetAsync(id, ct);

    /// <summary>Uploads proof of payment. The file's real type is checked from its contents, then it is virus-scanned.</summary>
    [HttpPost]
    [Authorize(Policy = Policies.LandlordOrTenant)]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(6 * 1024 * 1024)] // 5 MB file plus form overhead; anything bigger is refused before it is read
    [RequestFormLimits(MultipartBodyLengthLimit = 6 * 1024 * 1024)]
    [ProducesResponseType<PaymentDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<PaymentDto>> Submit([FromForm] SubmitPaymentForm form, CancellationToken ct)
    {
        if (form.File is null)
            return ValidationProblem(new ValidationProblemDetails(new Dictionary<string, string[]>
            {
                ["file"] = ["Choose the file to upload."],
            }));

        await using var stream = form.File.OpenReadStream();
        var created = await payments.SubmitAsync(
            new SubmitPaymentRequest(form.LeaseId, form.Amount, form.PaidOn, form.Period, form.Reference),
            new UploadedFile(form.File.FileName, form.File.Length, stream), ct);
        return Created($"/api/v1/payments/{created.Id}", created);
    }

    /// <summary>Verifies the payment, applies it in the ledger and issues a receipt.</summary>
    [HttpPost("{id:guid}/verify")]
    [Authorize(Policy = Policies.Landlord)]
    public Task<PaymentDto> Verify(Guid id, CancellationToken ct) => payments.VerifyAsync(id, ct);

    /// <summary>Rejects the payment. A reason is required (BR11).</summary>
    [HttpPost("{id:guid}/reject")]
    [Authorize(Policy = Policies.Landlord)]
    public Task<PaymentDto> Reject(Guid id, RejectPaymentRequest request, CancellationToken ct) =>
        payments.RejectAsync(id, request, ct);

    /// <summary>The uploaded file. Served with its checked content type and as an attachment, never rendered inline.</summary>
    [HttpGet("{id:guid}/file")]
    [Authorize(Policy = Policies.LandlordOrTenant)]
    public async Task<IActionResult> DownloadFile(Guid id, CancellationToken ct)
    {
        var (content, contentType, fileName) = await payments.OpenFileAsync(id, ct);
        return File(content, contentType, fileName);
    }

    [HttpGet("{id:guid}/receipt")]
    [Authorize(Policy = Policies.LandlordOrTenant)]
    [Produces("application/pdf")]
    public async Task<IActionResult> Receipt(Guid id, CancellationToken ct)
    {
        var (content, fileName) = await payments.OpenReceiptAsync(id, ct);
        return File(content, "application/pdf", fileName);
    }
}

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/leases/{leaseId:guid}")]
public sealed class LeaseMoneyController(RentService rent, DepositService deposits) : ControllerBase
{
    /// <summary>Charges, payments applied, balance and arrears for one lease (BR09).</summary>
    [HttpGet("ledger")]
    [Authorize(Policy = Policies.LandlordOrTenant)]
    public Task<LedgerDto> Ledger(Guid leaseId, CancellationToken ct) => rent.GetLedgerAsync(leaseId, ct);

    /// <summary>The deposit, shared by every version of the lease (BR13).</summary>
    [HttpGet("deposit")]
    [Authorize(Policy = Policies.LandlordOrTenant)]
    public Task<DepositDto> Deposit(Guid leaseId, CancellationToken ct) => deposits.GetAsync(leaseId, ct);

    [HttpPost("deposit/receive")]
    [Authorize(Policy = Policies.Landlord)]
    public Task<DepositDto> ReceiveDeposit(Guid leaseId, ReceiveDepositRequest request, CancellationToken ct) =>
        deposits.ReceiveAsync(leaseId, request, ct);

    /// <summary>Adds a deduction with a reason. Only after the lease has ended, and never more than is held.</summary>
    [HttpPost("deposit/deductions")]
    [Authorize(Policy = Policies.Landlord)]
    public Task<DepositDto> AddDeduction(Guid leaseId, DepositDeductionRequest request, CancellationToken ct) =>
        deposits.AddDeductionAsync(leaseId, request, ct);

    /// <summary>Records the refund of what is left after deductions.</summary>
    [HttpPost("deposit/refund")]
    [Authorize(Policy = Policies.Landlord)]
    public Task<DepositDto> RefundDeposit(Guid leaseId, RefundDepositRequest request, CancellationToken ct) =>
        deposits.RefundAsync(leaseId, request, ct);
}

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/arrears")]
[Authorize(Policy = Policies.Landlord)]
public sealed class ArrearsController(RentService rent) : ControllerBase
{
    /// <summary>Leases with overdue rent, most overdue first.</summary>
    [HttpGet]
    public Task<IReadOnlyList<ArrearsDto>> List(CancellationToken ct) => rent.ArrearsAsync(ct);
}
