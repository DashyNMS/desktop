using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopNMS.Core.Json;

/// <summary>
/// Shared serialiser configuration for the LibreNMS API.
/// </summary>
/// <remarks>
/// LibreNMS returns rows straight out of MySQL, so numeric and boolean columns
/// arrive as JSON strings ("1", "0") and timestamps as "yyyy-MM-dd HH:mm:ss"
/// rather than ISO-8601. Every converter here exists to absorb that.
/// </remarks>
public static class LibreNmsJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        options.Converters.Add(new FlexibleInt32Converter());
        options.Converters.Add(new FlexibleNullableInt32Converter());
        options.Converters.Add(new FlexibleInt64Converter());
        options.Converters.Add(new FlexibleBooleanConverter());
        options.Converters.Add(new LibreNmsDateTimeConverter());

        // LooseStringConverter is deliberately NOT registered globally: a
        // JsonConverter<string> in the options list is also consulted for
        // dictionary keys, which it cannot serve. Apply it per property instead.

        return options;
    }
}

/// <summary>Reads an <see cref="int"/> from a JSON number, string or boolean.</summary>
public sealed class FlexibleInt32Converter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => FlexibleNumber.ReadInt32(ref reader) ?? 0;

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value);
}

/// <summary>Reads a nullable <see cref="int"/>, tolerating empty strings and nulls.</summary>
public sealed class FlexibleNullableInt32Converter : JsonConverter<int?>
{
    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => FlexibleNumber.ReadInt32(ref reader);

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
        {
            writer.WriteNumberValue(value.Value);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}

/// <summary>Reads a <see cref="long"/> from a JSON number or string.</summary>
public sealed class FlexibleInt64Converter : JsonConverter<long>
{
    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return reader.TryGetInt64(out var number) ? number : (long)reader.GetDouble();
            case JsonTokenType.True:
                return 1;
            case JsonTokenType.False:
            case JsonTokenType.Null:
                return 0;
            case JsonTokenType.String:
                var text = reader.GetString();
                return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
            default:
                reader.Skip();
                return 0;
        }
    }

    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value);
}

/// <summary>Reads a <see cref="bool"/> from true/false, 1/0 or "1"/"0"/"true"/"yes".</summary>
public sealed class FlexibleBooleanConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.True:
                return true;
            case JsonTokenType.False:
            case JsonTokenType.Null:
                return false;
            case JsonTokenType.Number:
                return reader.TryGetInt64(out var number) && number != 0;
            case JsonTokenType.String:
                var text = reader.GetString();
                if (string.IsNullOrWhiteSpace(text))
                {
                    return false;
                }

                if (bool.TryParse(text, out var parsedBool))
                {
                    return parsedBool;
                }

                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedNumber))
                {
                    return parsedNumber != 0;
                }

                return text.Equals("yes", StringComparison.OrdinalIgnoreCase)
                    || text.Equals("on", StringComparison.OrdinalIgnoreCase);
            default:
                reader.Skip();
                return false;
        }
    }

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
        => writer.WriteBooleanValue(value);
}

/// <summary>
/// Reads a string property even when the server sends a number, boolean or an
/// embedded object (the alert "info" column can be any of these).
/// </summary>
public sealed class LooseStringConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return reader.GetString();
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.Number:
                return reader.TryGetInt64(out var number)
                    ? number.ToString(CultureInfo.InvariantCulture)
                    : reader.GetDouble().ToString(CultureInfo.InvariantCulture);
            case JsonTokenType.True:
                return "true";
            case JsonTokenType.False:
                return "false";
            default:
                using (var document = JsonDocument.ParseValue(ref reader))
                {
                    return document.RootElement.GetRawText();
                }
        }
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStringValue(value);
        }
    }
}

/// <summary>
/// Parses the MySQL DATETIME shape LibreNMS emits ("2024-05-01 09:31:07"),
/// falling back to ISO-8601 and Unix seconds.
/// </summary>
public sealed class LibreNmsDateTimeConverter : JsonConverter<DateTime?>
{
    private static readonly string[] Formats =
    {
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-dd HH:mm:ss.fff",
        "yyyy-MM-dd",
    };

    public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var unixSeconds))
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            reader.Skip();
            return null;
        }

        var text = reader.GetString();
        if (string.IsNullOrWhiteSpace(text) || text.StartsWith("0000-00-00", StringComparison.Ordinal))
        {
            return null;
        }

        if (DateTime.TryParseExact(text, Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
        {
            return DateTime.SpecifyKind(exact, DateTimeKind.Unspecified);
        }

        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var loose))
        {
            return DateTime.SpecifyKind(loose, DateTimeKind.Unspecified);
        }

        return null;
    }

    public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
        {
            writer.WriteStringValue(value.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}

internal static class FlexibleNumber
{
    public static int? ReadInt32(ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return reader.TryGetInt32(out var number) ? number : (int)reader.GetDouble();
            case JsonTokenType.True:
                return 1;
            case JsonTokenType.False:
                return 0;
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.String:
                var text = reader.GetString();
                if (string.IsNullOrWhiteSpace(text))
                {
                    return null;
                }

                return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
            default:
                reader.Skip();
                return null;
        }
    }
}
