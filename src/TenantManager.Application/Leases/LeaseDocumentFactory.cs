using System.Security.Cryptography;
using TenantManager.Application.Abstractions;
using TenantManager.Domain.Leases;

namespace TenantManager.Application.Leases;

/// <summary>Renders a lease PDF, stores it, and returns the document record (hash and size) for the lease.</summary>
public sealed class LeaseDocumentFactory(ILeaseDocumentRenderer renderer, IFileStorage files, IClock clock)
{
    /// <remarks>Needs the lease's Unit (with Property) and Tenant loaded.</remarks>
    public async Task<LeaseDocument> CreateAsync(Lease lease, LeaseDocumentKind kind, string landlordName,
        string templateVersion, CancellationToken ct)
    {
        var property = lease.Unit.Property;
        var tenant = lease.Tenant;
        var address = string.Join(", ", new[]
        {
            property.AddressLine1, property.AddressLine2, property.City, property.Province, property.PostalCode,
        }.Where(p => !string.IsNullOrWhiteSpace(p)));

        var signatures = kind == LeaseDocumentKind.Signed
            ? lease.Signatures.OrderBy(s => s.Party)
                .Select(s => new LeasePdfSignature(s.Party.ToString(), s.SignerName, s.SignedAt, s.IpAddress)).ToList()
            : [];

        var model = new LeasePdfModel(
            lease.LeaseNumber, lease.Version, lease.IsRenewal, templateVersion, landlordName,
            tenant.FullName, tenant.Email, tenant.Phone,
            property.Name, address, lease.Unit.Label,
            lease.StartDate, lease.EndDate, lease.MonthlyRent, lease.DepositAmount, lease.EscalationPct,
            signatures, clock.UtcNow);

        var bytes = renderer.Render(model);
        var key = $"leases/{lease.Id:N}/{Guid.NewGuid():N}.pdf";
        using (var stream = new MemoryStream(bytes, writable: false))
        {
            await files.SaveAsync(key, stream, "application/pdf", ct);
        }
        return new LeaseDocument(kind, key, Convert.ToHexString(SHA256.HashData(bytes)), bytes.LongLength);
    }
}
