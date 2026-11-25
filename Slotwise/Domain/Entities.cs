namespace Slotwise.Domain;

public enum BookingStatus
{
    Confirmed = 0,
    Cancelled = 1,
    Completed = 2,
    NoShow = 3,
}

public class Tenant
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public string TimeZoneId { get; set; } = "Asia/Singapore";
    public DateTimeOffset CreatedAt { get; set; }
}

public class Service
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string Name { get; set; }
    public int DurationMinutes { get; set; }
    public int PriceCents { get; set; }
    public bool IsActive { get; set; } = true;
}

public class StaffMember
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string Name { get; set; }
    public bool IsActive { get; set; } = true;
}

public class WorkingHours
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid StaffMemberId { get; set; }
    public DayOfWeek DayOfWeek { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
}

/// <summary>
/// A login for a tenant's admin area. One admin per tenant in v1.
/// PasswordHash is an ASP.NET Core PasswordHasher (PBKDF2) hash — the
/// plaintext password is never stored.
/// </summary>
public class TenantAdmin
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string Email { get; set; }
    public required string PasswordHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class Booking
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ServiceId { get; set; }
    public Guid StaffMemberId { get; set; }
    public required string CustomerName { get; set; }
    public required string CustomerContact { get; set; }
    public DateTimeOffset StartUtc { get; set; }
    public DateTimeOffset EndUtc { get; set; }
    public BookingStatus Status { get; set; } = BookingStatus.Confirmed;
    public DateTimeOffset? ReminderSentAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
