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
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GenOnlineService.Flags
{
	// Names of the evaluation context; every value comes from the session, never from what the client sends
	public static class FlagContext
	{
		public const string TargetingKey = "targetingKey";
		public const string Roles = "roles";
		public const string Client = "client";
		public const string ClientVersion = "client_version";
		public const string Country = "country";
		public const string Environment = "environment";

		public static JsonObject Create(Int64 userID, IEnumerable<string> roles, string? client, string? clientVersion, string? country, string? environment)
		{
			return new JsonObject
			{
				[TargetingKey] = userID.ToString(CultureInfo.InvariantCulture),
				[Roles] = new JsonArray(roles.OrderBy(r => r, StringComparer.Ordinal).Select(r => (JsonNode)r).ToArray()),
				[Client] = client,
				[ClientVersion] = clientVersion,
				[Country] = country,
				[Environment] = environment
			};
		}
	}

	public static class FlagReasons
	{
		public const string Static = "STATIC";
		public const string TargetingMatch = "TARGETING_MATCH";
		public const string Default = "DEFAULT";
		public const string Split = "SPLIT";
		public const string Error = "ERROR";
	}

	public readonly record struct FlagResult(JsonNode? Value, string Variant, string Reason);

	// Evaluates a flagd flag: the JSON Logic operators flagd documents, plus fractional, starts_with, ends_with and sem_ver
	public static class FlagEvaluator
	{
		public static FlagResult Evaluate(string flagKey, JsonObject flag, JsonObject context)
		{
			JsonObject variants = (JsonObject)flag["variants"]!;
			string defaultVariant = flag["defaultVariant"]!.GetValue<string>();
			JsonNode? targeting = flag["targeting"];
			if (targeting == null || (targeting is JsonObject empty && empty.Count == 0))
			{
				return new FlagResult(variants[defaultVariant]?.DeepClone(), defaultVariant, FlagReasons.Static);
			}

			var data = (JsonObject)context.DeepClone();
			data["$flagd"] = new JsonObject
			{
				["flagKey"] = flagKey,
				["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
			};

			JsonNode? chosen;
			try
			{
				chosen = new Logic(flagKey, data).Apply(targeting);
			}
			catch (Exception ex) when (ex is InvalidOperationException or FormatException or JsonException or ArgumentException)
			{
				return new FlagResult(variants[defaultVariant]?.DeepClone(), defaultVariant, FlagReasons.Error);
			}

			// null or an unknown name falls back to the default variant, as flagd does
			string? variant = chosen?.GetValueKind() == JsonValueKind.String ? chosen.GetValue<string>() : chosen?.GetValueKind() == JsonValueKind.True ? "true" : chosen?.GetValueKind() == JsonValueKind.False ? "false" : null;
			if (variant == null || !variants.ContainsKey(variant))
			{
				return new FlagResult(variants[defaultVariant]?.DeepClone(), defaultVariant, FlagReasons.Default);
			}
			bool split = targeting is JsonObject root && root.ContainsKey("fractional");
			return new FlagResult(variants[variant]?.DeepClone(), variant, split ? FlagReasons.Split : FlagReasons.TargetingMatch);
		}

		private sealed class Logic
		{
			private readonly string m_flagKey;
			private readonly JsonObject m_data;

			public Logic(string flagKey, JsonObject data)
			{
				m_flagKey = flagKey;
				m_data = data;
			}

			public JsonNode? Apply(JsonNode? rule)
			{
				if (rule is JsonArray list)
				{
					return new JsonArray(list.Select(Apply).ToArray());
				}
				if (rule is not JsonObject operation || operation.Count != 1)
				{
					return rule?.DeepClone();
				}

				(string op, JsonNode? raw) = operation.First();
				JsonArray args = raw as JsonArray ?? new JsonArray { raw?.DeepClone() };

				switch (op)
				{
					case "var":
						return Var(args);
					case "if":
					case "?:":
						for (int i = 0; i + 1 < args.Count; i += 2)
						{
							if (Truthy(Apply(args[i])))
							{
								return Apply(args[i + 1]);
							}
						}
						return args.Count % 2 == 1 ? Apply(args[^1]) : null;
					case "and":
					{
						JsonNode? last = null;
						foreach (JsonNode? arg in args)
						{
							last = Apply(arg);
							if (!Truthy(last)) return last;
						}
						return last;
					}
					case "or":
					{
						JsonNode? last = null;
						foreach (JsonNode? arg in args)
						{
							last = Apply(arg);
							if (Truthy(last)) return last;
						}
						return last;
					}
					case "!":
						return !Truthy(Apply(args.Count > 0 ? args[0] : null));
					case "!!":
						return Truthy(Apply(args.Count > 0 ? args[0] : null));
					case "==":
						return LooseEquals(Apply(args[0]), Apply(args[1]));
					case "!=":
						return !LooseEquals(Apply(args[0]), Apply(args[1]));
					case "===":
						return JsonNode.DeepEquals(Apply(args[0]), Apply(args[1]));
					case "!==":
						return !JsonNode.DeepEquals(Apply(args[0]), Apply(args[1]));
					case "<":
					case "<=":
					case ">":
					case ">=":
						return Compare(op, args);
					case "in":
					{
						JsonNode? needle = Apply(args[0]);
						JsonNode? haystack = Apply(args[1]);
						if (haystack is JsonArray items) return items.Any(item => JsonNode.DeepEquals(item, needle));
						if (haystack?.GetValueKind() == JsonValueKind.String) return haystack.GetValue<string>().Contains(TextOf(needle), StringComparison.Ordinal);
						return false;
					}
					case "cat":
						return String.Concat(args.Select(a => TextOf(Apply(a))));
					case "starts_with":
						return TextOf(Apply(args[0])).StartsWith(TextOf(Apply(args[1])), StringComparison.Ordinal);
					case "ends_with":
						return TextOf(Apply(args[0])).EndsWith(TextOf(Apply(args[1])), StringComparison.Ordinal);
					case "sem_ver":
						return SemVer(TextOf(Apply(args[0])), TextOf(Apply(args[1])), TextOf(Apply(args[2])));
					case "fractional":
						return Fractional(args);
					default:
						throw new InvalidOperationException($"Unsupported operator {op}");
				}
			}

			private JsonNode? Var(JsonArray args)
			{
				JsonNode? path = args.Count > 0 ? Apply(args[0]) : null;
				JsonNode? fallback = args.Count > 1 ? Apply(args[1]) : null;
				string name = TextOf(path);
				if (name.Length == 0)
				{
					return m_data.DeepClone();
				}

				JsonNode? current = m_data;
				foreach (string part in name.Split('.'))
				{
					current = current switch
					{
						JsonObject obj => obj[part],
						JsonArray array when int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index < array.Count => array[index],
						_ => null
					};
					if (current == null)
					{
						return fallback;
					}
				}
				return current.DeepClone();
			}

			// flagd: the first argument is the bucketing value when it is not a [variant, weight] pair, else flagKey + targetingKey
			private JsonNode? Fractional(JsonArray args)
			{
				int start = 0;
				string bucketBy;
				JsonNode? first = args.Count > 0 ? args[0] : null;
				if (first is JsonArray)
				{
					string? targetingKey = m_data[FlagContext.TargetingKey]?.GetValueKind() == JsonValueKind.String ? m_data[FlagContext.TargetingKey]!.GetValue<string>() : null;
					if (targetingKey == null)
					{
						return null;
					}
					bucketBy = m_flagKey + targetingKey;
				}
				else
				{
					bucketBy = TextOf(Apply(first));
					start = 1;
				}

				var buckets = new List<(string Variant, double Weight)>();
				for (int i = start; i < args.Count; i++)
				{
					if (args[i] is not JsonArray pair || pair.Count < 1)
					{
						return null;
					}
					string variant = TextOf(Apply(pair[0]));
					double weight = pair.Count > 1 ? NumberOf(Apply(pair[1])) : 1;
					buckets.Add((variant, weight));
				}
				double total = buckets.Sum(b => b.Weight);
				if (buckets.Count == 0 || total <= 0)
				{
					return null;
				}

				double bucket = BucketOf(bucketBy);
				double sum = 0;
				foreach ((string variant, double weight) in buckets)
				{
					sum += weight * 100 / total;
					if (bucket < sum)
					{
						return variant;
					}
				}
				return buckets[^1].Variant;
			}

			private JsonNode? Compare(string op, JsonArray args)
			{
				// between: {"<": [a, b, c]} is a < b < c
				double[] values = args.Select(a => NumberOf(Apply(a))).ToArray();
				for (int i = 0; i + 1 < values.Length; i++)
				{
					bool ok = op switch
					{
						"<" => values[i] < values[i + 1],
						"<=" => values[i] <= values[i + 1],
						">" => values[i] > values[i + 1],
						_ => values[i] >= values[i + 1]
					};
					if (!ok) return false;
				}
				return values.Length >= 2;
			}
		}

		// 0 to 100 (exclusive) for a bucketing value; murmur3 32-bit seed 0, as flagd
		public static double BucketOf(string bucketBy)
		{
			int hash = unchecked((int)Murmur3(Encoding.UTF8.GetBytes(bucketBy)));
			double ratio = Math.Abs((double)hash) / int.MaxValue;
			return ratio * 100;
		}

		public static uint Murmur3(byte[] data, uint seed = 0)
		{
			const uint c1 = 0xcc9e2d51;
			const uint c2 = 0x1b873593;
			uint h = seed;
			int blocks = data.Length / 4;
			for (int i = 0; i < blocks; i++)
			{
				uint k = BitConverter.ToUInt32(data, i * 4);
				if (!BitConverter.IsLittleEndian)
				{
					k = System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(k);
				}
				k *= c1;
				k = (k << 15) | (k >> 17);
				k *= c2;
				h ^= k;
				h = (h << 13) | (h >> 19);
				h = h * 5 + 0xe6546b64;
			}

			uint tail = 0;
			int remainder = data.Length & 3;
			int offset = blocks * 4;
			if (remainder == 3) tail ^= (uint)data[offset + 2] << 16;
			if (remainder >= 2) tail ^= (uint)data[offset + 1] << 8;
			if (remainder >= 1)
			{
				tail ^= data[offset];
				tail *= c1;
				tail = (tail << 15) | (tail >> 17);
				tail *= c2;
				h ^= tail;
			}

			h ^= (uint)data.Length;
			h ^= h >> 16;
			h *= 0x85ebca6b;
			h ^= h >> 13;
			h *= 0xc2b2ae35;
			h ^= h >> 16;
			return h;
		}

		private static bool Truthy(JsonNode? value)
		{
			if (value == null) return false;
			return value.GetValueKind() switch
			{
				JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined => false,
				JsonValueKind.True => true,
				JsonValueKind.Number => NumberValue(value) != 0,
				JsonValueKind.String => value.GetValue<string>().Length > 0,
				JsonValueKind.Array => ((JsonArray)value).Count > 0,
				_ => true
			};
		}

		private static bool LooseEquals(JsonNode? a, JsonNode? b)
		{
			if (a == null || b == null) return a == null && b == null;
			JsonValueKind ka = a.GetValueKind();
			JsonValueKind kb = b.GetValueKind();
			if (ka == JsonValueKind.Number || kb == JsonValueKind.Number)
			{
				return TryNumber(a, out double x) && TryNumber(b, out double y) && x == y;
			}
			return JsonNode.DeepEquals(a, b);
		}

		private static bool TryNumber(JsonNode? node, out double value)
		{
			value = 0;
			switch (node?.GetValueKind())
			{
				case JsonValueKind.Number:
					value = NumberValue(node);
					return true;
				case JsonValueKind.String:
					return Double.TryParse(node.GetValue<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
				case JsonValueKind.True:
					value = 1;
					return true;
				case JsonValueKind.False:
					return true;
				default:
					return false;
			}
		}

		// A JsonValue made from an int refuses GetValue<double>, so numbers are read from their JSON text
		private static double NumberValue(JsonNode node) => System.Double.Parse(node.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);

		private static double NumberOf(JsonNode? node) => TryNumber(node, out double value) ? value : Double.NaN;

		private static string TextOf(JsonNode? node) => node?.GetValueKind() switch
		{
			JsonValueKind.String => node.GetValue<string>(),
			JsonValueKind.Number => node.ToJsonString(),
			JsonValueKind.True => "true",
			JsonValueKind.False => "false",
			null or JsonValueKind.Null => String.Empty,
			_ => node.ToJsonString()
		};

		// {"sem_ver": [version, op, version]}: = != < <= > >= ^ (same major) ~ (same major and minor); a leading v is allowed
		private static bool SemVer(string left, string op, string right)
		{
			if (!TryVersion(left, out int[] a) || !TryVersion(right, out int[] b))
			{
				return false;
			}
			int order = 0;
			for (int i = 0; i < 3 && order == 0; i++)
			{
				order = a[i].CompareTo(b[i]);
			}
			return op switch
			{
				"=" => order == 0,
				"!=" => order != 0,
				"<" => order < 0,
				"<=" => order <= 0,
				">" => order > 0,
				">=" => order >= 0,
				"^" => a[0] == b[0],
				"~" => a[0] == b[0] && a[1] == b[1],
				_ => false
			};
		}

		private static bool TryVersion(string text, out int[] parts)
		{
			parts = new int[3];
			string core = text.TrimStart('v', 'V').Split('-', '+')[0];
			string[] pieces = core.Split('.');
			if (pieces.Length == 0 || pieces.Length > 3)
			{
				return false;
			}
			for (int i = 0; i < pieces.Length; i++)
			{
				if (!int.TryParse(pieces[i], NumberStyles.None, CultureInfo.InvariantCulture, out parts[i]))
				{
					return false;
				}
			}
			return true;
		}
	}
}
