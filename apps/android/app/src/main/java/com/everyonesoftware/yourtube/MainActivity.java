package com.everyonesoftware.yourtube;

import android.content.ActivityNotFoundException;
import android.net.Uri;
import android.os.Bundle;
import android.view.View;
import android.view.WindowManager;
import android.widget.TextView;
import androidx.activity.ComponentActivity;
import androidx.activity.result.ActivityResultLauncher;
import androidx.activity.result.contract.ActivityResultContracts;
import androidx.core.graphics.Insets;
import androidx.core.view.ViewCompat;
import androidx.core.view.WindowInsetsCompat;
import androidx.media3.common.AudioAttributes;
import androidx.media3.common.MediaItem;
import androidx.media3.common.PlaybackException;
import androidx.media3.common.Player;
import androidx.media3.common.util.UnstableApi;
import androidx.media3.datasource.ContentDataSource;
import androidx.media3.exoplayer.ExoPlayer;
import androidx.media3.exoplayer.source.MediaSource;
import androidx.media3.exoplayer.source.ProgressiveMediaSource;
import androidx.media3.ui.PlayerView;

@UnstableApi
public final class MainActivity extends ComponentActivity {
    private final ActivityResultLauncher<String> videoPicker = registerForActivityResult(
            new ActivityResultContracts.GetContent(), this::openDocument);
    private PlayerView stage;
    private TextView status;
    private ExoPlayer player;
    private MediaSource source;
    private long resumePosition;
    private boolean started;
    private boolean playWhenAttached;

    @Override protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setContentView(R.layout.activity_main);
        stage = findViewById(R.id.player);
        status = findViewById(R.id.status);
        View root = findViewById(R.id.root);
        int left = root.getPaddingLeft(), top = root.getPaddingTop();
        int right = root.getPaddingRight(), bottom = root.getPaddingBottom();
        ViewCompat.setOnApplyWindowInsetsListener(root, (view, windowInsets) -> {
            Insets bars = windowInsets.getInsets(WindowInsetsCompat.Type.systemBars() | WindowInsetsCompat.Type.displayCutout());
            view.setPadding(left + bars.left, top + bars.top, right + bars.right, bottom + bars.bottom);
            return windowInsets;
        });
        findViewById(R.id.open_video).setOnClickListener(v -> {
            if (player != null) player.pause();
            try {
                videoPicker.launch("video/*");
            } catch (ActivityNotFoundException ignored) {
                status.setText(R.string.picker_error);
            }
        });
    }

    private void openDocument(Uri uri) {
        if (uri == null) return;
        if (!"content".equals(uri.getScheme())) {
            status.setText(R.string.playback_error);
            return;
        }
        openSource(new ProgressiveMediaSource.Factory(() -> new ContentDataSource(this))
                .createMediaSource(MediaItem.fromUri(uri)), true);
    }

    void openSource(MediaSource source, boolean autoplay) {
        releasePlayer();
        this.source = source;
        resumePosition = 0;
        playWhenAttached = autoplay;
        status.setText(R.string.development_status);
        attachPlayer();
    }

    @Override protected void onStart() {
        super.onStart();
        started = true;
        attachPlayer();
    }

    @Override protected void onStop() {
        started = false;
        if (player != null) resumePosition = player.getCurrentPosition();
        playWhenAttached = false;
        releasePlayer();
        super.onStop();
    }

    private void attachPlayer() {
        if (!started || source == null || player != null) return;
        ExoPlayer attached = new ExoPlayer.Builder(this).build();
        player = attached;
        stage.setPlayer(attached);
        attached.setAudioAttributes(AudioAttributes.DEFAULT, true);
        attached.setHandleAudioBecomingNoisy(true);
        attached.addListener(new Player.Listener() {
            @Override public void onIsPlayingChanged(boolean playing) {
                if (player != attached) return;
                if (playing) getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
                else getWindow().clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
            }

            @Override public void onPlayerError(PlaybackException error) {
                if (player == attached) status.setText(R.string.playback_error);
            }
        });
        attached.setMediaSource(source, resumePosition);
        attached.setPlayWhenReady(playWhenAttached);
        attached.prepare();
    }

    private void releasePlayer() {
        ExoPlayer retiring = player;
        player = null;
        stage.setPlayer(null);
        getWindow().clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        if (retiring != null) retiring.release();
    }
}
