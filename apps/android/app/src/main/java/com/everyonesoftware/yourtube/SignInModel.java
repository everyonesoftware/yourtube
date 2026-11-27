package com.everyonesoftware.yourtube;

import android.os.Handler;
import android.os.Looper;
import androidx.lifecycle.ViewModel;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.Objects;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.function.Consumer;

/** Main-thread state, retained while Android recreates the activity. */
public final class SignInModel extends ViewModel {
    enum Screen { RESTORE, WELCOME, REQUEST, CODE, WAITING, ACCOUNTS, CHOOSE, HISTORY, ERROR }
    enum Retry { BEGIN, RESTORE, ACCOUNTS, HISTORY, SIGN_OUT }

    // One ordered lane for credential operations, including when an activity is closed/reopened.
    private static final ExecutorService WORK = Executors.newSingleThreadExecutor();
    private final Handler main = new Handler(Looper.getMainLooper());
    private final Runnable expiry = this::expireCode;
    private YouTubeAuth api;
    private YouTubeLoginStore store;
    private Future<?> pending;
    private boolean finishOnClose;
    private volatile long generation;
    private Runnable observer;
    private YouTubeAuth.Session session;
    private boolean saved;
    private boolean hasSelection;
    private boolean persistSelection;
    private boolean clearing;
    private Restored pendingRestore;
    Screen screen = Screen.RESTORE;
    Retry retry;
    int message;
    YouTubeAuth.Authorization authorization;
    List<YouTubeAuth.Identity> identities = Collections.emptyList();
    YouTubeAuth.Identity selected;
    List<YouTubeAuth.Video> history;
    boolean loading;

    void connect(YouTubeAuth provider, YouTubeLoginStore loginStore, Runnable redraw) {
        observer = redraw;
        if (api != null) { redraw.run(); return; }
        api = provider;
        store = loginStore;
        restore();
    }

    void detach() { observer = null; }

    boolean isSigningOut() { return clearing; }

    private void restore() {
        show(Screen.RESTORE);
        if (pendingRestore != null) {
            saveRotation(pendingRestore);
            return;
        }
        run(Retry.RESTORE, true, owner -> {
            var record = store.load();
            if (record == null) return null;
            current(owner);
            var restored = api.refresh(record.refreshToken());
            var updated = new YouTubeLoginStore.Saved(restored.refreshToken(), record.hasSelection(), record.pageId());
            boolean stored = record.refreshToken().equals(restored.refreshToken()) || trySave(updated);
            return new Restored(restored, updated, stored);
        }, result -> {
            if (result == null) { show(Screen.WELCOME); return; }
            if (result.stored) finishRestore(result);
            else {
                pendingRestore = result;
                error(Retry.RESTORE, R.string.auth_storage_error);
            }
        });
    }

    private void saveRotation(Restored result) {
        run(Retry.RESTORE, true, owner -> {
            store.save(result.record);
            return result;
        }, this::finishRestore);
    }

    private void finishRestore(Restored result) {
        pendingRestore = null;
        saved = true;
        session = result.session;
        listAccounts(result.record);
    }

    private record Restored(YouTubeAuth.Session session, YouTubeLoginStore.Saved record, boolean stored) { }

    private boolean trySave(YouTubeLoginStore.Saved record) {
        try { store.save(record); return true; }
        catch (YouTubeLoginStore.Failure ignored) { return false; }
    }

    void begin() {
        if (screen != Screen.WELCOME && !(screen == Screen.ERROR && retry == Retry.BEGIN)) return;
        show(Screen.REQUEST);
        run(Retry.BEGIN, owner -> api.begin(store.deviceId()), result -> {
            authorization = result;
            if (expireCode()) return;
            show(Screen.CODE);
            main.postDelayed(expiry, Math.max(0, result.expiresAtMs() - System.currentTimeMillis()));
        });
    }

