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
	// "mail test <to>": sends one test mail through the configured Mail:delivery_method, so an operator sees whether account
	// mail would arrive. Exit 0 sent, 1 not configured or refused (the reason printed, never a secret), 2 a bad command line.
	public static class MailCli
	{
		private const string Usage =
			"Usage:\n"
			+ "  GenOnlineService mail test <email address>\n"
			+ "Sends one test mail through Mail:delivery_method and prints whether the provider accepted it.";

		public static Task<int> Run(string[] args) => Run(args, Program.OpenConfiguration(), new HttpClient(), Console.Out);

		public static async Task<int> Run(string[] args, IConfiguration config, HttpClient http, TextWriter output)
		{
			if (args.Length != 3 || !args[1].Equals("test", StringComparison.OrdinalIgnoreCase))
			{
				output.WriteLine(Usage);
				return 2;
			}
			string to = args[2].Trim();
			if (!MailSettings.IsAddress(to))
			{
				output.WriteLine($"Not one email address: {to}{Environment.NewLine}{Usage}");
				return 2;
			}

			MailSettings settings;
			try
			{
				settings = MailSettings.Read(config);
			}
			catch (InvalidOperationException ex)
			{
				output.WriteLine(ex.Message);
				return 1;
			}
			if (!settings.Enabled)
			{
				output.WriteLine("Mail is not configured (Mail:delivery_method is none), so nothing was sent.");
				return 1;
			}

			string method = settings.DeliveryMethod.ToString().ToLowerInvariant();
			var message = new EmailMessage(to, "Generals Online test mail",
				"<p>This is a test mail from Generals Online. If you can read it, account mail reaches this address.</p>",
				"This is a test mail from Generals Online. If you can read it, account mail reaches this address.");
			try
			{
				await EmailSenders.Create(settings, http).SendAsync(message);
			}
			catch (EmailDeliveryException ex)
			{
				output.WriteLine(ex.Message);
				return 1;
			}

			output.WriteLine($"Sent a test mail to {to} through {method}.");
			return 0;
		}
	}
}
