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

namespace GenOnlineService.Mail
{
	public static class MailSetup
	{
		public const string HttpClientName = "mail";

		// Reads Mail: (a misconfigured method stops the start, naming the key) and registers the sender. The sender is
		// transient so each use gets a client from the factory, whose handlers rotate (DNS changes of the provider).
		public static MailSettings AddMail(this IServiceCollection services, IConfiguration config)
		{
			MailSettings settings = MailSettings.Read(config);
			if (settings.Enabled)
			{
				PublicOrigin(config["Accounts:issuer"]);
			}
			services.AddSingleton(settings);
			services.AddHttpClient(HttpClientName);
			services.AddTransient<IEmailSender>(sp => EmailSenders.Create(settings, sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName)));
			return settings;
		}

		// The origin of account mail links: Accounts:issuer, never the request's Host header (which any client sets, and
		// AllowedHosts is "*"), so a forged Host cannot send a victim a link to another site. Required with mail.
		public static string PublicOrigin(string? issuer)
		{
			string value = (issuer ?? String.Empty).Trim();
			if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? parsed) || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp)
				|| !String.IsNullOrEmpty(parsed.Query) || !String.IsNullOrEmpty(parsed.Fragment))
			{
				throw new InvalidOperationException("Accounts:issuer is required when Mail:delivery_method is not none: account mail links use this public address of the sign-in pages (for example https://login.generals.online/), never the request's Host header.");
			}
			return value.TrimEnd('/');
		}
	}
}
