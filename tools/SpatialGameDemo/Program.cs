using Concierge.Shared.Design;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: dotnet run --project tools/SpatialGameDemo -- <export-folder>");
    return 2;
}

// Original synthetic geometry. No private room scans or licensed game assets.
const string room = """
    {"walls":[
      {"a":[-2,-1.5],"b":[2,-1.5],"height":2.6,"openings":[{"kind":"door","u0":1.4,"u1":2.2,"v0":0,"v1":2}]},
      {"a":[2,-1.5],"b":[2,1.5],"height":2.6,"openings":[]},
      {"a":[2,1.5],"b":[-2,1.5],"height":2.6,"openings":[{"kind":"window","u0":1,"u1":2,"v0":0.8,"v1":2}]},
      {"a":[-2,1.5],"b":[-2,-1.5],"height":2.6,"openings":[]}],
      "objects":[
        {"category":"Desk","center":[-0.8,0.4,0],"size":[1,0.8,0.7],"rotY":0},
        {"category":"Lamp","center":[0.6,0.5,-0.5],"size":[0.4,1,0.4],"rotY":0},
        {"category":"Shelf","center":[1,0.7,0.7],"size":[0.7,1.4,0.4],"rotY":0},
        {"category":"Chair","center":[-0.5,0.4,0.7],"size":[0.5,0.8,0.5],"rotY":0}]}
    """;
var document = RoomCaptureImport.Into(DesignDocument.Blank(), room, "Studio").Document;
var path = SpatialGamePackage.Create(document, "Circle Signal Hunt", args[0], rightsConfirmed: true);
Console.WriteLine(path);
return 0;
