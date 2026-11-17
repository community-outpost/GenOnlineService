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

using GenOnlineService.Mail;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text;
using Xunit;

namespace GenOnlineService.Tests;

// `mail test <to>`: the operator's check that the configured method delivers, and the services AddMail registers
public class MailCliTests
{
	private sealed class Recorder : HttpMessageHandler
	{
		public int Calls;
		public HttpStatusCode Status = HttpStatusCode.OK;

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Calls++;
			return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent("{\"id\":\"1\"}", Encoding.UTF8, "application/json") });
		}
	}

	private static IConfiguration Config(params (string Key, string Value)[] values)
		=> new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

	private static readonly (string, string)[] s_resend = [("Mail:delivery_method", "resend"), ("Mail:from", "noreply@generals.online"), ("Mail:resend:api_key", "k")];

	[Theory]
	[InlineData("mail")]
	[InlineData("mail", "test")]
	[InlineData("mail", "send", "a@b.org")]
	[InlineData("mail", "test", "a@b.org", "extra")]
	public async Task BadCommandLinesExitTwo(params string[] args)
	{
		var output = new StringWriter();
		Assert.Equal(2, await MailCli.Run(args, Config(s_resend), new HttpClient(new Recorder()), output));
		Assert.Contains("Usage", output.ToString());
	}

	[Fact]
	public async Task NoMailMethodExitsOneAndSaysSo()
	{
		var output = new StringWriter();
		Assert.Equal(1, await MailCli.Run(["mail", "test", "a@b.org"], Config(), new HttpClient(new Recorder()), output));
		Assert.Contains("Mail:delivery_method is none", output.ToString());
	}

	[Fact]
	public async Task NotAnAddressExitsTwo()
	{
		var recorder = new Recorder();
		var output = new StringWriter();
		Assert.Equal(2, await MailCli.Run(["mail", "test", "not an address"], Config(s_resend), new HttpClient(recorder), output));
		Assert.Equal(0, recorder.Calls);
	}

	[Fact]
	public async Task ATestMailGoesOutThroughTheMethod()
	{
		var recorder = new Recorder();
		var output = new StringWriter();
		Assert.Equal(0, await MailCli.Run(["mail", "test", "ops@example.org"], Config(s_resend), new HttpClient(recorder), output));
		Assert.Equal(1, recorder.Calls);
		Assert.Contains("Sent a test mail to ops@example.org through resend", output.ToString());
	}

	[Fact]
	public async Task ARefusalExitsOneWithTheReason()
	{
		var output = new StringWriter();
		Assert.Equal(1, await MailCli.Run(["mail", "test", "ops@example.org"], Config(s_resend), new HttpClient(new Recorder { Status = HttpStatusCode.Forbidden }), output));
		Assert.Contains("Resend refused the mail: HTTP 403", output.ToString());
	}

	// the service resolves a sender for the configured method, and a disabled one when there is none
	[Fact]
	public void AddMailRegistersTheConfiguredSender()
	{
		var services = new ServiceCollection();
		services.AddMail(Config(s_resend));
		using ServiceProvider provider = services.BuildServiceProvider();
		Assert.IsType<ResendEmailSender>(provider.GetRequiredService<IEmailSender>());
		Assert.Equal(DeliveryMethod.Resend, provider.GetRequiredService<MailSettings>().DeliveryMethod);

		var none = new ServiceCollection();
		none.AddMail(Config());
		using ServiceProvider noneProvider = none.BuildServiceProvider();
		Assert.False(noneProvider.GetRequiredService<IEmailSender>().Enabled);
	}

	// a misconfigured method stops the start, naming the key
	[Fact]
	public void AddMailRefusesAMisconfiguredMethod()
	{
		var services = new ServiceCollection();
		var refused = Assert.Throws<InvalidOperationException>(() => services.AddMail(Config(("Mail:delivery_method", "resend"), ("Mail:from", "a@b.org"))));
		Assert.Contains("Mail:resend:api_key", refused.Message);
	}
}
