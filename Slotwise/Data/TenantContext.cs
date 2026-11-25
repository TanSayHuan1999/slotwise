namespace Slotwise.Data;

/// <summary>
/// Holds the current tenant for the request / unit of work. The DbContext's
/// global query filters read this, so every query is tenant-scoped without
/// each call site having to remember a TenantId predicate.
///
/// Null means "no tenant selected yet" (tenant creation, slug resolution,
/// health checks). For tenant-owned entities the filters fail closed in that
/// state: a query with no tenant selected returns nothing.
/// </summary>
public interface ITenantContext
{
    Guid? TenantId { get; set; }
}

public class TenantContext : ITenantContext
{
    public Guid? TenantId { get; set; }
}
