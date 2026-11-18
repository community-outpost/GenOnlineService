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

using GenOnlineService.Realtime;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace GenOnlineService.OpenApi
{
	// The generator reads the host-wide JSON options, while /api/v1 writes with the hub's snake_case options
	// (ApiResults.Json). This puts the wire names, strict numbers and string enums of those options into the schemas.
	// The generator runs schema transformers only on the schemas it starts from, not on types it reaches through their
	// properties, so FixComponents goes over every component once more at the end
	internal sealed class WireNamesSchemaTransformer : IOpenApiSchemaTransformer
	{
		private static readonly JsonSerializerOptions s_wire = WireOptions();

		// every type a schema was made for, the types behind the other components are reached from these
		private static readonly ConcurrentDictionary<Type, byte> s_seen = new();

		private static JsonSerializerOptions WireOptions()
		{
			JsonSerializerOptions options = RealtimeJson.Create();
			options.TypeInfoResolver = new DefaultJsonTypeInfoResolver();
			options.MakeReadOnly();
			return options;
		}

		public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
		{
			s_seen.TryAdd(context.JsonTypeInfo.Type, 0);
			Fix(schema, context.JsonTypeInfo, context.JsonTypeInfo.Options);
			return Task.CompletedTask;
		}

		public static void FixComponents(OpenApiDocument document, IServiceProvider services)
		{
			if (document.Components?.Schemas is not { Count: > 0 } components)
			{
				return;
			}

			JsonSerializerOptions generated = services.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
			Dictionary<string, Type> types = new(StringComparer.Ordinal);
			HashSet<Type> visited = new();
			Queue<Type> queue = new(s_seen.Keys);
			while (queue.TryDequeue(out Type? type))
			{
				if (!visited.Add(type))
				{
					continue;
				}

				JsonTypeInfo info = generated.GetTypeInfo(type);
				if (OpenApiOptions.CreateDefaultSchemaReferenceId(info) is string id)
				{
					types.TryAdd(id, type);
				}
				foreach (Type next in Reached(info))
				{
					queue.Enqueue(next);
				}
			}

			foreach ((string id, IOpenApiSchema component) in components)
			{
				if (component is OpenApiSchema schema && types.TryGetValue(id, out Type? type))
				{
					Fix(schema, generated.GetTypeInfo(type), generated);
				}
			}
		}

		private static IEnumerable<Type> Reached(JsonTypeInfo info)
		{
			if (Nullable.GetUnderlyingType(info.Type) is Type underlying)
			{
				yield return underlying;
			}
			if (info.ElementType != null)
			{
				yield return info.ElementType;
			}
			if (info.KeyType != null)
			{
				yield return info.KeyType;
			}
			if (info.Kind == JsonTypeInfoKind.Object)
			{
				foreach (JsonPropertyInfo property in info.Properties)
				{
					yield return property.PropertyType;
				}
			}
		}

		// One schema of a type, and the inline schemas of its properties and items, safe to run twice
		private static void Fix(OpenApiSchema schema, JsonTypeInfo info, JsonSerializerOptions generated)
		{
			Type type = info.Type;
			Type plain = Nullable.GetUnderlyingType(type) ?? type;

			if (plain.IsEnum)
			{
				// written as snake_case strings, never as numbers
				schema.Type = type != plain || schema.Type?.HasFlag(JsonSchemaType.Null) == true ? JsonSchemaType.String | JsonSchemaType.Null : JsonSchemaType.String;
				schema.Format = null;
				schema.Pattern = null;
				schema.Enum = Enum.GetNames(plain).Select(name => (JsonNode)JsonValue.Create(EnumWireName(plain, name))!).ToList();
			}
			else if (IsNumber(plain))
			{
				// numbers are strict on the wire, a quoted number is refused
				if (schema.Type is JsonSchemaType numberType)
				{
					schema.Type = numberType & ~JsonSchemaType.String;
				}
				schema.Pattern = null;
			}
			else if (plain.IsGenericType && plain.GetGenericTypeDefinition() == typeof(Optional<>))
			{
				// a patch field: its value, or null to clear it, and leaving the field out keeps the current value
				Type inner = plain.GetGenericArguments()[0];
				Type innerPlain = Nullable.GetUnderlyingType(inner) ?? inner;
				schema.Type = (innerPlain == typeof(string) ? JsonSchemaType.String : IsNumber(innerPlain) ? JsonSchemaType.Integer : innerPlain == typeof(bool) ? JsonSchemaType.Boolean : JsonSchemaType.Object) | JsonSchemaType.Null;
				schema.Format = innerPlain == typeof(int) ? "int32" : innerPlain == typeof(long) ? "int64" : null;
				schema.Description ??= "Leave the field out to keep the current value.";
			}
			else if (info.Kind == JsonTypeInfoKind.Object)
			{
				if (schema.Properties is { Count: > 0 } properties)
				{
					foreach (JsonPropertyInfo property in info.Properties)
					{
						if ((properties.TryGetValue(property.Name, out IOpenApiSchema? value) || properties.TryGetValue(WireName(property), out value)) && value is OpenApiSchema inline)
						{
							Fix(inline, generated.GetTypeInfo(property.PropertyType), generated);
						}
					}
					Rename(schema, properties, info);
				}
			}
			else if (info.Kind == JsonTypeInfoKind.Enumerable && info.ElementType != null && schema.Items is OpenApiSchema items)
			{
				Fix(items, generated.GetTypeInfo(info.ElementType), generated);
			}
			else if (info.Kind == JsonTypeInfoKind.Dictionary && info.ElementType != null && schema.AdditionalProperties is OpenApiSchema values)
			{
				Fix(values, generated.GetTypeInfo(info.ElementType), generated);
			}
		}

		private static void Rename(OpenApiSchema schema, IDictionary<string, IOpenApiSchema> properties, JsonTypeInfo info)
		{
			Dictionary<string, string> names = new(StringComparer.Ordinal);
			foreach (JsonPropertyInfo property in info.Properties)
			{
				string wire = WireName(property);
				if (wire != property.Name)
				{
					names[property.Name] = wire;
				}
			}
			if (!properties.Keys.Any(names.ContainsKey))
			{
				return;
			}

			// in place, the generator may share the dictionary
			List<KeyValuePair<string, IOpenApiSchema>> renamed = properties.Select(p => new KeyValuePair<string, IOpenApiSchema>(names.GetValueOrDefault(p.Key, p.Key), p.Value)).ToList();
			properties.Clear();
			foreach ((string name, IOpenApiSchema value) in renamed)
			{
				properties[name] = value;
			}

			if (schema.Required is { Count: > 0 } required)
			{
				schema.Required = new HashSet<string>(required.Select(name => names.GetValueOrDefault(name, name)), StringComparer.Ordinal);
			}
		}

		// The name of a property in the wire options, matched by the member behind it
		private static string WireName(JsonPropertyInfo property)
		{
			JsonTypeInfo wire = s_wire.GetTypeInfo(property.DeclaringType);
			return wire.Properties.FirstOrDefault(p => SameMember(p.AttributeProvider, property.AttributeProvider))?.Name ?? property.Name;
		}

		private static bool SameMember(ICustomAttributeProvider? a, ICustomAttributeProvider? b)
		{
			return a is MemberInfo left && b is MemberInfo right && left.Name == right.Name && left.DeclaringType == right.DeclaringType && left.MemberType == right.MemberType;
		}

		private static string EnumWireName(Type type, string name)
		{
			JsonStringEnumMemberNameAttribute? custom = type.GetField(name)?.GetCustomAttribute<JsonStringEnumMemberNameAttribute>();
			return custom?.Name ?? JsonNamingPolicy.SnakeCaseLower.ConvertName(name);
		}

		private static bool IsNumber(Type type)
		{
			return type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte) || type == typeof(sbyte)
				|| type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort)
				|| type == typeof(float) || type == typeof(double) || type == typeof(decimal);
		}
	}
}
