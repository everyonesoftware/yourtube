package com.everyonesoftware.yourtube;

import java.io.IOException;
import java.net.URI;
import java.util.ArrayList;
import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.locks.ReentrantLock;
import java.util.function.LongSupplier;
import java.util.regex.Matcher;
import java.util.regex.Pattern;
import okhttp3.Call;
import okhttp3.Callback;
import okhttp3.CookieJar;
import okhttp3.MediaType;
import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.RequestBody;
import okhttp3.Response;
import okhttp3.ResponseBody;
import okio.BufferedSource;
import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;
import org.json.JSONTokener;

/** Unsupported TV protocol reference: MediaServiceCore c1d202fc21bd97d800a5f92d058cd8f5fbbd65d8 (MIT); see THIRD-PARTY-NOTICES.md. */
final class TvYouTubeAuth implements YouTubeAuth {
    private static final URI ORIGIN = URI.create("https://www.youtube.com");
    private static final String AGENT = "Mozilla/5.0 (Linux armeabi-v7a; Android 7.1.2; Fire OS 6.0) Cobalt/22.lts.3.306369-gold (unlike Gecko) v8/8.8.278.8-jit gles Starboard/13, Amazon_ATV_mediatek8695_2019/NS6294 (Amazon, AFTMM, Wireless) com.amazon.firetv.youtube/22.3.r2.v66.0";
    private static final String BROWSER_VERSION = "22.lts.3.306369-gold";
    private static final String TOKEN_PATH = "/o/oauth2/token";
    private static final String HISTORY_PATH = "/youtubei/v1/browse";
    private static final int MAX_BOOTSTRAP = 8 * 1024 * 1024;
    private static final int MAX_PRIVATE = 4 * 1024 * 1024;
    private static final int MAX_DEPTH = 64;
    private static final Pattern[] SCRIPT_PATHS = {
            Pattern.compile("id=\"base-js\"\\s+src=\"([^\"\\r\\n]{1,4096})\""),
            Pattern.compile("\\.src\\s*=\\s*'([^'\\r\\n]{1,4096}m=base)'"),
            Pattern.compile("\\.src\\s*=\\s*'([^'\\r\\n]{1,4096})';\\s*[$\\w]+\\.id\\s*=\\s*'base-js'")
    };
    private static final Pattern CREDENTIALS = Pattern.compile("clientId:\"([-\\w]{1,256}\\.apps\\.googleusercontent\\.com)\",\\s{0,100}[$\\w]{1,100}:\"(\\w{1,256})\"");
    private static final Pattern VERSION = Pattern.compile("\"INNERTUBE_CLIENT_VERSION\"\\s*:\\s*\"([0-9.]{1,40})\"");
    private static final Pattern VISITOR = Pattern.compile("\"visitorData\"\\s*:\\s*\"([A-Za-z0-9_%=+/-]{1,512})\"");
    private static final Pattern VIDEO_ID = Pattern.compile("[A-Za-z0-9_-]{11}");

    interface Transport {
        Reply fetch(URI uri, Map<String, String> headers, String body, int limit)
                throws IOException, InterruptedException, Failure;
    }
    interface Delay { void sleep(long millis) throws InterruptedException; }
    record Reply(int status, String body) {
        @Override public String toString() { return "Reply[redacted]"; }
    }
    private record Client(String id, String secret, String version, String visitor) {
        @Override public String toString() { return "Client[redacted]"; }
    }

    private final Transport transport;
    private final LongSupplier now;
    private final Delay delay;
    private final ReentrantLock discoveryLock = new ReentrantLock();
    private volatile Client discovered;

    TvYouTubeAuth() { this(new HttpTransport(), System::currentTimeMillis, Thread::sleep); }

    TvYouTubeAuth(Transport transport, LongSupplier now, Delay delay) {
        this.transport = transport;
        this.now = now;
        this.delay = delay;
    }

