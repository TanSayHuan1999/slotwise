using Slotwise.Data;

namespace Slotwise.Web;

/// <summary>Claim types an admin's auth cookie carries.</summary>
public static class SlotwiseClaims
{
    public const string TenantId = "slotwise:tenant_id";
    public const string TenantSlug = "slotwise:tenant_slug";
    public const string TenantName = "slotwise:tenant_name";
}

/// <summary>
/// Bridges cookie authentication and multi-tenancy. An authenticated admin
/// carries their tenant id as a claim; this middleware (registered after
/// UseAuthentication) copies it into the scoped <see cref="ITenantContext"/>,
/// so the DbContext's global query filters scope every admin query to that
/// tenant without page code having to set it. Public/API requests have no
/// such claim and set the context themselves (by slug), as in stage 1.
/// </summary>
public class TenantClaimMiddleware
{
    private readonly RequestDelegate _next;

    public TenantClaimMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, ITenantContext tenantContext)
    {
        var claim = context.User.FindFirst(SlotwiseClaims.TenantId);
        if (claim is not null && Guid.TryParse(claim.Value, out var tenantId))
            tenantContext.TenantId = tenantId;

        await _next(context);
    }
}
