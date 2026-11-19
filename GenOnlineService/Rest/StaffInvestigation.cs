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

using GenOnlineService.Api;
using GenOnlineService.Identity;
using GenOnlineService.Investigation;
using GenOnlineService.Portal.Client;
using GenOnlineService.Realtime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GenOnlineService.Rest
{
	public static class StaffInvestigation
	{
		public static PortalAddressFacts? FactsOf(AddressFacts? facts)
		{
			return facts == null ? null : new PortalAddressFacts { Country = facts.Country, City = facts.City, Asn = facts.Asn, NetworkOwner = facts.NetworkOwner };
		}

		// Searches first and writes the audit row with the number of accounts found; a failed audit write shows nothing.
		// With raw (a device key search only) the answer also carries the value the client sent, after a device_reveal row
		public static async Task<(Result<PortalInvestigation> Answer, bool HasNext)> SearchAsync(IDbContextFactory<AppDbContext> dbFactory, StaffEnrichment enrichment, Database.ModerationActor actor, IdentifierQuery query, OffsetPage page, CancellationToken cancellationToken = default, bool raw = false)
		{
			if (query.Kind == IdentifierKinds.Country && (long)page.Page * page.PerPage > IdentifierSearch.MaximumCountryAccounts)
			{
				return (Result.Fail(ErrorCodes.InvalidArgument, new { Field = "page", MaximumAccounts = IdentifierSearch.MaximumCountryAccounts }), false);
			}

			await using AppDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
			(List<Database.IdentifierAccount> Accounts, bool HasNext)? found = await Database.IdentifierAccounts.Find(db, query, page.Skip, page.PerPage, cancellationToken);
			if (found == null)
			{
				return (Result.Fail(ErrorCodes.InvalidArgument, new { Field = "value", MaximumRows = Database.IdentifierAccounts.MaximumRangeRows }), false);
			}

			await Database.Moderation.AddAuditEntry(db, actor, ModerationActions.InvestigateSearch, AuditTargetKinds.Investigation, null, null,
				new { kind = query.Kind, value = query.Value, page = page.Page, results = found.Value.Accounts.Count });

			string? rawValue = null;
			if (raw)
			{
				byte[] hash = query.Hashes[0];
				rawValue = await db.Devices.AsNoTracking().Where(d => d.Kind == query.Kind && d.ValueHmac == hash && d.ValueRaw != null)
					.Select(d => d.ValueRaw).FirstOrDefaultAsync(cancellationToken);
				await Database.Moderation.AddAuditEntry(db, actor, ModerationActions.DeviceReveal, AuditTargetKinds.Investigation, null, null,
					new { kind = query.Kind, key = query.Value, found = rawValue != null });
			}

			return (Result.Success(new PortalInvestigation
			{
				Kind = query.Kind,
				Value = query.Value,
				Vendor = query.Kind == IdentifierKinds.Mac ? enrichment.Vendors.VendorOf(query.Value) : null,
				Network = query.Kind == IdentifierKinds.Ip ? FactsOf(enrichment.Addresses.Lookup(query.Value)) : null,
				RawValue = rawValue,
				Accounts = found.Value.Accounts.Select(a => new PortalInvestigationAccount
				{
					Id = a.UserID,
					Name = a.Name,
					Tag = PlayerTags.Of(a.NameTag),
					State = a.State,
					FirstSeenAt = a.FirstSeenAt,
					LastSeenAt = a.LastSeenAt,
					Events = a.Events
				}).ToList()
			}), found.Value.HasNext);
		}
	}

	// The reverse lookups of the staff pages: every account that used a device or network identifier
	[ApiController]
	[Authorize]
	public sealed class StaffInvestigationController : ControllerBase
	{
		private readonly IDbContextFactory<AppDbContext> m_dbFactory;

		public StaffInvestigationController(IDbContextFactory<AppDbContext> dbFactory)
		{
			m_dbFactory = dbFactory;
		}

		// ?kind=&value= (or &key= for a device of the inspection), offset paged; every search writes an audit row. &raw=true
		// with a key also reveals the value the client sent, players.inspect being an admin and owner permission
		[AllowSignInCookie]
		[Authorize(Policy = PermissionPolicies.Prefix + Permissions.PlayersInspect)]
		[EndpointSummary("Finds every account that used a MAC address, machine id, volume serial, IP address, IP range or country.")]
		[ProducesResponseType<PortalInvestigation>(StatusCodes.Status200OK)]
		[HttpGet("~/api/v1/staff/investigate")]
		public async Task<IActionResult> Search()
		{
			(IdentifierQuery? query, Failure? failure) = IdentifierSearch.Read(StaffPlayerActions.First(Request.Query, "kind"), StaffPlayerActions.First(Request.Query, "value"), StaffPlayerActions.First(Request.Query, "key"));
			if (failure != null)
			{
				return RestResults.Fail(failure.Value);
			}
			(OffsetPage page, Failure? pageFailure) = Paging.ReadOffset(Request);
			if (pageFailure != null)
			{
				return RestResults.Fail(pageFailure.Value);
			}
			string? rawText = StaffPlayerActions.First(Request.Query, "raw");
			bool raw = rawText == "true";
			if (rawText != null && ((!raw && rawText != "false") || (raw && StaffPlayerActions.First(Request.Query, "key") == null)))
			{
				return RestResults.Fail(Result.Invalid("raw"));
			}

			(Result<PortalInvestigation> answer, bool hasNext) = await StaffInvestigation.SearchAsync(m_dbFactory, StaffEnrichment.Of(HttpContext.RequestServices), CallerActors.Of(User), query!, page, HttpContext.RequestAborted, raw);
			if (answer.Ok)
			{
				Paging.WriteOffsetHeaders(HttpContext, page, hasNext);
			}
			return ApiResults.Answer(answer);
		}
	}
}