    @Override public Authorization begin(String deviceId) throws Failure, InterruptedException {
        if (!present(deviceId)) throw invalid();
        Client client = bootstrap();
        JSONObject result = post("/o/oauth2/device/code", object("client_id", client.id(), "device_id", deviceId,
                "model_name", "ytlr::", "scope", "http://gdata.youtube.com https://www.googleapis.com/auth/youtube-paid-content"), null, null, false);
        URI activation = parseUri(required(result, result.has("verification_url") ? "verification_url" : "verification_uri"));
        if (!YouTubeAuth.allowedActivation(activation)) throw invalid();
        return new Authorization(required(result, "user_code"), activation, required(result, "device_code"),
                expiry(result), positive(result, "interval"));
    }

    @Override public Session complete(Authorization authorization) throws Failure, InterruptedException {
        if (authorization == null || authorization.intervalSeconds() <= 0 || !present(authorization.deviceCode())) throw invalid();
        checkInterrupted();
        if (now.getAsLong() >= authorization.expiresAtMs()) throw new Failure(Problem.EXPIRED);
        Client client = bootstrap();
        JSONObject query = object("code", authorization.deviceCode(), "client_id", client.id(), "client_secret", client.secret(),
                "grant_type", "http://oauth.net/grant_type/device/1.0");
        long interval = authorization.intervalSeconds() * 1000L;
        while (true) {
            long remaining = authorization.expiresAtMs() - now.getAsLong();
            if (remaining <= 0) throw new Failure(Problem.EXPIRED);
            delay.sleep(Math.min(remaining, interval));
            checkInterrupted();
            if (remaining <= interval || now.getAsLong() >= authorization.expiresAtMs()) throw new Failure(Problem.EXPIRED);
            JSONObject result = post(TOKEN_PATH, query, null, null, true);
            if (now.getAsLong() >= authorization.expiresAtMs()) throw new Failure(Problem.EXPIRED);
            switch (string(result.opt("error"))) {
                case "slow_down": interval += 5000L; break;
                case "authorization_pending": break;
                default: return session(result, null);
            }
        }
    }

    @Override public Session refresh(String token) throws Failure, InterruptedException {
        if (!present(token)) throw invalid();
        Client client = bootstrap();
        return session(post(TOKEN_PATH, object("refresh_token", token, "client_id", client.id(), "client_secret", client.secret(),
                "grant_type", "refresh_token"), null, null, false), token);
    }

    @Override public List<Identity> identities(Session session) throws Failure, InterruptedException {
        validateSession(session);
        Client client = bootstrap();
        JSONObject result = post("/youtubei/v1/account/accounts_list", object("context", context(client, null), "accountReadMask",
                object("returnOwner", true, "returnBrandAccounts", true, "returnPersonaAccounts", false)), session, null, false);
        List<Identity> accounts = new ArrayList<>();
        JSONArray contents = array(result.opt("contents"));
        boolean recognized = contents.length() == 0;
        for (int i = 0; i < contents.length(); i++) {
            Object wrapper = at(contents.opt(i), "accountSectionListRenderer", "contents");
            if (wrapper != null) {
                JSONArray sections = array(wrapper);
                for (int j = 0; j < sections.length(); j++) recognized |= readIdentities(sections.opt(j), accounts);
            } else {
                recognized |= readIdentities(contents.opt(i), accounts);
            }
        }
        if (!recognized) throw invalid();
        checkInterrupted();
        return Collections.unmodifiableList(accounts);
    }

    private static boolean readIdentities(Object section, List<Identity> accounts) throws Failure {
        Object items = at(section, "accountItemSectionRenderer");
        if (items == null) return false;
        JSONArray entries = array(at(items, "contents"));
        for (int i = 0; i < entries.length(); i++) {
            Object account = at(entries.opt(i), "accountItem");
            if (account == null || Boolean.TRUE.equals(at(account, "isDisabled"))) continue;
            String name = text(at(account, "accountName"));
            if (!present(name)) continue;
            String byline = text(at(account, "accountByline"));
            String pageId = null;
            JSONObject endpoint = optionalObject(account, "serviceEndpoint");
            JSONObject selection = optionalObject(endpoint, "selectActiveIdentityEndpoint");
            Object supported = at(selection, "supportedTokens");
            if (supported != null) {
                JSONArray tokens = array(supported);
                for (int j = 0; j < tokens.length(); j++) {
                    if (!(tokens.opt(j) instanceof JSONObject token)) throw invalid();
                    JSONObject page = optionalObject(token, "pageIdToken");
                    if (page == null) continue; // accountStateToken and Gaia IDs are not page IDs.
                    String candidate = string(page.opt("pageId"));
                    if (!validHeader(candidate)) throw invalid();
                    if (pageId == null) pageId = candidate;
                }
            }
            accounts.add(new Identity(name, present(byline) ? byline : null, pageId));
        }
        return true;
    }

