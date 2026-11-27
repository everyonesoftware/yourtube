package com.everyonesoftware.yourtube;

import android.content.Context;
import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import java.io.DataInputStream;
import java.io.File;
import java.io.IOException;
import java.io.InputStream;
import java.nio.ByteBuffer;
import java.nio.CharBuffer;
import java.nio.channels.FileChannel;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.NoSuchFileException;
import java.nio.file.StandardCopyOption;
import java.nio.file.StandardOpenOption;
import java.security.GeneralSecurityException;
import java.security.KeyStore;
import java.util.Arrays;
import java.util.UUID;
import javax.crypto.Cipher;
import javax.crypto.KeyGenerator;
import javax.crypto.SecretKey;
import javax.crypto.spec.GCMParameterSpec;

/**
 * Synchronous local storage. Call off the UI thread and serialize operations across instances.
 * A missing login returns null; an unreadable or unauthenticated record throws Failure and is not
 * automatically removed. There is no plaintext fallback. Only save creates the encryption key.
 */
final class YouTubeLoginStore {
    private static final String KEYSTORE = "AndroidKeyStore";
    private static final String TRANSFORMATION = "AES/GCM/NoPadding";
    // File layout: version (also authenticated as AAD), IV, ciphertext length, ciphertext + tag.
    private static final int VERSION = 1;
    private static final int IV_BYTES = 12;
    private static final int TAG_BYTES = 16;
    private static final int MAX_FIELD_BYTES = 16_384;
    private static final int MAX_CIPHERTEXT_BYTES = 1 + Integer.BYTES + 2 * MAX_FIELD_BYTES + TAG_BYTES;
    private static final int NO_SELECTION = 0;
    private static final int OWNER_SELECTION = 1;
    private static final int BRAND_SELECTION = 2;

    private final String alias;
    private final File login;
    private final File device;

    YouTubeLoginStore(Context context) {
        this(context.getNoBackupFilesDir().toPath().resolve("youtube-sign-in").toFile(),
                "com.everyonesoftware.yourtube.youtube-sign-in.v1");
    }

    // Isolated filesystem and Keystore namespace for instrumentation.
    YouTubeLoginStore(File directory, String alias) {
        this.alias = alias;
        login = new File(directory, "login");
        device = new File(directory, "device-id");
    }

    record Saved(String refreshToken, boolean hasSelection, String pageId) {
        @Override public String toString() { return "YouTubeLoginStore.Saved[redacted]"; }
    }

    static final class Failure extends Exception {
        private static final long serialVersionUID = 1L;

        private Failure() {
            super("Could not access saved YouTube sign-in on this device.");
        }
    }

    Saved load() throws Failure {
        try (InputStream stream = openIfPresent(login)) {
            if (stream == null) return null;
            DataInputStream input = new DataInputStream(stream);
            if (input.readUnsignedByte() != VERSION) throw new IOException();
            byte[] iv = exactBytes(input, IV_BYTES);
            int size = input.readInt();
            if (size <= TAG_BYTES || size > MAX_CIPHERTEXT_BYTES) throw new IOException();
            byte[] ciphertext = exactBytes(input, size);
            if (input.read() != -1) throw new IOException();

            SecretKey key = (SecretKey) keyStore().getKey(alias, null);
            if (key == null) throw new GeneralSecurityException();
            Cipher cipher = Cipher.getInstance(TRANSFORMATION);
            cipher.init(Cipher.DECRYPT_MODE, key, new GCMParameterSpec(TAG_BYTES * Byte.SIZE, iv));
            cipher.updateAAD(new byte[] {VERSION});
            byte[] plaintext = cipher.doFinal(ciphertext);
            try {
                return decode(plaintext);
            } finally {
                Arrays.fill(plaintext, (byte) 0);
            }
        } catch (IOException | GeneralSecurityException | RuntimeException ignored) {
            throw new Failure();
        }
    }

    void save(Saved saved) throws Failure {
        try {
            byte[] plaintext = encode(saved);
            try {
                Cipher cipher = Cipher.getInstance(TRANSFORMATION);
                cipher.init(Cipher.ENCRYPT_MODE, getOrCreateKey());
                cipher.updateAAD(new byte[] {VERSION});
                byte[] iv = cipher.getIV();
                if (iv == null || iv.length != IV_BYTES) throw new GeneralSecurityException();
                byte[] ciphertext = cipher.doFinal(plaintext);
                byte[] file = ByteBuffer.allocate(1 + IV_BYTES + Integer.BYTES + ciphertext.length)
                        .put((byte) VERSION).put(iv).putInt(ciphertext.length).put(ciphertext).array();
                atomicWrite(login, file);
            } finally {
                Arrays.fill(plaintext, (byte) 0);
            }
        } catch (IOException | GeneralSecurityException | RuntimeException ignored) {
            throw new Failure();
        }
    }

    void clear() throws Failure {
        // Revoke first so that a failed unlink cannot leave a usable session. If revocation itself
        // fails, still try to remove both copies and tell the caller that cleanup was incomplete.
        boolean failed = false;
        try {
            keyStore().deleteEntry(alias);
        } catch (IOException | GeneralSecurityException | RuntimeException ignored) {
            failed = true;
        }
        boolean deleted = delete(login);
        boolean deletedPending = delete(pending(login));
        if (failed || !deleted || !deletedPending) throw new Failure();
    }

