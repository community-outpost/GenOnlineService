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


using GenOnlineService;
using GenOnlineService.Realtime;
using Xunit;

namespace GenOnlineService.Tests;

// Chat badges: each staff role shows the badge of the same id, highest first; an owner shows the admin badge unless an
// owner badge is configured
public class RealtimeBadgesTests
{
	private static readonly Dictionary<string, BadgeDefinition> s_defaults = new(StringComparer.Ordinal)
	{
		[StaffRoles.Admin] = new BadgeDefinition { Text = "ADMIN", Color = "#ffffff" },
		[StaffRoles.Moderator] = new BadgeDefinition { Text = "MOD", Color = "#ffffff" },
	};

	[Fact]
	public void AnOwnerShowsTheAdminBadge()
	{
		Assert.Equal(new[] { StaffRoles.Admin }, RealtimeBadges.ForRoles(new[] { StaffRoles.Owner }, s_defaults));
		Assert.Equal(new[] { StaffRoles.Admin, StaffRoles.Moderator }, RealtimeBadges.ForRoles(new[] { StaffRoles.Owner, StaffRoles.Admin, StaffRoles.Moderator }, s_defaults));
	}

	[Fact]
	public void AConfiguredOwnerBadgeWins()
	{
		var withOwner = new Dictionary<string, BadgeDefinition>(s_defaults) { [StaffRoles.Owner] = new BadgeDefinition { Text = "OWNER", Color = "#ffffff" } };
		Assert.Equal(new[] { StaffRoles.Owner }, RealtimeBadges.ForRoles(new[] { StaffRoles.Owner }, withOwner));
	}

	// the previous behaviour: admin before moderator, roles without a badge show none
	[Fact]
	public void OtherRolesKeepTheirBadges()
	{
		Assert.Equal(new[] { StaffRoles.Admin, StaffRoles.Moderator }, RealtimeBadges.ForRoles(new[] { StaffRoles.Moderator, StaffRoles.Admin }, s_defaults));
		Assert.Equal(new[] { StaffRoles.Moderator }, RealtimeBadges.ForRoles(new[] { StaffRoles.Moderator, "streamer" }, s_defaults));
		Assert.Empty(RealtimeBadges.ForRoles(new[] { "streamer" }, s_defaults));
		Assert.Empty(RealtimeBadges.ForRoles(Array.Empty<string>(), s_defaults));
	}
}
