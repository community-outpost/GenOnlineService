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


using Microsoft.Extensions.Configuration;
using Xunit;

namespace GenOnlineService.Tests
{
	// Client features come from the user's roles through the Features:roles map, streamer switches them on without being staff
	public class ClientFeaturesTests
	{
		private static readonly IReadOnlyDictionary<string, string[]> s_map = new Dictionary<string, string[]>(StringComparer.Ordinal)
		{
			[StaffRoles.Streamer] = new[] { ClientFeatures.StatsExporter, ClientFeatures.Observer },
			[StaffRoles.Moderator] = new[] { ClientFeatures.Observer, ClientFeatures.StatsExporter },
			[StaffRoles.Admin] = new[] { "zeta_tool", ClientFeatures.Observer }
		};

		private static IConfiguration Config(params (string Key, string? Value)[] values)
		{
			return new ConfigurationBuilder()
				.AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
				.Build();
		}

		[Fact]
		public void NoRolesGiveNoFeatures()
		{
			Assert.Empty(ClientFeatures.For(s_map, Array.Empty<string>()));
		}

		[Fact]
		public void SeveralRolesMergeIntoOneSortedDistinctList()
		{
			Assert.Equal(new[] { "observer", "stats_exporter", "zeta_tool" }, ClientFeatures.For(s_map, new[] { StaffRoles.Admin, StaffRoles.Streamer, StaffRoles.Moderator }));
			Assert.Equal(new[] { "observer", "stats_exporter" }, ClientFeatures.For(s_map, new[] { StaffRoles.Streamer }));
		}

		[Fact]
		public void ARoleOutsideTheMapAddsNothing()
		{
			Dictionary<string, string[]> map = new(StringComparer.Ordinal) { [StaffRoles.Streamer] = new[] { ClientFeatures.Observer } };
			Assert.Equal(new[] { "observer" }, ClientFeatures.For(map, new[] { StaffRoles.Admin, StaffRoles.Streamer, "unknown" }));
		}

		[Fact]
		public void ConfigListsAreReadPerRole()
		{
			List<string> errors = new(), warnings = new();
			IReadOnlyDictionary<string, string[]> map = ClientFeatures.Parse(Config(
				("Features:roles:streamer:0", "stats_exporter"),
				("Features:roles:streamer:1", "observer"),
				("Features:roles:streamer:2", "observer"),
				("Features:roles:admin", "observer")), errors, warnings);

			Assert.Empty(errors);
			Assert.Empty(warnings);
			Assert.Equal(new[] { "stats_exporter", "observer" }, map[StaffRoles.Streamer]);
			Assert.Equal(new[] { "observer" }, map[StaffRoles.Admin]);
		}

		[Theory]
		[InlineData("Observer")]
		[InlineData("stats-exporter")]
		[InlineData("_observer")]
		[InlineData("observer_")]
		[InlineData("stats__exporter")]
		[InlineData("1observer")]
		[InlineData("gx:observer")]
		[InlineData("")]
		public void ABadFeatureNameIsAnError(string name)
		{
			List<string> errors = new(), warnings = new();
			ClientFeatures.Parse(Config(("Features:roles:streamer:0", "observer"), ("Features:roles:streamer:1", name)), errors, warnings);

			Assert.Single(errors);
			Assert.Contains("Features:roles:streamer", errors[0]);
			Assert.False(ClientFeatures.IsValidName(name));
		}

		[Theory]
		[InlineData("observer")]
		[InlineData("stats_exporter")]
		[InlineData("tool2_v3")]
		public void SnakeCaseNamesAreValid(string name)
		{
			Assert.True(ClientFeatures.IsValidName(name));
		}

