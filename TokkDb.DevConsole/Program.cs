using TokkDb.Core.Buffer;
using TokkDb.Data.Documents;
using TokkDb.Data.Documents.Values;
using TokkDb.DevConsole.Model;

var filePath = "/Users/ts/Student/db/temp/tokkdb.db";
/*using var db = new TokkDatabase(filePath);
db.Open();*/
var bufferSize = 1024;

using var stream = new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.ReadWrite);
var buffer = new byte[bufferSize];
//

var s = new DocumentSerializer();
var document = s.Serialize(new User {
  Id = 1,
  Name = "Test",
  Age = 18,
  Email = "test@qq.com",
  Password = null
});

var writer = new TokkValueWriter(new TokkBuffer(buffer));
document.Write(writer);

stream.Write(buffer, 0, bufferSize);
stream.Flush();
stream.Position = 0;
var readBuffer = new byte[bufferSize];
stream.Read(readBuffer, 0, bufferSize);

var readDocument = new TokkDocument();
var reader = new TokkValueReader(new TokkBuffer(readBuffer));
readDocument.Read(reader);
Console.WriteLine(readDocument.Value);