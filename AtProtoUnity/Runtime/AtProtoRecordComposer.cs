using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AtProtoUnity
{
    public enum AtProtoRecordValueType
    {
        String,
        Boolean,
        Integer,
        Number,
        Json,
        UtcTimestamp,
        JsonObject,
        JsonArray
    }

    [Serializable]
    public sealed class AtProtoRecordField
    {
        [SerializeField] string path;
        [SerializeField] AtProtoRecordValueType valueType = AtProtoRecordValueType.String;
        [SerializeField] string value;
        [SerializeField] UnityEngine.Object bindingSource;
        [SerializeField] string bindingMember;
        [SerializeField] bool bound;

        internal bool IsIncomplete =>
            string.IsNullOrWhiteSpace(path) || path.EndsWith(".", StringComparison.Ordinal);

        public string Path
        {
            get => path;
            set => path = value;
        }

        public AtProtoRecordValueType ValueType
        {
            get => valueType;
            set => valueType = value;
        }

        public string Value
        {
            get => value;
            set => this.value = value;
        }

        public UnityEngine.Object BindingSource
        {
            get => bindingSource;
            set => bindingSource = value;
        }

        public string BindingMember
        {
            get => bindingMember;
            set => bindingMember = value;
        }

        public bool Bound
        {
            get => bound;
            set => bound = value;
        }

        internal JToken BuildValue()
        {
            if (bound && valueType != AtProtoRecordValueType.UtcTimestamp)
                return AtProtoRecordBinding.Read(bindingSource, bindingMember, valueType, path);

            return AtProtoRecordValueConverter.ToJson(value ?? string.Empty, valueType, path);
        }
    }

    // Converts literal text or a linked member's value into the JSON a field's Type expects.
    public static class AtProtoRecordValueConverter
    {
        public static bool IsValid(string literal, AtProtoRecordValueType valueType)
        {
            try
            {
                ToJson(literal ?? string.Empty, valueType, string.Empty);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        public static JToken ToJson(object value, AtProtoRecordValueType valueType, string path)
        {
            if (valueType == AtProtoRecordValueType.UtcTimestamp)
                return new JValue(AtProtoRecordComposer.GetUtcTimestamp());
            if (value == null)
                throw new FormatException("Record field '" + path + "' is linked to a null value.");

            try
            {
                switch (valueType)
                {
                    case AtProtoRecordValueType.String:
                        if (value is bool flag)
                            return new JValue(flag ? "true" : "false");
                        return new JValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                    case AtProtoRecordValueType.Boolean:
                        if (value is bool boolean)
                            return new JValue(boolean);
                        if (value is string booleanText && bool.TryParse(booleanText, out var parsed))
                            return new JValue(parsed);
                        break;
                    case AtProtoRecordValueType.Integer:
                        if (value is string integerText)
                        {
                            if (long.TryParse(
                                integerText,
                                NumberStyles.Integer,
                                CultureInfo.InvariantCulture,
                                out var integer))
                                return new JValue(integer);
                            break;
                        }
                        if (value is bool)
                            break;
                        return new JValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                    case AtProtoRecordValueType.Number:
                        if (value is string numberText)
                        {
                            if (decimal.TryParse(
                                numberText,
                                NumberStyles.Float,
                                CultureInfo.InvariantCulture,
                                out var number))
                                return new JValue(number);
                            break;
                        }
                        if (value is bool)
                            break;
                        return new JValue(Convert.ToDecimal(value, CultureInfo.InvariantCulture));
                    case AtProtoRecordValueType.Json:
                    case AtProtoRecordValueType.JsonObject:
                    case AtProtoRecordValueType.JsonArray:
                        if (!(value is string json))
                            break;
                        var token = JToken.Parse(json);
                        if (valueType == AtProtoRecordValueType.JsonObject
                            && token.Type != JTokenType.Object)
                            break;
                        if (valueType == AtProtoRecordValueType.JsonArray
                            && token.Type != JTokenType.Array)
                            break;
                        return token;
                }
            }
            catch (Exception exception) when (
                exception is OverflowException
                || exception is FormatException
                || exception is InvalidCastException
                || exception is JsonException)
            {
                throw new FormatException(
                    "Record field '" + path + "' cannot convert its value to " + valueType + ".",
                    exception);
            }

            throw new FormatException(
                "Record field '" + path + "' cannot convert its value to " + valueType + ".");
        }
    }

    // Anything the writer can ask for a record's JSON.
    public interface IAtProtoRecordJsonSource
    {
        string BuildRecordJson();
    }

    // Builds record JSON from starting JSON, then fields, then runtime Set* overrides, then timestamps.
    public sealed class AtProtoRecordComposer : MonoBehaviour, IAtProtoRecordJsonSource
    {
        [SerializeField, TextArea(8, 30)] string baseJson = "{}";
        [SerializeField] List<AtProtoRecordField> fields = new();
        // Editor layout only: keeps empty groups and group order.
        [SerializeField] List<string> groups = new();
        [SerializeField] bool addCreatedAt;
        [SerializeField] string createdAtPath = "createdAt";
        [SerializeField] bool addUpdatedAt;
        [SerializeField] string updatedAtPath = "updatedAt";

        [NonSerialized] Dictionary<string, JToken> runtimeValues;

        public string BaseJson
        {
            get => baseJson;
            set => baseJson = value;
        }

        public IReadOnlyList<AtProtoRecordField> Fields => fields;

        public bool AddCreatedAt
        {
            get => addCreatedAt;
            set => addCreatedAt = value;
        }

        public string CreatedAtPath
        {
            get => createdAtPath;
            set => createdAtPath = value;
        }

        public bool AddUpdatedAt
        {
            get => addUpdatedAt;
            set => addUpdatedAt = value;
        }

        public string UpdatedAtPath
        {
            get => updatedAtPath;
            set => updatedAtPath = value;
        }

        public string BuildRecordJson()
        {
            return BuildJson(Formatting.None);
        }

        public string BuildJson(Formatting formatting = Formatting.None)
        {
            return BuildValue().ToString(formatting);
        }

        public JObject BuildValue()
        {
            var root = ParseBaseJson();
            if (fields != null)
            {
                foreach (var field in fields)
                {
                    if (field == null || field.IsIncomplete)
                        continue;
                    SetPath(root, field.Path, field.BuildValue());
                }
            }

            if (runtimeValues != null)
            {
                foreach (var entry in runtimeValues)
                    SetPath(root, entry.Key, entry.Value?.DeepClone() ?? JValue.CreateNull());
            }

            if (addCreatedAt)
                SetPath(root, createdAtPath, new JValue(GetUtcTimestamp()));
            if (addUpdatedAt)
                SetPath(root, updatedAtPath, new JValue(GetUtcTimestamp()));

            return root;
        }

        public AtProtoRecordField AddField(
            string path,
            AtProtoRecordValueType valueType = AtProtoRecordValueType.String,
            string value = null)
        {
            fields ??= new List<AtProtoRecordField>();
            var field = new AtProtoRecordField
            {
                Path = path,
                ValueType = valueType,
                Value = value
            };
            fields.Add(field);
            return field;
        }

        public void SetValue(string path, JToken value)
        {
            EnsurePath(path);
            runtimeValues ??= new Dictionary<string, JToken>(StringComparer.Ordinal);
            runtimeValues[path] = value?.DeepClone() ?? JValue.CreateNull();
        }

        public void SetValue(string path, object value)
        {
            if (value is JToken token)
            {
                SetValue(path, token);
                return;
            }

            SetValue(path, value == null ? null : JToken.FromObject(value));
        }

        public void SetString(string path, string value)
        {
            SetValue(path, new JValue(value ?? string.Empty));
        }

        public void SetBoolean(string path, bool value)
        {
            SetValue(path, new JValue(value));
        }

        public void SetInteger(string path, long value)
        {
            SetValue(path, new JValue(value));
        }

        public void SetNumber(string path, decimal value)
        {
            SetValue(path, new JValue(value));
        }

        public void SetJson(string path, string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new FormatException("JSON value for '" + path + "' is required.");
            SetValue(path, JToken.Parse(json));
        }

        public void SetObject(string path, JObject value)
        {
            SetValue(path, value);
        }

        public void SetArray(string path, JArray value)
        {
            SetValue(path, value);
        }

        public void SetNull(string path)
        {
            SetValue(path, JValue.CreateNull());
        }

        public void SetCreatedAtNow()
        {
            SetString(createdAtPath, GetUtcTimestamp());
        }

        public void SetUpdatedAtNow()
        {
            SetString(updatedAtPath, GetUtcTimestamp());
        }

        public void ClearRuntimeValues()
        {
            runtimeValues?.Clear();
        }

        JObject ParseBaseJson()
        {
            if (string.IsNullOrWhiteSpace(baseJson))
                return new JObject();

            try
            {
                var token = JToken.Parse(baseJson);
                if (!(token is JObject root))
                    throw new FormatException("Composer base JSON must be an object.");
                return (JObject)root.DeepClone();
            }
            catch (FormatException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new FormatException("Composer base JSON is invalid.", exception);
            }
        }

        static void SetPath(JObject root, string path, JToken value)
        {
            EnsurePath(path);
            var parts = path.Split('.');
            var current = root;
            for (var index = 0; index < parts.Length - 1; index++)
            {
                var child = current[parts[index].Trim()] as JObject;
                if (child == null)
                {
                    child = new JObject();
                    current[parts[index].Trim()] = child;
                }
                current = child;
            }

            current[parts[parts.Length - 1].Trim()] = value;
        }

        static void EnsurePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("A record field path is required.", nameof(path));

            var parts = path.Split('.');
            foreach (var part in parts)
            {
                if (string.IsNullOrWhiteSpace(part))
                    throw new ArgumentException(
                        "A record field path contains an empty segment.",
                        nameof(path));
            }
        }

        internal static string GetUtcTimestamp()
        {
            return DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        }
    }
}