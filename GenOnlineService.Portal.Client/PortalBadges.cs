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

using BlazorBlueprint.Components;

namespace GenOnlineService.Portal.Client
{
	// One badge colour per state on every page, so "open" or "banned" looks the same on the dashboard and in the tables
	public static class PortalBadges
	{
		// Muted, as GitLab's: the stone surface for a settled state, the outline for a set-aside one, the accent tint only for
		// what waits for staff and the muted red only for what restricts a player. The palette variants (SoftSuccess,
		// SoftWarning, SoftInfo) would bring Tailwind's green, amber and blue back, so they are not used.
		public static BadgeVariant OfReport(string status) => status switch
		{
			"open" => BadgeVariant.Soft,
			"actioned" => BadgeVariant.Secondary,
			_ => BadgeVariant.Outline
		};

		public static BadgeVariant OfPlayerState(string state) => state == "active" ? BadgeVariant.Secondary : BadgeVariant.SoftDestructive;

		public static BadgeVariant OfSanction(bool active) => active ? BadgeVariant.SoftDestructive : BadgeVariant.Outline;

		public static string IconOfSanction(string type) => type switch
		{
			"ban" => "gavel",
			"mute" => "mic-off",
			_ => "log-out"
		};
	}
}
