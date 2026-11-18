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
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GenOnlineService.TextRules
{
	// The input a text check runs on; each kind has its own rule workflow
	public static class TextKinds
	{
		public const string DisplayName = "display_name";
		public const string LobbyName = "lobby_name";
		public const string Chat = "chat";

		public static readonly IReadOnlyList<string> All = [DisplayName, LobbyName, Chat];

		public static bool IsKnown(string? kind) => kind != null && All.Contains(kind, StringComparer.Ordinal);
	}

	public static class TextLanguages
	{
		public const string English = "en";
		public const string Arabic = "ar";

		public static readonly IReadOnlyList<string> All = [English, Arabic];
	}

	public static class TextCategories
	{
		public const string Slur = "slur";
		public const string Sexual = "sexual";
		public const string Profanity = "profanity";
		public const string Insult = "insult";

		// Words that make a display name look like staff or the service
		public const string Impersonation = "impersonation";

		// The rest of the reserved name words the Keycloak plugin kept
		public const string Reserved = "reserved";

		public static readonly IReadOnlyList<string> All = [Slur, Sexual, Profanity, Insult, Impersonation, Reserved];

		// Name only categories: they count apart from the block and flag counts the rules read
		public static bool IsNameOnly(string category) => category is Impersonation or Reserved;
	}

	public static class TextSeverities
	{
		public const string Block = "block";
		public const string Flag = "flag";

		public static readonly IReadOnlyList<string> All = [Block, Flag];
	}

	// How a listed word is matched against the folded text
	public static class TextMatchModes
	{
		// The whole word only (an Arabic word may carry the article or a conjunction in front)
		public const string Word = "word";

		// Anywhere in a word, unless an exception word covers the hit
		public const string Anywhere = "anywhere";

		// The Keycloak plugin's rule: lower-cased text contains the lower-cased word, no other folding
		public const string Literal = "literal";

		public static readonly IReadOnlyList<string> All = [Word, Anywhere, Literal];
	}

	// What a rule decides for its input
	public static class TextOutcomes
	{
		public const string Allow = "allow";
		public const string Refuse = "refuse";
		public const string Block = "block";
		public const string Mask = "mask";

		public static IReadOnlyList<string> For(string kind) => kind == TextKinds.Chat ? [Allow, Block, Mask] : [Allow, Refuse];
	}

	// One listed word or phrase
	public class TextRuleTerm
	{
		public const int TermMaxLength = 64;

		public Int64 ID { get; set; }
		public string Term { get; set; } = String.Empty;
		public string Language { get; set; } = TextLanguages.English;
		public string Category { get; set; } = TextCategories.Profanity;
		public string Severity { get; set; } = TextSeverities.Block;
		public string Match { get; set; } = TextMatchModes.Word;

		// seed, reserved (the plugin's list) or staff
		public string Source { get; set; } = TextRuleSources.Staff;
		public string UpdatedBy { get; set; } = String.Empty;
		public DateTime UpdatedAt { get; set; } = DateTime.UnixEpoch;
	}

	// A word that contains a listed word and stays allowed, such as Scunthorpe
	public class TextRuleException
	{
		public Int64 ID { get; set; }
		public string Term { get; set; } = String.Empty;
		public string Language { get; set; } = TextLanguages.English;
		public string Source { get; set; } = TextRuleSources.Staff;
		public string UpdatedBy { get; set; } = String.Empty;
		public DateTime UpdatedAt { get; set; } = DateTime.UnixEpoch;
	}

	// The rules of one input kind as JSON (TextRuleWorkflows.Parse reads it)
	public class TextRuleWorkflow
	{
		public string Kind { get; set; } = String.Empty;
		public string Definition { get; set; } = "{}";
		public string UpdatedBy { get; set; } = String.Empty;
		public DateTime UpdatedAt { get; set; } = DateTime.UnixEpoch;
	}

	public static class TextRuleSources
	{
		public const string Seed = "seed";
		public const string Reserved = "reserved";
		public const string Staff = "staff";
	}

	public class TextRuleTermConfiguration : IEntityTypeConfiguration<TextRuleTerm>
	{
		public void Configure(EntityTypeBuilder<TextRuleTerm> builder)
		{
			builder.ToTable("text_rule_terms");
			builder.HasKey(e => e.ID);
			builder.Property(e => e.ID).HasColumnName("id");
			builder.Property(e => e.Term).HasColumnName("term").HasMaxLength(TextRuleTerm.TermMaxLength);
			builder.Property(e => e.Language).HasColumnName("language").HasMaxLength(8);
			builder.Property(e => e.Category).HasColumnName("category").HasMaxLength(32);
			builder.Property(e => e.Severity).HasColumnName("severity").HasMaxLength(16);
			builder.Property(e => e.Match).HasColumnName("match_mode").HasMaxLength(16);
			builder.Property(e => e.Source).HasColumnName("source").HasMaxLength(16);
			builder.Property(e => e.UpdatedBy).HasColumnName("updated_by").HasMaxLength(64);
			builder.Property(e => e.UpdatedAt).HasColumnName("updated_at");
			builder.HasIndex(e => new { e.Language, e.Term }).IsUnique().HasDatabaseName("ux_text_rule_terms_language_term");
		}
	}

	public class TextRuleExceptionConfiguration : IEntityTypeConfiguration<TextRuleException>
	{
		public void Configure(EntityTypeBuilder<TextRuleException> builder)
		{
			builder.ToTable("text_rule_exceptions");
			builder.HasKey(e => e.ID);
			builder.Property(e => e.ID).HasColumnName("id");
			builder.Property(e => e.Term).HasColumnName("term").HasMaxLength(TextRuleTerm.TermMaxLength);
			builder.Property(e => e.Language).HasColumnName("language").HasMaxLength(8);
			builder.Property(e => e.Source).HasColumnName("source").HasMaxLength(16);
			builder.Property(e => e.UpdatedBy).HasColumnName("updated_by").HasMaxLength(64);
			builder.Property(e => e.UpdatedAt).HasColumnName("updated_at");
			builder.HasIndex(e => new { e.Language, e.Term }).IsUnique().HasDatabaseName("ux_text_rule_exceptions_language_term");
		}
	}

	public class TextRuleWorkflowConfiguration : IEntityTypeConfiguration<TextRuleWorkflow>
	{
		public void Configure(EntityTypeBuilder<TextRuleWorkflow> builder)
		{
			builder.ToTable("text_rule_workflows");
			builder.HasKey(e => e.Kind);
			builder.Property(e => e.Kind).HasColumnName("kind").HasMaxLength(32);
			builder.Property(e => e.Definition).HasColumnName("definition");
			builder.Property(e => e.UpdatedBy).HasColumnName("updated_by").HasMaxLength(64);
			builder.Property(e => e.UpdatedAt).HasColumnName("updated_at");
		}
	}
}
