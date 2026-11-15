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
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

public sealed class AppDbContextFactoryAdapter<TContext> : IDbContextFactory<AppDbContext> where TContext : AppDbContext
{
	private readonly IDbContextFactory<TContext> _inner;

	public AppDbContextFactoryAdapter(IDbContextFactory<TContext> inner)
	{
		_inner = inner;
	}

	public AppDbContext CreateDbContext()
	{
		return _inner.CreateDbContext();
	}

	public async Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
	{
		return await _inner.CreateDbContextAsync(cancellationToken);
	}
}

// MySQL/MariaDB: utf8mb4 text, datetime(6) timestamps (the provider default), json columns
public class MySqlAppDbContext : AppDbContext
{
	// Only used by the design time factory, at runtime the server version is detected from the connection
	public static readonly ServerVersion DesignTimeServerVersion = new MariaDbServerVersion(new Version(12, 1, 2));

	public MySqlAppDbContext(DbContextOptions<MySqlAppDbContext> options)
		: base(options)
	{
	}

	protected override void OnModelCreating(ModelBuilder modelBuilder)
	{
		base.OnModelCreating(modelBuilder);

		modelBuilder.HasCharSet("utf8mb4");

		ConfigureJsonColumns(modelBuilder, p => p.HasColumnType("json"));

		// Uniqueness keys compare exactly, as on PostgreSQL; the default collations ignore case and accents
		modelBuilder.Entity<User>(e =>
		{
			e.Property(x => x.UsernameNormalized).UseCollation("utf8mb4_bin");
			e.Property(x => x.EmailNormalized).UseCollation("utf8mb4_bin");
		});

		modelBuilder.Entity<UserIdentity>(e =>
		{
			e.Property(x => x.Provider).UseCollation("utf8mb4_bin");
			e.Property(x => x.ExternUid).UseCollation("utf8mb4_bin");
		});
	}
}

// PostgreSQL: timestamptz timestamps (the provider default), jsonb columns
public class PostgresAppDbContext : AppDbContext
{
	public PostgresAppDbContext(DbContextOptions<PostgresAppDbContext> options)
		: base(options)
	{
	}

	protected override void OnModelCreating(ModelBuilder modelBuilder)
	{
		base.OnModelCreating(modelBuilder);

		ConfigureJsonColumns(modelBuilder, p => p.HasColumnType("jsonb"));
	}
}

// For "dotnet ef". Creating a migration doesn't connect, so the connection strings only need to be well-formed.
public class MySqlAppDbContextFactory : IDesignTimeDbContextFactory<MySqlAppDbContext>
{
	public MySqlAppDbContext CreateDbContext(string[] args)
	{
		var options = new DbContextOptionsBuilder<MySqlAppDbContext>()
			.UseMySql("Server=localhost;Database=genonline_design_time;User ID=root;Password=design_time_placeholder", MySqlAppDbContext.DesignTimeServerVersion)
			.Options;

		return new MySqlAppDbContext(options);
	}
}

public class PostgresAppDbContextFactory : IDesignTimeDbContextFactory<PostgresAppDbContext>
{
	public PostgresAppDbContext CreateDbContext(string[] args)
	{
		var options = new DbContextOptionsBuilder<PostgresAppDbContext>()
			.UseNpgsql("Host=localhost;Database=genonline_design_time;Username=postgres;Password=design_time_placeholder")
			.Options;

		return new PostgresAppDbContext(options);
	}
}
