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

namespace GenOnlineService.OpenApi
{
	// The JSON body an action reads itself (ReadBodyAsync), for the document only: binding stays as it is
	[AttributeUsage(AttributeTargets.Method, Inherited = false)]
	public sealed class ApiRequestBodyAttribute : Attribute
	{
		public ApiRequestBodyAttribute(Type type)
		{
			Type = type;
		}

		public Type Type { get; }
	}

	// The permissions an action checks in its own code, any one of them is enough, for the document only.
	// Permissions of [Authorize(Policy = "perm:...")] are read from the policy and need no attribute
	[AttributeUsage(AttributeTargets.Method, Inherited = false)]
	public sealed class ApiPermissionAttribute : Attribute
	{
		public ApiPermissionAttribute(params string[] anyOf)
		{
			AnyOf = anyOf;
		}

		public IReadOnlyList<string> AnyOf { get; }
	}
}
