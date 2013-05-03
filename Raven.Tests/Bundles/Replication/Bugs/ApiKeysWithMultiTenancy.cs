﻿// -----------------------------------------------------------------------
//  <copyright file="ApiKeysWithMultiTenancy.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------
using System.Collections.Generic;
using Raven.Abstractions.Data;
using Raven.Client.Document;
using Raven.Database.Server;
using Raven.Json.Linq;
using Raven.Tests.Bugs;
using Xunit;
using Raven.Client.Extensions;

namespace Raven.Tests.Bundles.Replication.Bugs
{
	public class ApiKeysWithMultiTenancy : ReplicationBase
	{
		private const string apikey = "test/ThisIsMySecret";

		protected override void ConfigureServer(Database.Config.RavenConfiguration serverConfiguration)
		{
			serverConfiguration.AnonymousUserAccessMode = AnonymousUserAccessMode.None;
		}

		protected override void ConfigureDatbase(Database.DocumentDatabase database)
		{
			database.Put("Raven/ApiKeys/test", null, RavenJObject.FromObject(new ApiKeyDefinition
			{
				Name = "test",
				Secret = "ThisIsMySecret",
				Enabled = true,
				Databases = new List<DatabaseAccess>
				{
					new DatabaseAccess {TenantId = "*", Admin = true},
					new DatabaseAccess {TenantId = Constants.SystemDatabase, Admin = true},
				}
			}), new RavenJObject(), null);
		}

		[Fact]
		public void CanReplicationToChildDbsUsingApiKeys()
		{
			var store1 = CreateStore(configureStore: store =>
			{
				store.ApiKey = apikey;
				store.Conventions.FailoverBehavior=FailoverBehavior.FailImmediately;
			});
			var store2 = CreateStore(configureStore: store =>
			{
				store.ApiKey = apikey;
				store.Conventions.FailoverBehavior = FailoverBehavior.FailImmediately;
			});


			store1.DatabaseCommands.CreateDatabase(new DatabaseDocument
			{
				Id = "repl",
				Settings =
				{
					{"Raven/RunInMemory", "true"},
					{"Raven/DataDir", "~/Databases/db1"},
					{"Raven/ActiveBundles", "Replication"}
				}
			});
			store2.DatabaseCommands.CreateDatabase(new DatabaseDocument
			{
				Id = "repl",
				Settings =
				{
					{"Raven/RunInMemory", "true"},
					{"Raven/DataDir", "~/Databases/db2"},
					{"Raven/ActiveBundles", "Replication"}
				}
			});

			RunReplication(store1, store2, apiKey: apikey, db: "repl");

			using (var s = store1.OpenSession("repl"))
			{
				s.Store(new AccurateCount.User());
				s.SaveChanges();
			}

			WaitForReplication(store2, "users/1", db: "repl");
		}
	}
}