/*
**    GeneralsOnline Game Services - Backend Services for Command & Conquer Generals Online: Zero Hour
**    Copyright (C) 2025  GeneralsOnline Development Team
**
**    This program is free software: you can redistribute it and/or modify
**    it under the terms of the GNU Affero General Public License as
**    published by the Free Software Foundation, either version 3 of the
**    License, or (at your option) any later version.
**
**    This program is distributed in the hope that it will be useful,
**    but WITHOUT ANY WARRANTY; without even the implied warranty of
**    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
**    GNU Affero General Public License for more details.
**
**    You should have received a copy of the GNU Affero General Public License
**    along with this program.  If not, see <https://www.gnu.org/licenses/>.
*/

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GenOnlineService.Realtime
{
	// Payload options shared by the hub and the contract tests
	public static class RealtimeJson
	{
		public static void Configure(JsonSerializerOptions options)
		{
			options.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
			options.DictionaryKeyPolicy = null;
			options.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
			options.NumberHandling = JsonNumberHandling.Strict;
			options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
			options.Converters.Add(new UtcSecondsConverter());
		}

		public static JsonSerializerOptions Create()
		{
			JsonSerializerOptions options = new JsonSerializerOptions();
			Configure(options);
			return options;
		}
	}

	// Writes times as ISO 8601 UTC with whole seconds
	public sealed class UtcSecondsConverter : JsonConverter<DateTime>
	{
		public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
		{
			return DateTime.Parse(reader.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
		}

		public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
		{
			DateTime utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
			writer.WriteStringValue(utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
		}
	}

	// A patch field: omitted keeps the current value, present (even null) replaces it
	[JsonConverter(typeof(OptionalConverterFactory))]
	public readonly struct Optional<T> : IEquatable<Optional<T>>
	{
		public Optional(T value)
		{
			Value = value;
			HasValue = true;
		}

		public bool HasValue { get; }
		public T Value { get; }

		public static implicit operator Optional<T>(T value) => new Optional<T>(value);

		public bool Equals(Optional<T> other) => HasValue == other.HasValue && EqualityComparer<T>.Default.Equals(Value, other.Value);
		public override bool Equals(object? obj) => obj is Optional<T> other && Equals(other);
		public override int GetHashCode() => HashCode.Combine(HasValue, Value);
	}

	public sealed class OptionalConverterFactory : JsonConverterFactory
	{
		public override bool CanConvert(Type typeToConvert) => typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(Optional<>);

		public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
		{
			Type inner = typeToConvert.GetGenericArguments()[0];
			return (JsonConverter)Activator.CreateInstance(typeof(OptionalConverter<>).MakeGenericType(inner))!;
		}

		private sealed class OptionalConverter<T> : JsonConverter<Optional<T>>
		{
			public override bool HandleNull => true;

			public override Optional<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
			{
				return new Optional<T>(JsonSerializer.Deserialize<T>(ref reader, options)!);
			}

			public override void Write(Utf8JsonWriter writer, Optional<T> value, JsonSerializerOptions options)
			{
				JsonSerializer.Serialize(writer, value.Value, options);
			}
		}
	}
}
