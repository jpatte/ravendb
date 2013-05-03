﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Raven.Abstractions.Connection;
using System.Linq;
using Raven.Abstractions.Extensions;
using Raven.Abstractions.Util;
#if SILVERLIGHT
using System.Net.Browser;
using Raven.Client.Changes;

#endif

namespace Raven.Abstractions.OAuth
{
	public class SecuredAuthenticator : AbstractAuthenticator
	{
		private readonly string apiKey;

		public SecuredAuthenticator(string apiKey)
		{
			this.apiKey = apiKey;
		}

		public override void ConfigureRequest(object sender, WebRequestEventArgs e)
		{
			if (CurrentOauthToken != null)
			{
				base.ConfigureRequest(sender, e);
				return;
			}
			if (apiKey != null)
			{
				e.Request.Headers["Has-Api-Key"] = "true";
			}
		}

		private Tuple<HttpWebRequest, string> PrepareOAuthRequest(string oauthSource, string serverRSAExponent, string serverRSAModulus, string challenge)
		{
#if !SILVERLIGHT
			var authRequest = (HttpWebRequest)WebRequest.Create(oauthSource);
#else
			var authRequest = (HttpWebRequest)WebRequestCreator.ClientHttp.Create(new Uri(oauthSource));
#endif
			authRequest.Headers["grant_type"] = "client_credentials";
			authRequest.Accept = "application/json;charset=UTF-8";
			authRequest.Method = "POST";

			if (!string.IsNullOrEmpty(serverRSAExponent) && !string.IsNullOrEmpty(serverRSAModulus) && !string.IsNullOrEmpty(challenge))
			{
				var exponent = OAuthHelper.ParseBytes(serverRSAExponent);
				var modulus = OAuthHelper.ParseBytes(serverRSAModulus);

				var apiKeyParts = apiKey.Split(new[] { '/' }, StringSplitOptions.None);

				if (apiKeyParts.Length > 2)
				{
					apiKeyParts[1] = string.Join("/", apiKeyParts.Skip(1));
				}

				if (apiKeyParts.Length < 2)
					throw new InvalidOperationException("Invalid API key");

				var apiKeyName = apiKeyParts[0].Trim();
				var apiSecret = apiKeyParts[1].Trim();


				var data = OAuthHelper.DictionaryToString(new Dictionary<string, string>
				{
					{OAuthHelper.Keys.RSAExponent, serverRSAExponent},
					{OAuthHelper.Keys.RSAModulus, serverRSAModulus},
					{
						OAuthHelper.Keys.EncryptedData,
						OAuthHelper.EncryptAsymmetric(exponent, modulus, OAuthHelper.DictionaryToString(new Dictionary<string, string>
						{
							{OAuthHelper.Keys.APIKeyName, apiKeyName},
							{OAuthHelper.Keys.Challenge, challenge},
							{
								OAuthHelper.Keys.Response,
								OAuthHelper.Hash(string.Format(OAuthHelper.Keys.ResponseFormat, challenge, apiSecret))
							}
						}))
					}
				});

				return Tuple.Create(authRequest, data);
			}
			authRequest.ContentLength = 0;
			return Tuple.Create(authRequest, (string)null);
		}


#if !SILVERLIGHT
		public override Action<HttpWebRequest> DoOAuthRequest(string oauthSource)
		{
			string serverRSAExponent = null;
			string serverRSAModulus = null;
			string challenge = null;

			// Note that at two tries will be needed in the normal case.
			// The first try will get back a challenge,
			// the second try will try authentication. If something goes wrong server-side though
			// (e.g. the server was just rebooted or the challenge timed out for some reason), we
			// might get a new challenge back, so we try a third time just in case.
			int tries = 0;
			while (true)
			{
				tries++;
				var authRequestTuple = PrepareOAuthRequest(oauthSource, serverRSAExponent, serverRSAModulus, challenge);
				var authRequest = authRequestTuple.Item1;
				if (authRequestTuple.Item2 != null)
				{
					using (var stream = authRequest.GetRequestStream())
					using (var writer = new StreamWriter(stream))
					{
						writer.Write(authRequestTuple.Item2);
					}
				}

				try
				{
					using (var authResponse = authRequest.GetResponse())
					using (var stream = authResponse.GetResponseStreamWithHttpDecompression())
					using (var reader = new StreamReader(stream))
					{
						CurrentOauthToken = "Bearer " + reader.ReadToEnd();
						return (Action<HttpWebRequest>)(request => SetHeader(request.Headers, "Authorization", CurrentOauthToken));
					}
				}
				catch (WebException ex)
				{
					if (tries > 2)
						// We've already tried three times and failed
						throw;

					var authResponse = ex.Response as HttpWebResponse;
					if (authResponse == null || authResponse.StatusCode != HttpStatusCode.PreconditionFailed)
						throw;

					var header = authResponse.Headers[HttpResponseHeader.WwwAuthenticate];
					if (string.IsNullOrEmpty(header) || !header.StartsWith(OAuthHelper.Keys.WWWAuthenticateHeaderKey))
						throw;

					authResponse.Close();

					var challengeDictionary = OAuthHelper.ParseDictionary(header.Substring(OAuthHelper.Keys.WWWAuthenticateHeaderKey.Length).Trim());
					serverRSAExponent = challengeDictionary.GetOrDefault(OAuthHelper.Keys.RSAExponent);
					serverRSAModulus = challengeDictionary.GetOrDefault(OAuthHelper.Keys.RSAModulus);
					challenge = challengeDictionary.GetOrDefault(OAuthHelper.Keys.Challenge);

					if (string.IsNullOrEmpty(serverRSAExponent) || string.IsNullOrEmpty(serverRSAModulus) || string.IsNullOrEmpty(challenge))
					{
						throw new InvalidOperationException("Invalid response from server, could not parse raven authentication information: " + header);
					}
				}
			}
		}
#endif

