using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using TokkDb.Data.Documents.Values;
using TokkDb.Values;

namespace TokkDb.Data.Documents.Serializers;

public class DocumentSerializer<T> {
  public ObjectDocument Serialize(T obj, IDocumentValue keyValue = null) {
    var type = typeof(T);
    if (!type.IsClass) {
      throw new ArgumentException("Type must be class", nameof(obj));
    }
    var doc = new ObjectDocument();
    doc.SetValue(Serialize(obj, type));
    doc.SetIdentifierValue(keyValue ?? SerializeIdentifierValue(obj, type));
    return doc;
  }

  public T Deserialize(ObjectDocument document) {
    var type = typeof(T);
    if (!type.IsClass) {
      throw new ArgumentException("Type must be class", nameof(document));
    }
    var value = document.Value;
    return (T)Deserialize(value, type);
  }
  
  protected virtual IDocumentValue SerializeIdentifierValue(object value, Type type) {
    var keyProperty = GetProperties(type).FirstOrDefault(info => info.GetCustomAttribute<KeyAttribute>() != null);
    if (keyProperty == null) {
      throw new ArgumentException("Type must have a key property", nameof(value));
    }
    var key = keyProperty.GetValue(value);
    return Serialize(key, keyProperty.PropertyType);
  }
  
  protected virtual object Deserialize(IDocumentValue value, Type type) {
    if (value.Type == ValueTypeEnum.Null) {
      return DeserializeNullValue(value, type);
    }
    if (value.Type == ValueTypeEnum.Int) {
      return DeserializeIntValue(value, type);
    }
    if (value.Type == ValueTypeEnum.String) {
      return DeserializeStringValue(value, type);
    }
    if (value.Type == ValueTypeEnum.Array) {
      return DeserializeArrayValue(value, type);
    }
    if (value.Type == ValueTypeEnum.Object) {
      return DeserializeObjectValue(value, type);
    }
    throw new NotImplementedException();
  }

  protected virtual object DeserializeIntValue(IDocumentValue value, Type type) {
    var intValue = (IntDocumentValue)value;
    return intValue.Value;
  }

  protected virtual object DeserializeStringValue(IDocumentValue value, Type type) {
    var stringValue = (StringDocumentValue)value;
    return stringValue.Value;
  }

  protected virtual object DeserializeNullValue(IDocumentValue value, Type type) {
    return null;
  }
  
  protected virtual object DeserializeObjectValue(IDocumentValue value, Type type) {
    var objectValue = (ObjectDocumentValue)value;
    return DeserializeObjectValue(objectValue.Values, type);
  }

  protected virtual object DeserializeObjectValue(Dictionary<string, IDocumentValue> values, Type type) {
    var obj = Activator.CreateInstance(type);
    foreach (var (key, value) in values) {
      var property = GetProperty(type, key);
      if (property == null) {
        continue;
      }
      property.SetValue(obj, Deserialize(value, property.PropertyType));
    }
    return obj;
  }
  
  protected virtual object DeserializeArrayValue(IDocumentValue value, Type type) {
    var arrayValue = (ArrayDocumentValue)value;
    return DeserializeArrayValue(arrayValue.Values, type);
  }
  
  protected virtual Array DeserializeArrayValue(IDocumentValue[] values, Type type) {
    var elementType = type.GetElementType();
    var array = Array.CreateInstance(elementType, values.Length);
    for (var i = 0; i < values.Length; i++) {
      var value = values[i];
      var arrayItem = Deserialize(value, elementType);
      array.SetValue(arrayItem, i);
    }
    return array;
  }
  
  protected virtual IDocumentValue Serialize(object value, Type type) {
    if (value is null) {
      return SerializeNullValue();
    }
    if (value is int intValue) {
      return SerializeIntValue(intValue);
    }
    if (value is string stringValue) {
      return SerializeStringValue(stringValue);
    }
    if (type.IsArray && value is IEnumerable enumerable) {
      return SerializeArrayValue(type, enumerable);
    }
    if (type.IsClass) {
      return SerializeObjectValue(value, type);
    }
    throw new NotImplementedException();
  }

  protected virtual IDocumentValue SerializeObjectValue(object value, Type type) {
    var properties = GetProperties(type)
      .ToDictionary(property => property.Name, property => Serialize(property.GetValue(value), property.PropertyType));
    return SerializeObjectValue(properties);
  }

  protected virtual IDocumentValue SerializeArrayValue(Type type, IEnumerable enumerable) {
    var items = enumerable.Cast<object>().Select(item => Serialize(item, type.GetElementType())).ToArray();
    return SerializeArrayValue(items);
  }

  protected virtual IDocumentValue SerializeObjectValue(Dictionary<string, IDocumentValue> properties) {
    return new ObjectDocumentValue { Values = properties };
  }

  protected virtual IDocumentValue SerializeArrayValue(IDocumentValue[] items) {
    return new ArrayDocumentValue { Values = items };
  }

  protected virtual IDocumentValue SerializeStringValue(string stringValue) {
    return new StringDocumentValue { Value = stringValue };
  }

  protected virtual IDocumentValue SerializeIntValue(int intValue) {
    return new IntDocumentValue { Value = intValue };
  }

  protected virtual IDocumentValue SerializeNullValue() {
    return new NullDocumentValue();
  }

  protected virtual PropertyInfo[] GetProperties(Type type) {
    return type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
  }
  
  protected virtual PropertyInfo GetProperty(Type type, string key) {
    return type.GetProperty(key, BindingFlags.Public | BindingFlags.Instance);
  }
}
