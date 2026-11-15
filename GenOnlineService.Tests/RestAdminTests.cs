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
using GenOnlineService.Rest;
using Microsoft.AspNetCore.Http;
using System.Text.Json.Nodes;
using Xunit;

namespace GenOnlineService.Tests
{
	// Validation of the operator routes: roles, application settings, announcements, audit filters and the admin permissions
	public class RestAdminTests
	{
		private static string FieldOf(Failure? failure)
		{
			Assert.NotNull(failure);
			Assert.Equal(ErrorCodes.InvalidArgument, failure!.Value.Code);
			return (string)failure.Value.Params.GetType().GetProperty("Field")!.GetValue(failure.Value.Params)!;
		}

		[Fact]
		public void AdminHoldsAuditReadAndSettingsManage()
		{
			HashSet<string> admin = StaffRoles.PermissionsFor([StaffRoles.Admin]);
			Assert.Contains(Permissions.AuditRead, admin);
			Assert.Contains(Permissions.SettingsManage, admin);
			Assert.Equal("audit.read", Permissions.AuditRead);
			Assert.Equal("settings.manage", Permissions.SettingsManage);
			Assert.Contains(Permissions.AuditRead, Permissions.Human);
			Assert.Contains(Permissions.SettingsManage, Permissions.Human);
		}

		[Fact]
		public void ModeratorHoldsNeitherAuditReadNorSettingsManage()
		{
			HashSet<string> moderator = StaffRoles.PermissionsFor([StaffRoles.Moderator]);
			Assert.DoesNotContain(Permissions.AuditRead, moderator);
			Assert.DoesNotContain(Permissions.SettingsManage, moderator);
		}

		[Fact]
		public void RoleListIsLowercasedDeduplicatedAndSorted()
		{
			(IReadOnlyList<string>? roles, Failure? failure) = RoleRules.Normalize(["Moderator", "admin", "moderator"]);
			Assert.Null(failure);
			Assert.Equal(["admin", "moderator"], roles);
		}

		[Fact]
		public void EmptyRoleListIsAccepted()
		{
			(IReadOnlyList<string>? roles, Failure? failure) = RoleRules.Normalize([]);
			Assert.Null(failure);
			Assert.Empty(roles!);
		}

		[Theory]
		[InlineData("owner")]
		[InlineData("")]
		[InlineData(null)]
		public void UnknownRoleNamesTheRolesField(string? role)
		{
			(_, Failure? failure) = RoleRules.Normalize(["admin", role]);
			Assert.Equal("roles", FieldOf(failure));
		}

		[Fact]
		public void MissingRoleListNamesTheRolesField()
		{
			(_, Failure? failure) = RoleRules.Normalize(null);
			Assert.Equal("roles", FieldOf(failure));
		}

		[Fact]
		public void SettingsSetAndClearTheMotd()
		{
			(IReadOnlyDictionary<string, string?>? set, Failure? setFailure) = ApplicationSettingsRules.Parse(JsonNode.Parse("{\"motd\":\"Welcome back\"}"));
			Assert.Null(setFailure);
			Assert.Equal("Welcome back", set![SettingKeys.Motd]);

			(IReadOnlyDictionary<string, string?>? cleared, Failure? clearFailure) = ApplicationSettingsRules.Parse(JsonNode.Parse("{\"motd\":null}"));
			Assert.Null(clearFailure);
			Assert.True(cleared!.ContainsKey(SettingKeys.Motd));
			Assert.Null(cleared[SettingKeys.Motd]);
		}

		[Fact]
		public void OmittedSettingsStayUnchanged()
		{
			(IReadOnlyDictionary<string, string?>? changes, Failure? failure) = ApplicationSettingsRules.Parse(JsonNode.Parse("{}"));
			Assert.Null(failure);
			Assert.Empty(changes!);
		}

		[Theory]
		[InlineData("{\"sync_cursor\":\"x\"}", "sync_cursor")]
		[InlineData("{\"motd\":\"hi\",\"token_lifetime\":3}", "token_lifetime")]
		[InlineData("{\"motd\":5}", "motd")]
		[InlineData("{\"motd\":{\"text\":\"hi\"}}", "motd")]
		[InlineData("[]", "body")]
		[InlineData(null, "body")]
		public void SettingsOutsideTheAllowListOrOfTheWrongTypeNameTheirField(string? body, string field)
		{
			(_, Failure? failure) = ApplicationSettingsRules.Parse(body == null ? null : JsonNode.Parse(body));
			Assert.Equal(field, FieldOf(failure));
		}

		[Fact]
		public void MotdIsAtMost2000Characters()
		{
			Assert.Null(ApplicationSettingsRules.Parse(new JsonObject { ["motd"] = new string('a', 2000) }).Failure);
			Assert.Equal("motd", FieldOf(ApplicationSettingsRules.Parse(new JsonObject { ["motd"] = new string('a', 2001) }).Failure));
		}

		[Fact]
		public void AnnouncementIsTrimmed()
		{
			(string? message, Failure? failure) = Announcements.Validate("  Restart in 5 minutes  ");
			Assert.Null(failure);
			Assert.Equal("Restart in 5 minutes", message);
		}

		[Theory]
		[InlineData(null)]
		[InlineData("")]
		[InlineData("   ")]
		public void BlankAnnouncementNamesTheMessageField(string? message)
		{
			Assert.Equal("message", FieldOf(Announcements.Validate(message).Failure));
		}

		[Fact]
		public void AnnouncementIsAtMost512Characters()
		{
			Assert.Null(Announcements.Validate(" " + new string('a', 512) + " ").Failure);
			Assert.Equal("message", FieldOf(Announcements.Validate(new string('a', 513)).Failure));
		}

		[Theory]
		[InlineData("?actor_id=x", "actor_id")]
		[InlineData("?target_id=-1", "target_id")]
		[InlineData("?after_id=0", "after_id")]
		[InlineData("?per_page=101", "per_page")]
		[InlineData("?per_page=0", "per_page")]
		public void BadAuditFiltersNameTheirField(string query, string field)
		{
			DefaultHttpContext context = new DefaultHttpContext();
			context.Request.QueryString = new QueryString(query);
			Assert.Equal(field, FieldOf(AuditEventsQuery.Read(context.Request).Failure));
		}

		[Fact]
		public void AuditFiltersAreRead()
		{
			DefaultHttpContext context = new DefaultHttpContext();
			context.Request.QueryString = new QueryString("?actor_id=7&target_id=42&action=role_grant&after_id=900&per_page=50");
			(AuditEventsQuery? query, Failure? failure) = AuditEventsQuery.Read(context.Request);
			Assert.Null(failure);
			Assert.Equal(new AuditEventsQuery(7, 42, "role_grant", 900, 50), query);

			(AuditEventsQuery? defaults, _) = AuditEventsQuery.Read(new DefaultHttpContext().Request);
			Assert.Equal(new AuditEventsQuery(null, null, null, null, 20), defaults);
		}
	}
}
