package com.everyonesoftware.yourtube;

import static org.junit.Assert.*;

import android.os.SystemClock;
import android.view.View;
import android.widget.TextView;
import androidx.media3.common.util.UnstableApi;
import androidx.test.core.app.ActivityScenario;
import androidx.test.ext.junit.runners.AndroidJUnit4;
import androidx.test.platform.app.InstrumentationRegistry;
import java.net.URI;
import java.util.List;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;

import org.junit.Test;
import org.junit.runner.RunWith;

@RunWith(AndroidJUnit4.class)
@UnstableApi
public final class SignInFlowTest {
    @Test public void launcherDoesNotCreateAnIndependentSignedInTask() throws Exception {
        var context = InstrumentationRegistry.getInstrumentation().getTargetContext();
        var activity = context.getPackageManager().getActivityInfo(new android.content.ComponentName(context, MainActivity.class), 0);
        assertEquals(android.content.pm.ActivityInfo.LAUNCH_SINGLE_TASK, activity.launchMode);
        assertEquals(android.content.pm.ActivityInfo.DOCUMENT_LAUNCH_NEVER, activity.documentLaunchMode);
    }

    @Test public void storageRetrySavesRotatedCredentialWithoutRefreshingOldOneAgain() throws Exception {
        Fake provider = new Fake(false);
        var context = InstrumentationRegistry.getInstrumentation().getTargetContext();
        java.io.File blocked = new java.io.File(context.getNoBackupFilesDir(), "youtube-sign-in/login.new");
        java.io.File obstruction = new java.io.File(blocked, "obstruction");
        withFixture(provider, store -> {
            store.save(new YouTubeLoginStore.Saved("old-fixture", true, "brand-page"));
            assertTrue(blocked.mkdir());
            assertTrue(obstruction.createNewFile());
            try (ActivityScenario<MainActivity> scenario = ActivityScenario.launch(MainActivity.class)) {
                await(scenario, "Couldn’t access saved sign-in on this phone.");
                assertTrue(obstruction.delete());
                assertTrue(blocked.delete());
                tap(scenario, "retry");
                await(scenario, "Brand history");
                assertEquals(1, provider.refreshes.get());
                assertEquals("refresh-fixture", store.load().refreshToken());
                assertEquals("brand-page", store.load().pageId());
            } finally {
                obstruction.delete();
                blocked.delete();
            }
        });
    }

    @Test public void closingDuringRotationDoesNotLoseReplacementToken() throws Exception {
        Fake provider = new Fake(false);
        provider.holdRotation = true;
        withFixture(provider, store -> {
            store.save(new YouTubeLoginStore.Saved("old-fixture", true, "brand-page"));
            try (ActivityScenario<MainActivity> ignored = ActivityScenario.launch(MainActivity.class)) {
                assertTrue(provider.started.await(8, TimeUnit.SECONDS));
            } finally {
                provider.release.countDown();
            }
            try (ActivityScenario<MainActivity> reopened = ActivityScenario.launch(MainActivity.class)) {
                await(reopened, "Brand history");
                assertEquals(1, provider.oldTokenRequests.get());
                assertEquals("refresh-fixture", store.load().refreshToken());
            }
        });
    }

    @Test public void handoffSelectsBrandReadsHistoryRestoresAndSignsOut() throws Exception {
        Fake provider = new Fake(false);
        withFixture(provider, store -> {
            try (ActivityScenario<MainActivity> scenario = ActivityScenario.launch(MainActivity.class)) {
                tap(scenario, "signin");
                assertEquals(0, provider.polls.get());
                tap(scenario, "browser");
                tap(scenario, "identity1");
                await(scenario, "Brand history");
                assertEquals("brand-page", provider.historyIdentity);
                assertEquals(1, provider.polls.get());
                scenario.recreate();
                await(scenario, "Brand history");
            }
            try (ActivityScenario<MainActivity> reopened = ActivityScenario.launch(MainActivity.class)) {
                await(reopened, "Brand history");
                assertEquals(1, provider.refreshes.get());
                tap(reopened, "signout");
                await(reopened, "Connect your YouTube account");
                assertNull(store.load());
            }
        });
    }

    @Test public void cancelRejectsAnOldApprovalThatIgnoresInterrupt() throws Exception {
        Fake provider = new Fake(true);
        withFixture(provider, store -> {
            try (ActivityScenario<MainActivity> scenario = ActivityScenario.launch(MainActivity.class)) {
                tap(scenario, "signin");
                tap(scenario, "browser");
                assertTrue(provider.started.await(5, TimeUnit.SECONDS));
                tap(scenario, "cancel");
                provider.release.countDown();
                assertTrue(provider.returning.await(5, TimeUnit.SECONDS));
                await(scenario, "Connect your YouTube account");
                assertEquals(0, provider.accountReads.get());
                assertNull(store.load());
            }
        });
    }

