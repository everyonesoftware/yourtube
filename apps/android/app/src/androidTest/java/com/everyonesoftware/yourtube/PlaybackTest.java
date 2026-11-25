package com.everyonesoftware.yourtube;

import static org.junit.Assert.*;

import android.content.Context;
import android.net.Uri;
import android.os.SystemClock;
import androidx.lifecycle.Lifecycle;
import androidx.media3.common.C;
import androidx.media3.common.MediaItem;
import androidx.media3.common.PlaybackException;
import androidx.media3.common.Player;
import androidx.media3.common.util.UnstableApi;
import androidx.media3.datasource.AssetDataSource;
import androidx.media3.exoplayer.dash.DashMediaSource;
import androidx.media3.ui.PlayerView;
import androidx.test.core.app.ActivityScenario;
import androidx.test.ext.junit.runners.AndroidJUnit4;
import androidx.test.platform.app.InstrumentationRegistry;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicLong;
import java.util.function.Predicate;
import org.junit.Test;
import org.junit.runner.RunWith;

@RunWith(AndroidJUnit4.class)
@UnstableApi
public final class PlaybackTest {
    @Test public void nativeStageRendersDashAndSeeksWhilePaused() {
        try (ActivityScenario<MainActivity> scenario = ActivityScenario.launch(MainActivity.class)) {
            AtomicBoolean firstFrame = new AtomicBoolean();
            scenario.onActivity(activity -> {
                activity.openSource(fixture(), true);
                Player player = stage(activity).getPlayer();
                assertNotNull("The native stage must attach the opened source", player);
                player.addListener(new Player.Listener() {
                    @Override public void onRenderedFirstFrame() { firstFrame.set(true); }
                });
            });
            await(scenario, player -> firstFrame.get() && player.isPlaying()
                    && player.getCurrentPosition() >= 700
                    && player.getCurrentTracks().isTypeSelected(C.TRACK_TYPE_AUDIO)
                    && player.getCurrentTracks().isTypeSelected(C.TRACK_TYPE_VIDEO), "first frame and advancing audio/video");
            scenario.onActivity(activity -> {
                Player player = stage(activity).getPlayer();
                player.pause();
                player.seekTo(8000);
            });
            await(scenario, player -> player.getPlaybackState() == Player.STATE_READY
                    && !player.getPlayWhenReady() && Math.abs(player.getCurrentPosition() - 8000) < 250,
                    "paused seek landing");
            scenario.onActivity(activity -> stage(activity).getPlayer().play());
            await(scenario, player -> player.isPlaying() && player.getCurrentPosition() > 8400, "resume after seek");
        }
    }

    @Test public void leavingForegroundDetachesAndReturnsPaused() {
        try (ActivityScenario<MainActivity> scenario = ActivityScenario.launch(MainActivity.class)) {
            scenario.onActivity(activity -> activity.openSource(fixture(), true));
            await(scenario, player -> player.isPlaying() && player.getCurrentPosition() > 1000, "before background");
            AtomicLong position = new AtomicLong();
            scenario.onActivity(activity -> position.set(stage(activity).getPlayer().getCurrentPosition()));
            scenario.moveToState(Lifecycle.State.CREATED);
            scenario.onActivity(activity -> assertNull("Stopped stage must release its attachment", stage(activity).getPlayer()));
            scenario.moveToState(Lifecycle.State.RESUMED);
            await(scenario, player -> player.getPlaybackState() == Player.STATE_READY && !player.getPlayWhenReady()
                    && Math.abs(player.getCurrentPosition() - position.get()) < 1000, "paused foreground restoration");
        }
    }

    private static PlayerView stage(MainActivity activity) { return activity.findViewById(R.id.player); }

    private static DashMediaSource fixture() {
        Context test = InstrumentationRegistry.getInstrumentation().getContext();
        return new DashMediaSource.Factory(() -> new AssetDataSource(test))
                .createMediaSource(MediaItem.fromUri(Uri.parse("asset:///manifest.mpd")));
    }

    private static void await(ActivityScenario<MainActivity> scenario, Predicate<Player> condition, String phase) {
        AtomicBoolean passed = new AtomicBoolean();
        long deadline = SystemClock.elapsedRealtime() + 30_000;
        do {
            scenario.onActivity(activity -> {
                Player player = stage(activity).getPlayer();
                assertNotNull("Player missing: " + phase, player);
                PlaybackException error = player.getPlayerError();
                assertNull(error == null ? "" : error.getErrorCodeName(), error);
                passed.set(condition.test(player));
            });
            if (passed.get()) return;
            SystemClock.sleep(80);
        } while (SystemClock.elapsedRealtime() < deadline);
        fail("Timed out: " + phase);
    }
}
