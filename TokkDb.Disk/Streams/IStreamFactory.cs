namespace TokkDb.Disk.Streams;

public interface IStreamFactory {
  Stream Get(bool readOnly);
}
