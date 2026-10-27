using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;

namespace MinutoUtil.Core;

public static class Json
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <summary>
    /// JSON schema for <typeparamref name="T"/>, used to force structured LLM output. [Description] attributes become
    /// "description" fields, so the model reads what each field is for right next to it.
    /// </summary>
    public static JsonNode SchemaFor<T>() =>
        Options.GetJsonSchemaAsNode(typeof(T), new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = (context, node) =>
            {
                var provider = context.PropertyInfo?.AttributeProvider;
                if (provider?.GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), inherit: true)
                        .OfType<System.ComponentModel.DescriptionAttribute>().FirstOrDefault() is { } description
                    && node is JsonObject obj)
                    obj.Insert(0, "description", description.Description);
                return node;
            },
        });
}
