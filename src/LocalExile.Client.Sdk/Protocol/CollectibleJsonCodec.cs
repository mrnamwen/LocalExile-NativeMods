using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace LocalExile.Client.Sdk.Protocol;

/// <summary>
/// Allocation-light, zero-static-cache JSON serializer/deserializer built directly on <see cref="Utf8JsonWriter"/>
/// and <see cref="JsonDocument"/> for types defined inside collectible <see cref="System.Runtime.Loader.AssemblyLoadContext"/>s.
/// Produces camelCase JSON wire-compatible with <see cref="MessageCodec.JsonOptions"/> without touching
/// <see cref="JsonSerializer"/>'s internal global metadata caches, guaranteeing clean ALC unloading.
/// </summary>
internal static class CollectibleJsonCodec
{
    public static string Serialize(object? value, Type declaredType)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteValue(writer, value, value?.GetType() ?? declaredType);
        }

        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    public static object? Deserialize(string json, Type targetType)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        return ReadValue(doc.RootElement, targetType);
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value, Type type)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        Type actualType = value.GetType();
        Type? nullableUnderlying = Nullable.GetUnderlyingType(actualType);
        if (nullableUnderlying is not null)
        {
            actualType = nullableUnderlying;
        }

        if (actualType == typeof(string))
        {
            writer.WriteStringValue((string)value);
            return;
        }

        if (actualType == typeof(bool))
        {
            writer.WriteBooleanValue((bool)value);
            return;
        }

        if (actualType == typeof(int))
        {
            writer.WriteNumberValue((int)value);
            return;
        }

        if (actualType == typeof(long))
        {
            writer.WriteNumberValue((long)value);
            return;
        }

        if (actualType == typeof(uint))
        {
            writer.WriteNumberValue((uint)value);
            return;
        }

        if (actualType == typeof(ulong))
        {
            writer.WriteNumberValue((ulong)value);
            return;
        }

        if (actualType == typeof(short))
        {
            writer.WriteNumberValue((short)value);
            return;
        }

        if (actualType == typeof(ushort))
        {
            writer.WriteNumberValue((ushort)value);
            return;
        }

        if (actualType == typeof(byte))
        {
            writer.WriteNumberValue((byte)value);
            return;
        }

        if (actualType == typeof(sbyte))
        {
            writer.WriteNumberValue((sbyte)value);
            return;
        }

        if (actualType == typeof(float))
        {
            writer.WriteNumberValue((float)value);
            return;
        }

        if (actualType == typeof(double))
        {
            writer.WriteNumberValue((double)value);
            return;
        }

        if (actualType == typeof(decimal))
        {
            writer.WriteNumberValue((decimal)value);
            return;
        }

        if (actualType == typeof(Guid))
        {
            writer.WriteStringValue((Guid)value);
            return;
        }

        if (actualType == typeof(DateTime))
        {
            writer.WriteStringValue((DateTime)value);
            return;
        }

        if (actualType == typeof(DateTimeOffset))
        {
            writer.WriteStringValue((DateTimeOffset)value);
            return;
        }

        if (actualType.IsEnum)
        {
            writer.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
            return;
        }

        if (actualType == typeof(byte[]))
        {
            writer.WriteBase64StringValue((byte[])value);
            return;
        }

        if (value is JsonElement jsonElement)
        {
            jsonElement.WriteTo(writer);
            return;
        }

        if (value is IDictionary dict)
        {
            writer.WriteStartObject();
            foreach (DictionaryEntry entry in dict)
            {
                string key = Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty;
                writer.WritePropertyName(key);
                WriteValue(writer, entry.Value, entry.Value?.GetType() ?? typeof(object));
            }

            writer.WriteEndObject();
            return;
        }

        if (TryGetReadOnlyDictionaryEntries(value, actualType, out IEnumerable<(string Key, object? Val)>? roEntries))
        {
            writer.WriteStartObject();
            foreach ((string key, object? val) in roEntries!)
            {
                writer.WritePropertyName(key);
                WriteValue(writer, val, val?.GetType() ?? typeof(object));
            }

            writer.WriteEndObject();
            return;
        }

        if (value is IEnumerable enumerable)
        {
            writer.WriteStartArray();
            foreach (object? item in enumerable)
            {
                WriteValue(writer, item, item?.GetType() ?? typeof(object));
            }

            writer.WriteEndArray();
            return;
        }

        writer.WriteStartObject();
        foreach (PropertyInfo prop in actualType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead || prop.GetIndexParameters().Length > 0)
            {
                continue;
            }

            string jsonName = ToCamelCase(prop.Name);
            writer.WritePropertyName(jsonName);
            object? propVal = prop.GetValue(value);
            WriteValue(writer, propVal, propVal?.GetType() ?? prop.PropertyType);
        }

        writer.WriteEndObject();
    }

    private static object? ReadValue(JsonElement element, Type targetType)
    {
        if (element.ValueKind == JsonValueKind.Null || element.ValueKind == JsonValueKind.Undefined)
        {
            return targetType.IsValueType && Nullable.GetUnderlyingType(targetType) is null
                ? Activator.CreateInstance(targetType)
                : null;
        }

        Type? nullableUnderlying = Nullable.GetUnderlyingType(targetType);
        if (nullableUnderlying is not null)
        {
            return ReadValue(element, nullableUnderlying);
        }

        if (targetType == typeof(string))
        {
            return element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText();
        }

        if (targetType == typeof(bool))
        {
            return element.GetBoolean();
        }

        if (targetType == typeof(int))
        {
            return element.GetInt32();
        }

        if (targetType == typeof(long))
        {
            return element.GetInt64();
        }

        if (targetType == typeof(uint))
        {
            return element.GetUInt32();
        }

        if (targetType == typeof(ulong))
        {
            return element.GetUInt64();
        }

        if (targetType == typeof(short))
        {
            return element.GetInt16();
        }

        if (targetType == typeof(ushort))
        {
            return element.GetUInt16();
        }

        if (targetType == typeof(byte))
        {
            return element.GetByte();
        }

        if (targetType == typeof(sbyte))
        {
            return element.GetSByte();
        }

        if (targetType == typeof(float))
        {
            return element.GetSingle();
        }

        if (targetType == typeof(double))
        {
            return element.GetDouble();
        }

        if (targetType == typeof(decimal))
        {
            return element.GetDecimal();
        }

        if (targetType == typeof(Guid))
        {
            return element.GetGuid();
        }

        if (targetType == typeof(DateTime))
        {
            return element.GetDateTime();
        }

        if (targetType == typeof(DateTimeOffset))
        {
            return element.GetDateTimeOffset();
        }

        if (targetType.IsEnum)
        {
            if (element.ValueKind == JsonValueKind.String && element.GetString() is { } enumName)
            {
                return Enum.Parse(targetType, enumName, ignoreCase: true);
            }

            return Enum.ToObject(targetType, element.GetInt64());
        }

        if (targetType == typeof(byte[]))
        {
            return element.GetBytesFromBase64();
        }

        if (targetType == typeof(JsonElement))
        {
            return element.Clone();
        }

        if (targetType == typeof(object))
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => element.TryGetInt64(out long l) ? l : element.GetDouble(),
                _ => element.Clone(),
            };
        }

        if (targetType.IsArray && element.ValueKind == JsonValueKind.Array)
        {
            Type elemType = targetType.GetElementType()!;
            int count = element.GetArrayLength();
            Array array = Array.CreateInstance(elemType, count);
            int idx = 0;
            foreach (JsonElement item in element.EnumerateArray())
            {
                array.SetValue(ReadValue(item, elemType), idx++);
            }

            return array;
        }

        if (TryGetStringDictionaryValueType(targetType, out Type? dictValType) && element.ValueKind == JsonValueKind.Object)
        {
            Type concreteDictType = typeof(Dictionary<,>).MakeGenericType(typeof(string), dictValType!);
            IDictionary dict = (IDictionary)Activator.CreateInstance(concreteDictType)!;
            foreach (JsonProperty prop in element.EnumerateObject())
            {
                dict[prop.Name] = ReadValue(prop.Value, dictValType!);
            }

            return dict;
        }

        if (TryGetListElementType(targetType, out Type? listElemType) && element.ValueKind == JsonValueKind.Array)
        {
            Type concreteListType = typeof(List<>).MakeGenericType(listElemType!);
            IList list = (IList)Activator.CreateInstance(concreteListType)!;
            foreach (JsonElement item in element.EnumerateArray())
            {
                list.Add(ReadValue(item, listElemType!));
            }

            return list;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            return ReadComplexObject(element, targetType);
        }

        throw new InvalidDataException($"Cannot deserialize JSON token '{element.ValueKind}' into type '{targetType.FullName}'.");
    }

    private static object ReadComplexObject(JsonElement element, Type targetType)
    {
        var propsByName = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty prop in element.EnumerateObject())
        {
            propsByName[prop.Name] = prop.Value;
        }

        ConstructorInfo[] ctors = targetType.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        ConstructorInfo? parameterless = ctors.FirstOrDefault(c => c.GetParameters().Length == 0);
        ConstructorInfo? primaryCtor = ctors
            .Where(c => c.GetParameters().Length > 0)
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault();

        object instance;
        HashSet<string> assigned = new(StringComparer.OrdinalIgnoreCase);

        if (parameterless is not null && (primaryCtor is null || CanSetAllJsonProperties(targetType, propsByName.Keys)))
        {
            instance = parameterless.Invoke(null);
        }
        else if (primaryCtor is not null)
        {
            ParameterInfo[] parameters = primaryCtor.GetParameters();
            object?[] args = new object?[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                ParameterInfo param = parameters[i];
                if (param.Name is not null && propsByName.TryGetValue(param.Name, out JsonElement val))
                {
                    args[i] = ReadValue(val, param.ParameterType);
                    assigned.Add(param.Name);
                }
                else if (param.HasDefaultValue)
                {
                    args[i] = param.DefaultValue;
                }
                else
                {
                    args[i] = param.ParameterType.IsValueType ? Activator.CreateInstance(param.ParameterType) : null;
                }
            }

            instance = primaryCtor.Invoke(args);
        }
        else if (targetType.IsValueType)
        {
            instance = Activator.CreateInstance(targetType)!;
        }
        else
        {
            throw new InvalidDataException($"Type '{targetType.FullName}' has no public constructor usable for JSON deserialization.");
        }

        foreach (PropertyInfo prop in targetType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanWrite || prop.GetIndexParameters().Length > 0 || assigned.Contains(prop.Name))
            {
                continue;
            }

            if (propsByName.TryGetValue(prop.Name, out JsonElement val))
            {
                prop.SetValue(instance, ReadValue(val, prop.PropertyType));
            }
        }

        return instance;
    }

    private static bool CanSetAllJsonProperties(Type targetType, IEnumerable<string> jsonPropNames)
    {
        var writable = new HashSet<string>(
            targetType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanWrite && p.GetIndexParameters().Length == 0)
                .Select(p => p.Name),
            StringComparer.OrdinalIgnoreCase);

        foreach (string name in jsonPropNames)
        {
            if (!writable.Contains(name))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryGetStringDictionaryValueType(Type type, out Type? valueType)
    {
        valueType = null;
        if (!type.IsGenericType)
        {
            return false;
        }

        Type def = type.GetGenericTypeDefinition();
        if (def == typeof(Dictionary<,>) || def == typeof(IDictionary<,>) || def == typeof(IReadOnlyDictionary<,>))
        {
            Type[] args = type.GetGenericArguments();
            if (args[0] == typeof(string))
            {
                valueType = args[1];
                return true;
            }
        }

        return false;
    }

    private static bool TryGetListElementType(Type type, out Type? elementType)
    {
        elementType = null;
        if (!type.IsGenericType)
        {
            return false;
        }

        Type def = type.GetGenericTypeDefinition();
        if (def == typeof(List<>) || def == typeof(IList<>) || def == typeof(IReadOnlyList<>)
            || def == typeof(ICollection<>) || def == typeof(IReadOnlyCollection<>) || def == typeof(IEnumerable<>))
        {
            elementType = type.GetGenericArguments()[0];
            return true;
        }

        return false;
    }

    private static bool TryGetReadOnlyDictionaryEntries(object value, Type actualType, out IEnumerable<(string Key, object? Val)>? entries)
    {
        entries = null;
        Type? roDict = actualType.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>) && i.GetGenericArguments()[0] == typeof(string));
        if (roDict is null)
        {
            return false;
        }

        var list = new List<(string, object?)>();
        foreach (object? item in (IEnumerable)value)
        {
            if (item is null)
            {
                continue;
            }

            Type kvType = item.GetType();
            string key = (string)(kvType.GetProperty("Key")?.GetValue(item) ?? string.Empty);
            object? val = kvType.GetProperty("Value")?.GetValue(item);
            list.Add((key, val));
        }

        entries = list;
        return true;
    }

    private static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name) || !char.IsUpper(name[0]))
        {
            return name;
        }

        char[] chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (i == 1 && !char.IsUpper(chars[i]))
            {
                break;
            }

            bool hasNext = i + 1 < chars.Length;
            if (i > 0 && hasNext && !char.IsUpper(chars[i + 1]))
            {
                break;
            }

            chars[i] = char.ToLowerInvariant(chars[i]);
        }

        return new string(chars);
    }
}
