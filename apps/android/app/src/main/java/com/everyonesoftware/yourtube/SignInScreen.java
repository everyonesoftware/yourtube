package com.everyonesoftware.yourtube;

import android.app.Activity;
import android.content.ActivityNotFoundException;
import android.content.Context;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.graphics.drawable.RippleDrawable;
import android.content.res.ColorStateList;
import android.view.Gravity;
import android.view.View;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.ProgressBar;
import android.widget.ScrollView;
import android.widget.TextView;
import androidx.core.view.ViewCompat;
import java.net.URI;
import java.util.function.Consumer;

/** The native welcome, external-browser handoff, and identity/History screens. */
final class SignInScreen {
    private static final int TEXT = 0xffedf3ea, MUTED = 0xffc2cec3, SAGE = 0xffc0e3b2;
    private final Context context;
    private final ScrollView scroll;
    private final LinearLayout content;
    private final SignInModel model;
    private final Consumer<URI> launchBrowser;
    private final Runnable local;
    private TextView browserError;

    SignInScreen(Activity activity, SignInModel model, Consumer<URI> launchBrowser, Runnable local) {
        context = activity;
        scroll = activity.findViewById(R.id.account_scroll);
        content = activity.findViewById(R.id.account_content);
        this.model = model;
        this.launchBrowser = launchBrowser;
        this.local = local;
    }

    void render() {
        content.removeAllViews();
        scroll.scrollTo(0, 0);
        actionAtBottom = null;
        browserError = null;
        content.setPadding(dp(4), dp(28), dp(4), dp(12));
        switch (model.screen) {
            case WELCOME -> {
                eyebrow(R.string.auth_welcome);
                heading(R.string.auth_connect_title);
                body(R.string.auth_connect_body);
                action("signin", R.string.auth_signin, true, model::begin);
                action("local", R.string.auth_skip, false, local);
            }
            case RESTORE -> {
                progress(model.isSigningOut() ? R.string.auth_signout_wait : R.string.auth_restore);
                action("local", R.string.auth_local, false, local);
            }
            case REQUEST -> {
                progress(R.string.auth_request);
                cancel();
            }
            case CODE, WAITING -> {
                eyebrow(R.string.auth_step_browser);
                heading(R.string.auth_browser_title);
                body(R.string.auth_browser_instructions);
                TextView code = text(model.authorization.userCode(), 27, SAGE, true);
                code.setTypeface(Typeface.MONOSPACE, Typeface.BOLD);
                code.setGravity(Gravity.CENTER);
                code.setPadding(dp(8), dp(14), dp(8), dp(14));
                code.setBackground(shape(0xff243025, 0xff526652));
                code.setContentDescription(context.getString(R.string.auth_code_label) + ": " + model.authorization.userCode());
                if (model.screen == SignInModel.Screen.CODE) {
                    browserError = body(R.string.auth_browser_error);
                    browserError.setVisibility(View.GONE);
                    browserError.setAccessibilityLiveRegion(View.ACCESSIBILITY_LIVE_REGION_POLITE);
                    action("browser", R.string.auth_open_browser, true, this::openBrowser);
                } else {
                    progress(R.string.auth_wait);
                }
                cancel();
            }
            case ACCOUNTS -> { progress(R.string.auth_accounts_wait); cancel(); }
            case CHOOSE -> {
                eyebrow(R.string.auth_step_identity);
                heading(R.string.auth_choose);
                body(R.string.auth_choose_body);
                for (int i = 0; i < model.identities.size(); i++) {
                    var identity = model.identities.get(i);
                    String label = identity.byline() == null || identity.byline().trim().isEmpty() ? identity.name()
                            : context.getString(R.string.auth_identity, identity.name(), identity.byline());
                    addButton(content, "identity" + i, label, false, true, () -> model.choose(identity));
                }
                cancel();
            }
            case HISTORY -> {
                eyebrow(R.string.auth_history_scope);
                heading(R.string.auth_history);
                text(model.selected.name(), 16, SAGE, true);
                if (!model.loading && model.identities.size() > 1) {
                    inlineAction("switch", R.string.auth_switch, model::switchIdentity);
                }
                inlineAction("signout", R.string.auth_signout, model::signOut);
                inlineAction("local", R.string.auth_local, local);
                if (model.loading) progress(R.string.auth_history_wait);
                else if (model.history.isEmpty()) body(R.string.auth_history_empty);
                else for (var video : model.history) {
                    text(video.title(), 16, TEXT, true);
                    if (video.channel() != null && !video.channel().trim().isEmpty()) text(video.channel(), 14, MUTED, false);
                    if (video.percentWatched() != null) text(context.getString(R.string.auth_percent, video.percentWatched()), 13, SAGE, false);
                }

            }
            case ERROR -> {
                heading(R.string.auth_error_title);
                TextView error = body(model.message);
                error.setAccessibilityLiveRegion(View.ACCESSIBILITY_LIVE_REGION_POLITE);
                int retryLabel = model.message == R.string.auth_expired ? R.string.auth_new_code : R.string.auth_retry;
                action("retry", retryLabel, true, model::retry);
                if (model.retry == SignInModel.Retry.HISTORY) {
                    if (model.canSwitchIdentity()) action("switch", R.string.auth_switch, false, model::switchIdentity);
                    action("signout", R.string.auth_signout, false, model::signOut);
                    action("local", R.string.auth_local, false, local);
                } else if (model.retry != SignInModel.Retry.SIGN_OUT) cancel();
                else action("local", R.string.auth_local, false, local);
            }
        }
    }