    private boolean expireCode() {
        if (authorization == null || authorization.expiresAtMs() > System.currentTimeMillis()) return false;
        // A dispatched poll may already have accepted approval; the provider loop owns its expiry.
        if (screen == Screen.WAITING) return false;
        invalidate();
        authorization = null;
        error(Retry.BEGIN, R.string.auth_expired);
        return true;
    }

    boolean canOpenBrowser() { return screen == Screen.CODE && !expireCode(); }

    void browserOpened() {
        if (!canOpenBrowser()) return;
        var request = authorization;
        show(Screen.WAITING);
        run(Retry.BEGIN, owner -> api.complete(request), result -> {
            authorization = null;
            main.removeCallbacks(expiry);
            session = result;
            listAccounts(null);
        });
    }

    private YouTubeLoginStore.Saved restoreChoice;
    private void listAccounts(YouTubeLoginStore.Saved choice) {
        restoreChoice = choice;
        show(Screen.ACCOUNTS);
        var snapshot = session;
        run(Retry.ACCOUNTS, owner -> new ArrayList<>(api.identities(snapshot)), accounts -> {
            identities = accounts;
            if (identities.isEmpty()) { error(Retry.ACCOUNTS, R.string.auth_no_accounts); return; }
            restoreChoice = null;
            if (choice != null && choice.hasSelection()) {
                var matches = identities.stream().filter(i -> Objects.equals(i.pageId(), choice.pageId())).toArray(YouTubeAuth.Identity[]::new);
                if (matches.length == 1) {
                    selected = matches[0];
                    hasSelection = true;
                    loadHistory(false);
                    return;
                }
            }
            show(Screen.CHOOSE);
        });
    }

    void choose(YouTubeAuth.Identity identity) {
        if (screen != Screen.CHOOSE || !identities.contains(identity)) return;
        selected = identity;
        hasSelection = true;
        loadHistory(true);
    }

    void switchIdentity() {
        if (!canSwitchIdentity()) return;
        invalidate();
        history = null;
        show(Screen.CHOOSE);
    }

    boolean canSwitchIdentity() {
        return identities.size() > 1 && !persistSelection && ((screen == Screen.HISTORY && !loading)
                || (screen == Screen.ERROR && retry == Retry.HISTORY));
    }

    private void loadHistory(boolean persist) {
        if (!hasSelection || selected == null || session == null) return;
        history = null;
        persistSelection = persist;
        loading = true;
        show(Screen.HISTORY);
        var snapshot = session;
        if (snapshot.expiresAtMs() <= System.currentTimeMillis() + 60_000) {
            var identity = selected;
            run(Retry.HISTORY, true, owner -> {
                var active = api.refresh(snapshot.refreshToken());
                boolean mustSave = persist || !snapshot.refreshToken().equals(active.refreshToken());
                boolean stored = !mustSave || trySave(new YouTubeLoginStore.Saved(active.refreshToken(), true, identity.pageId()));
                return new Refreshed(active, stored);
            }, result -> {
                session = result.session;
                persistSelection = !result.stored;
                if (persistSelection) error(Retry.HISTORY, R.string.auth_storage_error);
                else {
                    saved = true;
                    readHistory(session, identity);
                }
            });
        } else saveAndReadHistory();
    }

    private record Refreshed(YouTubeAuth.Session session, boolean stored) { }

    private void saveAndReadHistory() {
        var identity = selected;
        var active = session;
        if (persistSelection) {
            run(Retry.HISTORY, true, owner -> {
                store.save(new YouTubeLoginStore.Saved(active.refreshToken(), true, identity.pageId()));
                return null;
            }, ignored -> {
                saved = true;
                persistSelection = false;
                readHistory(active, identity);
            });
        } else readHistory(active, identity);
    }

    private void readHistory(YouTubeAuth.Session active, YouTubeAuth.Identity identity) {
        run(Retry.HISTORY, owner -> new ArrayList<>(api.history(active, identity)), result -> {
            loading = false;
            history = result;
            show(Screen.HISTORY);
        });
    }

