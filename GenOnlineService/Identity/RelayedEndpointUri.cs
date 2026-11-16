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

using OpenIddict.Client;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Client.OpenIddictClientEvents;
using static OpenIddict.Client.OpenIddictClientHandlerFilters;

namespace GenOnlineService.Identity
{
	// OpenIddict's ValidateEndpointUri compares the callback URL the browser arrived at with the redirect_uri sent to
	// the provider. A provider with Accounts:providers:<name>:redirect_uri returns to another host, which redirects the
	// browser to this provider's own callback here. That callback, and only it, then counts as the relay URL; every
	// other comparison is the built-in one unchanged.
	public sealed class RelayedEndpointUri : IOpenIddictClientHandler<ProcessAuthenticationContext>
	{
		private static readonly OpenIddictClientHandlers.ValidateEndpointUri s_builtIn = new();
		private readonly Dictionary<string, (Uri Relay, string CallbackPath)> m_relays;

		public RelayedEndpointUri(IEnumerable<UpstreamProviderSettings> providers)
		{
			m_relays = providers.Where(p => p.RedirectUri != null)
				.ToDictionary(p => p.Name, p => (new Uri(p.RedirectUri!, UriKind.Absolute), p.CallbackPath), StringComparer.Ordinal);
		}

		// Takes the built-in handler's place: same filters, same order
		public static OpenIddictClientHandlerDescriptor CreateDescriptor(RelayedEndpointUri handler)
			=> OpenIddictClientHandlerDescriptor.CreateBuilder<ProcessAuthenticationContext>()
				.AddFilter<RequireStateTokenPrincipal>()
				.AddFilter<RequireStateTokenValidated>()
				.UseSingletonHandler(handler)
				.SetOrder(OpenIddictClientHandlers.ValidateEndpointUri.Descriptor.Order)
				.SetType(OpenIddictClientHandlerType.Custom)
				.Build();

		public async ValueTask HandleAsync(ProcessAuthenticationContext context)
		{
			ArgumentNullException.ThrowIfNull(context);

			Uri? requested = context.RequestUri;
			Uri? relayed = RelayedRequestUri(context);
			if (relayed == null)
			{
				await s_builtIn.HandleAsync(context);
				return;
			}

			context.RequestUri = relayed;
			try
			{
				await s_builtIn.HandleAsync(context);
			}
			finally
			{
				context.RequestUri = requested;
			}
		}

		// The relay URL with this request's query when the flow was started with the relay and the browser is now at
		// that same provider's callback here; null otherwise
		private Uri? RelayedRequestUri(ProcessAuthenticationContext context)
		{
			// the registration is resolved from the state token only after this check, so the provider is read from the
			// signed state token itself
			if (context.RequestUri is not Uri request || context.BaseUri is not Uri baseUri
				|| context.StateTokenPrincipal?.FindFirst(Claims.Private.ProviderName)?.Value is not string provider
				|| !m_relays.TryGetValue(provider, out var relay)
				|| context.StateTokenPrincipal?.FindFirst(Claims.Private.RedirectUri)?.Value is not string stored
				|| !Uri.TryCreate(stored, UriKind.Absolute, out Uri? sent)
				|| WithoutQuery(sent) != WithoutQuery(relay.Relay)
				|| WithoutQuery(request) != WithoutQuery(new Uri(baseUri, relay.CallbackPath)))
			{
				return null;
			}

			return new UriBuilder(sent) { Query = request.Query.TrimStart('?') }.Uri;
		}

		private static Uri WithoutQuery(Uri uri) => new UriBuilder(uri) { Query = null, Fragment = null }.Uri;
	}
}
