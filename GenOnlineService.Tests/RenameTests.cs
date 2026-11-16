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
using GenOnlineService.Identity;
using GenOnlineService.Names;
using GenOnlineService.Realtime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Xunit;

namespace GenOnlineService.Tests
{
	// Renames: the name key, reserved words, the name rules, the staff checks, ForceRename gone
	public class RenameTests
	{
		// The plugin's key: Bob, BOB and B0b are one name, so are look-alike letters from other scripts
		[Fact]
		public void NameKeyMatchesThePluginKey()
		{
			Assert.True(NameKey.TableSize > 9000);
			Assert.Equal(NameKey.Of("Bob"), NameKey.Of("BOB"));
			Assert.Equal(NameKey.Of("Bob"), NameKey.Of("B0b"));
			Assert.Equal(NameKey.Of("Bob"), NameKey.Of("B\u043Eb"));
			Assert.Equal(NameKey.Of("Ann"), NameKey.Of("\uFF21nn"));
			Assert.Equal("bob", NameKey.Of("B0b"));
			Assert.NotEqual(NameKey.Of("Bob"), NameKey.Of("Rob"));
		}

		// REST staff actions are audited as api, the realtime hub keeps realtime
		[Fact]
		public void RestStaffActionsUseTheApiSource()
		{
			Assert.Equal(("api", "user:5", 5L), (SanctionActions.ApiActorOf(5).Source, SanctionActions.ApiActorOf(5).Name, SanctionActions.ApiActorOf(5).UserID!.Value));
			Assert.Equal("realtime", SanctionActions.ActorOf(5).Source);
		}

		// The plugin's rule (NameRules.check): its default list, a case-insensitive substring match, the word as listed
		[Fact]
		public void ReservedWordsFollowThePluginRule()
		{
			// genonline-keycloak NameRules.DEFAULT_RESERVED_WORDS
			Assert.Equal(new[]
			{
				"admin", "staff", "mass^", "mas^", "m4ss^", "m4s^", "moderator",
				"hitler", "h1tler", "h1tl3r", "hittler", "h1ttler", "h1ttl3r",
				"olda", "oldanalytics", "ibra", "x64", "ronin"
			}, ProfileActions.ReservedNameWords);

			Assert.Null(ProfileActions.ReservedWordIn("AliceByMod"));
			Assert.Null(ProfileActions.ReservedWordIn("Modern"));
			Assert.Null(ProfileActions.ReservedWordIn("Model"));
			Assert.Equal("staff", ProfileActions.ReservedWordIn("AliceStaffSet"));
			Assert.Equal("admin", ProfileActions.ReservedWordIn("Admin"));
			Assert.Equal("admin", ProfileActions.ReservedWordIn("SUPERADMIN"));
			Assert.Equal("moderator", ProfileActions.ReservedWordIn("TheModerator"));
			Assert.Equal("staff", ProfileActions.ReservedWordIn("\u0130STAFF"));
		}

		private static object? Param(Failure failure, string name)
		{
			JsonElement element = JsonSerializer.SerializeToElement(failure.Params, ApiResults.Json);
			if (!element.TryGetProperty(name, out JsonElement value))
			{
				return null;
			}
			return value.ValueKind == JsonValueKind.Number ? value.GetInt32() : value.GetString();
		}

		// A name whose normalized form outgrows the 32-character column answers name.length before any write
		[Theory]
		[InlineData("\uFB03\uFB03\uFB03\uFB03\uFB03\uFB03\uFB03\uFB03\uFB03\uFB03\uFB03")]
		[InlineData("\uFDFA\uFDFA\uFDFA")]
		public void UnstorableNamesAnswerNameLength(string name)
		{
			Failure? failure = ProfileActions.CheckNameRules(name);
			Assert.NotNull(failure);
			Assert.Equal(ErrorCodes.NameLength, failure!.Value.Code);
			Assert.Equal(3, Param(failure.Value, "min"));
			Assert.Equal(16, Param(failure.Value, "max"));
			Assert.Null(ProfileActions.CheckNameRules("Bobby"));
		}

		// Staff never rename themselves, need a higher rank than the target, and keep reasons to 512 characters
		[Fact]
		public void StaffRenameFollowsTheRankRule()
		{
			Assert.Null(PlayerNames.CheckStaff(1, 1, 2, 0, null));
			Assert.Null(PlayerNames.CheckStaff(1, 2, 2, 1, "offensive name"));
			Assert.Null(PlayerNames.CheckStaff(null, StaffRoles.AdminRank, 2, 1, null));
			Assert.Equal(ErrorCodes.ModerationTargetOutranks, PlayerNames.CheckStaff(1, 1, 2, 1, null)!.Value.Code);
			Assert.Equal(ErrorCodes.ModerationTargetOutranks, PlayerNames.CheckStaff(1, 1, 2, 2, null)!.Value.Code);
			Assert.Equal(ErrorCodes.ModerationTargetOutranks, PlayerNames.CheckStaff(null, StaffRoles.AdminRank, 2, StaffRoles.AdminRank, null)!.Value.Code);
			Assert.Equal(ErrorCodes.InvalidArgument, PlayerNames.CheckStaff(1, 2, 1, 0, null)!.Value.Code);
			Assert.Equal(ErrorCodes.InvalidArgument, PlayerNames.CheckStaff(1, 2, 0, 0, null)!.Value.Code);
			Assert.Equal(ErrorCodes.ModerationReasonTooLong, PlayerNames.CheckStaff(1, 2, 2, 0, new string('r', ModerationManager.MaximumReasonLength + 1))!.Value.Code);
		}

		private sealed class DenyAll : IAuthorizationService
		{
			public List<string> Policies { get; } = new();

			public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements) => Task.FromResult(AuthorizationResult.Failed());

			public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName)
			{
				Policies.Add(policyName);
				return Task.FromResult(AuthorizationResult.Failed());
			}
		}

		[Fact]
		public async Task StaffRenameNeedsModerationRename()
		{
			DenyAll authorization = new();
			ModerationController controller = new(null!, authorization, null!)
			{
				ControllerContext = new ControllerContext
				{
					HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "7") }, "test")) }
				}
			};

			IActionResult answer = await controller.RenamePlayer(8);

			JsonResult json = Assert.IsType<JsonResult>(answer);
			Assert.Equal(403, json.StatusCode);
			ApiError error = Assert.IsType<ApiError>(json.Value);
			Assert.Equal(ErrorCodes.Forbidden, error.Error);
			Assert.Equal(new[] { PermissionPolicies.For(Permissions.ModerationRename) }, authorization.Policies);
		}

		[Fact]
		public void ForceRenameIsGone()
		{
			Assert.Null(typeof(RealtimeHub).GetMethod("ForceRename"));
			Assert.DoesNotContain(typeof(RealtimeHub).Assembly.GetTypes(), t => t.Name == "ForceRenameRequest");
			Assert.DoesNotContain(typeof(IRealtimeClient).GetMethods(), m => m.Name.Contains("Rename", StringComparison.Ordinal));

			// the docs in the repository, a stale copy in the build output does not count
			DirectoryInfo? dir = new(AppContext.BaseDirectory);
			while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "docs", "realtime-fixtures")))
			{
				dir = dir.Parent;
			}
			Assert.NotNull(dir);
			Assert.False(File.Exists(Path.Combine(dir!.FullName, "docs", "realtime-fixtures", "ForceRename.request.json")));
		}
	}
}
