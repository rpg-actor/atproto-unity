using System;
using UnityEngine;

namespace AtProtoUnity
{
    public enum AtProtoSchemaFieldType
    {
        String,
        UtcTimestamp,
        Boolean,
        Integer,
        JsonObject,
        JsonArray,
        Json
    }

    [Serializable]
    public sealed class AtProtoSchemaRecordField
    {
        [SerializeField] string path;
        [SerializeField] AtProtoSchemaFieldType fieldType = AtProtoSchemaFieldType.String;
        [SerializeField] bool required;
        [SerializeField] bool included = true;
        [SerializeField] string description;
        [SerializeField] string value;

        public string Path
        {
            get => path;
            set => path = value;
        }

        public AtProtoSchemaFieldType FieldType
        {
            get => fieldType;
            set => fieldType = value;
        }

        public bool Required
        {
            get => required;
            set => required = value;
        }

        public bool Included
        {
            get => included;
            set => included = value;
        }

        public string Description
        {
            get => description;
            set => description = value;
        }

        public string Value
        {
            get => value;
            set => this.value = value;
        }
    }

    [AddComponentMenu("")]
    public sealed class AtProtoSchemaRecordComposer : AtProtoSchemaComposer
    {
    }

}