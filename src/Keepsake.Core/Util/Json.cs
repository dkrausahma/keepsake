using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Keepsake.Core.Util
{
    /// <summary>
    /// JSON settings shared by every repository object. Serialization must stay deterministic
    /// (property declaration order, no indentation) because tree IDs are computed over the bytes.
    /// </summary>
    public static class Json
    {
        public static readonly JsonSerializerOptions Compact = Create(false);
        public static readonly JsonSerializerOptions Indented = Create(true);

        private static JsonSerializerOptions Create(bool indented)
        {
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                WriteIndented = indented,
            };
            options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
            return options;
        }

        public static byte[] ToBytes<T>(T value, bool indented = false) =>
            JsonSerializer.SerializeToUtf8Bytes(value, indented ? Indented : Compact);

        public static string ToText<T>(T value, bool indented = true) =>
            JsonSerializer.Serialize(value, indented ? Indented : Compact);

        public static T FromBytes<T>(byte[] data)
        {
            T value;
            try
            {
                value = JsonSerializer.Deserialize<T>(data, Compact);
            }
            catch (JsonException ex)
            {
                throw new RepositoryFormatException("Invalid JSON in " + typeof(T).Name + ": " + ex.Message, ex);
            }
            if (value == null)
                throw new RepositoryFormatException("Empty JSON document for " + typeof(T).Name + ".");
            return value;
        }

        public static T FromText<T>(string text) => FromBytes<T>(System.Text.Encoding.UTF8.GetBytes(text));
    }
}
