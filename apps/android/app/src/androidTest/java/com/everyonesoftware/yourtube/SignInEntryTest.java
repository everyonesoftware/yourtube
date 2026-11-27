package com.everyonesoftware.yourtube;

import static org.junit.Assert.*;

import android.view.View;
import android.widget.TextView;
import androidx.media3.common.util.UnstableApi;
import androidx.test.core.app.ActivityScenario;
import androidx.test.ext.junit.runners.AndroidJUnit4;
import org.junit.Test;
import org.junit.runner.RunWith;

@RunWith(AndroidJUnit4.class)
@UnstableApi
public final class SignInEntryTest {
    @Test public void welcomeOffersSignInAndLocalPlaybackStillHasAccountEntry() {
        try (ActivityScenario<MainActivity> scenario = ActivityScenario.launch(MainActivity.class)) {
            boolean[] ready = {false};
            long deadline = android.os.SystemClock.elapsedRealtime() + 8_000;
            do {
                scenario.onActivity(activity -> ready[0] = visible(activity.findViewById(android.R.id.content), "Sign in to YouTube"));
                if (!ready[0]) android.os.SystemClock.sleep(50);
            } while (!ready[0] && android.os.SystemClock.elapsedRealtime() < deadline);
            scenario.onActivity(activity -> {
                View root = activity.findViewById(android.R.id.content);
                assertTrue("Welcome must offer YouTube sign-in", visible(root, "Sign in to YouTube"));
                View local = root.findViewWithTag("local");
                assertNotNull("Local playback must not require authorization", local);
                local.performClick();
                assertTrue(activity.findViewById(R.id.open_video).isShown());
                assertTrue(visible(root, "Account"));
                root.findViewWithTag("account").performClick();
                assertTrue(visible(root, "Sign in to YouTube"));
            });
        }
    }

    private static boolean visible(View root, String text) {
        if (!root.isShown()) return false;
        if (root instanceof TextView v && text.contentEquals(v.getText())) return true;
        if (root instanceof android.view.ViewGroup group)
            for (int i = 0; i < group.getChildCount(); i++)
                if (visible(group.getChildAt(i), text)) return true;
        return false;
    }
}
