using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using TokkDb.Data.Documents.Values;

namespace TokkDb.Data.Documents;

public class DocumentSerializer {
  public TokkDocument Serialize<T>(T obj, BaseValue keyValue = null) where T : class {
    var type = typeof(T);
    if (!type.IsClass) {
      throw new ArgumentException("Type must be class", nameof(obj));
    }
    var value = SerializeValue(obj, type);
    keyValue ??= SerializeBaseValue(obj, type);
    return new TokkDocument {
      Value = value,
      IdentifierValue = keyValue
    };
  }

  protected virtual BaseValue SerializeBaseValue(object value, Type type) {
    var keyProperty = GetProperties(type).FirstOrDefault(info => info.GetCustomAttribute<KeyAttribute>() != null);
    if (keyProperty == null) {
      throw new ArgumentException("Type must have a key property", nameof(value));
    }
    var key = keyProperty.GetValue(value);
    return SerializeValue(key, keyProperty.PropertyType);
  }
  
  protected virtual BaseValue SerializeValue(object value, Type type) {
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
}
