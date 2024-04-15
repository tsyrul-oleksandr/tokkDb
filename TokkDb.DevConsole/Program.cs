using TokkDb.Core.Buffer;
using TokkDb.Core.Metadata;

var filePath = "/Users/ts/Student/db/temp/tokkdb.db";
/*using var db = new TokkDatabase(filePath);
db.Open();*/
var bufferSize = 1024;

using var stream = new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.ReadWrite);
var buffer = new byte[bufferSize];
stream.Read(buffer, 0, bufferSize);
var page = new MetadataPage(new TokkBuffer(buffer));
page.Initialize();
page.Header.Id = 10;
page.Header.Type = 11;
page.Save();
stream.Write(buffer, 0, bufferSize);
stream.Flush();