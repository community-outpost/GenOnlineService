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
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Options;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;

namespace GenOnlineService.Identity
{
	// One ASP.NET Core data protection key (cookies, antiforgery); every copy of an environment reads the same ring
	public sealed class DataProtectionKeyRow
	{
		public const int FriendlyNameMaxLength = 128;

		public long ID { get; set; }
		public string? FriendlyName { get; set; }
		public string Xml { get; set; } = String.Empty;
		public DateTime CreatedAt { get; set; }
	}

	public class DataProtectionKeyConfiguration : IEntityTypeConfiguration<DataProtectionKeyRow>
	{
		public void Configure(EntityTypeBuilder<DataProtectionKeyRow> builder)
		{
			builder.ToTable("data_protection_keys");

			builder.HasKey(e => e.ID);

			builder.Property(e => e.ID).HasColumnName("id").ValueGeneratedOnAdd();
			builder.Property(e => e.FriendlyName).HasColumnName("friendly_name").HasMaxLength(DataProtectionKeyRow.FriendlyNameMaxLength);
			builder.Property(e => e.Xml).HasColumnName("xml");
			builder.Property(e => e.CreatedAt).HasColumnName("created_at");
		}
	}

	// The key ring lives in the service database instead of the container's file system
	public sealed class DatabaseXmlRepository : IXmlRepository
	{
		private readonly IDbContextFactory<AppDbContext> m_dbFactory;

		public DatabaseXmlRepository(IDbContextFactory<AppDbContext> dbFactory)
		{
			m_dbFactory = dbFactory;
		}

		public IReadOnlyCollection<XElement> GetAllElements()
		{
			using AppDbContext db = m_dbFactory.CreateDbContext();
			return db.DataProtectionKeys.AsNoTracking().OrderBy(k => k.ID).Select(k => k.Xml).AsEnumerable().Select(x => XElement.Parse(x)).ToList().AsReadOnly();
		}

		public void StoreElement(XElement element, string friendlyName)
		{
			using AppDbContext db = m_dbFactory.CreateDbContext();
			db.DataProtectionKeys.Add(new DataProtectionKeyRow
			{
				FriendlyName = friendlyName.Length <= DataProtectionKeyRow.FriendlyNameMaxLength ? friendlyName : friendlyName.Substring(0, DataProtectionKeyRow.FriendlyNameMaxLength),
				Xml = element.ToString(SaveOptions.DisableFormatting),
				CreatedAt = DateTime.UtcNow
			});
			db.SaveChanges();
		}
	}

	public sealed class DataProtectionSettings
	{
		public const string DevelopmentApplicationName = "genonline-development";

		// Every copy of one environment needs the same name, or they cannot read each other's cookies
		public string? ApplicationName { get; init; }

		// The first certificate encrypts new keys at rest; every listed certificate decrypts
		public List<string> Certificates { get; init; } = new();
		public string? CertificatePassword { get; init; }

		public static DataProtectionSettings Read(IConfiguration config)
		{
			IConfigurationSection section = config.GetSection("DataProtection");
			return new DataProtectionSettings
			{
				ApplicationName = section.GetValue<string>("application_name"),
				Certificates = section.GetSection("certificates").Get<List<string>>() ?? new(),
				CertificatePassword = section.GetValue<string>("certificate_password")
			};
		}
	}

	public static class KeyStorage
	{
		public static void AddGenOnlineDataProtection(this IServiceCollection services, IConfiguration config, bool development)
		{
			DataProtectionSettings settings = DataProtectionSettings.Read(config);

			string? applicationName = settings.ApplicationName;
			if (String.IsNullOrWhiteSpace(applicationName))
			{
				if (!development)
				{
					throw new InvalidOperationException("DataProtection:application_name is required outside Development; every copy of one environment must use the same name.");
				}

				applicationName = DataProtectionSettings.DevelopmentApplicationName;
			}

			IDataProtectionBuilder builder = services.AddDataProtection().SetApplicationName(applicationName);
			services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(sp => new ConfigureOptions<KeyManagementOptions>(options =>
				options.XmlRepository = new DatabaseXmlRepository(sp.GetRequiredService<IDbContextFactory<AppDbContext>>())));

			if (settings.Certificates.Count > 0)
			{
				X509Certificate2[] certificates = LoadAll(settings.Certificates, settings.CertificatePassword, "DataProtection:certificates");
				builder.ProtectKeysWithCertificate(certificates[0]).UnprotectKeysWithAnyCertificate(certificates);
			}
		}

		public static X509Certificate2[] LoadAll(IReadOnlyList<string> entries, string? password, string setting)
		{
			return entries.Select((entry, index) => Load(entry, password, $"{setting}:{index}")).ToArray();
		}

		// A .pfx or .p12 file, a PEM file holding the certificate and its private key, or that PEM text itself
		public static X509Certificate2 Load(string entry, string? password, string setting)
		{
			string value = entry.Trim();
			X509Certificate2 certificate;
			try
			{
				// PEM text may carry leading lines (openssl "Bag Attributes"), so any PEM marker or line break means text, never a path
				if (value.Contains("-----BEGIN", StringComparison.Ordinal))
				{
					certificate = X509Certificate2.CreateFromPem(value, value);
				}
				else if (value.IndexOfAny(new[] { '\r', '\n' }) >= 0)
				{
					throw new InvalidOperationException($"{setting}: neither a file path nor PEM text.");
				}
				else if (value.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase) || value.EndsWith(".p12", StringComparison.OrdinalIgnoreCase))
				{
					certificate = X509CertificateLoader.LoadPkcs12FromFile(value, password, X509KeyStorageFlags.EphemeralKeySet);
				}
				else
				{
					certificate = X509Certificate2.CreateFromPemFile(value);
				}
			}
			catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException or UnauthorizedAccessException or ArgumentException)
			{
				// never echo the entry itself: it may be the private key
				throw new InvalidOperationException($"{setting}: cannot load the certificate ({ex.GetType().Name}: {ex.Message})");
			}

			if (!certificate.HasPrivateKey)
			{
				throw new InvalidOperationException($"{setting}: the certificate has no private key.");
			}

			return certificate;
		}
	}
}
