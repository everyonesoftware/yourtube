package com.everyonesoftware.yourtube;

import android.content.ActivityNotFoundException;
import android.content.Intent;
import android.net.Uri;
import android.os.Bundle;
import android.view.View;
import android.view.WindowManager;
import android.widget.TextView;
import androidx.activity.ComponentActivity;
import androidx.activity.OnBackPressedCallback;
import androidx.activity.result.ActivityResultLauncher;
import androidx.activity.result.contract.ActivityResultContracts;
import androidx.core.graphics.Insets;
import androidx.core.view.ViewCompat;
import androidx.core.view.WindowInsetsCompat;
import androidx.lifecycle.ViewModelProvider;
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
import java.net.URI;
import java.util.function.Consumer;

@UnstableApi
public final class MainActivity extends ComponentActivity {
    static YouTubeAuth authForTest;
    static Consumer<URI> browserForTest;
    private final ActivityResultLauncher<String> videoPicker = registerForActivityResult(
            new ActivityResultContracts.GetContent(), this::openDocument);
    private PlayerView stage;
    private TextView status;
    private ExoPlayer player;
    private MediaSource source;
    private long resumePosition;
    private boolean started;
    private boolean playWhenAttached;
    private boolean local;
    private SignInModel signIn;
    private SignInScreen accountScreen;

    @Override protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setContentView(R.layout.activity_main);
        stage = findViewById(R.id.player);
        status = findViewById(R.id.status);
        local = savedInstanceState != null && savedInstanceState.getBoolean("local");
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
        signIn = new ViewModelProvider(this).get(SignInModel.class);
        accountScreen = new SignInScreen(this, signIn, this::openBrowser, () -> setLocal(true));
        findViewById(R.id.account).setOnClickListener(v -> setLocal(false));
        getOnBackPressedDispatcher().addCallback(this, new OnBackPressedCallback(true) {
            @Override public void handleOnBackPressed() {
                if (local) { setLocal(false); return; }
                switch (signIn.screen) {
                    case REQUEST, CODE, WAITING, ACCOUNTS, CHOOSE -> signIn.cancel();
                    case ERROR -> {
                        if (signIn.retry == SignInModel.Retry.BEGIN) signIn.cancel();
                        else setLocal(true);
                    }
                    default -> finish();
                }
            }
        });
        signIn.connect(authForTest != null ? authForTest : new TvYouTubeAuth(),
                new YouTubeLoginStore(getApplicationContext()), this::renderDestination);
    }

    private void openBrowser(URI uri) {
        if (browserForTest != null) { browserForTest.accept(uri); return; }
        startActivity(new Intent(Intent.ACTION_VIEW, Uri.parse(uri.toASCIIString()))
                .addCategory(Intent.CATEGORY_BROWSABLE));
    }

    private void setLocal(boolean next) {
        local = next;
        if (!local && player != null) player.pause();
        renderDestination();
    }

    private void renderDestination() {
        findViewById(R.id.local_player).setVisibility(local ? View.VISIBLE : View.GONE);
        findViewById(R.id.account_scroll).setVisibility(local ? View.GONE : View.VISIBLE);
        if (!local && signIn.screen != SignInModel.Screen.WELCOME) {
            getWindow().addFlags(WindowManager.LayoutParams.FLAG_SECURE);
        } else {
            getWindow().clearFlags(WindowManager.LayoutParams.FLAG_SECURE);
        }
        accountScreen.render();
    }

    @Override protected void onSaveInstanceState(Bundle outState) {
        super.onSaveInstanceState(outState);
        outState.putBoolean("local", local);
    }

    @Override protected void onDestroy() {
        if (signIn != null) signIn.detach();
        super.onDestroy();
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
        setLocal(true);
        releasePlayer();
        this.source = source;
        resumePosition = 0;
        playWhenAttached = autoplay;
        status.setText(R.string.local_status);
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
