﻿// -----------------------------------------------------------------------
//  <copyright file="PeriodicBackupTests.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------
using System;
using System.IO;
using System.Threading;
using Raven.Abstractions.Data;
using Raven.Abstractions.Smuggler;
using Raven.Database.Extensions;
using Raven.Database.Smuggler;
using Xunit;
using Raven.Abstractions.Extensions;

namespace Raven.Tests.Bundles.PeriodicBackups
{
	public class PeriodicBackupTests : RavenTest
	{
		protected override void ModifyConfiguration(Database.Config.InMemoryRavenConfiguration configuration)
		{
			configuration.Settings["Raven/ActiveBundles"] = "PeriodicBackup";
		}
		public class User
		{
			public string Name { get; set; }
		}

		[Fact]
		public void CanBackupToDirectory()
		{
			var backupPath = GetPath("BackupFolder");
			using (var store = NewDocumentStore())
			{
				using (var session = store.OpenSession())
				{
					session.Store(new User { Name = "oren" });
					var periodicBackupSetup = new PeriodicBackupSetup
					{
						LocalFolderName = backupPath,
						IntervalMilliseconds = 25
					};
					session.Store(periodicBackupSetup, PeriodicBackupSetup.RavenDocumentKey);

					session.SaveChanges();

				}
				SpinWait.SpinUntil(() =>
					 store.DatabaseCommands.Get(PeriodicBackupStatus.RavenDocumentKey) != null, 10000);

			}

			using (var store = NewDocumentStore())
			{
				var smugglerOptions = new SmugglerOptions
				{
					BackupPath = backupPath
				};
				var dataDumper = new DataDumper(store.DocumentDatabase, smugglerOptions);
				dataDumper.ImportData(smugglerOptions, true);

				using (var session = store.OpenSession())
				{
					Assert.Equal("oren", session.Load<User>(1).Name);
				}
			}
			IOExtensions.DeleteDirectory(backupPath);
		}

		[Fact]
		public void CanBackupToDirectory_MultipleBackups()
		{
			var backupPath = GetPath("BackupFolder");
			using (var store = NewDocumentStore())
			{
				using (var session = store.OpenSession())
				{
					session.Store(new User { Name = "oren" });
					var periodicBackupSetup = new PeriodicBackupSetup
					{
						LocalFolderName = backupPath,
						IntervalMilliseconds = 25
					};
					session.Store(periodicBackupSetup, PeriodicBackupSetup.RavenDocumentKey);

					session.SaveChanges();

				}
				SpinWait.SpinUntil(() =>
				{
					var jsonDocument = store.DatabaseCommands.Get(PeriodicBackupStatus.RavenDocumentKey);
					if (jsonDocument == null)
						return false;
					var periodicBackupStatus = jsonDocument.DataAsJson.JsonDeserialization<PeriodicBackupStatus>();
					return periodicBackupStatus.LastDocsEtag != Guid.Empty;
				});

				var etagForBackups= store.DatabaseCommands.Get(PeriodicBackupStatus.RavenDocumentKey).Etag;
				using (var session = store.OpenSession())
				{
					session.Store(new User { Name = "ayende" });
					session.SaveChanges();
				}
				SpinWait.SpinUntil(() =>
					 store.DatabaseCommands.Get(PeriodicBackupStatus.RavenDocumentKey).Etag != etagForBackups);

			}

			using (var store = NewDocumentStore())
			{
				var smugglerOptions = new SmugglerOptions
				{
					BackupPath = backupPath
				};
				var dataDumper = new DataDumper(store.DocumentDatabase, smugglerOptions);
				dataDumper.ImportData(smugglerOptions, true);

				using (var session = store.OpenSession())
				{
					Assert.Equal("oren", session.Load<User>(1).Name);
					Assert.Equal("ayende", session.Load<User>(2).Name);
				}
			}
			IOExtensions.DeleteDirectory(backupPath);
		}
	}
}
