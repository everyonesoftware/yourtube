namespace YourTube.App;

internal static class PlayerStageLayout
{
    // Use a 16:9 theater canvas when width is limiting. The native player fits
    // the actual video's aspect ratio inside that canvas (including Shorts).
    public static double FitHeight(double width, double height) => Math.Min(width * (9d / 16), height);
}
