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
using System.Text;

namespace GenOnlineService.Rules
{
	// The compiled current versions of the enabled rules, by trigger
	public sealed class RuleSet
	{
		public static readonly RuleSet Empty = new RuleSet(String.Empty, []);

		private readonly Dictionary<string, IReadOnlyList<CompiledRule>> m_byTrigger;

		public RuleSet(string stamp, IEnumerable<CompiledRule> rules)
		{
			Stamp = stamp;
			Rules = rules.ToList();
			m_byTrigger = Rules.GroupBy(r => r.Trigger, StringComparer.Ordinal).ToDictionary(g => g.Key, g => (IReadOnlyList<CompiledRule>)g.ToList(), StringComparer.Ordinal);
		}

		// The enabled rules with their mode and version, so a change of any reloads
		public string Stamp { get; }

		public IReadOnlyList<CompiledRule> Rules { get; }

		public IReadOnlyList<CompiledRule> For(string trigger) => m_byTrigger.TryGetValue(trigger, out IReadOnlyList<CompiledRule>? rules) ? rules : [];

		public bool Has(string trigger) => m_byTrigger.ContainsKey(trigger);
	}

	// The rules this copy runs. A staff change reloads this copy at once and the others through CatalogSignals, the
	// reconcile tick catches a missed signal, and an unchanged set is kept without compiling again.
	public static class RuleEngine
	{
		private static readonly ILogger s_log = AppLog.For(typeof(RuleEngine));

		private static RuleSet s_current = RuleSet.Empty;

		public static RuleSet Current => Volatile.Read(ref s_current);

		public static async Task ReloadAsync(AppDbContext db)
		{
			var rows = await db.AutomatedRules.AsNoTracking()
				.Where(r => r.Enabled && r.ArchivedAt == null)
				.OrderBy(r => r.ID)
				.Select(r => new { r.ID, r.CurrentVersion, r.Mode })
				.ToListAsync();
			var stamp = new StringBuilder();
			foreach (var row in rows)
			{
				stamp.Append(row.ID).Append(':').Append(row.CurrentVersion).Append(':').Append(row.Mode).Append(';');
			}
			if (stamp.ToString() == Current.Stamp)
			{
				return;
			}

			List<long> ids = rows.Select(r => r.ID).ToList();
			var documents = await db.AutomatedRuleVersions.AsNoTracking()
				.Where(v => ids.Contains(v.RuleID))
				.Join(db.AutomatedRules.AsNoTracking(), v => new { v.RuleID, v.Number }, r => new { RuleID = r.ID, Number = r.CurrentVersion }, (v, r) => new { v.RuleID, v.Document })
				.ToDictionaryAsync(v => v.RuleID, v => v.Document);

			var compiled = new List<CompiledRule>();
			foreach (var row in rows)
			{
				if (!documents.TryGetValue(row.ID, out string? json))
				{
					continue;
				}
				try
				{
					compiled.Add(CompiledRule.Compile(row.ID, row.CurrentVersion, row.Mode, RuleDocuments.Deserialize(json)));
				}
				catch (Exception ex)
				{
					// one rule that cannot compile stays off, the others still run
					s_log.LogError(ex, "Automated rule {RuleId} version {Version} did not compile", row.ID, row.CurrentVersion);
				}
			}
			Install(new RuleSet(stamp.ToString(), compiled));
		}

		public static void Install(RuleSet set)
		{
			Volatile.Write(ref s_current, set);
			s_log.LogInformation("{Count} automated rules are live", set.Rules.Count);
		}
	}
}
