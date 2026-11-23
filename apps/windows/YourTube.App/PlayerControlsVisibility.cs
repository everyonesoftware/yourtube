namespace YourTube.App;

internal static class PlayerControlsVisibility
{
    public static bool ShouldShow(bool fullscreen, bool playing, long? lastMouseActivity, long now, bool interacting) =>
        !fullscreen || !playing || interacting || (lastMouseActivity is long activity && now - activity < 3_000);
}
