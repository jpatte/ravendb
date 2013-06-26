using System;
using System.Collections.Generic;
using System.Linq;
using Raven.Abstractions.Data;
using Raven.Bundles.Replication.Plugins;
using Raven.Database.Impl;
using Raven.Json.Linq;

namespace Raven.Bundles.Replication.Responders
{
	public class DocumentReplicationBehavior : SingleItemReplicationBehavior<JsonDocument, RavenJObject>
	{
		public IEnumerable<AbstractDocumentReplicationConflictResolver> ReplicationConflictResolvers { get; set; }

		protected override DocumentChangeTypes ReplicationConflict
		{
			get { return DocumentChangeTypes.ReplicationConflict; }
		}

		protected override void DeleteItem(string id, Guid etag)
		{
			Database.Delete(id, etag, null);
		}

		protected override void MarkAsDeleted(string id, RavenJObject metadata)
		{
			Actions.Lists.Set(Constants.RavenReplicationDocsTombstones, id, metadata,UuidType.Documents);
		}

		protected override void AddWithoutConflict(string id, Guid? etag, RavenJObject metadata, RavenJObject incoming)
		{
			Database.Put(id, etag, incoming, metadata, null);
		}

		protected override void CreateConflict(string id, string newDocumentConflictId, 
			string existingDocumentConflictId, JsonDocument existingItem, RavenJObject existingMetadata)
		{
			existingMetadata.Add(Constants.RavenReplicationConflict, true);
			Actions.Documents.AddDocument(existingDocumentConflictId, Guid.Empty, existingItem.DataAsJson, existingItem.Metadata);
			var etag = existingMetadata.Value<bool>(Constants.RavenDeleteMarker) ? Guid.Empty : existingItem.Etag;
			Actions.Lists.Remove(Constants.RavenReplicationDocsTombstones, id);
			Actions.Documents.AddDocument(id, etag,
			                                              new RavenJObject
			                                              {
			                              	{"Conflicts", new RavenJArray(existingDocumentConflictId, newDocumentConflictId)}
			                                              },
			                                              new RavenJObject
			                                              {
				                                              {Constants.RavenReplicationConflict, true},
				                                              {"@Http-Status-Code", 409},
				                                              {"@Http-Status-Description", "Conflict"}
			                                              });
		}

		protected override void AppendToCurrentItemConflicts(string id, string newConflictId, RavenJObject existingMetadata, JsonDocument existingItem)
		{
			// just update the current doc with the new conflict document
			RavenJArray ravenJArray ;
			existingItem.DataAsJson["Conflicts"] =
				ravenJArray = new RavenJArray(existingItem.DataAsJson.Value<RavenJArray>("Conflicts"));
			ravenJArray.Add(RavenJToken.FromObject(newConflictId));
			Actions.Documents.AddDocument(id, existingItem.Etag, existingItem.DataAsJson, existingItem.Metadata);
		}

		protected override RavenJObject TryGetExisting(string id, out JsonDocument existingItem, out Guid existingEtag, out bool deleted)
		{
			var existingDoc = Actions.Documents.DocumentByKey(id, null);
			if(existingDoc != null)
			{
				existingItem = existingDoc;
				existingEtag = existingDoc.Etag.Value;
				deleted = false;
				return existingDoc.Metadata;
			}

			var listItem = Actions.Lists.Read(Constants.RavenReplicationDocsTombstones, id);
			if(listItem != null)
			{
				existingEtag = listItem.Etag;
				deleted = true;
				existingItem = new JsonDocument
				{
					Etag = listItem.Etag,
					DataAsJson = new RavenJObject(),
					Key = listItem.Key,
					Metadata = listItem.Data
				};
				return listItem.Data;
			}
			existingEtag = Guid.Empty;
			existingItem = null;
			deleted = false;
			return null;

		}

		protected override bool TryResolveConflict(string id, RavenJObject metadata, RavenJObject document, JsonDocument existing)
		{
			return ReplicationConflictResolvers.Any(
					replicationConflictResolver => replicationConflictResolver.TryResolve(id, metadata, document, existing, key => Actions.Documents.DocumentByKey(key, null)));
		}
	}
}
