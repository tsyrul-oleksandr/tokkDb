namespace TokkDb.Data.Collections;

public interface ITokkCollection<T> where T : class, new() {
  void Insert(T entity);
}
