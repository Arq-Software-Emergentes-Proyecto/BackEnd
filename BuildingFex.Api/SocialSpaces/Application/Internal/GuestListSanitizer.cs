using System.Text.Json;
using System.Text.Json.Nodes;

namespace BuildingFex.Api.SocialSpaces.Application.Internal;

/// <summary>
/// Normaliza la lista de invitados de una reserva: {id, name, checkedIn, checkedInAt}.
/// Si <c>allowCheckInChanges</c> es false (residente), el estado de ingreso se toma del servidor:
/// solo recepción/admin marca ingresos.
/// </summary>
public static class GuestListSanitizer
{
    public static string Sanitize(
        string? incomingJson,
        string? currentJson,
        bool allowCheckInChanges,
        DateTimeOffset now)
    {
        var current = new Dictionary<string, (bool CheckedIn, string? At)>(StringComparer.Ordinal);
        try
        {
            if (JsonNode.Parse(string.IsNullOrWhiteSpace(currentJson) ? "[]" : currentJson) is JsonArray existing)
            {
                foreach (var g in existing.OfType<JsonObject>())
                {
                    var gid = g["id"]?.ToString();
                    if (string.IsNullOrWhiteSpace(gid)) continue;
                    current[gid] = (ReadBool(g["checkedIn"]), g["checkedInAt"]?.ToString());
                }
            }
        }
        catch (JsonException)
        {
            // estado previo ilegible → sin check-ins
        }

        var result = new JsonArray();
        try
        {
            if (JsonNode.Parse(string.IsNullOrWhiteSpace(incomingJson) ? "[]" : incomingJson) is not JsonArray incoming)
                return currentJson ?? "[]";

            var index = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var g in incoming.OfType<JsonObject>())
            {
                var name = g["name"]?.ToString()?.Trim();
                if (string.IsNullOrEmpty(name)) continue;
                var gid = g["id"]?.ToString()?.Trim();
                if (string.IsNullOrEmpty(gid) || !seen.Add(gid))
                {
                    gid = $"guest-{now.ToUnixTimeMilliseconds()}-{index}";
                    seen.Add(gid);
                }

                current.TryGetValue(gid, out var prev);
                bool checkedIn;
                string? checkedInAt;
                if (allowCheckInChanges && g.ContainsKey("checkedIn"))
                {
                    checkedIn = ReadBool(g["checkedIn"]);
                    checkedInAt = checkedIn
                        ? (g["checkedInAt"]?.ToString() is { Length: > 0 } at ? at
                            : prev.CheckedIn ? prev.At : now.UtcDateTime.ToString("o"))
                        : null;
                }
                else
                {
                    checkedIn = prev.CheckedIn;
                    checkedInAt = prev.CheckedIn ? prev.At : null;
                }

                result.Add(new JsonObject
                {
                    ["id"] = gid,
                    ["name"] = name.Length > 120 ? name[..120] : name,
                    ["checkedIn"] = checkedIn,
                    ["checkedInAt"] = checkedInAt,
                });
                index++;
            }
        }
        catch (JsonException)
        {
            return currentJson ?? "[]";
        }

        return result.ToJsonString();
    }

    private static bool ReadBool(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<bool>(out var b) && b;
}
