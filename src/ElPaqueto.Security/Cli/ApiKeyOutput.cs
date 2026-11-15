using System.Globalization;
using System.Text;
using System.Text.Json;
using ElPaqueto.Security.ApiKeys;

namespace ElPaqueto.Security.Cli;

/// <summary>
/// An API key as the <c>admin key</c> commands show it: the key, its owner, and what it grants now.
/// </summary>
/// <param name="Key">The key, without secret or hash.</param>
/// <param name="OwnerName">The owner's user name.</param>
/// <param name="OwnerIsActive">Whether the owner is active.</param>
/// <param name="OwnerPermissions">The owner's current permissions.</param>
/// <param name="Now">The time the status is computed for (UTC).</param>
internal sealed record ApiKeyView(ApiKeyInfo Key, string OwnerName, bool OwnerIsActive, Permission OwnerPermissions, DateTime Now)
{
    /// <summary>The key's permissions that the owner still holds: what the key grants while its status is <c>active</c>.</summary>
    public Permission EffectivePermissions => Key.Permissions & OwnerPermissions;

    /// <summary><c>revoked</c>, <c>expired</c>, <c>owner-disabled</c> or <c>active</c>, in this order of precedence.</summary>
    public string Status
    {
        get
        {
            if (Key.RevokedAt is not null)
            {
                return "revoked";
            }

            if (Key.ExpiresAt is { } expires && expires <= Now)
            {
                return "expired";
            }

            return OwnerIsActive ? "active" : "owner-disabled";
        }
    }
}

/// <summary>
/// Renders API keys for <c>admin key …</c>, as text or as JSON.
/// </summary>
/// <remarks>
/// The JSON shape is documented and stable. It is written field by field from <see cref="ApiKeyView"/>,
/// which has neither the secret nor its hash; the token is only written by <see cref="CreatedAsJson"/>.
/// </remarks>
internal static class ApiKeyOutput
{
    public static string ListAsJson(IReadOnlyList<ApiKeyView> keys)
    {
        return Json(writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartArray("keys");
            foreach (var key in keys)
            {
                Write(writer, key);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    public static string KeyAsJson(ApiKeyView key) => Json(writer => Write(writer, key));

    /// <summary>The result of <c>create</c> or <c>rotate</c>: the token and the new key.</summary>
    public static string CreatedAsJson(string token, ApiKeyView key, string? replacedKeyId)
    {
        return Json(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("token", token);
            if (replacedKeyId is not null)
            {
                writer.WriteString("replacedKeyId", replacedKeyId);
            }

            writer.WritePropertyName("key");
            Write(writer, key);
            writer.WriteEndObject();
        });
    }

    public static string ListAsText(IReadOnlyList<ApiKeyView> keys)
    {
        if (keys.Count == 0)
        {
            return "No API keys." + Environment.NewLine;
        }

        string[] header = ["KEY ID", "NAME", "OWNER", "STATUS", "PERMISSIONS", "EFFECTIVE", "CREATED (UTC)", "EXPIRES (UTC)", "LAST USED (UTC)"];
        var rows = keys.Select(k => new[]
        {
            k.Key.KeyId,
            k.Key.Name,
            k.OwnerName,
            k.Status,
            PermissionText.Format(k.Key.Permissions),
            PermissionText.Format(k.EffectivePermissions),
            Timestamp(k.Key.CreatedAt),
            k.Key.ExpiresAt is { } expires ? Timestamp(expires) : "never",
            k.Key.LastUsedAt is { } used ? Timestamp(used) : "-",
        }).ToList();

        var widths = header.Select((h, i) => Math.Max(h.Length, rows.Max(r => r[i].Length))).ToArray();
        var text = new StringBuilder();
        foreach (var row in rows.Prepend(header))
        {
            text.AppendLine(string.Join("  ", row.Select((cell, i) => i == row.Length - 1 ? cell : cell.PadRight(widths[i]))));
        }

        return text.ToString();
    }

    public static string KeyAsText(ApiKeyView key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Key id:                {key.Key.KeyId}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Name:                  {key.Key.Name}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Owner:                 {key.OwnerName} ({key.Key.OwnerUserId})");
        text.AppendLine(CultureInfo.InvariantCulture, $"Status:                {key.Status}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Permissions:           {PermissionText.Format(key.Key.Permissions)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Effective permissions: {PermissionText.Format(key.EffectivePermissions)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Created:               {ExpiryText.Format(key.Key.CreatedAt)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Expires:               {ExpiryText.Format(key.Key.ExpiresAt)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Revoked:               {(key.Key.RevokedAt is { } revoked ? ExpiryText.Format(revoked) : "no")}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Last used:             {(key.Key.LastUsedAt is { } used ? ExpiryText.Format(used) : "never")}");
        return text.ToString();
    }

    private static void Write(Utf8JsonWriter writer, ApiKeyView key)
    {
        writer.WriteStartObject();
        writer.WriteString("keyId", key.Key.KeyId);
        writer.WriteString("name", key.Key.Name);
        writer.WriteString("ownerId", key.Key.OwnerUserId);
        writer.WriteString("owner", key.OwnerName);
        writer.WriteString("status", key.Status);
        Permissions(writer, "permissions", key.Key.Permissions);
        Permissions(writer, "effectivePermissions", key.EffectivePermissions);
        writer.WriteString("createdAt", Iso(key.Key.CreatedAt));
        NullableDate(writer, "expiresAt", key.Key.ExpiresAt);
        NullableDate(writer, "revokedAt", key.Key.RevokedAt);
        NullableDate(writer, "lastUsedAt", key.Key.LastUsedAt);
        writer.WriteEndObject();
    }

    private static void Permissions(Utf8JsonWriter writer, string name, Permission permissions)
    {
        writer.WriteStartArray(name);
        foreach (var flag in PermissionText.Names(permissions))
        {
            writer.WriteStringValue(flag);
        }

        writer.WriteEndArray();
    }

    private static void NullableDate(Utf8JsonWriter writer, string name, DateTime? value)
    {
        if (value is { } date)
        {
            writer.WriteString(name, Iso(date));
        }
        else
        {
            writer.WriteNull(name);
        }
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
