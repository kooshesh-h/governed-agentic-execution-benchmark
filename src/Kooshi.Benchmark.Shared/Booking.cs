using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kooshi.Benchmark.Shared;

public sealed class Booking
{
    public string? CustomerName { get; set; }
    public string? Email { get; set; }
    public string? Product { get; set; }
    public DateOnly? PreferredDate { get; set; }
    public string? TimeZone { get; set; }
}

public static class BookingRules
{
    public static readonly IReadOnlyList<BookingField> Fields =
    [
        new("customerName", "string", true),
        new("email", "email", true, "email"),
        new("product", "string", true),
        new("preferredDate", "date", true, "today-or-later"),
        new("timeZone", "timezone", true, "system-timezone")
    ];

    public static List<string> Validate(Booking booking, DateOnly? today = null)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(booking.CustomerName)) errors.Add("customerName is required");
        if (string.IsNullOrWhiteSpace(booking.Email)) errors.Add("email is required");
        else if (!Regex.IsMatch(booking.Email, @"^[^\s@]+@[^\s@]+\.[^\s@]+$")) errors.Add("email is invalid");
        if (string.IsNullOrWhiteSpace(booking.Product)) errors.Add("product is required");
        if (booking.PreferredDate is null) errors.Add("preferredDate is required (YYYY-MM-DD)");
        else if (booking.PreferredDate < (today ?? DateOnly.FromDateTime(DateTime.UtcNow))) errors.Add("preferredDate must not be in the past");
        if (string.IsNullOrWhiteSpace(booking.TimeZone)) errors.Add("timeZone is required");
        else if (!TimeZoneInfo.TryFindSystemTimeZoneById(booking.TimeZone, out _)) errors.Add("timeZone is invalid");
        return errors;
    }

    public static void Merge(Booking target, JsonElement source)
    {
        if (source.ValueKind != JsonValueKind.Object) return;
        foreach (var property in source.EnumerateObject())
        {
            var value = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
            if (value is null) continue;
            switch (property.Name.ToLowerInvariant())
            {
                case "customername": target.CustomerName = value.Trim(); break;
                case "email": target.Email = value.Trim(); break;
                case "product": target.Product = value.Trim(); break;
                case "preferreddate": if (DateOnly.TryParseExact(value, "yyyy-MM-dd", out var date)) target.PreferredDate = date; break;
                case "timezone": target.TimeZone = value.Trim(); break;
            }
        }
    }
}

public sealed record BookingField(string Name, string Type, bool Required, string? Validation = null);
