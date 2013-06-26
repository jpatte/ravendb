using System;
using System.Collections.Generic;
using System.Security.Principal;
using System.Linq;
using Raven.Abstractions.Data;
using Raven.Database.Server.Abstractions;
using Raven.Database.Extensions;

namespace Raven.Database.Server.Security.OAuth
{
	public class OAuthRequestAuthorizer : AbstractRequestAuthorizer
	{
		public bool Authorize(IHttpContext ctx, bool hasApiKey, bool ignoreDbAccess)
		{
			var httpRequest = ctx.Request;

			var isGetRequest = IsGetRequest(httpRequest.HttpMethod, httpRequest.Url.AbsolutePath);
			var allowUnauthenticatedUsers = // we need to auth even if we don't have to, for bundles that want the user 
				Settings.AnonymousUserAccessMode == AnonymousUserAccessMode.All ||
				Settings.AnonymousUserAccessMode == AnonymousUserAccessMode.Admin || 
			        Settings.AnonymousUserAccessMode == AnonymousUserAccessMode.Get &&
			        isGetRequest;

			var token = GetToken(ctx);
			
			if (token == null)
			{
				if (allowUnauthenticatedUsers)
					return true;

				WriteAuthorizationChallenge(ctx, hasApiKey ? 412 : 401, "invalid_request", "The access token is required");
				
				return false;
			}

			AccessTokenBody tokenBody;
			if (!AccessToken.TryParseBody(Settings.OAuthTokenKey, token, out tokenBody))
			{
				if (allowUnauthenticatedUsers)
					return true;
				WriteAuthorizationChallenge(ctx, 401, "invalid_token", "The access token is invalid");

				return false;
			}

			if (tokenBody.IsExpired())
			{
				if (allowUnauthenticatedUsers)
					return true;
				WriteAuthorizationChallenge(ctx, 401, "invalid_token", "The access token is expired");

				return false;
			}

			var writeAccess = isGetRequest == false;
			if(!tokenBody.IsAuthorized(TenantId, writeAccess))
			{
				if (allowUnauthenticatedUsers || ignoreDbAccess)
					return true;

				WriteAuthorizationChallenge(ctx, 403, "insufficient_scope", 
					writeAccess ?
					"Not authorized for read/write access for tenant " + TenantId :
					"Not authorized for tenant " + TenantId);
	   
				return false;
			}
			
			ctx.User = new OAuthPrincipal(tokenBody, TenantId);
			CurrentOperationContext.Headers.Value[Constants.RavenAuthenticatedUser] = tokenBody.UserId;
			CurrentOperationContext.User.Value = ctx.User;
			return true;
		}

		public List<string> GetApprovedDatabases(IPrincipal user)
		{
			var oAuthUser = user as OAuthPrincipal;
			if (oAuthUser == null)
				return new List<string>();
			return oAuthUser.GetApprovedDatabases();
		}

		public override void Dispose()
		{
			
		}

		static string GetToken(IHttpContext ctx)
		{
			const string bearerPrefix = "Bearer ";

			var auth = ctx.Request.Headers["Authorization"];
			if(auth == null)
			{
				auth = ctx.Request.GetCookie("OAuth-Token");
				if (auth != null)
					auth = Uri.UnescapeDataString(auth);
			}
			if (auth == null || auth.Length <= bearerPrefix.Length ||
				!auth.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
				return null;

			var token = auth.Substring(bearerPrefix.Length, auth.Length - bearerPrefix.Length);
			
			return token;
		}

		void WriteAuthorizationChallenge(IHttpContext ctx, int statusCode, string error, string errorDescription)
		{
			if (string.IsNullOrEmpty(Settings.OAuthTokenServer) == false)
			{
				if (Settings.UseDefaultOAuthTokenServer == false)
				{
					ctx.Response.AddHeader("OAuth-Source", Settings.OAuthTokenServer);
				}
				else
				{
					ctx.Response.AddHeader("OAuth-Source", new UriBuilder(Settings.OAuthTokenServer)
					{
						Host = ctx.Request.Url.Host,
						Port = ctx.Request.Url.Port
					}.Uri.ToString());
			
				}
			}
			ctx.Response.StatusCode = statusCode;
			ctx.Response.AddHeader("WWW-Authenticate", string.Format("Bearer realm=\"Raven\", error=\"{0}\",error_description=\"{1}\"", error, errorDescription));
		}

		public IPrincipal GetUser(IHttpContext ctx, bool hasApiKey)
		{
			var token = GetToken(ctx);

			if (token == null)
			{
				WriteAuthorizationChallenge(ctx, hasApiKey ? 412 : 401, "invalid_request", "The access token is required");

				return null;
			}

			AccessTokenBody tokenBody;
			if (!AccessToken.TryParseBody(Settings.OAuthTokenKey, token, out tokenBody))
			{
				WriteAuthorizationChallenge(ctx, 401, "invalid_token", "The access token is invalid");

				return null;
			}

			return new OAuthPrincipal(tokenBody, null);
		}
	}

	public class OAuthPrincipal : IPrincipal, IIdentity
	{
		private readonly AccessTokenBody tokenBody;
		private readonly string tenantId;

		public OAuthPrincipal(AccessTokenBody tokenBody, string tenantId)
		{
			this.tokenBody = tokenBody;
			this.tenantId = tenantId;
		}

		public bool IsInRole(string role)
		{
			if ("Administrators".Equals(role, StringComparison.InvariantCultureIgnoreCase) == false)
				return false;

			var databaseAccess = tokenBody.AuthorizedDatabases
				.Where(x=>
					string.Equals(x.TenantId, tenantId, StringComparison.InvariantCultureIgnoreCase) ||
					x.TenantId == "*");

			return databaseAccess.Any(access => access.Admin);
		}

		public IIdentity Identity
		{
			get { return this; }
		}

		public string Name
		{
			get { return tokenBody.UserId; }
		}

		public string AuthenticationType
		{
			get { return "OAuth"; }
		}

		public bool IsAuthenticated
		{
			get { return true; }
		}

		public List<string> GetApprovedDatabases()
		{
			return tokenBody.AuthorizedDatabases.Select(access => access.TenantId).ToList();
		}

		public AccessTokenBody TokenBody
		{
			get { return tokenBody; }
		}
	}
}
