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

using Microsoft.AspNetCore.DataProtection;
using System.Globalization;
using System.Security.Cryptography;

namespace GenOnlineService.Identity
{
	// The provider's own name, carried from the sign-in callback to the choose-a-name page and never stored (S17): an
	// encrypted cookie bound to the account, gone after a few minutes or once a name is chosen
	public static class NameSuggestion
	{
		public const string CookieName = "go_name_suggestion";
		public const string PagePath = "/users/sign_up/welcome";

		private const string Purpose = "GenOnline.Identity.NameSuggestion";
		private static readonly TimeSpan s_lifetime = TimeSpan.FromMinutes(15);

		public static string Protect(IDataProtectionProvider protection, Int64 userID, string name)
		{
			return protection.CreateProtector(Purpose).ToTimeLimitedDataProtector()
				.Protect(userID.ToString(CultureInfo.InvariantCulture) + ":" + name, s_lifetime);
		}

		public static void Write(HttpContext context, Int64 userID, string name)
		{
			var protection = context.RequestServices.GetRequiredService<IDataProtectionProvider>();
			context.Response.Cookies.Append(CookieName, Protect(protection, userID, name), Options(context, s_lifetime));
		}

		// The suggestion for this account; null when there is none, it expired, or it belongs to another account
		public static string? Read(HttpContext context, Int64 userID)
		{
			if (!context.Request.Cookies.TryGetValue(CookieName, out string? value) || String.IsNullOrEmpty(value))
			{
				return null;
			}

			string payload;
			try
			{
				payload = context.RequestServices.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).ToTimeLimitedDataProtector().Unprotect(value);
			}
			catch (CryptographicException)
			{
				return null;
			}

			string prefix = userID.ToString(CultureInfo.InvariantCulture) + ":";
			return payload.StartsWith(prefix, StringComparison.Ordinal) ? payload.Substring(prefix.Length) : null;
		}

		public static void Clear(HttpContext context)
		{
			context.Response.Cookies.Delete(CookieName, Options(context, null));
		}

		private static CookieOptions Options(HttpContext context, TimeSpan? maxAge) => new()
		{
			HttpOnly = true,
			Secure = context.Request.IsHttps,
			SameSite = SameSiteMode.Lax,
			Path = PagePath,
			MaxAge = maxAge,
			IsEssential = true
		};
	}
}