    String deviceId() throws Failure {
        try (InputStream input = openIfPresent(device)) {
            if (input != null) {
                String value = new String(exactBytes(new DataInputStream(input), 36), StandardCharsets.US_ASCII);
                if (input.read() != -1 || !UUID.fromString(value).toString().equals(value)) throw new IOException();
                return value;
            }
            String value = UUID.randomUUID().toString();
            atomicWrite(device, value.getBytes(StandardCharsets.US_ASCII));
            return value;
        } catch (IOException | RuntimeException ignored) {
            throw new Failure();
        }
    }

    private SecretKey getOrCreateKey() throws IOException, GeneralSecurityException {
        SecretKey key = (SecretKey) keyStore().getKey(alias, null);
        if (key != null) return key;
        KeyGenerator generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, KEYSTORE);
        generator.init(new KeyGenParameterSpec.Builder(alias,
                KeyProperties.PURPOSE_ENCRYPT | KeyProperties.PURPOSE_DECRYPT)
                .setKeySize(256)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setRandomizedEncryptionRequired(true)
                .build());
        return generator.generateKey();
    }

    private static KeyStore keyStore() throws IOException, GeneralSecurityException {
        KeyStore store = KeyStore.getInstance(KEYSTORE);
        store.load(null);
        return store;
    }

    private static byte[] encode(Saved saved) throws IOException {
        if (!saved.hasSelection() && saved.pageId() != null) throw new IOException();
        int kind = !saved.hasSelection() ? NO_SELECTION
                : saved.pageId() == null ? OWNER_SELECTION : BRAND_SELECTION;
        ByteBuffer token = fieldBytes(saved.refreshToken());
        ByteBuffer page = null;
        try {
            page = kind == BRAND_SELECTION ? fieldBytes(saved.pageId()) : ByteBuffer.allocate(0);
            return ByteBuffer.allocate(1 + Integer.BYTES + token.remaining() + page.remaining())
                    .put((byte) kind).putInt(token.remaining()).put(token).put(page).array();
        } finally {
            wipe(token);
            if (page != null) wipe(page);
        }
    }

    private static ByteBuffer fieldBytes(String value) throws IOException {
        if (value == null || value.isEmpty() || value.length() > MAX_FIELD_BYTES) throw new IOException();
        ByteBuffer bytes = StandardCharsets.UTF_8.newEncoder().encode(CharBuffer.wrap(value));
        if (bytes.remaining() <= MAX_FIELD_BYTES) return bytes;
        wipe(bytes);
        throw new IOException();
    }

    private static void wipe(ByteBuffer bytes) {
        bytes.clear();
        while (bytes.hasRemaining()) bytes.put((byte) 0);
    }

    private static Saved decode(byte[] plaintext) throws IOException {
        ByteBuffer data = ByteBuffer.wrap(plaintext);
        if (data.remaining() < 1 + Integer.BYTES) throw new IOException();
        int kind = data.get();
        if (kind != NO_SELECTION && kind != OWNER_SELECTION && kind != BRAND_SELECTION) throw new IOException();
        int size = data.getInt();
        if (size <= 0 || size > MAX_FIELD_BYTES || size > data.remaining()) throw new IOException();
        String token = StandardCharsets.UTF_8.newDecoder().decode(ByteBuffer.wrap(plaintext, data.position(), size)).toString();
        data.position(data.position() + size);
        if (kind != BRAND_SELECTION) {
            if (data.hasRemaining()) throw new IOException();
            return new Saved(token, kind == OWNER_SELECTION, null);
        }
        if (!data.hasRemaining() || data.remaining() > MAX_FIELD_BYTES) throw new IOException();
        return new Saved(token, true, StandardCharsets.UTF_8.newDecoder().decode(data).toString());
    }

    private static InputStream openIfPresent(File file) throws IOException {
        try {
            return Files.newInputStream(file.toPath());
        } catch (NoSuchFileException absent) {
            return null;
        }
    }

    private static byte[] exactBytes(DataInputStream input, int size) throws IOException {
        byte[] bytes = new byte[size];
        input.readFully(bytes);
        return bytes;
    }

    private static File pending(File file) {
        return new File(file.getParentFile(), file.getName() + ".new");
    }

    private static void atomicWrite(File file, byte[] bytes) throws IOException {
        Files.createDirectories(file.getParentFile().toPath());
        File temporary = pending(file);
        // A process may have stopped before renaming the previous temporary file.
        Files.deleteIfExists(temporary.toPath());
        try {
            try (FileChannel output = FileChannel.open(temporary.toPath(), StandardOpenOption.CREATE_NEW,
                    StandardOpenOption.WRITE)) {
                ByteBuffer data = ByteBuffer.wrap(bytes);
                while (data.hasRemaining()) output.write(data);
                output.force(true);
            }
            Files.move(temporary.toPath(), file.toPath(), StandardCopyOption.ATOMIC_MOVE,
                    StandardCopyOption.REPLACE_EXISTING);
        } finally {
            delete(temporary);
        }
    }

    private static boolean delete(File file) {
        try {
            Files.deleteIfExists(file.toPath());
            return true;
        } catch (IOException | RuntimeException ignored) {
            return false;
        }
    }
}
