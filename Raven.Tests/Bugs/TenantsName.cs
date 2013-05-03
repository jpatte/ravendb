using System;
using System.Net;
using Raven.Client.Document;
using Raven.Client.Extensions;
using Raven.Database.Config;
using Raven.Json.Linq;
using Xunit;

namespace Raven.Tests.Bugs
{
	public class TenantsName : RavenTest
	{
		protected override void ModifyConfiguration(InMemoryRavenConfiguration ravenConfiguration)
		{
			ravenConfiguration.RunInMemory = false;
			ravenConfiguration.DefaultStorageTypeName = "esent";
		}

		[Fact]
		public void CannotContainSpaces()
		{
			using (GetNewServer())
			using (var documentStore = new DocumentStore { Url = "http://localhost:8079" }.Initialize())
			{
				const string tenantName = "   Tenant with some    spaces     in it ";
				// TODO: we better throw here.
				Assert.Throws<InvalidOperationException>(() => documentStore.DatabaseCommands.EnsureDatabaseExists(tenantName));	
				
				var databaseCommands = documentStore.DatabaseCommands.ForDatabase(tenantName);
				// TODO: we better throw here with a better error message than "tenant not found".
				var webException = Assert.Throws<WebException>(() => databaseCommands.Put("posts/", null, new RavenJObject(), new RavenJObject()));
				Assert.Equal(HttpStatusCode.NotFound,((HttpWebResponse)webException.Response).StatusCode);
			}
		}
	}
}
