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

using GenOnlineService.OpenApi;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GenOnlineService.Api
{
	using GenOnlineService.Realtime;

	// Friends, friend requests and blocks of the caller (section 10.1)
	public sealed class SocialController : ApiControllerBase
	{
		private readonly IDbContextFactory<AppDbContext> m_dbFactory;

		public SocialController(IDbContextFactory<AppDbContext> dbFactory, IAuthorizationService authorization) : base(authorization)
		{
			m_dbFactory = dbFactory;
		}

		[EndpointSummary("Lists the friends and pending friend requests of the caller.")]
		[ProducesResponseType<FriendsResponse>(StatusCodes.Status200OK)]
		[HttpGet("~/api/v1/me/friends")]
		public async Task<IActionResult> GetFriends()
		{
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			SocialLists lists = await SocialActions.ListsAsync(db, Me);
			return ApiResults.Ok(new FriendsResponse { Friends = lists.Friends, Incoming = lists.Incoming, Outgoing = lists.Outgoing });
		}

		[EndpointSummary("Lists the players the caller blocks.")]
		[ProducesResponseType<BlocksResponse>(StatusCodes.Status200OK)]
		[HttpGet("~/api/v1/me/blocks")]
		public async Task<IActionResult> GetBlocks()
		{
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			SocialLists lists = await SocialActions.ListsAsync(db, Me);
			return ApiResults.Ok(new BlocksResponse { Blocked = lists.Blocked });
		}

		[EndpointSummary("Sends a friend request.")]
		[ProducesResponseType(StatusCodes.Status204NoContent)]
		[ApiRequestBody(typeof(FriendRequestRequest))]
		[HttpPost("~/api/v1/me/friend_requests")]
		public async Task<IActionResult> SendFriendRequest()
		{
			(FriendRequestRequest? body, Failure? malformed) = await ReadBodyAsync<FriendRequestRequest>();
			if (malformed != null)
			{
				return ApiResults.Fail(malformed.Value);
			}
			return ApiResults.NoContent(await SocialActions.SendFriendRequestAsync(m_dbFactory, Me, body?.UserId ?? 0));
		}

		[EndpointSummary("Accepts a friend request.")]
		[ProducesResponseType(StatusCodes.Status204NoContent)]
		[HttpPost("~/api/v1/me/friend_requests/{userID:long}/accept")]
		public async Task<IActionResult> AcceptFriendRequest(long userID) => ApiResults.NoContent(await SocialActions.AcceptFriendRequestAsync(m_dbFactory, Me, userID));

		[EndpointSummary("Declines or withdraws a friend request.")]
		[ProducesResponseType(StatusCodes.Status204NoContent)]
		[HttpDelete("~/api/v1/me/friend_requests/{userID:long}")]
		public async Task<IActionResult> DeleteFriendRequest(long userID) => ApiResults.NoContent(await SocialActions.DeleteFriendRequestAsync(m_dbFactory, Me, userID));

		[EndpointSummary("Removes a friend.")]
		[ProducesResponseType(StatusCodes.Status204NoContent)]
		[HttpDelete("~/api/v1/me/friends/{userID:long}")]
		public async Task<IActionResult> RemoveFriend(long userID) => ApiResults.NoContent(await SocialActions.RemoveFriendAsync(m_dbFactory, Me, userID));

		[EndpointSummary("Blocks a player.")]
		[ProducesResponseType(StatusCodes.Status204NoContent)]
		[HttpPut("~/api/v1/me/blocks/{userID:long}")]
		public async Task<IActionResult> Block(long userID) => ApiResults.NoContent(await SocialActions.BlockAsync(m_dbFactory, Me, userID));

		[EndpointSummary("Unblocks a player.")]
		[ProducesResponseType(StatusCodes.Status204NoContent)]
		[HttpDelete("~/api/v1/me/blocks/{userID:long}")]
		public async Task<IActionResult> Unblock(long userID) => ApiResults.NoContent(await SocialActions.UnblockAsync(m_dbFactory, Me, userID));
	}
}
