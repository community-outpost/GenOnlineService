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

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenIddict.EntityFrameworkCore.Models;
using System.Text;

// Runtime settings an operator or the service changes without a deploy; the value is JSON
public class ApplicationSetting
{
	public string Key { get; set; } = String.Empty;
	public string Value { get; set; } = "null";
	public string UpdatedBy { get; set; } = String.Empty;
	public DateTime UpdatedAt { get; set; } = DateTime.UnixEpoch;
}

public class ApplicationSettingConfiguration : IEntityTypeConfiguration<ApplicationSetting>
{
	public void Configure(EntityTypeBuilder<ApplicationSetting> builder)
	{
		builder.ToTable("application_settings");

		builder.HasKey(e => e.Key);

		builder.Property(e => e.Key).HasColumnName("key").HasMaxLength(64);
		builder.Property(e => e.Value).HasColumnName("value");
		builder.Property(e => e.UpdatedBy).HasColumnName("updated_by").HasMaxLength(64);
		builder.Property(e => e.UpdatedAt).HasColumnName("updated_at");
	}
}

public static class OpenIddictModel
{
	// OpenIddict entities keep their shape; tables and columns follow the schema naming rules
	public static void Configure(ModelBuilder modelBuilder)
	{
		modelBuilder.UseOpenIddict();

		Rename(modelBuilder.Entity<OpenIddictEntityFrameworkCoreApplication>().Metadata, "oauth_applications");
		Rename(modelBuilder.Entity<OpenIddictEntityFrameworkCoreAuthorization>().Metadata, "oauth_authorizations");
		Rename(modelBuilder.Entity<OpenIddictEntityFrameworkCoreScope>().Metadata, "oauth_scopes");
		Rename(modelBuilder.Entity<OpenIddictEntityFrameworkCoreToken>().Metadata, "oauth_tokens");

		// the subject is a user id; the default 400 characters push OpenIddict's composite indexes past MySQL's 3072 byte key limit
		modelBuilder.Entity<OpenIddictEntityFrameworkCoreAuthorization>().Property(e => e.Subject).HasMaxLength(64);
		modelBuilder.Entity<OpenIddictEntityFrameworkCoreToken>().Property(e => e.Subject).HasMaxLength(64);
	}

	private static void Rename(IMutableEntityType entity, string table)
	{
		entity.SetTableName(table);

		foreach (IMutableProperty property in entity.GetProperties())
		{
			property.SetColumnName(SnakeCase(property.Name));
		}

		foreach (IMutableIndex index in entity.GetIndexes())
		{
			index.SetDatabaseName("ix_" + table + "_" + String.Join("_", index.Properties.Select(p => SnakeCase(p.Name))));
		}
	}

	private static string SnakeCase(string name)
	{
		var result = new StringBuilder(name.Length + 8);
		for (int i = 0; i < name.Length; i++)
		{
			if (Char.IsUpper(name[i]) && i > 0 && !Char.IsUpper(name[i - 1]))
			{
				result.Append('_');
			}

			result.Append(Char.ToLowerInvariant(name[i]));
		}

		return result.ToString();
	}
}