    private void openBrowser() {
        if (!model.canOpenBrowser()) return;
        URI uri = model.authorization.verificationUri();
        try {
            if (!YouTubeAuth.allowedActivation(uri)) throw new ActivityNotFoundException();
            launchBrowser.accept(uri);
            model.browserOpened();
        } catch (ActivityNotFoundException | SecurityException ignored) {
            if (browserError != null) {
                browserError.setVisibility(View.VISIBLE);

            }
        }
    }

    private void cancel() { action("cancel", R.string.auth_cancel, false, model::cancel); }

    private void inlineAction(String tag, int title, Runnable press) {
        addButton(content, tag, context.getString(title), false, false, press);
    }

    private void eyebrow(int value) {
        TextView label = text(context.getString(value), 12, SAGE, true);
        label.setLetterSpacing(.12f);
        label.setAllCaps(true);
    }

    private void heading(int value) {
        ViewCompat.setAccessibilityHeading(text(context.getString(value), 27, TEXT, true), true);
    }

    private TextView body(int value) { return text(context.getString(value), 16, MUTED, false); }

    private TextView text(String value, int sp, int color, boolean bold) {
        TextView view = new TextView(context);
        view.setText(value);
        view.setTextSize(sp);
        view.setTextColor(color);
        if (bold) view.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        content.addView(view, params(dp(10)));
        return view;
    }

    private void progress(int label) {
        ProgressBar indicator = new ProgressBar(context);
        indicator.setIndeterminateTintList(ColorStateList.valueOf(SAGE));
        indicator.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_NO);
        LinearLayout.LayoutParams size = new LinearLayout.LayoutParams(dp(24), dp(24));
        size.bottomMargin = dp(10);
        content.addView(indicator, size);
        body(label).setAccessibilityLiveRegion(View.ACCESSIBILITY_LIVE_REGION_POLITE);
    }

    private LinearLayout actionAtBottom;
    private void action(String tag, int title, boolean primary, Runnable press) {
        if (actionAtBottom == null) {
            actionAtBottom = new LinearLayout(context);
            actionAtBottom.setOrientation(LinearLayout.VERTICAL);
            var space = new View(context);
            space.setMinimumHeight(dp(16));
            content.addView(space, new LinearLayout.LayoutParams(1, dp(16), 1));
            content.addView(actionAtBottom, params(0));
        }
        addButton(actionAtBottom, tag, context.getString(title), primary, false, press);
    }

    private void addButton(LinearLayout group, String tag, String title, boolean primary, boolean alignStart, Runnable press) {
        Button button = new Button(context);
        button.setAllCaps(false);
        button.setText(title);
        button.setTextSize(15);
        button.setTag(tag);
        button.setMinHeight(dp(48));
        button.setMinimumHeight(dp(48));
        button.setPadding(dp(12), dp(6), dp(12), dp(6));
        button.setGravity(Gravity.CENTER_VERTICAL | (alignStart ? Gravity.START : Gravity.CENTER_HORIZONTAL));
        button.setTextColor(primary ? 0xff20351c : TEXT);
        int fill = primary ? SAGE : 0xff202820;
        button.setBackground(new RippleDrawable(ColorStateList.valueOf(primary ? 0x3020351c : 0x60cee3c4),
                shape(fill, primary ? SAGE : 0xff536354), null));
        button.setOnClickListener(v -> press.run());
        group.addView(button, params(dp(8)));
    }

    private GradientDrawable shape(int fill, int edge) {
        GradientDrawable shape = new GradientDrawable();
        shape.setColor(fill);
        shape.setCornerRadius(dp(12));
        shape.setStroke(dp(1), edge);
        return shape;
    }

    private static LinearLayout.LayoutParams params(int bottom) {
        LinearLayout.LayoutParams p = new LinearLayout.LayoutParams(-1, -2);
        p.bottomMargin = bottom;
        return p;
    }

    private int dp(int value) { return Math.round(value * context.getResources().getDisplayMetrics().density); }
}