    @Override public List<Video> history(Session session, Identity identity) throws Failure, InterruptedException {
        validateSession(session);
        if (identity == null || (identity.pageId() != null && !validHeader(identity.pageId()))) throw invalid();
        Client client = bootstrap();
        String pageId = identity.pageId();
        JSONObject result = post(HISTORY_PATH, object("context", context(client, pageId), "browseId", "FEhistory",
                "racyCheckOk", true, "contentCheckOk", true), session, pageId, false);
        List<Video> videos = new ArrayList<>();
        readHistory(result.opt("contents"), videos);
        checkInterrupted();
        return Collections.unmodifiableList(videos);
    }

    private static void readHistory(Object value, List<Video> videos) throws Failure {
        if (!(value instanceof JSONObject node)) throw invalid();
        if (node.has("tvBrowseRenderer")) {
            readHistory(at(node, "tvBrowseRenderer", "content"), videos);
        } else if (node.has("tvSurfaceContentRenderer")) {
            readHistory(at(node, "tvSurfaceContentRenderer", "content"), videos);
        } else if (node.has("tvSecondaryNavRenderer")) {
            JSONArray sections = array(at(node, "tvSecondaryNavRenderer", "sections"));
            JSONObject active = null;
            for (int i = 0; i < sections.length(); i++) {
                Object section = at(sections.opt(i), "tvSecondaryNavSectionRenderer");
                if (section == null) continue;
                JSONObject selected = selectedTab(array(at(section, "tabs")));
                if (selected == null) continue;
                if (active != null) throw invalid();
                active = selected;
            }
            readHistory(at(active, "content"), videos);
        } else if (node.has("tabs")) {
            readHistory(at(selectedTab(array(node.opt("tabs"))), "content"), videos);
        } else if (node.has("twoColumnRenderer")) {
            readHistory(at(node, "twoColumnRenderer", "rightColumn"), videos);
        } else if (node.has("gridRenderer")) {
            readItems(at(node, "gridRenderer", "items"), videos);
        } else if (node.has("horizontalListRenderer")) {
            readItems(at(node, "horizontalListRenderer", "items"), videos);
        } else if (node.has("sectionListRenderer")) {
            readItems(at(node, "sectionListRenderer", "contents"), videos);
        } else if (node.has("itemSectionRenderer")) {
            readItems(at(node, "itemSectionRenderer", "contents"), videos);
        } else if (node.has("playlistVideoListRenderer")) {
            readItems(at(node, "playlistVideoListRenderer", "contents"), videos);
        } else if (node.has("shelfRenderer")) {
            readHistory(at(node, "shelfRenderer", "content"), videos);
        } else if (node.has("items")) {
            readItems(node.opt("items"), videos);
        } else if (!(node.opt("messageRenderer") instanceof JSONObject)) {
            throw invalid();
        }
    }

    private static JSONObject selectedTab(JSONArray tabs) throws Failure {
        JSONObject active = null;
        for (int i = 0; i < tabs.length(); i++) {
            if (at(tabs.opt(i), "tabRenderer") instanceof JSONObject tab && Boolean.TRUE.equals(tab.opt("selected"))) {
                if (active != null) throw invalid();
                active = tab;
            }
        }
        return active;
    }

    private static void readItems(Object value, List<Video> videos) throws Failure {
        JSONArray items = array(value);
        for (int i = 0; i < items.length(); i++) {
            if (!(items.opt(i) instanceof JSONObject item)) throw invalid();
            if (item.has("shelfRenderer") || item.has("itemSectionRenderer") || item.has("gridRenderer")
                    || item.has("horizontalListRenderer") || item.has("sectionListRenderer")) {
                readHistory(item, videos);
                continue;
            }
            Video video = readVideo(item);
            if (video != null) videos.add(video);
        }
    }

