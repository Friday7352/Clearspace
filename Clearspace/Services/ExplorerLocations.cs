namespace Clearspace.Services;

public static class ExplorerLocations
{
    public const string MyPcPath = "clearspace://my-pc";
    public const string NetworkPath = "clearspace://network";
    public const string YourFilesPath = "clearspace://your-files";
    public const string PinnedPath = "clearspace://pinned";
    public const string CloudPath = "clearspace://cloud";
    public const string RecentPath = "clearspace://recent"; // NEW (recent): files and folders opened lately
    public const string ProductivityPath = "clearspace://productivity"; // NEW (productivity): tab groups, recent work, folders
    public const string TabGroupPrefix = "clearspace://tab-group/"; // NEW (productivity): a saved tab group in the sidebar (opens it; never a tab's place)
    public const string CategoryPathPrefix = "clearspace://category/";
}