    void cancel() {
        if (saved && screen == Screen.CHOOSE && hasSelection) {
            // The last confirmed identity was not changed by opening the chooser.
            loadHistory(false);
            return;
        }
        if (saved || screen == Screen.RESTORE || retry == Retry.RESTORE || retry == Retry.SIGN_OUT) {
            signOut();
            return;
        }
        invalidate();
        clearMemory();
        show(Screen.WELCOME);
    }

    void signOut() {
        if (clearing) return;
        invalidate();
        clearMemory();
        clearing = true;
        show(Screen.RESTORE);
        run(Retry.SIGN_OUT, true, owner -> { store.clear(); return null; }, ignored -> {
            clearing = false;
            show(Screen.WELCOME);
        });
    }

    void retry() {
        if (screen != Screen.ERROR) return;
        switch (retry) {
            case BEGIN -> begin();
            case RESTORE -> restore();
            case ACCOUNTS -> listAccounts(restoreChoice);
            case HISTORY -> loadHistory(persistSelection);
            case SIGN_OUT -> signOut();
        }
    }

    private void clearMemory() {
        authorization = null;
        session = null;
        identities = Collections.emptyList();
        selected = null;
        restoreChoice = null;
        pendingRestore = null;
        history = null;
        hasSelection = false;
        persistSelection = false;
        saved = false;
        loading = false;
    }

    private void invalidate() {
        generation++;
        if (pending != null) pending.cancel(true);
        pending = null;
        finishOnClose = false;
        main.removeCallbacks(expiry);
    }

    private void current(long owner) throws InterruptedException {
        if (generation != owner || Thread.currentThread().isInterrupted()) throw new InterruptedException();
    }

    private interface Work<T> { T run(long owner) throws YouTubeAuth.Failure, YouTubeLoginStore.Failure, InterruptedException; }
    private <T> void run(Retry recovery, Work<T> action, Consumer<T> done) {
        run(recovery, false, action, done);
    }

    private <T> void run(Retry recovery, boolean durable, Work<T> action, Consumer<T> done) {
        long owner = ++generation;
        finishOnClose = durable;
        pending = WORK.submit(() -> {
            try {
                if (!durable) current(owner);
                T result = action.run(owner);
                main.post(() -> { if (generation == owner) { pending = null; done.accept(result); } });
            } catch (InterruptedException ignored) {
                Thread.currentThread().interrupt();
            } catch (YouTubeAuth.Failure failure) {
                main.post(() -> { if (generation == owner) handleFailure(recovery, failure.problem); });
            } catch (YouTubeLoginStore.Failure failure) {
                main.post(() -> { if (generation == owner) error(recovery, R.string.auth_storage_error); });
            }
        });
    }

    private void handleFailure(Retry recovery, YouTubeAuth.Problem problem) {
        if (problem == YouTubeAuth.Problem.AUTH_REQUIRED && recovery != Retry.BEGIN) {
            signOut();
            return;
        }
        error(recovery, switch (problem) {
            case EXPIRED -> R.string.auth_expired;
            case DECLINED -> R.string.auth_declined;
            case AUTH_REQUIRED -> R.string.auth_rejected;
            case HISTORY_DENIED -> R.string.auth_history_denied;
            case NETWORK -> R.string.auth_network_error;
            case INVALID_RESPONSE -> R.string.auth_response_error;
        });
    }

    private void error(Retry recovery, int text) {
        authorization = null;
        main.removeCallbacks(expiry);
        loading = false;
        if (recovery == Retry.SIGN_OUT) clearing = false;
        retry = recovery;
        message = text;
        show(Screen.ERROR);
    }

    private void show(Screen next) {
        screen = next;
        if (next != Screen.ERROR) retry = null;
        if (observer != null) observer.run();
    }

    @Override protected void onCleared() {
        observer = null;
        // Close drops UI results, not an ordered credential mutation. Explicit sign-out still
        // cancels the current operation and queues its clear after any already-running save.
        if (finishOnClose) {
            generation++;
            pending = null;
            main.removeCallbacks(expiry);
        } else invalidate();
        clearMemory();
    }
}
