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

namespace GenOnlineService
{
	// The deploy environment this service runs as: live, test or dev.
	// It is separate from the ASP.NET environment, which only switches framework behaviour, so a live and a test pod can both run as Production.
	public sealed class ServiceEnvironment
	{
		public const string ConfigSection = "Service";
		public const string ConfigKey = "environment";

		public const string Live = "live";
		public const string Test = "test";
		public const string Dev = "dev";

		public static readonly IReadOnlyList<string> Names = [Live, Test, Dev];

		public string Name { get; }

		private ServiceEnvironment(string name)
		{
			Name = name;
		}

		// An unset value is dev on an ASP.NET Development host and stops the start everywhere else, so a live pod never reports itself as test or dev by accident
		public static ServiceEnvironment Read(IConfiguration config, bool isDevelopment)
		{
			string? value = config.GetSection(ConfigSection)[ConfigKey];
			if (string.IsNullOrWhiteSpace(value))
			{
				if (isDevelopment)
				{
					return new ServiceEnvironment(Dev);
				}
				throw new InvalidOperationException($"{ConfigSection}:{ConfigKey} is not set. Set it to live, test or dev.");
			}
			if (!Names.Contains(value, StringComparer.Ordinal))
			{
				throw new InvalidOperationException($"{ConfigSection}:{ConfigKey} is `{value}`. It must be live, test or dev.");
			}
			return new ServiceEnvironment(value);
		}

		// Startup: reads the setting once, so a missing or unknown one stops the service before it serves anything
		public static ServiceEnvironment Configure(IConfiguration config, IHostEnvironment environment)
		{
			return Read(config, environment.IsDevelopment());
		}

		public override string ToString() => Name;
	}
}
