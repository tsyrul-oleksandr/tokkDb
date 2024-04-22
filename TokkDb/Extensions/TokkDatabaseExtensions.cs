using Microsoft.Extensions.DependencyInjection;
using TokkDb.Data.Collections;
using TokkDb.Data.Documents.Serializers;
using TokkDb.Disk;
using TokkDb.Disk.Cache;
using TokkDb.Disk.Streams;
using TokkDb.Options;
using TokkDb.Pages;

namespace TokkDb.Extensions;

public static class TokkDatabaseExtensions {
  public static void AddTokkDb(this IServiceCollection services, TokkDatabaseOptions options) {
    services.AddSingleton<IStreamFactory>(new FileStreamFactory(options.FilePath));
    services.AddSingleton<DiskReader>();
    services.AddSingleton<DiskWriter>();
    services.AddSingleton<DiskManager>();
    services.AddSingleton<PageMemoryCache>();
    services.AddSingleton<PageManager>();
    services.AddTransient<TokkDatabase>();
    services.AddSingleton(typeof(DocumentSerializer<>));
    services.AddSingleton(typeof(TokkCollection<>));
    services.AddSingleton(options);
  }
}