    private static Video readVideo(JSONObject item) {
        Object card;
        String id;
        String title;
        String channel = "";
        Object progress = null;
        if ((card = item.opt("tileRenderer")) instanceof JSONObject) {
            if (!"TILE_CONTENT_TYPE_VIDEO".equals(at(card, "contentType"))) return null;
            id = string(at(card, "onSelectCommand", "watchEndpoint", "videoId"));

            Object meta = at(card, "metadata", "tileMetadataRenderer");
            title = text(at(meta, "title"));
            Object lines = at(meta, "lines");
            if (lines instanceof JSONArray rows) {
                Object parts = at(rows.opt(0), "lineRenderer", "items");
                if (parts instanceof JSONArray entries) channel = text(at(entries.opt(0), "lineItemRenderer", "text"));
            }
            progress = overlayProgress(at(card, "header", "tileHeaderRenderer", "thumbnailOverlays"));
        } else if ((card = item.opt("lockupViewModel")) instanceof JSONObject) {
            if (!"LOCKUP_CONTENT_TYPE_VIDEO".equals(at(card, "contentType"))) return null;
            id = string(at(card, "contentId"));
            Object meta = at(card, "metadata", "lockupMetadataViewModel");
            title = text(at(meta, "title"));
            Object rows = at(meta, "metadata", "contentMetadataViewModel", "metadataRows");
            if (rows instanceof JSONArray entries) {
                Object parts = at(entries.opt(0), "metadataParts");
                if (parts instanceof JSONArray byline) channel = text(at(byline.opt(0), "text"));
            }
            progress = find(at(card, "contentImage"), "startPercent");
        } else if ((card = item.opt("shortsLockupViewModel")) instanceof JSONObject) {
            id = string(at(card, "onTap", "innertubeCommand", "reelWatchEndpoint", "videoId"));
            title = text(at(card, "overlayMetadata", "primaryText"));
        } else {
            card = item.has("gridVideoRenderer") ? item.opt("gridVideoRenderer") : item.opt("videoRenderer");
            if (!(card instanceof JSONObject)) return null;
            id = string(at(card, "videoId"));
            title = text(at(card, "title"));
            channel = text(at(card, "shortBylineText"));
            if (channel.isEmpty()) channel = text(at(card, "longBylineText"));
            progress = overlayProgress(at(card, "thumbnailOverlays"));
        }
        if (!VIDEO_ID.matcher(id).matches() || !present(title)) return null;
        Integer watched = null;
        if (progress instanceof Number number) {
            double value = number.doubleValue();
            if (Double.isFinite(value) && value == Math.rint(value)) watched = (int) Math.max(0, Math.min(100, value));
        }
        return new Video(title, channel, watched);
    }

    private static Object overlayProgress(Object overlays) {
        if (overlays instanceof JSONArray entries) {
            for (int i = 0; i < entries.length(); i++) {
                Object watched = at(entries.opt(i), "thumbnailOverlayResumePlaybackRenderer", "percentDurationWatched");
                if (watched instanceof Number) return watched;
            }
        }
        return null;
    }

    private Client bootstrap() throws Failure, InterruptedException {
        checkInterrupted();
        Client cached = discovered;
        if (cached != null) return cached;
        discoveryLock.lockInterruptibly();
        try {
            cached = discovered;
            if (cached != null) return cached;
            String html = get(ORIGIN.resolve("/tv"));
            String path = "";
            for (Pattern pattern : SCRIPT_PATHS) {
                path = match(pattern, html);
                if (!path.isEmpty()) break;
            }
            if (path.isEmpty()) throw invalid();
            URI script = ORIGIN.resolve(parseUri(path));
            if (!"https".equalsIgnoreCase(script.getScheme()) || !"www.youtube.com".equalsIgnoreCase(script.getHost())
                    || (script.getPort() != -1 && script.getPort() != 443) || script.getRawUserInfo() != null
                    || script.getRawFragment() != null || script.getPath().isEmpty()) throw invalid();
            Matcher credentials = CREDENTIALS.matcher(get(script));
            if (!credentials.find()) throw invalid();
            String version = match(VERSION, html);
            checkInterrupted();
            cached = new Client(credentials.group(1), credentials.group(2), version.isEmpty() ? "7.20260901.15.00" : version,
                    match(VISITOR, html));
            discovered = cached;
            return cached;
        } finally {
            discoveryLock.unlock();
        }
    }

