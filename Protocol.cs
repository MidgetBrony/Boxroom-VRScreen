namespace Boxroom_VRScreen;
internal static class Protocol
{
    internal const string MapName = "BoxroomVRScreenFrames_v2";
    internal const int HeaderSize = 48, MaxWidth = 1280, MaxHeight = 720;
    internal const int Capacity = HeaderSize + MaxWidth * MaxHeight * 4;
    internal const int Waiting = 1, Playing = 2, Returned = 3, Closed = 4;
    internal const int ResumeRequested = 5, ReturnRequested = 6;
}
