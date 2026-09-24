using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopNMS.Core.Models;

/// <summary>One Graylog stream, from GET streams - what the Graylog tab's stream filter lists.</summary>
public sealed class GraylogStream
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("disabled")]
    public bool Disabled { get; set; }

    /// <summary>"Title (description)", the same text LibreNMS's stream picker shows.</summary>
    [JsonIgnore]
    public string DisplayText => string.IsNullOrWhiteSpace(Description)
        ? Title ?? Id
        : $"{Title ?? Id} ({Description})";
}

/// <summary>GET streams' envelope.</summary>
public sealed class GraylogStreamList
{
    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("streams")]
    public List<GraylogStream> Streams { get; set; } = new();
}

/// <summary>One page of results from a relative-time search.</summary>
public sealed class GraylogSearchResult
{
    [JsonPropertyName("messages")]
    public List<GraylogMessageEnvelope> Messages { get; set; } = new();

    [JsonPropertyName("total_results")]
    public long TotalResults { get; set; }
}

/// <summary>A search hit: the message's fields plus the index it came from.</summary>
public sealed class GraylogMessageEnvelope
{
    [JsonPropertyName("message")]
    public GraylogMessage Message { get; set; } = new();

    [JsonPropertyName("index")]
    public string? Index { get; set; }
}

/// <summary>
/// A Graylog message. Graylog messages are free-form - any input or
/// extractor can add fields - so every field is kept, and the handful the
/// Graylog tab shows as columns (the same ones LibreNMS's table shows) are
/// read out of them rather than being the only thing parsed.
/// </summary>
[JsonConverter(typeof(GraylogMessageConverter))]
public sealed class GraylogMessage
{
    public GraylogMessage()
        : this(new Dictionary<string, JsonElement>(StringComparer.Ordinal))
    {
    }

    public GraylogMessage(IReadOnlyDictionary<string, JsonElement> fields)
    {
        Fields = fields;
    }

    public IReadOnlyDictionary<string, JsonElement> Fields { get; }

    public string? Id => GetText("_id");

    public string? Source => GetText("source");

    public string? Text => GetText("message");

    public string? FullMessage => GetText("full_message");

    /// <summary>The address the message arrived from - LibreNMS's "Origin" column.</summary>
    public string? RemoteIp => GetText("gl2_remote_ip");

    /// <summary>Syslog facility - a number or a name, depending on the input that received it.</summary>
    public string? Facility => GetText("facility");

    /// <summary>Syslog severity, 0 (emergency) to 7 (debug), when the message has one.</summary>
    public int? Level
    {
        get
        {
            if (!Fields.TryGetValue("level", out var value))
            {
                return null;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            {
                return number;
            }

            return value.ValueKind == JsonValueKind.String
                   && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
        }
    }

    /// <summary>When Graylog received the message, in UTC.</summary>
    public DateTimeOffset? Timestamp =>
        DateTimeOffset.TryParse(GetText("timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;

    /// <summary>Any field as display text - strings as-is, everything else as its JSON.</summary>
    public string? GetText(string field)
    {
        if (!Fields.TryGetValue(field, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => value.GetRawText(),
        };
    }
}

internal sealed class GraylogMessageConverter : JsonConverter<GraylogMessage>
{
    public override GraylogMessage Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);

        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (document.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in document.RootElement.EnumerateObject())
            {
                // Cloned so the values outlive the document being disposed.
                fields[property.Name] = property.Value.Clone();
            }
        }

        return new GraylogMessage(fields);
    }

    public override void Write(Utf8JsonWriter writer, GraylogMessage value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (name, field) in value.Fields)
        {
            writer.WritePropertyName(name);
            field.WriteTo(writer);
        }

        writer.WriteEndObject();
    }
}
