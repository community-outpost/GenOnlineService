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

namespace GenOnlineService.Identity
{
	// The agreement pages (S19): the rule cards one by one, then the Terms, then back to the sign-in that sent the player here
	public sealed partial class AccountsController
	{
		public const string AgreementsPath = "/users/agreements";

		private static string AgreementsUrl(string target) => $"{AgreementsPath}?{ReturnUrlField}={Uri.EscapeDataString(target)}";
	}
}
