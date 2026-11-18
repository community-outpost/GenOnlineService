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

namespace GenOnlineService.Mail
{
	// One mail to one address, with an HTML part and the same content as plain text
	public sealed record EmailMessage(string To, string Subject, string Html, string Text);

	// The provider refused or could not be reached; the message never names a secret
	public sealed class EmailDeliveryException : Exception
	{
		public EmailDeliveryException(string message, Exception? inner = null) : base(message, inner) { }
	}

	// Sends account mail through the configured Mail:delivery_method
	public interface IEmailSender
	{
		// False when no delivery method is configured: callers then skip mail and keep the no-mail behaviour
		bool Enabled { get; }

		// Throws EmailDeliveryException when the provider refuses the mail or does not answer in time
		Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
	}

	// Mail:delivery_method none: nothing is sent and callers know it
	public sealed class DisabledEmailSender : IEmailSender
	{
		public bool Enabled => false;

		public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
			=> throw new InvalidOperationException("Mail is not configured (Mail:delivery_method is none).");
	}
}
