using System;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AtProtoUnity
{
    // Reads a public field, property or parameterless method from an object for a composer field.
    public static class AtProtoRecordBinding
    {
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance;
        const string MethodSuffix = "()";

        public static List<string> GetMembers(UnityEngine.Object source)
        {
            var result = new List<string>();
            if (source == null)
                return result;

            if (source is GameObject gameObject)
            {
                foreach (var component in gameObject.GetComponents<Component>())
                {
                    if (component == null)
                        continue;
                    foreach (var member in GetMembers(component))
                        result.Add(component.GetType().Name + "." + member);
                }
                return result;
            }

            var type = source.GetType();
            foreach (var field in type.GetFields(Flags))
            {
                if (IsBindable(field.DeclaringType) && IsSupported(field.FieldType))
                    result.Add(field.Name);
            }
            foreach (var property in type.GetProperties(Flags))
            {
                if (property.CanRead
                    && property.GetIndexParameters().Length == 0
                    && IsBindable(property.DeclaringType)
                    && IsSupported(property.PropertyType))
                    result.Add(property.Name);
            }
            foreach (var method in type.GetMethods(Flags))
            {
                if (!method.IsSpecialName
                    && !method.IsGenericMethod
                    && method.GetParameters().Length == 0
                    && IsBindable(method.DeclaringType)
                    && IsSupported(method.ReturnType))
                    result.Add(method.Name + MethodSuffix);
            }
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        public static JToken Read(
            UnityEngine.Object source,
            string member,
            AtProtoRecordValueType valueType,
            string path)
        {
            return AtProtoRecordValueConverter.ToJson(
                ReadValue(source, member, path),
                valueType,
                path);
        }

        static object ReadValue(UnityEngine.Object source, string member, string path)
        {
            if (source == null)
                throw new FormatException("Record field '" + path + "' has no linked source.");
            if (string.IsNullOrEmpty(member))
                throw new FormatException("Record field '" + path + "' has no linked member.");

            object target = source;
            var name = member;
            if (source is GameObject gameObject)
            {
                var separator = member.IndexOf('.');
                if (separator < 0)
                    throw new FormatException(
                        "Record field '" + path + "' must link to Component.Member.");

                var typeName = member.Substring(0, separator);
                name = member.Substring(separator + 1);
                target = null;
                foreach (var component in gameObject.GetComponents<Component>())
                {
                    if (component != null && component.GetType().Name == typeName)
                    {
                        target = component;
                        break;
                    }
                }
                if (target == null)
                    throw new FormatException(
                        "Record field '" + path + "' could not find component " + typeName + ".");
            }

            var type = target.GetType();
            if (name.EndsWith(MethodSuffix, StringComparison.Ordinal))
            {
                var methodName = name.Substring(0, name.Length - MethodSuffix.Length);
                var method = type.GetMethod(methodName, Flags, null, Type.EmptyTypes, null);
                if (method == null || !IsSupported(method.ReturnType))
                    throw new FormatException(
                        "Record field '" + path + "' could not call " + name + ".");
                return method.Invoke(target, null);
            }

            var field = type.GetField(name, Flags);
            if (field != null && IsSupported(field.FieldType))
                return field.GetValue(target);

            var property = type.GetProperty(name, Flags);
            if (property == null || !property.CanRead || !IsSupported(property.PropertyType))
                throw new FormatException(
                    "Record field '" + path + "' could not read member " + name + ".");
            return property.GetValue(target);
        }

        static bool IsBindable(Type declaringType)
        {
            return declaringType != typeof(object)
                && declaringType != typeof(UnityEngine.Object)
                && declaringType != typeof(Component)
                && declaringType != typeof(Behaviour)
                && declaringType != typeof(MonoBehaviour);
        }

        static bool IsSupported(Type type)
        {
            if (type == typeof(string) || type == typeof(decimal) || type.IsEnum)
                return true;
            return type.IsPrimitive
                && type != typeof(char)
                && type != typeof(IntPtr)
                && type != typeof(UIntPtr);
        }
    }
}
