using System.Reflection;
using TokkDb.Pages;
using TokkDb.Values;

namespace TokkDb;

//Turns the properties of an entity type into the column list the catalogue stores. The type
//mapping follows what DocumentSerializer can actually write, so the catalogue never claims a
//column the engine could not store.
internal static class EntityColumns {
  public static List<ColumnDescriptor> Describe(Type entityType) {
    return entityType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
      .Select(property => new ColumnDescriptor(property.Name, MapType(property.PropertyType)))
      .ToList();
  }

  //Null stands for "the engine has no representation for this yet", which is exactly what
  //DocumentSerializer does with such a property.
  private static ValueTypeEnum MapType(Type type) {
    if (type == typeof(int)) {
      return ValueTypeEnum.Int;
    }
    if (type == typeof(string)) {
      return ValueTypeEnum.String;
    }
    if (type == typeof(long)) {
      return ValueTypeEnum.Long;
    }
    if (type == typeof(decimal)) {
      return ValueTypeEnum.Decimal;
    }
    if (type == typeof(DateTime)) {
      return ValueTypeEnum.DateTime;
    }
    if (type == typeof(Guid)) {
      return ValueTypeEnum.Guid;
    }
    if (type.IsArray) {
      return ValueTypeEnum.Array;
    }
    return type.IsClass ? ValueTypeEnum.Object : ValueTypeEnum.Null;
  }
}