    @Test public void deniedBrandHistoryKeepsSignInAndAllowsSwitchToOwner() throws Exception {
        Fake provider = new Fake(false);
        provider.denyBrand = true;
        withFixture(provider, store -> {
            try (ActivityScenario<MainActivity> scenario = ActivityScenario.launch(MainActivity.class)) {
                tap(scenario, "signin");
                tap(scenario, "browser");
                tap(scenario, "identity1");
                await(scenario, "YouTube did not allow access to this identity’s History.");
                assertEquals("brand-page", store.load().pageId());
                tap(scenario, "switch");
                tap(scenario, "identity0");
                await(scenario, "Owner history");
                assertTrue(store.load().hasSelection());
                assertNull(store.load().pageId());
                assertEquals(1, provider.polls.get());
            }
        });
    }

    private interface Run { void run(YouTubeLoginStore store) throws Exception; }
    private static void withFixture(Fake fake, Run run) throws Exception {
        YouTubeLoginStore store = new YouTubeLoginStore(InstrumentationRegistry.getInstrumentation().getTargetContext());
        store.clear();
        MainActivity.authForTest = fake;
        MainActivity.browserForTest = uri -> assertEquals("https://www.youtube.com/activate", uri.toString());
        try { run.run(store); }
        finally {
            fake.release.countDown();
            MainActivity.authForTest = null;
            MainActivity.browserForTest = null;
            store.clear();
        }
    }

    private static void tap(ActivityScenario<MainActivity> scenario, String tag) {
        waitFor(scenario, activity -> {
            View button = activity.findViewById(android.R.id.content).findViewWithTag(tag);
            return button != null && button.isShown() && button.performClick();
        }, tag);
    }

    private static void await(ActivityScenario<MainActivity> scenario, String text) {
        waitFor(scenario, activity -> visible(activity.findViewById(android.R.id.content), text), text);
    }

    private static void waitFor(ActivityScenario<MainActivity> scenario, java.util.function.Predicate<MainActivity> predicate, String label) {
        long deadline = SystemClock.elapsedRealtime() + 12_000;
        while (SystemClock.elapsedRealtime() < deadline) {
            boolean[] found = {false};
            scenario.onActivity(activity -> found[0] = predicate.test(activity));
            if (found[0]) return;
            SystemClock.sleep(50);
        }
        fail("Not shown: " + label);
    }

    private static boolean visible(View root, String text) {
        if (!root.isShown()) return false;
        if (root instanceof TextView v && text.contentEquals(v.getText())) return true;
        if (root instanceof android.view.ViewGroup group)
            for (int i = 0; i < group.getChildCount(); i++)
                if (visible(group.getChildAt(i), text)) return true;
        return false;
    }

    private static final class Fake implements YouTubeAuth {
        final boolean held;
        final AtomicInteger polls = new AtomicInteger();
        final AtomicInteger refreshes = new AtomicInteger();
        final AtomicInteger accountReads = new AtomicInteger();
        final CountDownLatch started = new CountDownLatch(1), release = new CountDownLatch(1), returning = new CountDownLatch(1);
        volatile String historyIdentity;
        boolean denyBrand;
        boolean holdRotation;
        final AtomicInteger oldTokenRequests = new AtomicInteger();
        Fake(boolean held) { this.held = held; }
        @Override public Authorization begin(String id) {
            return new Authorization("SAMPLE", URI.create("https://www.youtube.com/activate"), "device-fixture", expiry(), 1);
        }
        @Override public Session complete(Authorization authorization) {
            polls.incrementAndGet();
            started.countDown();
            while (held && release.getCount() > 0) {
                try { if (release.await(5, TimeUnit.SECONDS)) break; }
                catch (InterruptedException ignored) { /* Stale-result fixture deliberately ignores cancel. */ }
            }
            returning.countDown();
            return session();
        }
        @Override public Session refresh(String token) throws Failure {
            refreshes.incrementAndGet();
            if (holdRotation && "old-fixture".equals(token)) {
                if (oldTokenRequests.incrementAndGet() != 1) throw new Failure(Problem.AUTH_REQUIRED);
                started.countDown();
                while (release.getCount() != 0) {
                    try { release.await(1, TimeUnit.SECONDS); }
                    catch (InterruptedException ignored) { /* The provider has already rotated the token. */ }
                }
            }
            return session();
        }
        @Override public List<Identity> identities(Session session) {
            accountReads.incrementAndGet();
            return java.util.Arrays.asList(new Identity("Personal", null, null), new Identity("Brand", "Channel", "brand-page"));
        }
        @Override public List<Video> history(Session session, Identity identity) throws Failure {
            historyIdentity = identity.pageId();
            if (denyBrand && identity.pageId() != null) throw new Failure(Problem.HISTORY_DENIED);
            return java.util.Collections.singletonList(new Video(identity.pageId() == null ? "Owner history" : "Brand history", "Channel", 62));
        }
        private static long expiry() { return System.currentTimeMillis() + 90_000; }
        private static Session session() { return new Session("access-fixture", "refresh-fixture", expiry()); }
    }
}
