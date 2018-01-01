using System;
using Newtonsoft.Json;

namespace GitHubIntegration.Converters
{
    public class JsonDateTimeConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType)
        {
            return objectType.Equals(typeof(DateTimeOffset)) || objectType.Equals(typeof(DateTimeOffset?));
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            string str = reader.Value as string;
            if (str == null)
            {
                long val = (long)reader.Value;
                return DateTimeOffset.FromUnixTimeSeconds(val);
            }
            return DateTimeOffset.Parse(str);
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            writer.WriteValue(((DateTimeOffset)value).ToUniversalTime().ToString("O"));
        }
    }
}