package com.everyonesoftware.yourtube;

import static org.junit.Assert.*;

import android.content.Context;
import androidx.test.ext.junit.runners.AndroidJUnit4;
import androidx.test.platform.app.InstrumentationRegistry;
import java.io.File;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.security.Key;
import java.security.KeyStore;
import java.util.Arrays;
import java.util.UUID;
import org.junit.After;
import org.junit.Before;
import org.junit.Test;
import org.junit.runner.RunWith;

@RunWith(AndroidJUnit4.class)
public final class YouTubeLoginStoreTest {
    private static final String TOKEN = "fixture-refresh-token-not-a-real-credential";
    private static final String PAGE = "fixture-brand-page";

    private File directory;
    private String alias;
    private File login;
    private File pending;

    @Before public void setUp() throws Exception {
        Context context = InstrumentationRegistry.getInstrumentation().getTargetContext();
        directory = Files.createTempDirectory(context.getNoBackupFilesDir().toPath(),
                "login-store-test-").toFile();
        alias = directory.getName();
        login = new File(directory, "login");
        pending = new File(directory, "login.new");
    }

    @After public void tearDown() throws Exception {
        try {
            if (alias != null) keys().deleteEntry(alias);
        } finally {
            remove(directory);
        }
    }

    @Test public void reopensSavedBrandSelectionAndOverwritesWholeRecord() throws Exception {
        assertNull(open().load());
        assertFalse(keys().containsAlias(alias));
        YouTubeLoginStore.Saved brand = new YouTubeLoginStore.Saved(TOKEN, true, PAGE);
        open().save(brand);
        assertEquals(brand, open().load());

        YouTubeLoginStore.Saved noSelection = new YouTubeLoginStore.Saved("fixture-other", false, null);
        open().save(noSelection);
        assertEquals(noSelection, open().load());

        YouTubeLoginStore.Saved owner = new YouTubeLoginStore.Saved("fixture-rotated", true, null);
        open().save(owner);
        assertEquals(owner, open().load());
    }

    @Test public void ciphertextHidesBothValuesAndUsesFreshEncryptionForSameLogin() throws Exception {
        YouTubeLoginStore.Saved saved = new YouTubeLoginStore.Saved(TOKEN, true, PAGE);
        assertFalse(saved.toString().contains(TOKEN));
        assertFalse(saved.toString().contains(PAGE));
        open().save(saved);
        Key key = keys().getKey(alias, null);
        assertNotNull(key);
        assertNull(key.getEncoded());
        byte[] first = Files.readAllBytes(login.toPath());
        String onDisk = new String(first, StandardCharsets.ISO_8859_1);
        assertFalse(onDisk.contains(TOKEN));
        assertFalse(onDisk.contains(PAGE));
        open().save(saved);
        assertFalse(Arrays.equals(first, Files.readAllBytes(login.toPath())));
        assertFalse(pending.exists());
        assertEquals(saved, open().load());
    }

    @Test public void alteredCiphertextFailsWithOnlyFixedPublicError() throws Exception {
        open().save(new YouTubeLoginStore.Saved(TOKEN, true, PAGE));
        byte[] data = Files.readAllBytes(login.toPath());
        data[data.length - 1] ^= 1;
        Files.write(login.toPath(), data);
        assertSafeFailure(assertThrows(YouTubeLoginStore.Failure.class, () -> open().load()));
    }

    @Test public void failedReplacementKeepsPreviouslyCommittedLogin() throws Exception {
        YouTubeLoginStore.Saved old = new YouTubeLoginStore.Saved(TOKEN, true, PAGE);
        open().save(old);
        assertTrue(pending.mkdir());
        Files.write(new File(pending, "obstruction").toPath(), new byte[] {1});
        assertSafeFailure(assertThrows(YouTubeLoginStore.Failure.class,
                () -> open().save(new YouTubeLoginStore.Saved("fixture-new", false, null))));
        assertEquals(old, open().load());
    }

    @Test public void clearIsDurableAndLeavesInstallIdAlone() throws Exception {
        String id = open().deviceId();
        assertEquals(id, UUID.fromString(id).toString());
        assertEquals(id, open().deviceId());
        open().save(new YouTubeLoginStore.Saved(TOKEN, true, PAGE));
        open().clear();
        assertNull(open().load());
        assertFalse(login.exists());
        assertFalse(keys().containsAlias(alias));
        assertEquals(id, open().deviceId());
        open().clear();
        assertNull(open().load());
    }

    @Test public void failedFileRemovalStillRevokesKeyAndPreventsRestore() throws Exception {
        open().save(new YouTubeLoginStore.Saved(TOKEN, true, PAGE));
        byte[] orphan = Files.readAllBytes(login.toPath());
        Files.delete(login.toPath());
        assertTrue(login.mkdir());
        Files.write(new File(login, "obstruction").toPath(), new byte[] {1});
        assertSafeFailure(assertThrows(YouTubeLoginStore.Failure.class, () -> open().clear()));
        assertFalse(keys().containsAlias(alias));
        remove(login);
        Files.write(login.toPath(), orphan);
        assertSafeFailure(assertThrows(YouTubeLoginStore.Failure.class, () -> open().load()));
        assertFalse(keys().containsAlias(alias));
        YouTubeLoginStore.Saved replacement = new YouTubeLoginStore.Saved("fixture-new", false, null);
        open().save(replacement);
        assertEquals(replacement, open().load());
    }

    private YouTubeLoginStore open() {
        return new YouTubeLoginStore(directory, alias);
    }

    private static void assertSafeFailure(YouTubeLoginStore.Failure failure) {
        assertEquals("Could not access saved YouTube sign-in on this device.", failure.getMessage());
        assertNull(failure.getCause());
        assertEquals(0, failure.getSuppressed().length);
    }

    private static KeyStore keys() throws Exception {
        KeyStore store = KeyStore.getInstance("AndroidKeyStore");
        store.load(null);
        return store;
    }

    private static void remove(File file) throws Exception {
        if (file == null) return;
        File[] children = file.listFiles();
        if (children != null) for (File child : children) remove(child);
        Files.deleteIfExists(file.toPath());
    }
}
