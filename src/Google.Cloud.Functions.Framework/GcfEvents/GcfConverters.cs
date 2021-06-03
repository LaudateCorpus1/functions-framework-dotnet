﻿// Copyright 2020, Google LLC
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     https://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using CloudNative.CloudEvents;
using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Google.Cloud.Functions.Framework.GcfEvents
{
    /// <summary>
    /// Conversions from GCF events to CloudEvents.
    /// </summary>
    internal static class GcfConverters
    {
        private static class Services
        {
            internal const string FirebaseDatabase = "firebasedatabase.googleapis.com";
            internal const string FirebaseAuth = "firebaseauth.googleapis.com";
            internal const string FirebaseRemoteConfig = "firebaseremoteconfig.googleapis.com";
            internal const string FirebaseAnalytics = "firebaseanalytics.googleapis.com";
            internal const string Firestore = "firestore.googleapis.com";
            internal const string PubSub = "pubsub.googleapis.com";
            internal const string Storage = "storage.googleapis.com";
        }

        private static class EventTypes
        {
            private const string FirestoreDocumentV1 = "google.cloud.firestore.document.v1";
            internal const string FirestoreDocumentCreated = FirestoreDocumentV1 + ".created";
            internal const string FirestoreDocumentUpdated = FirestoreDocumentV1 + ".updated";
            internal const string FirestoreDocumentDeleted = FirestoreDocumentV1 + ".deleted";
            internal const string FirestoreDocumentWritten = FirestoreDocumentV1 + ".written";

            private const string FirebaseDatabaseDocumentV1 = "google.firebase.database.document.v1";
            internal const string FirebaseDatabaseDocumentCreated = FirebaseDatabaseDocumentV1 + ".created";
            internal const string FirebaseDatabaseDocumentUpdated = FirebaseDatabaseDocumentV1 + ".updated";
            internal const string FirebaseDatabaseDocumentDeleted = FirebaseDatabaseDocumentV1 + ".deleted";
            internal const string FirebaseDatabaseDocumentWritten = FirebaseDatabaseDocumentV1 + ".written";

            private const string FirebaseAuthUserV1 = "google.firebase.auth.user.v1";
            internal const string FirebaseAuthUserCreated = FirebaseAuthUserV1 + ".created";
            internal const string FirebaseAuthUserDeleted = FirebaseAuthUserV1 + ".deleted";

            internal const string FirebaseRemoteConfigUpdated = "google.firebase.remoteconfig.remoteConfig.v1.updated";

            internal const string FirebaseAnalyticsLogWritten = "google.firebase.analytics.log.v1.written";

            internal const string PubSubMessagePublished = "google.cloud.pubsub.topic.v1.messagePublished";

            private const string StorageObjectV1 = "google.cloud.storage.object.v1";
            internal const string StorageObjectFinalized = StorageObjectV1 + ".finalized";
            internal const string StorageObjectArchived = StorageObjectV1 + ".archived";
            internal const string StorageObjectDeleted = StorageObjectV1 + ".deleted";
            internal const string StorageObjectMetadataUpdated = StorageObjectV1 + ".metadataUpdated";

            // Note: this isn't documented in google-cloudevents; it's from the legacy Storage change notification.
            internal const string StorageObjectChanged = StorageObjectV1 + ".changed";
        }

        private const string JsonContentType = "application/json";

        private static readonly Dictionary<string, EventAdapter> s_eventTypeMapping = new Dictionary<string, EventAdapter>
        {
            { "google.pubsub.topic.publish", new PubSubEventAdapter(EventTypes.PubSubMessagePublished) },
            { "google.storage.object.finalize", new StorageEventAdapter(EventTypes.StorageObjectFinalized) },
            { "google.storage.object.delete", new StorageEventAdapter(EventTypes.StorageObjectDeleted) },
            { "google.storage.object.archive", new StorageEventAdapter(EventTypes.StorageObjectArchived) },
            { "google.storage.object.metadataUpdate", new StorageEventAdapter(EventTypes.StorageObjectMetadataUpdated) },
            { "providers/cloud.firestore/eventTypes/document.write",
                new FirestoreFirebaseDocumentEventAdapter(EventTypes.FirestoreDocumentWritten, Services.Firestore) },
            { "providers/cloud.firestore/eventTypes/document.create",
                new FirestoreFirebaseDocumentEventAdapter(EventTypes.FirestoreDocumentCreated, Services.Firestore) },
            { "providers/cloud.firestore/eventTypes/document.update",
                new FirestoreFirebaseDocumentEventAdapter(EventTypes.FirestoreDocumentUpdated, Services.Firestore) },
            { "providers/cloud.firestore/eventTypes/document.delete",
                new FirestoreFirebaseDocumentEventAdapter(EventTypes.FirestoreDocumentDeleted, Services.Firestore) },
            { "providers/firebase.auth/eventTypes/user.create",
                new FirebaseAuthEventAdapter(EventTypes.FirebaseAuthUserCreated, Services.FirebaseAuth) },
            { "providers/firebase.auth/eventTypes/user.delete",
                new FirebaseAuthEventAdapter(EventTypes.FirebaseAuthUserDeleted, Services.FirebaseAuth) },
            { "providers/google.firebase.analytics/eventTypes/event.log",
                new FirebaseAnalyticsEventAdapter(EventTypes.FirebaseAnalyticsLogWritten, Services.FirebaseAnalytics) },
            { "providers/google.firebase.database/eventTypes/ref.create",
                new FirestoreFirebaseDocumentEventAdapter(EventTypes.FirebaseDatabaseDocumentCreated, Services.FirebaseDatabase) },
            { "providers/google.firebase.database/eventTypes/ref.write",
                new FirestoreFirebaseDocumentEventAdapter(EventTypes.FirebaseDatabaseDocumentWritten, Services.FirebaseDatabase) },
            { "providers/google.firebase.database/eventTypes/ref.update",
                new FirestoreFirebaseDocumentEventAdapter(EventTypes.FirebaseDatabaseDocumentUpdated, Services.FirebaseDatabase) },
            { "providers/google.firebase.database/eventTypes/ref.delete",
                new FirestoreFirebaseDocumentEventAdapter(EventTypes.FirebaseDatabaseDocumentDeleted, Services.FirebaseDatabase) },
            { "providers/cloud.pubsub/eventTypes/topic.publish", new PubSubEventAdapter(EventTypes.PubSubMessagePublished) },
            { "providers/cloud.storage/eventTypes/object.change", new StorageEventAdapter(EventTypes.StorageObjectChanged) },
            { "google.firebase.remoteconfig.update", new EventAdapter(EventTypes.FirebaseRemoteConfigUpdated, Services.FirebaseRemoteConfig) },
        };

        internal static async Task<CloudEvent> ConvertGcfEventToCloudEvent(HttpRequest request, CloudEventFormatter formatter)
        {
            var jsonRequest = await ParseRequest(request);
            // Validated as not null or empty in ParseRequest
            string gcfType = jsonRequest.Context.Type!;
            if (!s_eventTypeMapping.TryGetValue(gcfType, out var eventAdapter))
            {
                throw new ConversionException($"Unexpected event type for function: '{gcfType}'");
            }
            return eventAdapter.ConvertToCloudEvent(jsonRequest, formatter);
        }

        private static async Task<Request> ParseRequest(HttpRequest request)
        {
            Request parsedRequest;
            if (request.ContentType != JsonContentType)
            {
                throw new ConversionException("Unable to convert request to CloudEvent.");
            }
            try
            {
                var json = await new StreamReader(request.Body).ReadToEndAsync();
                parsedRequest = JsonSerializer.Deserialize<Request>(json) ?? throw new ConversionException("Request body parsed to null request");
            }
            catch (JsonException e)
            {
                // Note: the details of the original exception will be lost in the normal case (where it's just logged)
                // but keeping in the ConversionException is useful for debugging purposes.
                throw new ConversionException($"Error parsing GCF event: {e.Message}", e);
            }

            parsedRequest.NormalizeContext();
            if (parsedRequest.Data is null ||
                string.IsNullOrEmpty(parsedRequest.Context.Id) ||
                string.IsNullOrEmpty(parsedRequest.Context.Type))
            {
                throw new ConversionException("Event is malformed; does not contain a payload, or the event ID is missing.");
            }
            return parsedRequest;
        }

        private static string ValidateNotNullOrEmpty(string? value, string name) =>
            string.IsNullOrEmpty(value)
            ? throw new ConversionException($"Event contained no {name}")
            : value;

        /// <summary>
        /// An event-specific adapter, picked based on the event type from the GCF event.
        /// This class provides seams for event-specific changes to the data and the source/subject,
        /// but the defult implementations of the seams are reasonable for most events.
        /// </summary>
        private class EventAdapter
        {
            private readonly string _cloudEventType;
            private readonly string _defaultService;

            internal EventAdapter(string cloudEventType, string defaultService) =>
                (_cloudEventType, _defaultService) = (cloudEventType, defaultService);

            internal CloudEvent ConvertToCloudEvent(Request request, CloudEventFormatter formatter)
            {
                MaybeReshapeData(request);

                var context = request.Context;
                var resource = context.Resource;
                var service = ValidateNotNullOrEmpty(resource.Service ?? _defaultService, "service");
                var resourceName = ValidateNotNullOrEmpty(resource.Name, "resource name");

                var evt = new CloudEvent
                {
                    Type = _cloudEventType,
                    Id = context.Id,
                    Time = context.Timestamp,
                    DataContentType = JsonContentType
                };
                PopulateAttributes(evt, service, resourceName, request.Data);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(request.Data);
                formatter.DecodeBinaryModeEventData(bytes, evt);
                return evt;
            }

            protected virtual void MaybeReshapeData(Request request) { }

            /// <summary>
            /// Populate the attributes in the CloudEvent that can't always be determined directly.
            /// In particular, this method must populate the Source context attribute,
            /// should populate the Subject context attribute if it's defined for this event type,
            /// and should populate any additional extension attributes to be compatible with Eventarc.
            /// This base implementation populate the Source based on the service and resource.
            /// </summary>
            /// <param name="evt">The event to populate</param>
            /// <param name="service">The service in the request (or the default service)</param>
            /// <param name="resource">The resource name in the request</param>
            /// <param name="data">The data in the legacy request</param>
            protected virtual void PopulateAttributes(CloudEvent evt, string service, string resource, Dictionary<string, object> data)
            {
                evt.Source = new Uri($"//{service}/{resource}", UriKind.RelativeOrAbsolute);
            }
        }

        private class StorageEventAdapter : EventAdapter
        {
            private static readonly CloudEventAttribute s_bucketAttribute = CloudEventAttribute.CreateExtension("bucket", CloudEventAttributeType.String);

            // Regex to split a storage resource name into bucket and object names.
            // Note the removal of "#" followed by trailing digits at the end of the object name;
            // this represents the object generation and should not be included in the CloudEvent subject.
            private static readonly Regex StorageResourcePattern =
                new Regex(@"^(?<resource>projects/_/buckets/(?<bucket>[^/]+))/(?<subject>objects/(?<object>.*?))(?:#\d+)?$", RegexOptions.Compiled);

            internal StorageEventAdapter(string cloudEventType) : base(cloudEventType, Services.Storage)
            {
            }

            /// <summary>
            /// Split a Storage object resource name into bucket (in the source) and object (in the subject)
            /// </summary>
            protected override void PopulateAttributes(CloudEvent evt, string service, string resource, Dictionary<string, object> data)
            {
                if (StorageResourcePattern.Match(resource) is { Success: true } match)
                {
                    // Resource name up to the bucket
                    resource = match.Groups["resource"].Value;
                    // Resource name from "objects" onwards, but without the generation
                    evt.Subject = match.Groups["subject"].Value;
                    // Use the base implementation to populate the Source.
                    base.PopulateAttributes(evt, service, resource, data);
                    evt[s_bucketAttribute] = match.Groups["bucket"].Value;
                }
                // If the resource name didn't match for whatever reason, just use the default behaviour.
                base.PopulateAttributes(evt, service, resource, data);
            }
        }

        private class PubSubEventAdapter : EventAdapter
        {
            private static readonly CloudEventAttribute s_topicAttribute = CloudEventAttribute.CreateExtension("topic", CloudEventAttributeType.String);

            private static readonly Regex PubSubResourcePattern =
                new Regex(@"^projects/(?<project>[^/]+)/topics/(?<topic>[^/]+)$", RegexOptions.Compiled);

            internal PubSubEventAdapter(string cloudEventType) : base(cloudEventType, Services.PubSub)
            {
            }

            protected override void PopulateAttributes(CloudEvent evt, string service, string resource, Dictionary<string, object> data)
            {
                base.PopulateAttributes(evt, service, resource, data);
                var match = PubSubResourcePattern.Match(resource);
                if (match.Success)
                {
                    evt[s_topicAttribute] = match.Groups["topic"].Value;
                }
            }

            /// <summary>
            /// Wrap the PubSub message and copy the message ID and publication time into
            /// it, for consistency with Eventarc.
            /// </summary>
            protected override void MaybeReshapeData(Request request)
            {
                if (request.Context.Id is string id)
                {
                    request.Data["messageId"] = id;
                }
                if (request.Context.Timestamp is DateTimeOffset timestamp)
                {
                    request.Data["publishTime"] = timestamp.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
                }
                request.Data = new Dictionary<string, object> { { "message", request.Data } };
            }
        }

        /// <summary>
        /// Adapter for Firebase/Firestore events that need extra source/subject handling.
        /// The resource names for Firebase and Firestore have "refs" and "documents" as the fifth segment respectively.
        /// For example:
        /// "projects/_/instances/my-project-id/refs/gcf-test/xyz" or
        /// "projects/project-id/databases/(default)/documents/gcf-test/IH75dRdeYJKd4uuQiqch".
        /// We validate that the fifth segment has the value we expect, and then use that segment onwards as the subject.
        /// </summary>
        private class FirestoreFirebaseDocumentEventAdapter : EventAdapter
        {
            private const int SubjectStartIndex = 4; // The subject always starts at the 5th segment
            private readonly string _expectedSubjectPrefix;

            internal FirestoreFirebaseDocumentEventAdapter(string cloudEventType, string defaultService)
                : base(cloudEventType, defaultService)
            {
                string expectedSegmentName = defaultService == Services.FirebaseDatabase ? "refs" : "documents";
                _expectedSubjectPrefix = $"{expectedSegmentName}/";
            }

            // Reformat the service/resource so that the part of the resource before "documents" is in the source, but
            // "documents" onwards is in the subject.
            protected override void PopulateAttributes(CloudEvent evt, string service, string resource, Dictionary<string, object> data)
            {
                string[] resourceSegments = resource.Split('/', SubjectStartIndex + 1);
                if (resourceSegments.Length < SubjectStartIndex ||
                    !resourceSegments[SubjectStartIndex].StartsWith(_expectedSubjectPrefix, StringComparison.Ordinal))
                {
                    // This is odd... just put everything in the source as normal.
                    base.PopulateAttributes(evt, service, resource, data);
                }

                string sourcePath = string.Join('/', resourceSegments.Take(SubjectStartIndex));
                base.PopulateAttributes(evt, service, sourcePath, data);
                evt.Subject = resourceSegments[SubjectStartIndex];
            }
        }

        private class FirebaseAuthEventAdapter : EventAdapter
        {
            internal FirebaseAuthEventAdapter(string cloudEventType, string defaultService)
                : base(cloudEventType, defaultService)
            {
            }

            protected override void PopulateAttributes(CloudEvent evt, string service, string resource, Dictionary<string, object> data)
            {
                // Source is as normal
                base.PopulateAttributes(evt, service, resource, data);
                // Obtained the subject from the data if possible.
                if (data.TryGetValue("uid", out var uid) && uid is JsonElement uidElement && uidElement.ValueKind == JsonValueKind.String)
                {
                    evt.Subject = $"users/{uidElement.GetString()}";
                }
            }

            /// <summary>
            /// Rename fields within metadata: lastSignedInAt => lastSignInTime, and createdAt => createTime.
            /// </summary>
            /// <param name="request"></param>
            protected override void MaybeReshapeData(Request request)
            {
                if (request.Data.TryGetValue("metadata", out var metadata) &&
                    metadata is JsonElement metadataElement &&
                    metadataElement.ValueKind == JsonValueKind.Object)
                {
                    // JsonElement itself is read-only, but we can just replace the value with a
                    // Dictionary<string, JsonElement> that has the appropriate key/value pairs.
                    var metadataDict = new Dictionary<string, JsonElement>();
                    foreach (var item in metadataElement.EnumerateObject())
                    {
                        string name = item.Name switch
                        {
                            "lastSignedInAt" => "lastSignInTime",
                            "createdAt" => "createTime",
                            var x => x
                        };
                        metadataDict[name] = item.Value;
                    }
                    request.Data["metadata"] = metadataDict;
                }
            }
        }

        private class FirebaseAnalyticsEventAdapter : EventAdapter
        {
            internal FirebaseAnalyticsEventAdapter(string cloudEventType, string defaultService)
                : base(cloudEventType, defaultService)
            {
            }

            protected override void PopulateAttributes(CloudEvent evt, string service, string resource, Dictionary<string, object> data)
            {
                // We need the event name and the app ID in order to create subject and source like this:
                // Subject://firebaseanalytics.googleapis.com/projects/{project}/apps/{app}
                // Source: events/{eventName}
                // The eventName is available in the resource, which is of the form projects/{project-id}/events/{event-name}
                // The app name is available via the data, via userDim -> appInfo -> appId
                // If either of these isn't where we expect, we throw a ConversionException

                string[] splitResource = resource.Split('/');
                string? eventName = splitResource.Length == 4 && splitResource[0] == "projects" && splitResource[2] == "events" ? splitResource[3] : null;
                string? appId = data.TryGetValue("userDim", out var userDim) && userDim is JsonElement userDimElement &&
                    userDimElement.TryGetProperty("appInfo", out var appInfoElement) &&
                    appInfoElement.TryGetProperty("appId", out var appIdElement) &&
                    appIdElement.ValueKind == JsonValueKind.String
                    ? appIdElement.GetString() : null;

                if (eventName is object && appId is object)
                {
                    string projectId = splitResource[1]; // Definitely valid, given that we got the event name.
                    evt.Source = new Uri($"//{service}/projects/{projectId}/apps/{appId}", UriKind.RelativeOrAbsolute);
                    evt.Subject = $"events/{eventName}";
                }
                else
                {
                    throw new ConversionException("Firebase Analytics event does not contain expected data");
                }
            }
        }

        /// <summary>
        /// Exception thrown to indicate that the conversion of an HTTP request
        /// into a CloudEvent has failed. This is handled within <see cref="CloudEventAdapter{TData}"></see> and <see cref="CloudEventAdapter"/>.
        /// </summary>
        internal sealed class ConversionException : Exception
        {
            internal ConversionException(string message) : base(message)
            {
            }

            internal ConversionException(string message, Exception innerException) : base(message, innerException)
            {
            }
        }
    }
}
