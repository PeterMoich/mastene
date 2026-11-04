namespace TenantManager.Domain.Common;

/// <summary>What anonymised personal details are replaced with (POPIA erasure, ADR-0020).</summary>
public static class Privacy
{
    public const string FormerTenant = "Former tenant";
    public const string Removed = "[removed: personal information erased]";
}
