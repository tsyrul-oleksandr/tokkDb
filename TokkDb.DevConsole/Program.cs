using Microsoft.Extensions.DependencyInjection;
using TokkDb;
using TokkDb.DevConsole.Model;
using TokkDb.Extensions;
using TokkDb.Options;

var filePath = "/Users/ts/Student/db/temp/tokkdb.db";

var user = new User {
  Id = 1,
  Name = "Test",
  Age = 18,
  Email = "test@qq.com",
  Password = null,
  Tags = ["tag1", "tag2"],
};

var services = new ServiceCollection();
services.AddTokkDb(new TokkDatabaseOptions {
  FilePath = filePath
});
var provider = services.BuildServiceProvider();
var db = provider.GetService<TokkDatabase>();
db.Open();
var collection = db.GetCollection<User>("user_collection");
collection.Insert(user);
