using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AtProtoUnity
{
    public sealed class AtProtoLexiconDocument
    {
        public const int MaximumDepth = 32;
        static readonly object tidLock = new();
        static ulong lastGeneratedTidValue;
        static bool hasGeneratedTidValue;
        readonly Dictionary<string, JObject> documents = new(StringComparer.Ordinal);

        public string Nsid { get; }
        public string SourceDid { get; }
        public string SourcePdsEndpoint { get; }
        public string RecordCid { get; }
        public string Description => MainDefinition.Value<string>("description") ?? Json.Value<string>("description");
        public string RecordKey => MainDefinition.Value<string>("key");
        public JObject Json { get; }
        public JObject MainDefinition => (JObject)Json["defs"]["main"];
        public JObject RecordDefinition => (JObject)MainDefinition["record"];

        public AtProtoLexiconDocument(
            JObject json,
            string sourceDid = null,
            string sourcePdsEndpoint = null,
            string recordCid = null,
            IReadOnlyDictionary<string, JObject> dependencies = null)
        {
            Nsid = json?.Value<string>("id");
            ValidateDocument(Nsid, json);
            if (json["defs"]?["main"]?.Value<string>("type") != "record"
                || !(json["defs"]?["main"]?["record"] is JObject record)
                || record.Value<string>("type") != "object"
                || !(record["properties"] is JObject)
                || string.IsNullOrEmpty(json["defs"]["main"].Value<string>("key")))
                throw new FormatException("The Lexicon must define main as a repository record with object properties and a record key.");

            Json = (JObject)json.DeepClone();
            SourceDid = sourceDid;
            SourcePdsEndpoint = sourcePdsEndpoint;
            RecordCid = recordCid;
            documents.Add(Nsid, Json);
            if (dependencies != null)
            {
                foreach (var dependency in dependencies)
                {
                    ValidateDocument(dependency.Key, dependency.Value);
                    if (dependency.Key != Nsid)
                        documents.Add(dependency.Key, (JObject)dependency.Value.DeepClone());
                }
            }
        }

        public static JObject ReadObject(string json)
        {
            using (var reader = new JsonTextReader(new StringReader(json ?? string.Empty))
            {
                DateParseHandling = DateParseHandling.None,
                MaxDepth = 128
            })
            {
                var result = JObject.Load(reader);
                if (reader.Read())
                    throw new FormatException("Unexpected content after the JSON object.");
                return result;
            }
        }

        public static void ValidateDocument(string nsid, JObject json)
        {
            if (!AtProtoLexiconClient.IsValidNsid(nsid)
                || json?.Value<string>("id") != nsid
                || json.Value<int?>("lexicon") != 1
                || !(json["defs"] is JObject definitions) || definitions.Count == 0)
                throw new FormatException("Invalid Lexicon document for '" + nsid + "'.");
            if (json["$type"] != null && json.Value<string>("$type") != "com.atproto.lexicon.schema")
                throw new FormatException("The published record is not a com.atproto.lexicon.schema record.");
        }

        public JObject ExportDependencies()
        {
            var result = new JObject();
            foreach (var document in documents)
            {
                if (document.Key != Nsid)
                    result[document.Key] = document.Value.DeepClone();
            }
            return result;
        }

        public JObject ResolveSchema(JObject schema, string ownerNsid, out string resolvedNsid)
        {
            resolvedNsid = ownerNsid;
            for (var depth = 0; schema?.Value<string>("type") == "ref"; depth++)
            {
                if (depth >= MaximumDepth)
                    throw new FormatException("The schema contains recursive references without an object boundary.");
                var reference = schema.Value<string>("ref");
                var separator = reference?.IndexOf('#') ?? -1;
                var documentNsid = separator == 0 ? resolvedNsid : separator > 0 ? reference.Substring(0, separator) : reference;
                var definition = separator >= 0 ? reference.Substring(separator + 1) : "main";
                if (documentNsid == null || !documents.TryGetValue(documentNsid, out var document)
                    || !(document["defs"]?[definition] is JObject target))
                    throw new FormatException("Unresolved Lexicon reference: " + reference);
                schema = target;
                resolvedNsid = documentNsid;
            }
            return schema ?? throw new FormatException("A field has no Lexicon schema.");
        }

        public static string GetTypeReference(string reference, string ownerNsid)
        {
            var result = reference.StartsWith("#", StringComparison.Ordinal) ? ownerNsid + reference : reference;
            return result.EndsWith("#main", StringComparison.Ordinal) ? result.Substring(0, result.Length - 5) : result;
        }

        public JToken CreateValue(JObject schema, string ownerNsid, int depth = 0)
        {
            if (depth > MaximumDepth)
                throw new FormatException("The schema exceeds the supported nesting depth.");
            schema = ResolveSchema(schema, ownerNsid, out ownerNsid);
            if (schema["const"] != null || schema["default"] != null)
                return (schema["const"] ?? schema["default"]).DeepClone();
            if (schema["enum"] is JArray options && options.Count > 0)
                return options[0].DeepClone();

            switch (schema.Value<string>("type"))
            {
                case "record":
                    var record = (JObject)CreateValue((JObject)schema["record"], ownerNsid, depth + 1);
                    record["$type"] = ownerNsid;
                    return record;
                case "object":
                    return PopulateRequired(new JObject(), schema, ownerNsid, depth + 1);
                case "string":
                    return new JValue(schema.Value<string>("format") == "datetime"
                        ? DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) : string.Empty);
                case "boolean":
                    return new JValue(false);
                case "integer":
                    return new JValue(schema.Value<long?>("minimum") ?? Math.Min(0, schema.Value<long?>("maximum") ?? 0));
                case "array":
                    var array = new JArray();
                    var minimum = schema.Value<int?>("minLength") ?? 0;
                    if (minimum > 256)
                        throw new FormatException("The schema requires more than 256 initial array items.");
                    for (var index = 0; index < minimum; index++)
                        array.Add(CreateValue((JObject)schema["items"], ownerNsid, depth + 1));
                    return array;
                case "union":
                    var reference = (schema["refs"] as JArray)?.First?.Value<string>();
                    if (reference == null)
                        return new JObject { ["$type"] = string.Empty };
                    var variant = (JObject)CreateValue(new JObject { ["type"] = "ref", ["ref"] = reference }, ownerNsid, depth + 1);
                    variant["$type"] = GetTypeReference(reference, ownerNsid);
                    return variant;
                case "bytes":
                    return new JObject { ["$bytes"] = string.Empty };
                case "cid-link":
                    return new JObject { ["$link"] = string.Empty };
                case "blob":
                    return new JObject
                    {
                        ["$type"] = "blob", ["ref"] = new JObject { ["$link"] = string.Empty },
                        ["mimeType"] = string.Empty, ["size"] = 0
                    };
                case "unknown":
                    return new JObject();
                default:
                    throw new FormatException("Unsupported Lexicon field type: " + schema.Value<string>("type"));
            }
        }

        public JObject CreateRecord(JObject previous = null)
        {
            var result = previous == null ? new JObject() : (JObject)previous.DeepClone();
            PopulateRequired(result, RecordDefinition, Nsid, 0);
            result["$type"] = Nsid;
            return result;
        }

        JObject PopulateRequired(JObject value, JObject schema, string ownerNsid, int depth)
        {
            if (depth > MaximumDepth)
                throw new FormatException("The schema exceeds the supported nesting depth.");
            var required = (schema["required"] as JArray)?.Values<string>() ?? Enumerable.Empty<string>();
            foreach (var property in ((JObject)schema["properties"]).Properties())
            {
                var childSchema = (JObject)property.Value;
                if (value[property.Name] == null && required.Contains(property.Name))
                    value[property.Name] = CreateValue(childSchema, ownerNsid, depth + 1);
                else if (value[property.Name] is JObject child)
                {
                    var resolved = ResolveSchema(childSchema, ownerNsid, out var childOwner);
                    if (resolved.Value<string>("type") == "object")
                        PopulateRequired(child, resolved, childOwner, depth + 1);
                }
            }
            return value;
        }

        public IReadOnlyList<string> ValidateRecord(JObject value)
        {
            var errors = new List<string>();
            if (value?.Value<string>("$type") != Nsid)
                errors.Add("$type must be " + Nsid + ".");
            ValidateValue(value, RecordDefinition, Nsid, string.Empty, errors, 0);
            ValidateDataModel(value, string.Empty, errors, 0);
            return errors;
        }

        public string GetRecordKeyValidationMessage(string key, AtProtoWriteMode mode)
        {
            if (string.IsNullOrEmpty(key))
                return mode == AtProtoWriteMode.Create && (RecordKey == "tid" || RecordKey == "any")
                    ? null : "A record key is required for " + RecordKey + ".";
            if (!IsValidRecordKey(key))
                return "The record key contains invalid characters or length.";
            if (RecordKey.StartsWith("literal:", StringComparison.Ordinal))
                return key == RecordKey.Substring("literal:".Length) ? null : "The record key must be " + RecordKey.Substring("literal:".Length) + ".";
            if (RecordKey == "tid")
                return IsValidTid(key) ? null : "This schema requires a 13-character TID record key.";
            if (RecordKey == "nsid")
                return AtProtoLexiconClient.IsValidNsid(key) ? null : "This schema requires an NSID record key.";
            return RecordKey == "any" ? null : "Unsupported Lexicon record key type: " + RecordKey;
        }

        public static bool IsValidRecordKey(string key)
        {
            return !string.IsNullOrEmpty(key) && key.Length <= 512 && key != "." && key != ".."
                && key.All(character => character >= 'a' && character <= 'z'
                    || character >= 'A' && character <= 'Z' || character >= '0' && character <= '9'
                    || ".-_:~".IndexOf(character) >= 0);
        }

        public static bool IsValidTid(string value)
        {
            return value?.Length == 13 && "234567abcdefghij".IndexOf(value[0]) >= 0
                && value.All(character => "234567abcdefghijklmnopqrstuvwxyz".IndexOf(character) >= 0);
        }

        public static string GenerateTid()
        {
            const string alphabet = "234567abcdefghijklmnopqrstuvwxyz";
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
            ulong value;
            lock (tidLock)
            {
                var timestamp = (ulong)Math.Max(0, (DateTime.UtcNow.Ticks - epoch) / 10);
                var randomBytes = new byte[2];
                using (var random = RandomNumberGenerator.Create())
                    random.GetBytes(randomBytes);
                var clockId = ((ulong)randomBytes[0] << 2) | ((ulong)randomBytes[1] >> 6);
                value = (timestamp << 10) | clockId;
                if (hasGeneratedTidValue && value <= lastGeneratedTidValue)
                    value = lastGeneratedTidValue + 1;
                lastGeneratedTidValue = value;
                hasGeneratedTidValue = true;
            }

            var characters = new char[13];
            for (var index = characters.Length - 1; index >= 0; index--)
            {
                characters[index] = alphabet[(int)(value & 31)];
                value >>= 5;
            }
            return new string(characters);
        }

        void ValidateValue(JToken value, JObject schema, string ownerNsid, string path, List<string> errors, int depth)
        {
            if (depth > MaximumDepth || errors.Count >= 30)
            {
                if (depth > MaximumDepth)
                    errors.Add(path + ": nesting is too deep.");
                return;
            }
            schema = ResolveSchema(schema, ownerNsid, out ownerNsid);
            var type = schema.Value<string>("type");
            var validType = type == "string" && value?.Type == JTokenType.String
                || type == "integer" && value?.Type == JTokenType.Integer
                || type == "boolean" && value?.Type == JTokenType.Boolean
                || type == "array" && value is JArray
                || (type == "object" || type == "record" || type == "union" || type == "unknown"
                    || type == "blob" || type == "cid-link" || type == "bytes") && value is JObject;
            if (!validType)
            {
                errors.Add(path + ": expected " + type + ".");
                return;
            }
            if (schema["const"] != null && !JToken.DeepEquals(schema["const"], value))
                errors.Add(path + ": must equal " + schema["const"].ToString(Formatting.None) + ".");
            if (schema["enum"] is JArray choices && !choices.Any(choice => JToken.DeepEquals(choice, value)))
                errors.Add(path + ": value is not in the allowed enum.");

            if (type == "record")
            {
                if (value.Value<string>("$type") != ownerNsid)
                    errors.Add(path + ": incorrect record $type.");
                ValidateValue(value, (JObject)schema["record"], ownerNsid, path, errors, depth + 1);
            }
            else if (type == "object")
            {
                var required = (schema["required"] as JArray)?.Values<string>() ?? Enumerable.Empty<string>();
                var nullable = (schema["nullable"] as JArray)?.Values<string>() ?? Enumerable.Empty<string>();
                var properties = (JObject)schema["properties"];
                foreach (var name in required)
                {
                    if (value[name] == null)
                        errors.Add(ChildPath(path, name) + ": required field is missing.");
                }
                foreach (var property in properties.Properties())
                {
                    var child = value[property.Name];
                    if (child == null || child.Type == JTokenType.Null && nullable.Contains(property.Name))
                        continue;
                    ValidateValue(child, (JObject)property.Value, ownerNsid, ChildPath(path, property.Name), errors, depth + 1);
                }
            }
            else if (type == "union")
            {
                var discriminator = value.Value<string>("$type");
                if (string.IsNullOrWhiteSpace(discriminator) || discriminator.EndsWith("#main", StringComparison.Ordinal))
                    errors.Add(path + ": union requires a valid $type.");
                else
                {
                    var reference = (schema["refs"] as JArray)?.Values<string>()
                        .FirstOrDefault(candidate => GetTypeReference(candidate, ownerNsid) == discriminator);
                    if (reference != null)
                        ValidateValue(value, new JObject { ["type"] = "ref", ["ref"] = reference }, ownerNsid, path, errors, depth + 1);
                    else if (schema.Value<bool?>("closed") == true)
                        errors.Add(path + ": unknown variant in a closed union.");
                }
            }
            else if (type == "array")
            {
                var array = (JArray)value;
                CheckBounds(array.Count, schema, "minLength", "maxLength", path, errors);
                for (var index = 0; index < array.Count; index++)
                    ValidateValue(array[index], (JObject)schema["items"], ownerNsid, path + "[" + index + "]", errors, depth + 1);
            }
            else if (type == "integer")
            {
                try { CheckBounds(value.Value<long>(), schema, "minimum", "maximum", path, errors); }
                catch (Exception exception) when (exception is OverflowException || exception is InvalidCastException)
                { errors.Add(path + ": integer exceeds the signed 64-bit range."); }
            }
            else if (type == "string")
            {
                var text = value.Value<string>();
                CheckBounds(Encoding.UTF8.GetByteCount(text), schema, "minLength", "maxLength", path, errors);
                CheckBounds(new StringInfo(text).LengthInTextElements, schema, "minGraphemes", "maxGraphemes", path, errors);
                var format = schema.Value<string>("format");
                if (!IsValidStringFormat(format, text))
                    errors.Add(path + ": invalid " + format + ".");
            }
            else if (type == "unknown" && (value["$link"] != null || value.Value<string>("$type") == "blob"))
                errors.Add(path + ": unknown must be a data object, not a CID link or blob.");
            else if (type == "cid-link" && string.IsNullOrWhiteSpace(value.Value<string>("$link")))
                errors.Add(path + ": CID is required.");
            else if (type == "bytes")
            {
                if (!TryGetBase64ByteLength(value.Value<string>("$bytes"), out var byteLength))
                    errors.Add(path + ": invalid base64 bytes.");
                else
                    CheckBounds(byteLength, schema, "minLength", "maxLength", path, errors);
            }
            else if (type == "blob")
            {
                if (value.Value<string>("$type") != "blob" || string.IsNullOrWhiteSpace(value["ref"]?.Value<string>("$link")))
                    errors.Add(path + ": a previously uploaded blob reference is required.");
                var mimeType = value.Value<string>("mimeType");
                var accept = schema["accept"] as JArray;
                if (string.IsNullOrWhiteSpace(mimeType) || accept != null && !accept.Values<string>().Any(pattern =>
                    pattern == "*/*" || pattern == mimeType || pattern.EndsWith("/*", StringComparison.Ordinal)
                    && mimeType.StartsWith(pattern.Substring(0, pattern.Length - 1), StringComparison.Ordinal)))
                    errors.Add(path + ": blob MIME type is not accepted.");
                if (value["size"]?.Type != JTokenType.Integer || value.Value<long>("size") < 0)
                    errors.Add(path + ": blob size must be a non-negative integer.");
                else
                    CheckBounds(value.Value<long>("size"), schema, "minimum", "maxSize", path, errors);
            }
        }

        static void ValidateDataModel(JToken value, string path, List<string> errors, int depth)
        {
            if (value == null || errors.Count >= 30)
                return;
            if (depth > MaximumDepth)
            {
                errors.Add(path + ": nesting is too deep.");
                return;
            }
            if (value.Type == JTokenType.Float)
                errors.Add(path + ": AT Protocol records do not support floating-point numbers.");
            else if (value is JObject obj)
            {
                if (obj.Property("$link") != null)
                {
                    if (obj.Count != 1 || obj["$link"]?.Type != JTokenType.String
                        || !IsValidCid(obj.Value<string>("$link")))
                        errors.Add(path + ": invalid CID link object.");
                    return;
                }
                if (obj.Property("$bytes") != null)
                {
                    if (obj.Count != 1 || obj["$bytes"]?.Type != JTokenType.String
                        || !TryGetBase64ByteLength(obj.Value<string>("$bytes"), out _))
                        errors.Add(path + ": invalid bytes object.");
                    return;
                }
                if (obj.Value<string>("$type") == "blob")
                {
                    var reference = obj["ref"] as JObject;
                    var size = obj["size"];
                    var validSize = size?.Type == JTokenType.Integer;
                    if (validSize)
                    {
                        try { validSize = size.Value<long>() >= 0; }
                        catch (Exception exception) when (exception is OverflowException || exception is InvalidCastException)
                        { validSize = false; }
                    }
                    if (reference == null || reference.Count != 1
                        || !IsValidCid(reference.Value<string>("$link"), 0x55)
                        || string.IsNullOrWhiteSpace(obj.Value<string>("mimeType")) || !validSize)
                        errors.Add(path + ": invalid blob object.");
                }
                foreach (var property in obj.Properties())
                    ValidateDataModel(property.Value, ChildPath(path, property.Name), errors, depth + 1);
            }
            else if (value is JArray array)
            {
                for (var index = 0; index < array.Count; index++)
                    ValidateDataModel(array[index], path + "[" + index + "]", errors, depth + 1);
            }
        }

        static bool IsValidStringFormat(string format, string value)
        {
            if (string.IsNullOrEmpty(format))
                return true;
            switch (format)
            {
                case "at-identifier":
                    return value.StartsWith("did:", StringComparison.Ordinal)
                        ? IsValidDid(value) : IsValidHandle(value);
                case "at-uri":
                    return IsValidAtUri(value);
                case "cid":
                    return IsValidCid(value);
                case "datetime":
                    return IsValidDateTime(value);
                case "did":
                    return IsValidDid(value);
                case "handle":
                    return IsValidHandle(value);
                case "nsid":
                    return AtProtoLexiconClient.IsValidNsid(value);
                case "record-key":
                    return IsValidRecordKey(value);
                case "tid":
                    return IsValidTid(value);
                case "uri":
                    return value.Length <= 8192 && Uri.IsWellFormedUriString(value, UriKind.Absolute);
                default:
                    return true;
            }
        }

        static bool IsValidHandle(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 253)
                return false;
            var labels = value.Split('.');
            if (labels.Length < 2 || labels[labels.Length - 1].Length == 0
                || !IsAsciiLetter(labels[labels.Length - 1][0]))
                return false;
            foreach (var label in labels)
            {
                if (label.Length == 0 || label.Length > 63 || label[0] == '-' || label[label.Length - 1] == '-')
                    return false;
                foreach (var character in label)
                {
                    if (!IsAsciiLetter(character) && !(character >= '0' && character <= '9') && character != '-')
                        return false;
                }
            }
            return true;
        }

        static bool IsValidDid(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 2048 || !value.StartsWith("did:", StringComparison.Ordinal))
                return false;
            var methodEnd = value.IndexOf(':', 4);
            if (methodEnd == 4 || methodEnd < 0 || methodEnd == value.Length - 1 || value[value.Length - 1] == ':')
                return false;
            for (var index = 4; index < methodEnd; index++)
            {
                if (value[index] < 'a' || value[index] > 'z')
                    return false;
            }
            for (var index = methodEnd + 1; index < value.Length; index++)
            {
                var character = value[index];
                if (!IsAsciiLetter(character) && !(character >= '0' && character <= '9')
                    && "._:%-".IndexOf(character) < 0)
                    return false;
            }
            return true;
        }

        static bool IsValidAtUri(string value)
        {
            if (string.IsNullOrEmpty(value) || !value.StartsWith("at://", StringComparison.Ordinal)
                || value.IndexOfAny(new[] { '?', '#' }) >= 0)
                return false;
            var path = value.Substring("at://".Length);
            if (path.Length == 0 || path.EndsWith("/", StringComparison.Ordinal))
                return false;
            var parts = path.Split('/');
            if (parts.Length < 1 || parts.Length > 3 || !IsValidAtIdentifier(parts[0]))
                return false;
            if (parts.Length > 1 && !AtProtoLexiconClient.IsValidNsid(parts[1]))
                return false;
            return parts.Length < 3 || IsValidRecordKey(parts[2]);
        }

        static bool IsValidAtIdentifier(string value)
        {
            return value.StartsWith("did:", StringComparison.Ordinal)
                ? IsValidDid(value) : IsValidHandle(value);
        }

        static bool IsValidCid(string value, byte? expectedCodec = null)
        {
            const string alphabet = "abcdefghijklmnopqrstuvwxyz234567";
            if (value == null || value.Length != 59 || value[0] != 'b')
                return false;
            var bytes = new List<byte>(36);
            uint buffer = 0;
            var bits = 0;
            for (var index = 1; index < value.Length; index++)
            {
                var digit = alphabet.IndexOf(value[index]);
                if (digit < 0)
                    return false;
                buffer = (buffer << 5) | (uint)digit;
                bits += 5;
                if (bits >= 8)
                {
                    bits -= 8;
                    bytes.Add((byte)(buffer >> bits));
                    buffer &= (uint)((1 << bits) - 1);
                }
            }
            return bits == 2 && buffer == 0 && bytes.Count == 36
                && bytes[0] == 1 && (bytes[1] == 0x55 || bytes[1] == 0x71)
                && (!expectedCodec.HasValue || bytes[1] == expectedCodec.Value)
                && bytes[2] == 0x12 && bytes[3] == 0x20;
        }

        static bool TryGetBase64ByteLength(string value, out int byteLength)
        {
            byteLength = 0;
            if (value == null || value.Any(char.IsWhiteSpace) || value.Length % 4 == 1)
                return false;
            var normalized = value;
            if (!normalized.Contains("="))
                normalized = normalized.PadRight(normalized.Length + (4 - normalized.Length % 4) % 4, '=');
            try
            {
                byteLength = Convert.FromBase64String(normalized).Length;
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        static bool IsValidDateTime(string value)
        {
            var match = Regex.Match(value ?? string.Empty,
                @"^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.(\d+))?(Z|([+-])(\d{2}):(\d{2}))$");
            if (!match.Success)
                return false;
            var year = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var month = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            var day = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
            var hour = int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture);
            var minute = int.Parse(match.Groups[5].Value, CultureInfo.InvariantCulture);
            var second = int.Parse(match.Groups[6].Value, CultureInfo.InvariantCulture);
            var calendarYear = year == 0 ? 400 : year;
            if (month < 1 || month > 12 || day < 1 || day > DateTime.DaysInMonth(calendarYear, month)
                || hour > 23 || minute > 59 || second > 59)
                return false;
            if (match.Groups[8].Value == "Z")
                return true;
            var offsetHour = int.Parse(match.Groups[10].Value, CultureInfo.InvariantCulture);
            var offsetMinute = int.Parse(match.Groups[11].Value, CultureInfo.InvariantCulture);
            if (offsetHour > 23 || offsetMinute > 59
                || match.Groups[9].Value == "-" && offsetHour == 0 && offsetMinute == 0)
                return false;
            return year != 0 || match.Groups[9].Value != "+" || offsetHour == 0 && offsetMinute == 0;
        }

        static bool IsAsciiLetter(char character)
        {
            return character >= 'a' && character <= 'z' || character >= 'A' && character <= 'Z';
        }

        static void CheckBounds(long value, JObject schema, string minimum, string maximum, string path, List<string> errors)
        {
            if (schema.Value<long?>(minimum) is long lower && value < lower)
                errors.Add(path + ": below " + minimum + " " + lower + ".");
            if (schema.Value<long?>(maximum) is long upper && value > upper)
                errors.Add(path + ": exceeds " + maximum + " " + upper + ".");
        }

        static string ChildPath(string path, string name)
        {
            return path.Length == 0 ? name : path + "." + name;
        }
    }
}