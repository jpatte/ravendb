using System;
using System.Net;
using System.Text;
using Raven.Abstractions.Data;
using Raven.Imports.Newtonsoft.Json;
using Raven.Client;
using Raven.Database.Config;
using Raven.Database.Extensions;
using Raven.Database.Server;
using Raven.Database.Server.Security.OAuth;
using Raven.Json.Linq;
using Raven.Server;
using Xunit;
using System.Linq;

namespace Raven.Tests.Security.OAuth
{

	public class AccessTokenAuthentication : RemoteClientTest, IDisposable
	{
		readonly string path;
		const string relativeUrl = "/docs";
		const string baseUrl = "http://localhost";
		const int port = 8079;

		public AccessTokenAuthentication()
		{
			path = GetPath("TestDb");
			NonAdminHttp.EnsureCanListenToWhenInNonAdminContext(8079);
		}

		protected override void ModifyConfiguration(InMemoryRavenConfiguration ravenConfiguration)
		{
			ravenConfiguration.AnonymousUserAccessMode = AnonymousUserAccessMode.None;
		}

		protected override void CreateDefaultIndexes(IDocumentStore documentStore)
		{
			// Do not create the default index "RavenDocumentsByEntityName".
		}

		public override void Dispose()
		{
			IOExtensions.DeleteDirectory(path);
			base.Dispose();
		}

		public string GetAccessToken(RavenDbServer server, string user = "jsmith", string databases = "*", bool valid = true, bool expired = false)
		{
			var issued = (DateTime.UtcNow - DateTime.MinValue).TotalMilliseconds;

			if (expired) issued -= TimeSpan.FromHours(1).TotalMilliseconds;

			var authorizedDatabases = databases.Split(',').Select(tenantId=> new DatabaseAccess{TenantId = tenantId}).ToList();
			var body = RavenJObject.FromObject(new AccessTokenBody { UserId = user, AuthorizedDatabases = authorizedDatabases, Issued = issued }).ToString(Formatting.None);

			var signature = valid ? AccessToken.Sign(body, server.Database.Configuration.OAuthTokenKey) : "InvalidSignature";

			var token = RavenJObject.FromObject(new { Body = body, Signature = signature }).ToString(Formatting.None);

			return token;
		}

		static HttpWebRequest GetNewWebRequest()
		{
			return ((HttpWebRequest)WebRequest.Create(baseUrl + ":" + port + relativeUrl));
		}

		[Fact]
		public void RequestsWithAValidAccessTokenShouldBeAccepted()
		{
			using (var server = GetNewServer())
			{
				var token = GetAccessToken(server, databases: "<system>");

				var request = GetNewWebRequest()
					.WithBearerTokenAuthorization(token);

				using (var response = request.MakeRequest())
				{
					Assert.Equal(HttpStatusCode.OK, response.StatusCode);
				}
			}
		}

		[Fact]
		public void RequestsWithoutAnAccessTokenShouldBeRejected()
		{
			var request = GetNewWebRequest();

			using (var server = GetNewServer())
			using (var response = request.MakeRequest())
			{
				Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
			}
		}

		[Fact]
		public void RequestsWithAnInvalidAccessTokenShouldBeRejected()
		{

			using (var server = GetNewServer())
			{
				var token = GetAccessToken(server, valid: false);

				var request = GetNewWebRequest()
					.WithBearerTokenAuthorization(token);

				using (var response = request.MakeRequest())
				{
					Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
					var challenge = response.Headers["WWW-Authenticate"];
					Assert.NotEmpty(challenge);
					Assert.True(challenge.StartsWith("Bearer"));
					Assert.Contains("error=\"invalid_token\"", challenge);
				}
			}
		}

		[Fact]
		public void RequestsWithAnExpiredAccessTokenShouldBeRejected()
		{

			using (var server = GetNewServer())
			{
				var token = GetAccessToken(server, expired: true);

				var request = GetNewWebRequest()
					.WithBearerTokenAuthorization(token);
				using (var response = request.MakeRequest())
				{
					Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
					var challenge = response.Headers["WWW-Authenticate"];
					Assert.NotEmpty(challenge);
					Assert.True(challenge.StartsWith("Bearer"));
					Assert.Contains("error=\"invalid_token\"", challenge);
				}
			}
		}

		[Fact]
		public void RequestsWithAnAccessTokenThatDoesNotHaveTheNeededScopeShouldBeRejected()
		{
			using (var server = GetNewServer())
			{
				var token = GetAccessToken(server, databases: "");

				var request = GetNewWebRequest()
					.WithBearerTokenAuthorization(token);

				using (var response = request.MakeRequest())
				{


					Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
					var challenge = response.Headers["WWW-Authenticate"];
					Assert.NotEmpty(challenge);
					Assert.True(challenge.StartsWith("Bearer"));
					Assert.Contains("error=\"insufficient_scope\"", challenge);
				}
			}
		}
	}
}