    private String get(URI uri) throws Failure, InterruptedException {
        Reply reply = exchange(uri, Collections.singletonMap("User-Agent", AGENT), null, MAX_BOOTSTRAP);
        if (reply.status() != 200) throw new Failure(reply.status() >= 500 || reply.status() == 429 ? Problem.NETWORK : Problem.INVALID_RESPONSE);
        return reply.body();
    }

    private JSONObject post(String path, JSONObject payload, Session session, String pageId, boolean polling)
            throws Failure, InterruptedException {
        var headers = new LinkedHashMap<String, String>();
        boolean history = HISTORY_PATH.equals(path);
        headers.put("User-Agent", AGENT);
        if (session != null) headers.put("Authorization", "Bearer " + session.accessToken());
        if (pageId != null) headers.put("X-Goog-PageId", pageId);
        if (history) headers.put("Referer", ORIGIN + "/tv");
        Reply reply = exchange(ORIGIN.resolve(path), Collections.unmodifiableMap(headers), payload.toString(), MAX_PRIVATE);
        int status = reply.status();
        if (status == 401) throw new Failure(Problem.AUTH_REQUIRED);
        if (status == 403) {
            throw new Failure(!history || explicitlyUnauthenticated(reply.body())
                    ? Problem.AUTH_REQUIRED : Problem.HISTORY_DENIED);
        }
        if (status == 429 || status >= 500) throw new Failure(Problem.NETWORK);
        if (status < 200 || (status >= 300 && status < 400)) throw invalid();
        JSONObject result = json(reply.body());
        Object error = result.opt("error");
        if (error != null) {
            String code = string(error);
            Object providerStatus = at(error, "status");
            if (polling && (status == 200 || status == 400)) {
                switch (code) {
                    case "authorization_pending", "slow_down": return result;
                    case "access_denied": throw new Failure(Problem.DECLINED);
                    case "expired_token": throw new Failure(Problem.EXPIRED);
                }
            }
            if ("invalid_grant".equals(code) || "invalid_token".equals(code) || "authorization_required".equals(code)
                    || "access_denied".equals(code) || "expired_token".equals(code)
                    || "UNAUTHENTICATED".equals(providerStatus)) {
                throw new Failure(Problem.AUTH_REQUIRED);
            }
            if ("PERMISSION_DENIED".equals(providerStatus)) {
                throw new Failure(history ? Problem.HISTORY_DENIED : Problem.AUTH_REQUIRED);
            }
            throw invalid();
        }
        if (status >= 300) throw invalid();
        if (session != null && (Boolean.TRUE.equals(at(result, "responseContext", "mainAppWebResponseContext", "loggedOut"))
                || Boolean.TRUE.equals(result.opt("loggedOut")) || find(result.opt("contents"), "tvSignInRenderer") != null)) {
            throw new Failure(Problem.AUTH_REQUIRED);
        }
        return result;
    }

    private static boolean explicitlyUnauthenticated(String body) {
        try {
            return "UNAUTHENTICATED".equals(at(json(body), "error", "status"));
        } catch (Failure ignored) {
            return false; // A bare or non-JSON History 403 is not evidence of expired authorization.
        }
    }

    private Reply exchange(URI uri, Map<String, String> headers, String body, int limit) throws Failure, InterruptedException {
        checkInterrupted();
        try {
            Reply reply = transport.fetch(uri, headers, body, limit);
            checkInterrupted();
            return reply;
        } catch (IOException | IllegalArgumentException ignored) {
            checkInterrupted();
            throw new Failure(Problem.NETWORK);
        }
    }

    private static final class HttpTransport implements Transport {
        private static final MediaType JSON = MediaType.get("application/json; charset=utf-8");
        private final OkHttpClient client = new OkHttpClient.Builder()
                .followRedirects(false).followSslRedirects(false).retryOnConnectionFailure(false)
                .cookieJar(CookieJar.NO_COOKIES).callTimeout(20, TimeUnit.SECONDS)
                .addNetworkInterceptor(chain -> {
                    Response response = chain.proceed(chain.request());
                    // OkHttp can replay a 503 with Retry-After: 0 even with connection retries off.
                    return response.code() == 503 ? response.newBuilder().removeHeader("Retry-After").build() : response;
                }).build();

