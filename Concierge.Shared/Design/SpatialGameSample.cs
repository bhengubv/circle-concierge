namespace Concierge.Shared.Design;

/// <summary>An original room fixture for trying the Mobile game flow without a scan or model.</summary>
public static class SpatialGameSample
{
    private const string Room = """
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

    public static DesignDocument AddTo(DesignDocument current)
        => RoomCaptureImport.Into(current, Room, "Sample room").Document;
}
