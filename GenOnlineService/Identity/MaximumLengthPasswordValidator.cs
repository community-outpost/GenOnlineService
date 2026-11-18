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

using Microsoft.AspNetCore.Identity;

namespace GenOnlineService.Identity
{
	// Identity has a minimum length only; a cap keeps hashing a huge password from costing the server
	public sealed class MaximumLengthPasswordValidator : IPasswordValidator<User>
	{
		public const string ErrorCode = "PasswordTooLong";

		public Task<IdentityResult> ValidateAsync(UserManager<User> manager, User user, string? password)
		{
			return Task.FromResult(password != null && password.Length > PasswordSignInSettings.MaximumLength
				? IdentityResult.Failed(new IdentityError { Code = ErrorCode, Description = $"Passwords must be at most {PasswordSignInSettings.MaximumLength} characters." })
				: IdentityResult.Success);
		}
	}
}