        @Override public Reply fetch(URI uri, Map<String, String> headers, String body, int limit)
                throws IOException, InterruptedException, Failure {
            Request.Builder request = new Request.Builder().url(uri.toASCIIString());
            headers.forEach(request::header);
            if (body != null) request.post(RequestBody.create(body, JSON));
            Call call = client.newCall(request.build());
            var pending = new CompletableFuture<Reply>();
            call.enqueue(new Callback() {
                @Override public void onFailure(Call ignored, IOException problem) { pending.completeExceptionally(problem); }
                @Override public void onResponse(Call ignored, Response response) {
                    try (Response closeable = response) {
                        int status = closeable.code();
                        boolean historyForbidden = status == 403 && HISTORY_PATH.equals(uri.getRawPath());
                        if ((status >= 300 && status < 400) || status == 401 || (status == 403 && !historyForbidden)
                                || status == 429 || status >= 500) {
                            pending.complete(new Reply(status, ""));
                            return;
                        }
                        ResponseBody responseBody = closeable.body();
                        if (responseBody == null || responseBody.contentLength() > limit) {
                            if (!historyForbidden) throw invalid();
                            pending.complete(new Reply(status, ""));
                            return;
                        }
                        BufferedSource source = responseBody.source();
                        // Limit decoded bytes, including transparent gzip, rather than trusting Content-Length.
                        boolean oversized = source.request(limit + 1L);
                        if (oversized && !historyForbidden) throw invalid();
                        pending.complete(new Reply(status, oversized ? "" : source.readUtf8()));
                    } catch (IOException | Failure problem) {
                        pending.completeExceptionally(problem);
                    }
                }
            });
            try {
                return pending.get();
            } catch (InterruptedException interrupted) {
                call.cancel();
                throw interrupted;
            } catch (ExecutionException failed) {
                if (failed.getCause() instanceof Failure safe) throw safe;
                throw new IOException();
            }
        }
    }

    private static JSONObject context(Client client, String pageId) throws Failure {
        JSONObject info = object("clientName", "TVHTML5", "clientVersion", client.version(), "clientScreen", "WATCH",
                "userAgent", AGENT, "browserName", "Cobalt", "browserVersion", BROWSER_VERSION, "hl", "en", "gl", "US",
                "acceptLanguage", "en", "acceptRegion", "US", "utcOffsetMinutes", "0", "webpSupport", false,
                "animatedWebpSupport", true, "tvAppInfo", object("appQuality", "TV_APP_QUALITY_FULL_ANIMATION", "zylonLeftNav", true));
        if (!client.visitor().isEmpty()) put(info, "visitorData", client.visitor());
        JSONObject user = object("enableSafetyMode", false, "lockedSafetyMode", false);
        if (pageId != null) put(user, "onBehalfOfUser", pageId);
        return object("client", info, "user", user);
    }

    private void validateSession(Session session) throws Failure, InterruptedException {
        checkInterrupted();
        if (session == null || !validHeader(session.accessToken()) || session.expiresAtMs() <= now.getAsLong()) {
            throw new Failure(Problem.AUTH_REQUIRED);
        }
    }

    private Session session(JSONObject result, String previousRefresh) throws Failure {
        if (result.has("token_type") && !"Bearer".equalsIgnoreCase(string(result.opt("token_type")))) throw invalid();
        String access = required(result, "access_token");
        if (!validHeader(access)) throw invalid();
        String refresh = result.has("refresh_token") || previousRefresh == null ? required(result, "refresh_token") : previousRefresh;
        return new Session(access, refresh, expiry(result));
    }

    private long expiry(JSONObject result) throws Failure { return now.getAsLong() + positive(result, "expires_in") * 1000L; }

    private static int positive(JSONObject result, String field) throws Failure {
        Object value = result.opt(field);
        if (value instanceof Number n) {
            double d = n.doubleValue();
            if (d > 0 && d <= Integer.MAX_VALUE && d == Math.rint(d)) return (int) d;
        }
        throw invalid();
    }

