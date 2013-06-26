﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Principal;
using Mono.CSharp;
using Raven.Abstractions;
using Raven.Abstractions.Data;
using Raven.Database.Server.Abstractions;
using Raven.Database.Server.Security.OAuth;
using Raven.Database.Server.Security.Windows;
using System.Linq;
using Raven.Database.Extensions;

namespace Raven.Database.Server.Security
{
	public class MixedModeRequestAuthorizer : AbstractRequestAuthorizer
	{
		private readonly WindowsRequestAuthorizer windowsRequestAuthorizer = new WindowsRequestAuthorizer();
		private readonly OAuthRequestAuthorizer oAuthRequestAuthorizer = new OAuthRequestAuthorizer();
		private readonly ConcurrentDictionary<string, OneTimeToken> singleUseAuthTokens = new ConcurrentDictionary<string, OneTimeToken>();

		private class OneTimeToken
		{
			private IPrincipal user;
			private IntPtr? windowsUserToken;
			public string DatabaseName { get; set; }
			public DateTime GeneratedAt { get; set; }
			public IPrincipal User
			{
				get
				{
					if (windowsUserToken != null)
					{
						return new WindowsPrincipal(new WindowsIdentity(windowsUserToken.Value));
					}
					return user;
				}
				set
				{
					var windowsPrincipal = value as WindowsPrincipal;
					if (windowsPrincipal != null)
					{
						user = null;
						windowsUserToken = ((WindowsIdentity)windowsPrincipal.Identity).Token;
						return;
					}
					windowsUserToken = null;
					user = value;
				}
			}
		}

		protected override void Initialize()
		{
			windowsRequestAuthorizer.Initialize(database, settings, tenantId, server);
			oAuthRequestAuthorizer.Initialize(database, settings, tenantId, server);
			base.Initialize();
		}

		public bool Authorize(IHttpContext context)
		{
			var requestUrl = context.GetRequestUrl();
			if ( NeverSecret.Urls.Contains(requestUrl))
				return true;

			var oneTimeToken = context.Request.Headers["Single-Use-Auth-Token"];
			if (string.IsNullOrEmpty(oneTimeToken) == false)
			{
				return AuthorizeOSingleUseAuthToken(context, oneTimeToken);
			}

			var authHeader = context.Request.Headers["Authorization"];
			var hasApiKey = "True".Equals(context.Request.Headers["Has-Api-Key"], StringComparison.CurrentCultureIgnoreCase);
			var hasOAuthTokenInCookie = context.Request.HasCookie("OAuth-Token");
			if (hasApiKey || hasOAuthTokenInCookie || 
				string.IsNullOrEmpty(authHeader) == false && authHeader.StartsWith("Bearer "))
			{
				return oAuthRequestAuthorizer.Authorize(context, hasApiKey, IgnoreDb.Urls.Contains(requestUrl));
			}
			return windowsRequestAuthorizer.Authorize(context, IgnoreDb.Urls.Contains(requestUrl));
		}

		private bool AuthorizeOSingleUseAuthToken(IHttpContext context, string token)
		{
			OneTimeToken value;
			if (singleUseAuthTokens.TryRemove(token, out value) == false)
			{
				context.SetStatusToForbidden();
				context.WriteJson(new
				{
					Error = "Unknown single use token, maybe it was already used?"
				});
				return false;
			}
			if (string.Equals(value.DatabaseName, TenantId, StringComparison.InvariantCultureIgnoreCase) == false)
			{
				context.SetStatusToForbidden();
				context.WriteJson(new
				{
					Error = "This single use token cannot be used for this database"
				}); 
				return false;
			}
			if ((SystemTime.UtcNow - value.GeneratedAt).TotalMinutes > 2.5)
			{
				context.SetStatusToForbidden();
				context.WriteJson(new
				{
					Error = "This single use token has expired"
				}); 
				return false;
			}

			if (value.User != null)
			{
				CurrentOperationContext.Headers.Value[Constants.RavenAuthenticatedUser] = value.User.Identity.Name;
			}
			CurrentOperationContext.User.Value = value.User;
			context.User = value.User;
			return true;
		}

		public IPrincipal GetUser(IHttpContext context)
		{
			var hasApiKey = "True".Equals(context.Request.Headers["Has-Api-Key"], StringComparison.CurrentCultureIgnoreCase);
			var authHeader = context.Request.Headers["Authorization"];
			var hasOAuthTokenInCookie = context.Request.HasCookie("OAuth-Token");
			if (hasApiKey || hasOAuthTokenInCookie ||
				string.IsNullOrEmpty(authHeader) == false && authHeader.StartsWith("Bearer "))
			{
				return oAuthRequestAuthorizer.GetUser(context, hasApiKey);
			}
			return windowsRequestAuthorizer.GetUser(context);
		}

		public List<string> GetApprovedDatabases(IPrincipal user, IHttpContext context)
		{
			var authHeader = context.Request.Headers["Authorization"];
			if (string.IsNullOrEmpty(authHeader) == false && authHeader.StartsWith("Bearer "))
			{
				return oAuthRequestAuthorizer.GetApprovedDatabases(user);
			}

			return windowsRequestAuthorizer.GetApprovedDatabases(user);
		}

		public override void Dispose()
		{
			windowsRequestAuthorizer.Dispose();
			oAuthRequestAuthorizer.Dispose();
		}

		public string GenerateSingleUseAuthToken(DocumentDatabase db, IPrincipal user)
		{
			var token = new OneTimeToken
			{
				DatabaseName = TenantId,
				GeneratedAt = SystemTime.UtcNow,
				User = user
			};
			var tokenString = Guid.NewGuid().ToString();

			singleUseAuthTokens.TryAdd(tokenString, token);

			if(singleUseAuthTokens.Count > 25)
			{
				foreach (var oneTimeToken in singleUseAuthTokens.Where(x => (x.Value.GeneratedAt - SystemTime.UtcNow).TotalMinutes > 5))
				{
					OneTimeToken value;
					singleUseAuthTokens.TryRemove(oneTimeToken.Key, out value);
				}
			}

			return tokenString;
		}
	}
}