using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Api.Models.StartupDtos;
using Jellyfin.Api.Models.UserDtos;
using Jellyfin.Api.Oidc;
using Jellyfin.Extensions.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Server.Integration.Tests
{
    /// <summary>
    /// Proves the OIDC filter is part of the real request pipeline: with password login disabled,
    /// the stock password endpoints are refused while Quick Connect keeps working.
    /// </summary>
    public sealed class OidcPasswordLoginDisabledTests : IClassFixture<OidcPasswordLoginDisabledTests.Factory>
    {
        private readonly Factory _factory;

        public OidcPasswordLoginDisabledTests(Factory factory)
        {
            _factory = factory;
        }

        [Fact]
        public async Task PasswordEndpoints_AreForbidden_QuickConnectWorks()
        {
            var client = _factory.CreateClient();
            var jsonOptions = JsonDefaults.Options;
            var ct = TestContext.Current.CancellationToken;

            var user = JsonSerializer.Deserialize<StartupUserDto>(await client.GetByteArrayAsync("/Startup/User", ct), jsonOptions)!;
            using (var complete = await client.PostAsync("/Startup/Complete", new ByteArrayContent(Array.Empty<byte>()), ct))
            {
                Assert.Equal(HttpStatusCode.NoContent, complete.StatusCode);
            }

            using var login = new HttpRequestMessage(HttpMethod.Post, "/Users/AuthenticateByName");
            login.Headers.TryAddWithoutValidation(AuthHelper.AuthHeaderName, AuthHelper.DummyAuthHeader);
            login.Content = JsonContent.Create(new AuthenticateUserByName { Username = user.Name, Pw = user.Password }, options: jsonOptions);
            using var loginResponse = await client.SendAsync(login, ct);
            Assert.Equal(HttpStatusCode.Forbidden, loginResponse.StatusCode);

            using var forgot = new HttpRequestMessage(HttpMethod.Post, "/Users/ForgotPassword");
            forgot.Content = JsonContent.Create(new ForgotPasswordDto { EnteredUsername = user.Name! }, options: jsonOptions);
            using var forgotResponse = await client.SendAsync(forgot, ct);
            Assert.Equal(HttpStatusCode.Forbidden, forgotResponse.StatusCode);

            using var quickConnect = new HttpRequestMessage(HttpMethod.Post, "/QuickConnect/Initiate");
            quickConnect.Headers.TryAddWithoutValidation(AuthHelper.AuthHeaderName, AuthHelper.DummyAuthHeader);
            using var quickConnectResponse = await client.SendAsync(quickConnect, ct);
            Assert.Equal(HttpStatusCode.OK, quickConnectResponse.StatusCode);

            var css = await client.GetStringAsync("/Branding/Css", ct);
            Assert.Contains("#loginPage .manualLoginForm", css, StringComparison.Ordinal);
        }

        /// <summary>
        /// The application factory with <c>JELLYFIN_OIDC_DISABLE_PASSWORD_LOGIN=true</c> (set through DI, so the
        /// process environment of other tests is untouched).
        /// </summary>
        public sealed class Factory : JellyfinApplicationFactory
        {
            /// <inheritdoc/>
            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                base.ConfigureWebHost(builder);
                builder.ConfigureTestServices(services => services.AddSingleton(new OidcOptions { DisablePasswordLogin = true }));
            }
        }
    }
}
