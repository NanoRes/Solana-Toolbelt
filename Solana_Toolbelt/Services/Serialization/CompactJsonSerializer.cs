using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace Solana.Unity.Toolbelt
{
    internal static class CompactJsonSerializer
    {
        private static readonly JsonSerializerSettings Settings = new();

        [ThreadStatic]
        private static JsonSerializer _serializer;

        [ThreadStatic]
        private static StringBuilder _stringBuilder;

        public static string Serialize<T>(T value)
        {
            var serializer = _serializer ??= JsonSerializer.CreateDefault(Settings);

            var sb = _stringBuilder ??= new StringBuilder(256);
            sb.Clear();

            using var stringWriter = new StringWriter(sb);
            using (var jsonWriter = new JsonTextWriter(stringWriter))
            {
                jsonWriter.Formatting = Formatting.None;
                serializer.Serialize(jsonWriter, value);
            }

            return sb.ToString();
        }
    }
}
