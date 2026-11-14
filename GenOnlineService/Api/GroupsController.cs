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

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GenOnlineService.Api
{
	using GenOnlineService.Realtime;

	// Group chats (section 10.1), their messages go through SendMessage to group:<id> on the hub
	public sealed class GroupsController : ApiControllerBase
	{
		private readonly IDbContextFactory<AppDbContext> m_dbFactory;

		public GroupsController(IDbContextFactory<AppDbContext> dbFactory, IAuthorizationService authorization) : base(authorization)
		{
			m_dbFactory = dbFactory;
		}

		[HttpPost("~/api/v1/groups")]
		public async Task<IActionResult> Create()
		{
			(CreateGroupRequest? body, Failure? malformed) = await ReadBodyAsync<CreateGroupRequest>();
			if (malformed != null)
			{
				return ApiResults.Fail(malformed.Value);
			}
			return ApiResults.Answer(await GroupActions.CreateAsync(m_dbFactory, Me, body ?? new CreateGroupRequest()));
		}

		[HttpPost("~/api/v1/groups/{groupID:long}/members")]
		public async Task<IActionResult> AddMembers(long groupID)
		{
			(AddGroupMembersRequest? body, Failure? malformed) = await ReadBodyAsync<AddGroupMembersRequest>();
			if (malformed != null)
			{
				return ApiResults.Fail(malformed.Value);
			}
			return ApiResults.NoContent(await GroupActions.AddMembersAsync(m_dbFactory, Me, groupID, body ?? new AddGroupMembersRequest()));
		}

		// The caller's own id leaves the group
		[HttpDelete("~/api/v1/groups/{groupID:long}/members/{userID:long}")]
		public async Task<IActionResult> RemoveMember(long groupID, long userID) => ApiResults.NoContent(await GroupActions.RemoveMemberAsync(m_dbFactory, Me, groupID, userID));

		[HttpPatch("~/api/v1/groups/{groupID:long}")]
		public async Task<IActionResult> Rename(long groupID)
		{
			(RenameGroupRequest? body, Failure? malformed) = await ReadBodyAsync<RenameGroupRequest>();
			if (malformed != null)
			{
				return ApiResults.Fail(malformed.Value);
			}
			return ApiResults.NoContent(await GroupActions.RenameAsync(m_dbFactory, Me, groupID, body ?? new RenameGroupRequest()));
		}
	}
}
