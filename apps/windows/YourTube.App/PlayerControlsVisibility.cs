namespace YourTube.App;

internal static class PlayerControlsVisibility
{
    public static bool ShouldShow(bool playing, long? lastActivity, long now, bool interacting) =>
        !playing || interacting || (lastActivity is long activity && now - activity < 3_000);
}
