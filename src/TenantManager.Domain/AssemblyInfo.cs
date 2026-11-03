using System.Runtime.CompilerServices;

// Domain tests exercise internal state changes (e.g. marking a unit occupied) that only the
// lease workflow may perform in production code.
[assembly: InternalsVisibleTo("TenantManager.Domain.Tests")]
