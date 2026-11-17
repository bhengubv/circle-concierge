using Concierge.Shared.Design;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: dotnet run --project tools/SpatialGameDemo -- <export-folder>");
    return 2;
}

// The phone's one-tap sample and this fixture use the same original room.
var document = SpatialGameSample.AddTo(DesignDocument.Blank());
var path = SpatialGamePackage.Create(document, "Circle Signal Hunt", args[0], rightsConfirmed: true);
Console.WriteLine(path);
return 0;
