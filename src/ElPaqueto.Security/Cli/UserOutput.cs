using System.Globalization;
using System.Text;
using System.Text.Json;
using ElPaqueto.Security.Users;

namespace ElPaqueto.Security.Cli;

/// <summary>
/// Renders users for <c>admin user list</c> and <c>admin user show</c>, as text or as JSON.
/// </summary>
/// <remarks>
/// The JSON shape is documented and stable. It is written field by field from <see cref="UserSummary"/>,
/// which has no password hash, so no serializer setting can ever add one.
/// </remarks>
internal static class UserOutput
{
    public const string FormatOption = "--format";

    public static AdminOption FormatOptionSpec { get; } = new(FormatOption, "text|json", "Output format. Default: text.");

    /// <summary>Reads <c>--format</c>: <see langword="true"/> for JSON, <see langword="false"/> for text.</summary>
    public static bool TryReadFormat(ParsedArguments arguments, out bool json, out string error)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var value = arguments.Value(FormatOption);
        json = false;
        error = string.Empty;

        if (value is null || string.Equals(value, "text", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(value, "json", StringComparison.OrdinalIgnoreCase))
        {
            json = true;
            return true;
        }

        error = $"The option '{FormatOption}' accepts 'text' or 'json'.";
        return false;
    }

    public static string ListAsJson(IReadOnlyList<UserSummary> users)
    {
        return Json(writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartArray("users");
            foreach (var user in users)
            {
                Write(writer, user);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    public static string UserAsJson(UserSummary user) => Json(writer => Write(writer, user));

    public static string ListAsText(IReadOnlyList<UserSummary> users)
    {
        if (users.Count == 0)
        {
            return "No users." + Environment.NewLine;
        }

        string[] header = ["USER", "ACTIVE", "PERMISSIONS", "CREATED (UTC)", "UPDATED (UTC)", "PASSWORD CHANGED (UTC)", "ID"];
        var rows = users.Select(u => new[]
        {
            u.UserName,
            u.IsActive ? "yes" : "no",
            PermissionText.Format(u.Permissions),
            Timestamp(u.CreatedAt),
            Timestamp(u.UpdatedAt),
            Timestamp(u.PasswordChangedAt),
            u.Id.ToString(),
        }).ToList();

        var widths = header.Select((h, i) => Math.Max(h.Length, rows.Max(r => r[i].Length))).ToArray();
        var text = new StringBuilder();
        foreach (var row in rows.Prepend(header))
        {
            text.AppendLine(string.Join("  ", row.Select((cell, i) => i == row.Length - 1 ? cell : cell.PadRight(widths[i]))));
        }

        return text.ToString();
    }

    public static string UserAsText(UserSummary user)
    {
        ArgumentNullException.ThrowIfNull(user);

        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"User:             {user.UserName}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Id:               {user.Id}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Active:           {(user.IsActive ? "yes" : "no")}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Permissions:      {PermissionText.Format(user.Permissions)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Created:          {Timestamp(user.CreatedAt)} UTC");
        text.AppendLine(CultureInfo.InvariantCulture, $"Updated:          {Timestamp(user.UpdatedAt)} UTC");
        text.AppendLine(CultureInfo.InvariantCulture, $"Password changed: {Timestamp(user.PasswordChangedAt)} UTC");
        return text.ToString();
    }

    private static void Write(Utf8JsonWriter writer, UserSummary user)
    {
        writer.WriteStartObject();
        writer.WriteString("id", user.Id);
        writer.WriteString("userName", user.UserName);
        writer.WriteBoolean("isActive", user.IsActive);
        writer.WriteStartArray("permissions");
        foreach (var name in PermissionText.Names(user.Permissions))
        {
            writer.WriteStringValue(name);
        }

        writer.WriteEndArray();
        writer.WriteString("createdAt", Iso(user.CreatedAt));
        writer.WriteString("updatedAt", Iso(user.UpdatedAt));
        writer.WriteString("passwordChangedAt", Iso(user.PasswordChangedAt));
        writer.WriteEndObject();
    }

    private static string Json(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            write(writer);
        }

        return Encoding.UTF8.GetString(buffer.ToArray()) + Environment.NewLine;
    }

    private static string Iso(DateTime value)
        => DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

    private static string Timestamp(DateTime value)
        => value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}