		[Fact]
		public void AnUnknownRoleIsAWarningAndIgnored()
		{
			List<string> errors = new(), warnings = new();
			IReadOnlyDictionary<string, string[]> map = ClientFeatures.Parse(Config(("Features:roles:caster:0", "observer"), ("Features:roles:streamer:0", "observer")), errors, warnings);

			Assert.Empty(errors);
			Assert.Single(warnings);
			Assert.Contains("caster", warnings[0]);
			Assert.False(map.ContainsKey("caster"));
			Assert.True(map.ContainsKey(StaffRoles.Streamer));
		}

		[Fact]
		public void NoSectionGivesTheDefaults()
		{
			List<string> errors = new(), warnings = new();
			IReadOnlyDictionary<string, string[]> map = ClientFeatures.Parse(Config(), errors, warnings);
			Assert.Same(ClientFeatures.Defaults, map);
			Assert.Equal(new[] { "observer", "stats_exporter" }, ClientFeatures.For(map, new[] { StaffRoles.Streamer }));
			Assert.Empty(errors);
		}

		[Fact]
		public void ASectionReplacesTheDefaultsEntirely()
		{
			List<string> errors = new(), warnings = new();
			IReadOnlyDictionary<string, string[]> map = ClientFeatures.Parse(Config(("Features:roles:streamer:0", "observer")), errors, warnings);
			Assert.Empty(ClientFeatures.For(map, new[] { StaffRoles.Admin }));
			Assert.Equal(new[] { "observer" }, ClientFeatures.For(map, new[] { StaffRoles.Streamer }));
		}

		[Fact]
		public void AnUnknownFeatureIsAnError()
		{
			List<string> errors = new(), warnings = new();
			IReadOnlyDictionary<string, string[]> map = ClientFeatures.Parse(Config(("Features:roles:streamer:0", "observr")), errors, warnings);
			Assert.Single(errors);
			Assert.Contains("observr", errors[0]);
			Assert.Empty(map[StaffRoles.Streamer]);
		}

		[Fact]
		public void OnlyUsersWhoseListChangesAreReported()
		{
			Dictionary<Int64, HashSet<string>> before = new()
			{
				[1] = new HashSet<string> { StaffRoles.Moderator },
				[2] = new HashSet<string> { StaffRoles.Streamer },
				[3] = new HashSet<string> { StaffRoles.Admin }
			};
			Dictionary<Int64, HashSet<string>> after = new()
			{
				// moderator and streamer give the same list, so user 1 hears nothing
				[1] = new HashSet<string> { StaffRoles.Moderator, StaffRoles.Streamer },
				[4] = new HashSet<string> { StaffRoles.Streamer },
				[3] = new HashSet<string> { StaffRoles.Admin }
			};

			var changes = ClientFeatures.Changes(s_map, before, after);

			Assert.Equal(new Int64[] { 2, 4 }, changes.Select(c => c.UserID));
			Assert.Empty(changes[0].Features);
			Assert.Equal(new[] { "observer", "stats_exporter" }, changes[1].Features);
		}

		[Fact]
		public void StreamerIsAKnownRoleWithoutPermissionsOrRank()
		{
			Assert.True(StaffRoles.IsKnown(StaffRoles.Streamer));
			Assert.Contains(StaffRoles.Streamer, StaffRoles.All);
			Assert.False(StaffRoles.IsStaff(StaffRoles.Streamer));
			Assert.True(StaffRoles.IsStaff(StaffRoles.Moderator));
			Assert.True(StaffRoles.IsStaff(StaffRoles.Admin));
			Assert.Empty(StaffRoles.PermissionsFor(new[] { StaffRoles.Streamer }));
			Assert.Equal(0, StaffRoles.RankOf(new[] { StaffRoles.Streamer }));
			Assert.Equal(StaffRoles.RankOf(new[] { StaffRoles.Moderator }), StaffRoles.RankOf(new[] { StaffRoles.Moderator, StaffRoles.Streamer }));
			Assert.True(StaffRoles.RankOf(new[] { StaffRoles.Moderator }) > StaffRoles.RankOf(new[] { StaffRoles.Streamer }));
		}
	}
}