    private static String required(JSONObject result, String field) throws Failure {
        String value = string(result.opt(field));
        if (!present(value)) throw invalid();
        return value;
    }

    private static boolean validHeader(String value) {
        if (!present(value)) return false;
        for (int i = 0; i < value.length(); i++) if (value.charAt(i) < 33 || value.charAt(i) > 126) return false;
        return true;
    }

    private static boolean present(String value) { return value != null && !value.trim().isEmpty(); }
    private static String string(Object value) { return value instanceof String text ? text : ""; }
    private static Failure invalid() { return new Failure(Problem.INVALID_RESPONSE); }
    private static void checkInterrupted() throws InterruptedException {
        if (Thread.interrupted()) throw new InterruptedException();
    }

    private static URI parseUri(String value) throws Failure {
        try { return URI.create(value); }
        catch (IllegalArgumentException ignored) { throw invalid(); }
    }

    private static String match(Pattern pattern, String source) {
        Matcher matcher = pattern.matcher(source);
        return matcher.find() ? matcher.group(1) : "";
    }

    private static JSONObject json(String body) throws Failure {
        // Android's JSONTokener is recursive and lenient; bound nesting before invoking it.
        int depth = 0;
        boolean quoted = false;
        boolean escaped = false;
        for (int i = 0; i < body.length(); i++) {
            char c = body.charAt(i);
            if (quoted) {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') quoted = false;
            } else if (c == '"') {
                quoted = true;
            } else if (c == '[' || c == '{') {
                if (++depth > MAX_DEPTH) throw invalid();
            } else if (c == ']' || c == '}') {
                if (--depth < 0) throw invalid();
            } else if (c == '\'' || c == '/' || c == '#' || c == '\0') {
                throw invalid(); // Exclude alternate quote/comment syntax before parsing/counting structure.
            }
        }
        if (depth != 0 || quoted) throw invalid();
        try {
            JSONTokener parser = new JSONTokener(body);
            Object root = parser.nextValue();
            if (root instanceof JSONObject object && parser.nextClean() == 0) return object;
        } catch (JSONException ignored) { /* Do not retain parser text. */ }
        throw invalid();
    }

    private static JSONObject object(Object... entries) throws Failure {
        JSONObject result = new JSONObject();
        for (int i = 0; i < entries.length; i += 2) put(result, (String) entries[i], entries[i + 1]);
        return result;
    }

    private static void put(JSONObject target, String key, Object value) throws Failure {
        try { target.put(key, value); }
        catch (JSONException ignored) { throw invalid(); }
    }

    private static Object at(Object node, String... keys) {
        for (String key : keys) {
            if (!(node instanceof JSONObject object)) return null;
            node = object.opt(key);
        }
        return node;
    }

    private static JSONArray array(Object node) throws Failure {
        if (node instanceof JSONArray items) return items;
        throw invalid();
    }

    private static JSONObject optionalObject(Object parent, String key) throws Failure {
        Object value = at(parent, key);
        if (value == null) return null; // Missing is allowed; explicit JSON null is not.
        if (value instanceof JSONObject object) return object;
        throw invalid();
    }

    private static String text(Object value) {
        if (value instanceof String direct) return direct;
        if (at(value, "content") instanceof String content) return content;
        if (at(value, "simpleText") instanceof String simple) return simple;
        if (!(at(value, "runs") instanceof JSONArray runs)) return "";
        StringBuilder joined = new StringBuilder();
        for (int i = 0; i < runs.length(); i++) joined.append(string(at(runs.opt(i), "text")));
        return joined.toString();
    }

    private static Object find(Object value, String key) {
        if (value instanceof JSONObject object) {
            if (object.has(key)) return object.opt(key);
            for (var keys = object.keys(); keys.hasNext();) {
                Object found = find(object.opt(keys.next()), key);
                if (found != null) return found;
            }
        } else if (value instanceof JSONArray array) {
            for (int i = 0; i < array.length(); i++) {
                Object found = find(array.opt(i), key);
                if (found != null) return found;
            }
        }
        return null;
    }
}
