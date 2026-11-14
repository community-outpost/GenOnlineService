using GenOnlineService.Realtime;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace GenOnlineService.Tests
{
	// Serializes the hub's own message types and compares them with docs/realtime-fixtures
	public class RealtimeFixtureTests
	{
		private static readonly JsonSerializerOptions s_options = RealtimeJson.Create();
		private static readonly string s_fixtureDir = Path.Combine(AppContext.BaseDirectory, "realtime-fixtures");

		public static IEnumerable<object[]> Fixtures()
		{
			return Directory.GetFiles(s_fixtureDir, "*.json").Select(path => new object[] { Path.GetFileName(path) }).OrderBy(name => (string)name[0]);
		}

		[Fact]
		public void FixturesArePresent()
		{
			Assert.True(Fixtures().Count() >= 88, $"expected the 88 fixtures in {s_fixtureDir}");
		}

		[Theory]
		[MemberData(nameof(Fixtures))]
		public void FixtureRoundTripsThroughItsType(string fileName)
		{
			Type? type = ResolveType(fileName);
			Assert.True(type != null, $"{fileName} has no C# message type");

			JsonNode expected = JsonNode.Parse(File.ReadAllText(Path.Combine(s_fixtureDir, fileName)))!;
			object? value = JsonSerializer.Deserialize(expected.ToJsonString(), type!, s_options);
			JsonNode actual = JsonNode.Parse(JsonSerializer.Serialize(value, type!, s_options))!;

			Assert.True(JsonNode.DeepEquals(expected, actual), $"{fileName} differs from {type!.Name}\nexpected: {expected.ToJsonString()}\nactual:   {actual.ToJsonString()}");
		}

		[Fact]
		public void EveryHubMethodHasARequestFixture()
		{
			string[] missing = HubMethods().Select(m => m.Name + ".request.json").Where(name => !File.Exists(Path.Combine(s_fixtureDir, name))).ToArray();
			Assert.Empty(missing);
		}

		[Fact]
		public void EveryEventHasAFixture()
		{
			string[] missing = typeof(IRealtimeClient).GetMethods().Select(m => m.Name + ".event.json").Where(name => !File.Exists(Path.Combine(s_fixtureDir, name))).ToArray();
			Assert.Empty(missing);
		}

		[Fact]
		public void EveryHubMethodTakesOneObjectArgument()
		{
			foreach (MethodInfo method in HubMethods())
			{
				ParameterInfo[] parameters = method.GetParameters();
				Assert.True(parameters.Length == 1 && parameters[0].ParameterType.IsClass && parameters[0].ParameterType != typeof(string), $"{method.Name} must take exactly one object argument");
			}
		}

		[Fact]
		public void PatchFieldsKeepOmittedApartFromNull()
		{
			UpdateMySlotRequest omitted = JsonSerializer.Deserialize<UpdateMySlotRequest>("{\"side\":2}", s_options)!;
			UpdateMySlotRequest cleared = JsonSerializer.Deserialize<UpdateMySlotRequest>("{\"team\":null}", s_options)!;
			UpdateMySlotRequest set = JsonSerializer.Deserialize<UpdateMySlotRequest>("{\"team\":3}", s_options)!;

			Assert.False(omitted.Team.HasValue);
			Assert.True(cleared.Team.HasValue);
			Assert.Null(cleared.Team.Value);
			Assert.Equal(3, set.Team.Value);
			Assert.Equal("{\"team\":null}", JsonSerializer.Serialize(cleared, s_options));
		}

		[Fact]
		public void FailedResultCarriesCodeAndParams()
		{
			Result<Empty> result = Result.Fail(ErrorCodes.ChatRateLimited, new { RetryAfterMs = 4200 });
			Assert.Equal("{\"ok\":false,\"code\":\"chat.rate_limited\",\"params\":{\"retry_after_ms\":4200}}", JsonSerializer.Serialize(result, s_options));
		}

		private static IEnumerable<MethodInfo> HubMethods()
		{
			return typeof(RealtimeHub).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(m => !m.IsSpecialName && m.GetBaseDefinition().DeclaringType == typeof(RealtimeHub));
		}

		private static Type? ResolveType(string fileName)
		{
			string[] parts = fileName.Split('.');
			if (parts.Length != 3)
			{
				return null;
			}

			string name = parts[0];
			string kind = parts[1];

			if (name == "Result")
			{
				return typeof(Result<Empty>);
			}

			if (kind == "event")
			{
				return typeof(IRealtimeClient).GetMethod(name)?.GetParameters().SingleOrDefault()?.ParameterType;
			}

			MethodInfo? method = HubMethods().SingleOrDefault(m => m.Name == name);
			if (method == null)
			{
				return null;
			}

			return kind switch
			{
				"request" => method.GetParameters().SingleOrDefault()?.ParameterType,
				"result" or "error" => ResultType(method.ReturnType),
				_ => null,
			};
		}

		private static Type? ResultType(Type returnType)
		{
			if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
			{
				Type inner = returnType.GetGenericArguments()[0];
				if (inner.IsGenericType && inner.GetGenericTypeDefinition() == typeof(Result<>))
				{
					return inner;
				}
			}
			return null;
		}
	}
}
