using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace AtProtoUnity.Tests
{
    [Category("Unit")]
    public sealed class AtProtoSchemaTests
    {
        GameObject gameObject;
        AtProtoSchemaComposer composer;

        [SetUp]
        public void SetUp()
        {
            gameObject = new GameObject("Schema composer test");
            composer = gameObject.AddComponent<AtProtoSchemaComposer>();
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(gameObject);
        }

        internal static AtProtoLexiconDocument MakeSchema(
            JObject properties,
            JArray required = null,
            string nsid = "dev.example.record",
            JObject definitions = null,
            IReadOnlyDictionary<string, JObject> dependencies = null,
            string key = "tid",
            JArray nullable = null)
        {
            var defs = definitions == null ? new JObject() : (JObject)definitions.DeepClone();
            defs["main"] = new JObject
            {
                ["type"] = "record",
                ["key"] = key,
                ["record"] = new JObject
                {
                    ["type"] = "object", ["properties"] = properties,
                    ["required"] = required ?? new JArray(), ["nullable"] = nullable ?? new JArray()
                }
            };
            return new AtProtoLexiconDocument(new JObject { ["lexicon"] = 1, ["id"] = nsid, ["defs"] = defs }, dependencies: dependencies);
        }

        static JObject StringSchema()
        {
            return new JObject { ["type"] = "string" };
        }

        [Test]
        public void ComponentScriptMatchesItsClassAndHasNsid()
        {
            Assert.That(composer.Nsid, Is.Empty);
            Assert.That(MonoScript.FromMonoBehaviour(composer).GetClass(), Is.EqualTo(typeof(AtProtoSchemaComposer)));
            Assert.That(new SerializedObject(composer).FindProperty("nsid"), Is.Not.Null);
        }

        [Test]
        public void UnitySelectsTheSchemaInspector()
        {
            var editor = UnityEditor.Editor.CreateEditor(composer);
            try { Assert.That(editor.GetType().Name, Is.EqualTo("AtProtoSchemaComposerEditor")); }
            finally { UnityEngine.Object.DestroyImmediate(editor); }
        }

        [Test]
        public void LegacyComponentStillLoadsWithTheSchemaInspector()
        {
            var legacy = gameObject.AddComponent<AtProtoSchemaRecordComposer>();
            Assert.That(MonoScript.FromMonoBehaviour(legacy).GetClass(), Is.EqualTo(typeof(AtProtoSchemaRecordComposer)));
            var editor = UnityEditor.Editor.CreateEditor(legacy);
            try { Assert.That(editor.GetType().Name, Is.EqualTo("AtProtoSchemaComposerEditor")); }
            finally { UnityEngine.Object.DestroyImmediate(editor); }
        }

        [TestCase("app.bsky.feed.post", true)]
        [TestCase("actor.rpg.stats", true)]
        [TestCase("com.example.createRecord2", true)]
        [TestCase("app.bsky.feed.", false)]
        [TestCase("app..record", false)]
        [TestCase("app.bsky.123", false)]
        [TestCase("com.example.bad-name", false)]
        [TestCase("at://did:plc:test/app.bsky.feed.post/key", false)]
        public void NsidValidationRejectsNonNsidInputs(string nsid, bool expected)
        {
            Assert.That(AtProtoLexiconClient.IsValidNsid(nsid), Is.EqualTo(expected));
        }

        [TestCase("app.bsky", true)]
        [TestCase("app.bsky.", true)]
        [TestCase("app.bsky.feed.", true)]
        [TestCase("app.bsky.feed", false)]
        [TestCase("app..feed.", false)]
        public void NamespaceAutoSearchAcceptsValidNamespacePrefixes(string prefix, bool expected)
        {
            var editor = UnityEditor.Editor.CreateEditor(composer);
            try
            {
                var method = editor.GetType().GetMethod("IsSearchableNamespace", BindingFlags.NonPublic | BindingFlags.Static);
                Assert.That(method, Is.Not.Null);
                Assert.That(method.Invoke(null, new object[] { prefix }), Is.EqualTo(expected));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(editor);
            }
        }

        [Test]
        public void DnsAuthorityUsesTheEntireNamespaceWithoutRecursing()
        {
            Assert.That(AtProtoLexiconClient.GetAuthorityDomain("edu.university.dept.lab.blogging.getBlogPost"),
                Is.EqualTo("blogging.lab.dept.university.edu"));
        }

        [Test]
        public void DnsTxtSegmentsAreDecodedAndCnameAnswersAreIgnored()
        {
            var response = new JObject
            {
                ["Status"] = 0,
                ["Answer"] = new JArray(
                    new JObject { ["type"] = 5, ["data"] = "some.alias.example" },
                    new JObject { ["type"] = 16, ["data"] = "\"did=did:web:\" \"example.com\"" })
            };
            Assert.That(AtProtoLexiconClient.ReadAuthorityDid(response), Is.EqualTo("did:web:example.com"));
        }

        [Test]
        public void ConflictingDnsAuthoritiesAreRejected()
        {
            var response = new JObject
            {
                ["Status"] = 0,
                ["Answer"] = new JArray(
                    new JObject { ["type"] = 16, ["data"] = "\"did=did:web:first.example\"" },
                    new JObject { ["type"] = 16, ["data"] = "\"did=did:web:second.example\"" })
            };
            Assert.Throws<FormatException>(() => AtProtoLexiconClient.ReadAuthorityDid(response));
        }

        [Test]
        public void DraftIncludesRequiredFieldsAndLeavesOptionalObjectsAbsent()
        {
            var schema = MakeSchema(new JObject
            {
                ["text"] = StringSchema(),
                ["reply"] = new JObject
                {
                    ["type"] = "object", ["required"] = new JArray("uri"),
                    ["properties"] = new JObject { ["uri"] = StringSchema() }
                }
            }, new JArray("text"));
            var draft = schema.CreateRecord();
            Assert.That(draft.Value<string>("$type"), Is.EqualTo(schema.Nsid));
            Assert.That(draft["text"].Type, Is.EqualTo(JTokenType.String));
            Assert.That(draft.Property("reply"), Is.Null);
            Assert.That(schema.ValidateRecord(draft), Is.Empty);
            Assert.That(schema.CreateValue((JObject)schema.RecordDefinition["properties"]["reply"], schema.Nsid)["uri"], Is.Not.Null);
        }

        [Test]
        public void ExternalRefsKeepTheirOwnLocalDefinitionContext()
        {
            var dependency = new JObject
            {
                ["lexicon"] = 1, ["id"] = "dev.shared.defs",
                ["defs"] = new JObject
                {
                    ["item"] = new JObject
                    {
                        ["type"] = "object", ["required"] = new JArray("name"),
                        ["properties"] = new JObject { ["name"] = new JObject { ["type"] = "ref", ["ref"] = "#name" } }
                    },
                    ["name"] = new JObject { ["type"] = "string", ["default"] = "Shared" }
                }
            };
            var schema = MakeSchema(new JObject
            {
                ["subject"] = new JObject { ["type"] = "ref", ["ref"] = "dev.shared.defs#item" }
            }, new JArray("subject"), dependencies: new Dictionary<string, JObject> { ["dev.shared.defs"] = dependency });
            var draft = schema.CreateRecord();
            Assert.That(draft["subject"].Value<string>("name"), Is.EqualTo("Shared"));
            Assert.That(draft["subject"]["$type"], Is.Null);
            Assert.That(schema.ValidateRecord(draft), Is.Empty);
        }

        [Test]
        public void ArraysAndUnionsGenerateTheirRequiredStructure()
        {
            var definitions = new JObject
            {
                ["variant"] = new JObject
                {
                    ["type"] = "object", ["required"] = new JArray("active"),
                    ["properties"] = new JObject { ["active"] = new JObject { ["type"] = "boolean" } }
                }
            };
            var schema = MakeSchema(new JObject
            {
                ["items"] = new JObject
                {
                    ["type"] = "array", ["minLength"] = 1,
                    ["items"] = new JObject { ["type"] = "union", ["refs"] = new JArray("#variant") }
                }
            }, new JArray("items"), definitions: definitions);
            var draft = schema.CreateRecord();
            Assert.That(draft["items"][0].Value<string>("$type"), Is.EqualTo("dev.example.record#variant"));
            Assert.That(draft["items"][0].Value<bool>("active"), Is.False);
            Assert.That(schema.ValidateRecord(draft), Is.Empty);
        }

        [Test]
        public void NullableIsDifferentFromOmittedAndFalse()
        {
            var schema = MakeSchema(new JObject { ["enabled"] = new JObject { ["type"] = "boolean" } },
                new JArray("enabled"), nullable: new JArray("enabled"));
            var draft = schema.CreateRecord();
            draft["enabled"] = JValue.CreateNull();
            Assert.That(schema.ValidateRecord(draft), Is.Empty);
            draft.Remove("enabled");
            Assert.That(schema.ValidateRecord(draft), Has.Some.Contains("required"));
        }

        [Test]
        public void KnownValuesRemainOpenButEnumsAreClosed()
        {
            var schema = MakeSchema(new JObject
            {
                ["suggestion"] = new JObject { ["type"] = "string", ["knownValues"] = new JArray("known") },
                ["choice"] = new JObject { ["type"] = "string", ["enum"] = new JArray("only") }
            }, new JArray("suggestion", "choice"));
            var draft = schema.CreateRecord();
            draft["suggestion"] = "custom";
            Assert.That(schema.ValidateRecord(draft), Is.Empty);
            draft["choice"] = "custom";
            Assert.That(schema.ValidateRecord(draft), Has.Some.Contains("enum"));
        }

        [Test]
        public void ValidationChecksUtf8LengthIntegerBoundsAndNestedFloats()
        {
            var schema = MakeSchema(new JObject
            {
                ["text"] = new JObject { ["type"] = "string", ["maxLength"] = 1 },
                ["count"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 3 },
                ["data"] = new JObject { ["type"] = "unknown" }
            }, new JArray("text", "count", "data"));
            var draft = schema.CreateRecord();
            draft["text"] = "\u00e9";
            draft["count"] = 4;
            draft["data"] = new JObject { ["nested"] = new JArray(1.5) };
            var errors = schema.ValidateRecord(draft);
            Assert.That(errors, Has.Some.Contains("maxLength"));
            Assert.That(errors, Has.Some.Contains("maximum"));
            Assert.That(errors, Has.Some.Contains("floating-point"));
        }

        [Test]
        public void StringFormatsValidateAtIdentifiersUrisCidsAndDatetimes()
        {
            var schema = MakeSchema(new JObject
            {
                ["handle"] = new JObject { ["type"] = "string", ["format"] = "handle" },
                ["did"] = new JObject { ["type"] = "string", ["format"] = "did" },
                ["atUri"] = new JObject { ["type"] = "string", ["format"] = "at-uri" },
                ["cid"] = new JObject { ["type"] = "string", ["format"] = "cid" },
                ["createdAt"] = new JObject { ["type"] = "string", ["format"] = "datetime" },
                ["tid"] = new JObject { ["type"] = "string", ["format"] = "tid" },
                ["key"] = new JObject { ["type"] = "string", ["format"] = "record-key" }
            }, new JArray("handle", "did", "atUri", "cid", "createdAt", "tid", "key"));
            var draft = schema.CreateRecord();
            draft["handle"] = "alice.example";
            draft["did"] = "did:plc:ewvi7nxzyoun6zhxrhs64oiz";
            draft["atUri"] = "at://did:plc:ewvi7nxzyoun6zhxrhs64oiz/app.bsky.feed.post/self";
            draft["cid"] = "bafyreidfayvfuwqa7qlnopdjiqrxzs6blmoeu4rujcjtnci5beludirz2a";
            draft["createdAt"] = "2026-11-03T10:00:00.123456789Z";
            draft["tid"] = "3jzfcijpj2z2a";
            draft["key"] = "key:one";

            Assert.That(schema.ValidateRecord(draft), Is.Empty);
            draft["handle"] = "alice..example";
            draft["createdAt"] = "2026-11-03t10:00:00Z";
            var errors = schema.ValidateRecord(draft);
            Assert.That(errors, Has.Some.Contains("handle: invalid handle"));
            Assert.That(errors, Has.Some.Contains("createdAt: invalid datetime"));
            Assert.DoesNotThrow(() =>
            {
                draft["handle"] = "alice.example.";
                schema.ValidateRecord(draft);
            });
        }

        [Test]
        public void UnknownFieldsValidateNestedAtProtoCompoundValues()
        {
            var schema = MakeSchema(new JObject
            {
                ["data"] = new JObject { ["type"] = "unknown" }
            }, new JArray("data"));
            var draft = schema.CreateRecord();
            draft["data"] = new JObject
            {
                ["validUnpaddedBytes"] = new JObject { ["$bytes"] = "Zg" },
                ["invalidBytes"] = new JObject { ["$bytes"] = "not base64" },
                ["invalidLink"] = new JObject { ["$link"] = "not a cid" },
                ["invalidBlob"] = new JObject
                {
                    ["$type"] = "blob", ["ref"] = new JObject { ["$link"] = "not a cid" },
                    ["mimeType"] = "image/png", ["size"] = 1
                }
            };

            var errors = schema.ValidateRecord(draft);
            Assert.That(errors, Has.Some.Contains("invalid bytes object"));
            Assert.That(errors, Has.Some.Contains("invalid CID link object"));
            Assert.That(errors, Has.Some.Contains("invalid blob object"));
        }

        [Test]
        public void TimestampStringsAreNotConvertedIntoDateTokens()
        {
            var parsed = AtProtoLexiconDocument.ReadObject("{\"createdAt\":\"2026-11-03T10:00:00.123Z\"}");
            Assert.That(parsed["createdAt"].Type, Is.EqualTo(JTokenType.String));
            Assert.That(parsed.Value<string>("createdAt"), Is.EqualTo("2026-11-03T10:00:00.123Z"));
        }

        [Test]
        public void RefreshPreservesValuesAndUnknownFields()
        {
            composer.LoadSchema(MakeSchema(new JObject { ["text"] = StringSchema() }, new JArray("text")));
            composer.SetValue("text", "Keep this");
            composer.SetValue("extension", new JObject { ["level"] = 7 });
            composer.LoadSchema(MakeSchema(new JObject { ["text"] = StringSchema(), ["newField"] = StringSchema() }, new JArray("text")));
            var value = composer.BuildValue();
            Assert.That(value.Value<string>("text"), Is.EqualTo("Keep this"));
            Assert.That(value["extension"].Value<int>("level"), Is.EqualTo(7));
            Assert.That(value.Property("newField"), Is.Null);
        }

        [Test]
        public void DifferentNsidDoesNotInheritThePreviousRecordsValues()
        {
            composer.LoadSchema(MakeSchema(new JObject { ["text"] = StringSchema() }, new JArray("text")));
            composer.SetValue("text", "Previous data");
            composer.LoadSchema(MakeSchema(new JObject { ["text"] = StringSchema() }, new JArray("text"), "dev.example.other"));
            Assert.That(composer.BuildValue().Value<string>("text"), Is.Empty);
        }

        [Test]
        public void SerializationRoundTripPreservesSchemaAndDraft()
        {
            composer.LoadSchema(MakeSchema(new JObject { ["text"] = StringSchema() }, new JArray("text")));
            composer.SetValue("text", "Saved value");
            var saved = EditorJsonUtility.ToJson(composer);
            var restored = gameObject.AddComponent<AtProtoSchemaComposer>();
            EditorJsonUtility.FromJsonOverwrite(saved, restored);
            Assert.That(restored.HasSchema, Is.True);
            Assert.That(restored.BuildValue().Value<string>("text"), Is.EqualTo("Saved value"));
        }

        [Test]
        public void SchemaLoadCanBeUndone()
        {
            Undo.RecordObject(composer, "Test schema load");
            composer.LoadSchema(MakeSchema(new JObject { ["text"] = StringSchema() }, new JArray("text")));
            Undo.FlushUndoRecordObjects();
            Undo.PerformUndo();
            Assert.That(composer.HasSchema, Is.False);
        }

        [Test]
        public void InvalidDraftRemainsEditableButCannotBeWritten()
        {
            composer.LoadSchema(MakeSchema(new JObject
            {
                ["text"] = new JObject { ["type"] = "string", ["minLength"] = 2 }
            }, new JArray("text")));
            Assert.That(composer.GetRecordValue()["text"], Is.Not.Null);
            Assert.Throws<FormatException>(() => composer.BuildRecordJson());
            composer.SetValue("text", "Valid");
            Assert.DoesNotThrow(() => composer.BuildRecordJson());
        }

        [Test]
        public void ReservedTypeCannotBeOverridden()
        {
            Assert.Throws<ArgumentException>(() => composer.SetValue("$type", "dev.example.wrong"));
        }

        [Test]
        public void ChangedNsidRejectsAStaleResponse()
        {
            composer.Nsid = "dev.example.record";
            var resolver = new PendingResolver();
            var pending = composer.ResolveSchemaAsync(resolver);
            composer.Nsid = "dev.example.other";
            resolver.Completions[0].SetResult(AtProtoResult<AtProtoLexiconDocument>.Success(MakeSchema(new JObject())));
            Assert.That(pending.GetAwaiter().GetResult().Succeeded, Is.False);
            Assert.That(composer.Nsid, Is.EqualTo("dev.example.other"));
            Assert.That(composer.HasSchema, Is.False);
        }

        [Test]
        public void NewerResolutionWinsWhenResponsesCompleteOutOfOrder()
        {
            composer.Nsid = "dev.example.record";
            var resolver = new PendingResolver();
            var first = composer.ResolveSchemaAsync(resolver);
            var second = composer.ResolveSchemaAsync(resolver);
            resolver.Completions[1].SetResult(AtProtoResult<AtProtoLexiconDocument>.Success(MakeSchema(new JObject { ["second"] = StringSchema() }, new JArray("second"))));
            Assert.That(second.GetAwaiter().GetResult().Succeeded, Is.True);
            resolver.Completions[0].SetResult(AtProtoResult<AtProtoLexiconDocument>.Success(MakeSchema(new JObject { ["first"] = StringSchema() }, new JArray("first"))));
            Assert.That(first.GetAwaiter().GetResult().Succeeded, Is.False);
            Assert.That(composer.GetRecordValue().Property("first"), Is.Null);
            Assert.That(composer.GetRecordValue().Property("second"), Is.Not.Null);
        }

        [Test]
        public void CancelledResolutionDoesNotChangeTheComponent()
        {
            composer.Nsid = "dev.example.record";
            using (var cancellation = new CancellationTokenSource())
            {
                var resolver = new PendingResolver();
                var pending = composer.ResolveSchemaAsync(resolver, cancellation.Token);
                cancellation.Cancel();
                resolver.Completions[0].SetResult(AtProtoResult<AtProtoLexiconDocument>.Success(MakeSchema(new JObject())));
                Assert.Throws<OperationCanceledException>(() => pending.GetAwaiter().GetResult());
                Assert.That(composer.HasSchema, Is.False);
            }
        }

        [Test]
        public void WriterConnectionSetsTheSourceCollectionAndLiteralKey()
        {
            composer.LoadSchema(MakeSchema(new JObject(), key: "literal:self"));
            var editor = UnityEditor.Editor.CreateEditor(composer);
            try
            {
                var connect = editor.GetType().GetMethod("ConnectWriter", BindingFlags.NonPublic | BindingFlags.Static);
                var writer = (AtProtoWriter)connect.Invoke(null, new object[] { composer });
                Assert.That(writer.Composer, Is.SameAs(composer));
                Assert.That(writer.CollectionNsid, Is.EqualTo(composer.Nsid));
                Assert.That(writer.RecordKey, Is.EqualTo("self"));
                Assert.That(writer.WriteOnLogin, Is.False);
                Assert.That(writer.BuildRecordJson(), Does.Contain("dev.example.record"));
            }
            finally { UnityEngine.Object.DestroyImmediate(editor); }
        }

        [TestCase("any", "rebel", true)]
        [TestCase("any", "..", false)]
        [TestCase("any", "quote\"", false)]
        [TestCase("tid", "3jzfcijpj2z2a", true)]
        [TestCase("tid", "rebel", false)]
        [TestCase("tid", "zzzzzzzzzzzzz", false)]
        [TestCase("literal:self", "self", true)]
        [TestCase("literal:self", "other", false)]
        [TestCase("nsid", "dev.example.record", true)]
        [TestCase("nsid", "rebel", false)]
        public void RecordKeysFollowTheDeclaredSchema(string keyType, string key, bool expected)
        {
            var schema = MakeSchema(new JObject(), key: keyType);
            Assert.That(schema.GetRecordKeyValidationMessage(key, AtProtoWriteMode.Create) == null, Is.EqualTo(expected));
        }

        [Test]
        public void GeneratedTidFollowsTheLexiconTidFormat()
        {
            Assert.That(AtProtoLexiconDocument.IsValidTid(AtProtoLexiconDocument.GenerateTid()), Is.True);
        }

        [Test]
        public void GeneratedTidsUseUnixEpochAndIncreaseWithinTheProcess()
        {
            var first = AtProtoLexiconDocument.GenerateTid();
            var second = AtProtoLexiconDocument.GenerateTid();
            var firstValue = DecodeTid(first);
            var secondValue = DecodeTid(second);
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var currentMicros = (ulong)((DateTime.UtcNow.Ticks - epoch.Ticks) / 10);

            Assert.That(secondValue, Is.GreaterThan(firstValue));
            Assert.That(firstValue >> 10, Is.InRange(currentMicros - 1000000, currentMicros + 1000000));
        }

        [Test]
        public void WriterCollectionTracksTheSchemaInsteadOfItsPreviousTarget()
        {
            var writer = gameObject.AddComponent<AtProtoWriter>();
            writer.CollectionNsid = "dev.previous.record";
            writer.Composer = composer;
            composer.Nsid = "dev.example.record";
            Assert.That(writer.CollectionNsid, Is.EqualTo(composer.Nsid));
            composer.Nsid = "dev.example.other";
            Assert.That(writer.CollectionNsid, Is.EqualTo("dev.example.other"));
        }

        [Test]
        public void DeleteRequiresARecordWrittenByThisWriterAndGrantedOAuthScope()
        {
            const string did = "did:plc:ewvi7nxzyoun6zhxrhs64oiz";
            const string collection = "dev.example.record";
            var writer = gameObject.AddComponent<AtProtoWriter>();
            var login = gameObject.AddComponent<AtProtoLogin>();
            var record = new AtProtoRecord(
                "at://" + did + "/" + collection + "/self",
                "bafyreidfayvfuwqa7qlnopdjiqrxzs6blmoeu4rujcjtnci5beludirz2a",
                new JObject());
            writer.Login = login;
            writer.LoadRecord(record);
            SetLoginSession(login, did, "atproto repo:" + collection + "?action=update");

            Assert.That(writer.GetDeleteValidationMessage(), Does.Contain("Write a record"));

            typeof(AtProtoWriter).GetField(
                "lastWrittenRecordCanBeDeleted",
                BindingFlags.Instance | BindingFlags.NonPublic).SetValue(writer, true);
            Assert.That(writer.GetDeleteValidationMessage(), Does.Contain("does not allow delete"));

            SetLoginSession(login, did, "atproto repo:" + collection + "?action=delete");
            Assert.That(writer.GetDeleteValidationMessage(), Is.Null);

            SetLoginSession(login, "did:plc:anotheraccount", "atproto repo:" + collection + "?action=delete");
            Assert.That(writer.GetDeleteValidationMessage(), Does.Contain("does not belong"));
        }

        static void SetLoginSession(AtProtoLogin login, string did, string scope)
        {
            var session = new AtProtoAuthSession(
                did,
                "alice.example",
                "https://pds.example",
                "https://auth.example",
                "https://auth.example/token",
                "client",
                scope,
                "https://pds.example",
                "access-token",
                "refresh-token",
                DateTime.UtcNow.AddHours(1));
            var client = (AtProtoAuthenticatedClient)Activator.CreateInstance(
                typeof(AtProtoAuthenticatedClient),
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new object[] { new AtProtoClient(), session, null, TimeSpan.Zero, null },
                null);
            typeof(AtProtoLogin).GetField(
                "session",
                BindingFlags.Instance | BindingFlags.NonPublic).SetValue(login, session);
            typeof(AtProtoLogin).GetField(
                "authenticatedClient",
                BindingFlags.Instance | BindingFlags.NonPublic).SetValue(login, client);
        }

        [Test]
        public void MaximumNsidLengthIsSupported()
        {
            var nsid = string.Join(".", new string('a', 63), new string('b', 63), new string('c', 63), new string('d', 61), new string('e', 63));
            Assert.That(nsid.Length, Is.EqualTo(317));
            Assert.That(AtProtoLexiconClient.IsValidNsid(nsid), Is.True);
            Assert.That(AtProtoRecordPermission.IsValidCollection(nsid), Is.True);
        }

        [UnityTest]
        public IEnumerator InspectorDrawsStructuredFieldsWithoutChangingTheDraft()
        {
            composer.LoadSchema(MakeSchema(new JObject
            {
                ["text"] = StringSchema(),
                ["enabled"] = new JObject { ["type"] = "boolean" },
                ["items"] = new JObject { ["type"] = "array", ["items"] = StringSchema(), ["minLength"] = 1 },
                ["data"] = new JObject { ["type"] = "unknown" }
            }, new JArray("text", "enabled", "items", "data")));
            composer.SetValue("data", new JObject { ["nested"] = new JObject { ["count"] = 3 } });
            var before = composer.GetRecordValue().ToString();
            var editor = UnityEditor.Editor.CreateEditor(composer);
            var window = ScriptableObject.CreateInstance<SchemaInspectorTestWindow>();
            window.Inspector = editor;
            try
            {
                window.Show();
                window.SendEvent(new Event { type = EventType.Layout });
                window.SendEvent(new Event { type = EventType.Repaint });
                yield return null;
                Assert.That(window.DrawCount, Is.GreaterThan(0));
                Assert.That(composer.GetRecordValue().ToString(), Is.EqualTo(before));
            }
            finally
            {
                window.Close();
                UnityEngine.Object.DestroyImmediate(editor);
            }
        }

        static ulong DecodeTid(string tid)
        {
            const string alphabet = "234567abcdefghijklmnopqrstuvwxyz";
            ulong value = 0;
            foreach (var character in tid)
                value = (value << 5) | (uint)alphabet.IndexOf(character);
            return value;
        }

        sealed class PendingResolver : IAtProtoLexiconResolver
        {
            public readonly List<AwaitableCompletionSource<AtProtoResult<AtProtoLexiconDocument>>> Completions = new();

            public Awaitable<AtProtoResult<AtProtoLexiconDocument>> ResolveAsync(string nsid, CancellationToken cancellationToken = default)
            {
                var completion = new AwaitableCompletionSource<AtProtoResult<AtProtoLexiconDocument>>();
                Completions.Add(completion);
                return completion.Awaitable;
            }
        }
    }

    [Category("Network")]
    public sealed class AtProtoSchemaNetworkTests
    {
        [UnityTest]
        public IEnumerator PostSchemaGeneratesTheMinimalPostWithoutOptionalEmbeds()
        {
            using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90)))
            {
                var pending = new AtProtoLexiconClient().ResolveAsync("app.bsky.feed.post", cancellation.Token);
                while (!pending.GetAwaiter().IsCompleted)
                    yield return null;
                var result = pending.GetAwaiter().GetResult();
                Assert.That(result.Succeeded, Is.True, result.Error?.Message);
                var draft = result.Value.CreateRecord();
                draft["text"] = "Schema test";
                Assert.That(draft.Property("reply"), Is.Null);
                Assert.That(draft.Property("embed"), Is.Null);
                Assert.That(result.Value.ValidateRecord(draft), Is.Empty);
            }
        }

        [UnityTest]
        public IEnumerator NamespaceSearchReturnsPublishedRecordTypes()
        {
            using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90)))
            {
                var pending = new AtProtoLexiconClient().SearchAsync("app.bsky.feed.", cancellation.Token);
                while (!pending.GetAwaiter().IsCompleted)
                    yield return null;
                var result = pending.GetAwaiter().GetResult();
                Assert.That(result.Succeeded, Is.True, result.Error?.Message);
                Assert.That(result.Value, Does.Contain("app.bsky.feed.post"));
                Assert.That(result.Value, Does.Contain("app.bsky.feed.like"));
                Assert.That(result.Value, Does.Not.Contain("app.bsky.feed.getTimeline"));
            }
        }

        [UnityTest]
        public IEnumerator EnteredNamespacePrefixAutomaticallyPopulatesPublishedRecordTypes()
        {
            var gameObject = new GameObject("Automatic namespace search test");
            var composer = gameObject.AddComponent<AtProtoSchemaComposer>();
            composer.Nsid = "app.bsky";
            var editor = UnityEditor.Editor.CreateEditor(composer);
            try
            {
                var suggestionsField = editor.GetType().GetField("suggestions", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.That(suggestionsField, Is.Not.Null);
                var deadline = EditorApplication.timeSinceStartup + 100;
                IReadOnlyList<string> suggestions = null;
                while (EditorApplication.timeSinceStartup < deadline)
                {
                    suggestions = suggestionsField.GetValue(editor) as IReadOnlyList<string>;
                    if (suggestions != null)
                        break;
                    yield return null;
                }
                Assert.That(suggestions, Is.Not.Null);
                Assert.That(suggestions, Does.Contain("app.bsky.feed.post"));
                Assert.That(suggestions, Does.Contain("app.bsky.feed.like"));
                Assert.That(suggestions, Does.Contain("app.bsky.graph.follow"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(editor);
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [UnityTest]
        public IEnumerator EnteredNsidAutomaticallyLoadsItsSchemaAndExternalFields()
        {
            var gameObject = new GameObject("Live schema test");
            var composer = gameObject.AddComponent<AtProtoSchemaComposer>();
            composer.Nsid = "app.bsky.feed.like";
            var editor = UnityEditor.Editor.CreateEditor(composer);
            try
            {
                var deadline = EditorApplication.timeSinceStartup + 100;
                while (!composer.HasSchema && EditorApplication.timeSinceStartup < deadline)
                    yield return null;
                var message = editor.GetType().GetField("message", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(editor);
                Assert.That(composer.HasSchema, Is.True, message?.ToString());
                var draft = composer.GetRecordValue();
                Assert.That(draft["subject"], Is.TypeOf<JObject>());
                Assert.That(draft["subject"]["uri"], Is.Not.Null);
                Assert.That(draft["subject"]["cid"], Is.Not.Null);
                Assert.That(draft["createdAt"].Type, Is.EqualTo(JTokenType.String));
                Assert.That(composer.SchemaRecordCid, Is.Not.Empty);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(editor);
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }
    }

    public sealed class SchemaInspectorTestWindow : EditorWindow
    {
        public UnityEditor.Editor Inspector { get; set; }
        public int DrawCount { get; private set; }

        void OnGUI()
        {
            if (Inspector == null)
                return;
            Inspector.OnInspectorGUI();
            DrawCount++;
        }
    }
}