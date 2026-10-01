using System.Text.Json;
using System.Text.Json.Serialization;

namespace SecureFact.SharedKernel.Domain;

/// <summary>Serialised as a plain GUID string so API consumers never see the wrapper object.</summary>
[JsonConverter(typeof(TenantIdJsonConverter))]
public readonly record struct TenantId(Guid Value)
{
    public static TenantId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}

public sealed class TenantIdJsonConverter : JsonConverter<TenantId>
{
    public override TenantId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetGuid());

    public override void Write(Utf8JsonWriter writer, TenantId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}
