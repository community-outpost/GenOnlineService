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

using GenOnlineService.Api;
using GenOnlineService.Realtime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace GenOnlineService.Tests
{
	// Serializes the REST request and response types and compares them with docs/api-fixtures
	public class ApiFixtureTests
	{
		private static readonly JsonSerializerOptions s_options = ApiResults.Json;
		private static readonly string s_fixtureDir = Path.Combine(AppContext.BaseDirectory, "api-fixtures");

		public static IEnumerable<object[]> Fixtures()
		{
			return Directory.GetFiles(s_fixtureDir, "*.json").Select(path => new object[] { Path.GetFileName(path) }).OrderBy(name => (string)name[0]);
		}

		[Fact]
		public void FixturesArePresent()
		{
			Assert.True(Fixtures().Count() >= 21, $"expected the 21 fixtures in {s_fixtureDir}");
		}

		[Theory]
		[MemberData(nameof(Fixtures))]
		public void FixtureRoundTripsThroughItsType(string fileName)
		{
			string name = Path.GetFileNameWithoutExtension(fileName);
			Type[] types = TypesByFixture().Where(t => t.Fixture == name).Select(t => t.Type).ToArray();
			Assert.True(types.Length == 1, $"{fileName} needs exactly one C# type with [ApiFixture(\"{name}\")], found {types.Length}");

			Type type = types[0];
			JsonNode expected = JsonNode.Parse(File.ReadAllText(Path.Combine(s_fixtureDir, fileName)))!;
			object? value = JsonSerializer.Deserialize(expected.ToJsonString(), type, s_options);
			JsonNode actual = JsonNode.Parse(JsonSerializer.Serialize(value, type, s_options))!;

			Assert.True(JsonNode.DeepEquals(expected, actual), $"{fileName} differs from {type.Name}\nexpected: {expected.ToJsonString()}\nactual:   {actual.ToJsonString()}");
		}

		[Fact]
		public void EveryFixtureNameHasAFile()
		{
			string[] missing = TypesByFixture().Select(t => t.Fixture + ".json").Where(file => !File.Exists(Path.Combine(s_fixtureDir, file))).ToArray();
			Assert.Empty(missing);
		}

		[Theory]
		[InlineData(ErrorCodes.InvalidArgument, 400)]
		[InlineData(ErrorCodes.Forbidden, 403)]
		[InlineData(ErrorCodes.ModerationTargetOutranks, 403)]
		[InlineData(ErrorCodes.NotFound, 404)]
		[InlineData(ErrorCodes.ReportDuplicate, 409)]
		[InlineData(ErrorCodes.RateLimited, 429)]
		[InlineData(ErrorCodes.GroupNotOwner, 422)]
		[InlineData(ErrorCodes.ModerationTargetOffline, 422)]
		public void ErrorCodesMapToTheirStatus(string code, int status)
		{
			Assert.Equal(status, ApiResults.StatusOf(code));
		}

		[Fact]
		public void ErrorBodyCarriesCodeAndParams()
		{
			Assert.Equal("{\"error\":\"invalid_argument\",\"params\":{\"field\":\"user_ids\"}}", JsonSerializer.Serialize(ApiResults.BodyOf(Result.Invalid("user_ids")), s_options));
			Assert.Equal("{\"error\":\"session.not_registered\",\"params\":{}}", JsonSerializer.Serialize(ApiResults.BodyOf(Result.Fail(ErrorCodes.SessionNotRegistered)), s_options));
		}

		[Fact]
		public async Task FailedRoleCheckOnARestEndpointAnswersTheForbiddenBodyWithoutParams()
		{
			DefaultHttpContext context = new DefaultHttpContext();
			context.Response.Body = new MemoryStream();
			ControllerActionDescriptor action = new ControllerActionDescriptor { ControllerTypeInfo = typeof(ProfileController).GetTypeInfo() };
			context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(action), "profile"));

			AuthorizationPolicy policy = new AuthorizationPolicyBuilder().RequireRole("GameClient").Build();
			await new ApiAuthorizationResultHandler().HandleAsync(_ => Task.CompletedTask, context, policy, PolicyAuthorizationResult.Forbid());

			Assert.Equal(403, context.Response.StatusCode);
			context.Response.Body.Position = 0;
			Assert.Equal("{\"error\":\"forbidden\",\"params\":{}}", await new StreamReader(context.Response.Body).ReadToEndAsync());
		}

		[Fact]
		public void FailedPermissionPolicyNamesThePermission()
		{
			AuthorizationFailure failure = AuthorizationFailure.Failed(new IAuthorizationRequirement[] { new PermissionRequirement("moderation.kick") });
			Assert.Equal("{\"error\":\"forbidden\",\"params\":{\"permission\":\"moderation.kick\"}}",
				JsonSerializer.Serialize(ApiResults.BodyOf(ApiAuthorizationResultHandler.ForbiddenOf(PolicyAuthorizationResult.Forbid(failure))), s_options));
		}

		[Fact]
		public void GroupRenameKeepsOmittedApartFromNull()
		{
			Assert.False(JsonSerializer.Deserialize<RenameGroupRequest>("{}", s_options)!.Name.HasValue);
			RenameGroupRequest cleared = JsonSerializer.Deserialize<RenameGroupRequest>("{\"name\":null}", s_options)!;
			Assert.True(cleared.Name.HasValue);
			Assert.Null(cleared.Name.Value);
		}

		private static IEnumerable<(string Fixture, Type Type)> TypesByFixture()
		{
			return typeof(ApiError).Assembly.GetTypes().SelectMany(type => type.GetCustomAttributes<ApiFixtureAttribute>().Select(attribute => (attribute.Name, type)));
		}
	}
}