		public Task<Action<HttpWebRequest>> DoOAuthRequestAsync(string baseUrl, string oauthSource)
		{
			return DoOAuthRequestAsync(baseUrl, oauthSource, null, null, null, 0);
		}

		private Task<Action<HttpWebRequest>> DoOAuthRequestAsync(string baseUrl, string oauthSource, string serverRsaExponent, string serverRsaModulus, string challenge, int tries)
		{
			if (oauthSource == null) throw new ArgumentNullException("oauthSource");

			var authRequestTuple = PrepareOAuthRequest(oauthSource, serverRsaExponent, serverRsaModulus, challenge);
			var authRequest = authRequestTuple.Item1;

			Task sendDataTask = new CompletedTask();
			if (authRequestTuple.Item2 != null)
			{
				sendDataTask = Task<Stream>.Factory.FromAsync(authRequest.BeginGetRequestStream, authRequest.EndGetRequestStream, null).ContinueWith(task =>
				{
					using (var stream = task.Result)
					using (var writer = new StreamWriter(stream))
					{
						writer.Write(authRequestTuple.Item2);
					}
				});
			}


			return sendDataTask.ContinueWith(t =>
			{
				t.Wait();

				return Task<WebResponse>.Factory.FromAsync(authRequest.BeginGetResponse, authRequest.EndGetResponse, null)
					.AddUrlIfFaulting(authRequest.RequestUri)
					.ConvertSecurityExceptionToServerNotFound()
					.ContinueWith(task =>
					{
						try
						{
							using (var stream = task.Result.GetResponseStreamWithHttpDecompression())
							using (var reader = new StreamReader(stream))
							{
								var currentOauthToken = "Bearer " + reader.ReadToEnd();
								CurrentOauthToken = currentOauthToken;
#if SILVERLIGHT
								BrowserCookieToAllowUserToUseStandardRequests(baseUrl, currentOauthToken);
#endif
								return
									CompletedTask.With(
										(Action<HttpWebRequest>)(request => SetHeader(request.Headers, "Authorization", CurrentOauthToken)));
							}
						}
						catch (AggregateException ae)
						{
							var ex = ae.ExtractSingleInnerException() as WebException;

							if (tries > 2 || ex == null)
								// We've already tried three times and failed
								throw;



							var authResponse = ex.Response as HttpWebResponse;
							if (authResponse == null || authResponse.StatusCode != HttpStatusCode.PreconditionFailed)
								throw;

							var header = authResponse.Headers["Www-Authenticate"];
							if (string.IsNullOrEmpty(header) || !header.StartsWith(OAuthHelper.Keys.WWWAuthenticateHeaderKey))
								throw;

							authResponse.Close();

							var challengeDictionary =
								OAuthHelper.ParseDictionary(header.Substring(OAuthHelper.Keys.WWWAuthenticateHeaderKey.Length).Trim());

							return DoOAuthRequestAsync(baseUrl, oauthSource,
													   challengeDictionary.GetOrDefault(OAuthHelper.Keys.RSAExponent),
													   challengeDictionary.GetOrDefault(OAuthHelper.Keys.RSAModulus),
													   challengeDictionary.GetOrDefault(OAuthHelper.Keys.Challenge),
													   tries + 1);
						}
					}).Unwrap();
			}).Unwrap();
		}
#if SILVERLIGHT
		private void BrowserCookieToAllowUserToUseStandardRequests(string baseUrl, string currentOauthToken)
		{
			var webRequest = WebRequestCreator.BrowserHttp.Create(new Uri(baseUrl + "/OAuth/Cookie"));
			webRequest.Headers["Authorization"] = currentOauthToken;
			webRequest.Headers["Has-Api-Key"] = "True";
			webRequest.Method = "POST";
			webRequest.BeginGetResponse(ar =>
			{
				try
				{
					using (webRequest.EndGetResponse(ar))
					{

					}
				}
				catch (WebException we)
				{
					Debug.WriteLine("Failed to set browser cookie: " + we.Message);
					using(we.Response)
					{
						
					}
				}
				catch (Exception e)
				{
					Debug.WriteLine("Failed to set browser cookie: " + e.Message);
				}
			}, null);
		}
#endif
	}
}