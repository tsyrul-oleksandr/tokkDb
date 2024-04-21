using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using TokkDb.Data.Documents.Values;

namespace TokkDb.Data.Documents;

public class DocumentSerializer {
  public ObjectDocument Serialize<T>(T obj, IDocumentValue keyValue = null) where T : class {
    var type = typeof(T);
    if (!type.IsClass) {
      throw new ArgumentException("Type must be class", nameof(obj));
    }
    var doc = new ObjectDocument();
    doc.SetValue(SerializeValue(obj, type));
    doc.SetIdentifierValue(keyValue ?? SerializeIdentifierValue(obj, type));
    return doc;
  }

  public T Deserialize<T>(ObjectDocument document) where T : class, new() {
    var type = typeof(T);
    if (!type.IsClass) {
      throw new ArgumentException("Type must be class", nameof(document));
    }
    var value = document.Value;
    return (T)DeserializeValue(value, type);
  }

  protected virtual object DeserializeValue(IDocumentValue value, Type type) {
    if (value is NullValue) {
      return null;
    }
    if (value is IntValue intValue) {
      return intValue.Value;
    }
    if (value is StringValue stringValue) {
      return stringValue.Value;
    }
    if (value is ArrayValue arrayValue) {
      return DeserializeArrayValue(type, arrayValue);
    }
    if (value is ObjectValue objectValue) {
      return DeserializeObjectValue(type, objectValue);
    }
    throw new NotImplementedException();
  }

  protected virtual object DeserializeObjectValue(Type type, ObjectValue objectValue) {
    var obj = Activator.CreateInstance(type);
    foreach (var (key, value) in objectValue.Values) {
      var property = GetProperty(type, key);
      if (property == null) {
        continue;
      }
      property.SetValue(obj, DeserializeValue(value, property.PropertyType));
    }
    return obj;
  }
  
  protected virtual object DeserializeArrayValue(Type type, ArrayValue arrayValue) {
    var elementType = type.GetElementType();
    var array = Array.CreateInstance(elementType, arrayValue.Values.Length);
    for (var i = 0; i < arrayValue.Values.Length; i++) {
      var value = arrayValue.Values[i];
      var arrayItem = DeserializeValue(value, elementType);
      array.SetValue(arrayItem, i);
    }
    return array;
  }

  protected virtual IDocumentValue SerializeIdentifierValue(object value, Type type) {
    var keyProperty = GetProperties(type).FirstOrDefault(info => info.GetCustomAttribute<KeyAttribute>() != null);
    if (keyProperty == null) {
      throw new ArgumentException("Type must have a key property", nameof(value));
    }
    var key = keyProperty.GetValue(value);
    return SerializeValue(key, keyProperty.PropertyType);
  }
  
  protected virtual IDocumentValue SerializeValue(object value, Type type) {
    if (value is null) {
      return new NullValue();
    }
    if (value is int intValue) {
      return new IntValue(intValue);
    }
    if (value is string stringValue) {
      return new StringValue(stringValue);
    }
    if (type.IsArray && value is IEnumerable enumerable) {
      var items = enumerable.Cast<object>().Select(item => SerializeValue(item, type.GetElementType())).ToArray();
      return new ArrayValue(items);
    }
    if (type.IsClass) {
      var properties = GetProperties(type);
      return new ObjectValue {
        Values = properties.ToDictionary(property => property.Name, 
          property => SerializeValue(property.GetValue(value), property.PropertyType))
      };
    }
    throw new NotImplementedException();
  }
  
  protected virtual PropertyInfo[] GetProperties(Type type) {
    return type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
  }
  
  protected virtual PropertyInfo GetProperty(Type type, string key) {
    return type.GetProperty(key, BindingFlags.Public | BindingFlags.Instance);
  }
}
