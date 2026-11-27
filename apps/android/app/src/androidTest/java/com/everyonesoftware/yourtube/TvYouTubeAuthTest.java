package com.everyonesoftware.yourtube;

import static org.junit.Assert.*;

import java.io.IOException;
import java.net.URI;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.List;
import java.util.Map;
import org.json.JSONObject;
import org.junit.Test;

public final class TvYouTubeAuthTest {
    private static final String TOKEN = "{\"access_token\":\"access-fixture\",\"refresh_token\":\"refresh-fixture\",\"expires_in\":3600,\"token_type\":\"Bearer\"}";
    private static final String DEVICE = "{\"user_code\":\"TEST-CODE\",\"device_code\":\"device-fixture\",\"verification_url\":\"https://www.youtube.com/activate\",\"expires_in\":1800,\"interval\":5}";

    @Test public void discoversTvClientAndPollsWithProviderSpacing() throws Exception {
        Fixture f = new Fixture();
        f.add(200, DEVICE);
        f.add(400, "{\"error\":\"authorization_pending\"}");
        f.add(400, "{\"error\":\"slow_down\"}");
        f.add(200, TOKEN);
        YouTubeAuth.Authorization auth = f.client.begin("device-id-fixture");
        assertEquals("TEST-CODE", auth.userCode());
        assertEquals(1_801_000, auth.expiresAtMs());
        assertEquals(URI.create("https://www.youtube.com/activate"), auth.verificationUri());
        assertFalse(auth.toString().contains("TEST-CODE"));
        YouTubeAuth.Session session = f.client.complete(auth);
        assertEquals("refresh-fixture", session.refreshToken());
        assertFalse(session.toString().contains("refresh-fixture"));
        assertEquals(Arrays.asList(5_000L, 5_000L, 10_000L), f.waits);
        assertEquals(Arrays.asList("/tv", "/s/tv/fixture/base.js", "/o/oauth2/device/code",
                "/o/oauth2/token", "/o/oauth2/token", "/o/oauth2/token"), f.paths());
        assertNull(f.requests.get(0).body);
        assertNull(f.requests.get(1).body);
        JSONObject issue = f.requests.get(2).json();
        assertEquals("fixture.apps.googleusercontent.com", issue.getString("client_id"));
        assertEquals("device-id-fixture", issue.getString("device_id"));
        assertEquals("ytlr::", issue.getString("model_name"));
        assertEquals("http://gdata.youtube.com https://www.googleapis.com/auth/youtube-paid-content", issue.getString("scope"));
        for (int i = 3; i < 6; i++) {
            JSONObject poll = f.requests.get(i).json();
            assertEquals("fixture.apps.googleusercontent.com", poll.getString("client_id"));
            assertEquals("syntheticSecret", poll.getString("client_secret"));
            assertEquals("device-fixture", poll.getString("code"));
            assertEquals("http://oauth.net/grant_type/device/1.0", poll.getString("grant_type"));
            assertFalse(poll.has("device_code"));
        }
        assertTrue(f.requests.stream().noneMatch(r -> r.headers.containsKey("Authorization")));
    }

    @Test public void expiryAndInterruptionStopBeforeAnotherPoll() throws Exception {
        Fixture f = new Fixture();
        f.add(200, DEVICE);
        f.add(400, "{\"error\":\"slow_down\"}");
        f.client.begin("device-id");
        var auth = new YouTubeAuth.Authorization("TEST", URI.create("https://www.youtube.com/activate"), "device-fixture", 10_000, 5);
        assertEquals(YouTubeAuth.Problem.EXPIRED, assertThrows(YouTubeAuth.Failure.class, () -> f.client.complete(auth)).problem);
        assertEquals(4, f.requests.size());
        f.interruptDelay = true;
        f.now = 1_000;
        assertThrows(InterruptedException.class, () -> f.client.complete(auth));
        assertEquals(4, f.requests.size());
    }

    @Test public void preservesOmittedRefreshTokenAndAcceptsRotation() throws Exception {
        Fixture f = new Fixture();
        f.add(200, "{\"access_token\":\"new-fixture\",\"expires_in\":60}");
        f.add(200, TOKEN);
        assertEquals("old-fixture", f.client.refresh("old-fixture").refreshToken());
        assertEquals("refresh-fixture", f.client.refresh("old-fixture").refreshToken());
        JSONObject refresh = f.requests.get(2).json();
        assertEquals("old-fixture", refresh.getString("refresh_token"));
        assertEquals("refresh_token", refresh.getString("grant_type"));
        assertFalse(refresh.has("code"));
        assertEquals(Arrays.asList("/tv", "/s/tv/fixture/base.js", "/o/oauth2/token", "/o/oauth2/token"), f.paths());
    }

    @Test public void activationPathAndBootstrapOriginAreRestricted() throws Exception {
        assertTrue(YouTubeAuth.allowedActivation(URI.create("https://www.youtube.com/activate")));
        assertTrue(YouTubeAuth.allowedActivation(URI.create("https://www.google.com/device")));
        for (String url : Arrays.asList("http://www.youtube.com/activate", "https://www.youtube.com.evil.invalid/activate",
                "https://www.youtube.com:444/activate", "https://x@www.youtube.com/activate", "https://www.youtube.com/watch",
                "https://www.youtube.com/%61ctivate", "https://www.youtube.com/activate?next=x", "https://www.google.com/device#x")) {
            assertFalse(url, YouTubeAuth.allowedActivation(URI.create(url)));
        }
        Fixture f = new Fixture();
        f.replies.clear();
        f.add(200, "<script id=\"base-js\" src=\"//other.invalid/base.js\"></script>");
        assertEquals(YouTubeAuth.Problem.INVALID_RESPONSE, assertThrows(YouTubeAuth.Failure.class,
                () -> f.client.begin("device-id")).problem);
        assertEquals(Arrays.asList("/tv"), f.paths());
    }

    @Test public void selectedIdentityAndActiveHistoryBodyStayScoped() throws Exception {
        Fixture f = new Fixture();
        f.add(200, """
                {"contents":[{"accountSectionListRenderer":{"contents":[{"accountItemSectionRenderer":{"contents":[
                  {"accountItem":{"accountName":{"runs":[{"text":"Owner"}]},"serviceEndpoint":{"selectActiveIdentityEndpoint":{"supportedTokens":[{"accountStateToken":{"obfuscatedGaiaId":"gaia-owner"}}]}}}},
                  {"accountItem":{"accountName":{"simpleText":"Brand"},"accountByline":{"simpleText":"@brand"},"serviceEndpoint":{"selectActiveIdentityEndpoint":{"supportedTokens":[{"accountStateToken":{"obfuscatedGaiaId":"gaia-brand"}},{"pageIdToken":{"pageId":"page-one"}},{"pageIdToken":{"pageId":"page-two"}}]}}}},
                  {"accountItem":{"isDisabled":true,"accountName":{"simpleText":"Disabled"}}}
                ]}}]}}],"header":{"accountItem":{"accountName":"Decoy"}}}
                """);
        String grid = """
                {"gridRenderer":{"items":[
                  {"gridVideoRenderer":{"videoId":"abcdefghijk","title":{"runs":[{"text":"First"}]},"shortBylineText":{"simpleText":"Channel A"},"thumbnailOverlays":[{"thumbnailOverlayResumePlaybackRenderer":{"percentDurationWatched":122}}]}},
                  {"tileRenderer":{"contentType":"TILE_CONTENT_TYPE_VIDEO","onSelectCommand":{"watchEndpoint":{"videoId":"lmnopqrstuv"}},"metadata":{"tileMetadataRenderer":{"title":{"simpleText":"Second"},"lines":[{"lineRenderer":{"items":[{"lineItemRenderer":{"text":{"simpleText":"Channel B"}}}]}}]}},"header":{"tileHeaderRenderer":{"thumbnailOverlays":[{"thumbnailOverlayResumePlaybackRenderer":{"percentDurationWatched":0}}]}}}},
                  {"lockupViewModel":{"contentType":"LOCKUP_CONTENT_TYPE_VIDEO","contentId":"12345678901","metadata":{"lockupMetadataViewModel":{"title":{"content":"Third"},"metadata":{"contentMetadataViewModel":{"metadataRows":[{"metadataParts":[{"text":{"content":"Channel C"}}]}]}}}},"contentImage":{"thumbnailViewModel":{}}}},
                  {"tileRenderer":{"contentType":"TILE_CONTENT_TYPE_PLAYLIST","onSelectCommand":{"watchEndpoint":{"videoId":"01234567890"}}}},
                  {"videoRenderer":{"videoId":"invalid/id!","title":"Invalid"}}
                ]}}
                """;
        f.add(200, String.format(java.util.Locale.ROOT, """
                {"header":{"videoRenderer":{"videoId":"01234567890","title":"Header"}},"contents":{"tvBrowseRenderer":{"content":{"tvSecondaryNavRenderer":{"sections":[{"tvSecondaryNavSectionRenderer":{"tabs":[
                  {"tabRenderer":{"selected":false,"content":{"gridRenderer":{"items":[{"videoRenderer":{"videoId":"01234567890","title":"Unselected"}}]}}}},
                  {"tabRenderer":{"selected":true,"content":{"tvSurfaceContentRenderer":{"header":{"videoRenderer":{"videoId":"01234567890","title":"Decoy"}},"content":%s}}}}
                ]}}]}}}}}
                """, grid));
        f.add(200, "{\"contents\":{\"gridRenderer\":{\"items\":[]}}}");
        var session = new YouTubeAuth.Session("access-fixture", "refresh-fixture", 99_000);
        var accounts = f.client.identities(session);
        assertEquals(Arrays.asList(new YouTubeAuth.Identity("Owner", null, null), new YouTubeAuth.Identity("Brand", "@brand", "page-one")), accounts);
        assertFalse(accounts.get(1).toString().contains("page-one"));
        assertEquals(Arrays.asList(new YouTubeAuth.Video("First", "Channel A", 100), new YouTubeAuth.Video("Second", "Channel B", 0),
                new YouTubeAuth.Video("Third", "Channel C", null)), f.client.history(session, accounts.get(1)));
        assertTrue(f.client.history(session, accounts.get(0)).isEmpty());
        assertEquals(Arrays.asList("/tv", "/s/tv/fixture/base.js", "/youtubei/v1/account/accounts_list", "/youtubei/v1/browse", "/youtubei/v1/browse"), f.paths());
        Request accountsRequest = f.requests.get(2);
        assertEquals("Bearer access-fixture", accountsRequest.headers.get("Authorization"));
        JSONObject mask = accountsRequest.json().getJSONObject("accountReadMask");
        assertTrue(mask.getBoolean("returnOwner"));
        assertTrue(mask.getBoolean("returnBrandAccounts"));
        assertFalse(mask.getBoolean("returnPersonaAccounts"));
        Request brand = f.requests.get(3);
        assertEquals("https://www.youtube.com/tv", brand.headers.get("Referer"));
        assertEquals("Bearer access-fixture", brand.headers.get("Authorization"));
        assertEquals("page-one", brand.headers.get("X-Goog-PageId"));
        JSONObject body = brand.json();
        assertEquals("FEhistory", body.getString("browseId"));
        assertFalse(body.has("continuation"));
        assertTrue(body.getBoolean("racyCheckOk"));
        assertTrue(body.getBoolean("contentCheckOk"));
        JSONObject user = body.getJSONObject("context").getJSONObject("user");
        assertEquals("page-one", user.getString("onBehalfOfUser"));
        assertFalse(user.getBoolean("enableSafetyMode"));
        assertFalse(user.getBoolean("lockedSafetyMode"));
        JSONObject client = body.getJSONObject("context").getJSONObject("client");
        assertEquals("7.123.0", client.getString("clientVersion"));
        assertEquals("visitor-fixture", client.getString("visitorData"));
        assertEquals("TVHTML5", client.getString("clientName"));
        assertEquals("Cobalt", client.getString("browserName"));
        Request owner = f.requests.get(4);
        assertFalse(owner.headers.containsKey("X-Goog-PageId"));
        assertFalse(owner.json().getJSONObject("context").getJSONObject("user").has("onBehalfOfUser"));
    }

    @Test public void malformedIdentityRouteNeverBecomesOwner() throws Exception {
        var session = new YouTubeAuth.Session("access", "refresh", 99_000);
        for (String route : Arrays.asList(
                "\"serviceEndpoint\":null",
                "\"serviceEndpoint\":[]",
                "\"serviceEndpoint\":{\"selectActiveIdentityEndpoint\":\"private-marker\"}",
                "\"serviceEndpoint\":{\"selectActiveIdentityEndpoint\":null}",
                "\"serviceEndpoint\":{\"selectActiveIdentityEndpoint\":{\"supportedTokens\":null}}",
                "\"serviceEndpoint\":{\"selectActiveIdentityEndpoint\":{\"supportedTokens\":{}}}",
                "\"serviceEndpoint\":{\"selectActiveIdentityEndpoint\":{\"supportedTokens\":[null]}}",
                "\"serviceEndpoint\":{\"selectActiveIdentityEndpoint\":{\"supportedTokens\":[true]}}",
                "\"serviceEndpoint\":{\"selectActiveIdentityEndpoint\":{\"supportedTokens\":[{\"pageIdToken\":null}]}}",
                "\"serviceEndpoint\":{\"selectActiveIdentityEndpoint\":{\"supportedTokens\":[{\"pageIdToken\":\"private-marker\"}]}}")) {
            Fixture f = new Fixture();
            f.add(200, "{\"contents\":[{\"accountItemSectionRenderer\":{\"contents\":["
                    + "{\"accountItem\":{\"accountName\":\"Brand\"," + route + "}}]}}]}");
            var failure = assertThrows("Malformed route must be rejected: " + route, YouTubeAuth.Failure.class,
                    () -> f.client.identities(session));
            assertEquals(YouTubeAuth.Problem.INVALID_RESPONSE, failure.problem);
            assertFalse(failure.toString().contains("private-marker"));
            assertNull(failure.getCause());
            assertEquals(Arrays.asList("/tv", "/s/tv/fixture/base.js", "/youtubei/v1/account/accounts_list"), f.paths());
        }
    }

    @Test public void missingIdentityRouteStillAllowsOwner() throws Exception {
        Fixture f = new Fixture();
        f.add(200, "{\"contents\":[{\"accountItemSectionRenderer\":{\"contents\":[{\"accountItem\":{\"accountName\":\"Owner\"}}]}}]}");
        assertEquals(Collections.singletonList(new YouTubeAuth.Identity("Owner", null, null)),
                f.client.identities(new YouTubeAuth.Session("access", "refresh", 99_000)));
    }

    @Test public void malformedHistoryAndRejectedSessionAreNotEmptyHistory() throws Exception {
        Fixture f = new Fixture();
        f.add(200, "{\"contents\":{\"gridRenderer\":{\"items\":\"private-marker\"}}}");
        f.add(200, "{\"contents\":{\"gridRenderer\":{}}}");
        f.add(200, "{\"contents\":{\"unrecognized\":[]}}");
        f.add(200, "{\"contents\":{\"tvSignInRenderer\":{\"text\":\"private-marker\"}}}");
        f.add(302, "{\"access_token\":\"private-marker\"}");
        f.add(200, "{\"contents\":{\"gridRenderer\":{\"items\":[]}}} trailing-private-marker");
        var session = new YouTubeAuth.Session("access", "refresh", 99_000);
        var owner = new YouTubeAuth.Identity("Owner", null, null);
        for (var expected : Arrays.asList(YouTubeAuth.Problem.INVALID_RESPONSE, YouTubeAuth.Problem.INVALID_RESPONSE,
                YouTubeAuth.Problem.INVALID_RESPONSE, YouTubeAuth.Problem.AUTH_REQUIRED, YouTubeAuth.Problem.INVALID_RESPONSE,
                YouTubeAuth.Problem.INVALID_RESPONSE)) {
            var failure = assertThrows(YouTubeAuth.Failure.class, () -> f.client.history(session, owner));
            assertEquals(expected, failure.problem);
            assertFalse(failure.toString().contains("private-marker"));
            assertNull(failure.getCause());
        }
        assertEquals(8, f.requests.size());
    }

    @Test public void historyPermissionDenialDoesNotInvalidateAuthorization() throws Exception {
        Fixture f = new Fixture();
        f.add(403, "");
        f.add(403, "{\"error\":{\"status\":\"PERMISSION_DENIED\",\"message\":\"private-marker\"}}");
        f.add(200, "{\"error\":{\"status\":\"PERMISSION_DENIED\",\"message\":\"private-marker\"}}");
        f.add(401, "");
        f.add(403, "{\"error\":{\"status\":\"UNAUTHENTICATED\",\"message\":\"private-marker\"}}");
        f.add(200, "{\"error\":{\"status\":\"UNAUTHENTICATED\",\"message\":\"private-marker\"}}");
        var session = new YouTubeAuth.Session("access", "refresh", 99_000);
        var brand = new YouTubeAuth.Identity("Brand", null, "page-one");
        for (var expected : Arrays.asList(YouTubeAuth.Problem.HISTORY_DENIED, YouTubeAuth.Problem.HISTORY_DENIED,
                YouTubeAuth.Problem.HISTORY_DENIED, YouTubeAuth.Problem.AUTH_REQUIRED,
                YouTubeAuth.Problem.AUTH_REQUIRED, YouTubeAuth.Problem.AUTH_REQUIRED)) {
            var failure = assertThrows(YouTubeAuth.Failure.class, () -> f.client.history(session, brand));
            assertEquals(expected, failure.problem);
            assertFalse(failure.toString().contains("private-marker"));
            assertNull(failure.getCause());
        }
        assertEquals(Arrays.asList("/tv", "/s/tv/fixture/base.js", "/youtubei/v1/browse", "/youtubei/v1/browse",
                "/youtubei/v1/browse", "/youtubei/v1/browse", "/youtubei/v1/browse", "/youtubei/v1/browse"), f.paths());
        for (Request request : f.requests.subList(2, f.requests.size())) {
            assertEquals("page-one", request.headers.get("X-Goog-PageId"));
        }
    }

    @Test public void accountAndRefreshDenialsRemainAuthenticationFailures() throws Exception {
        Fixture f = new Fixture();
        f.add(200, "{\"error\":{\"status\":\"PERMISSION_DENIED\",\"message\":\"private-marker\"}}");
        f.add(403, "");
        var session = new YouTubeAuth.Session("access", "refresh", 99_000);
        assertEquals(YouTubeAuth.Problem.AUTH_REQUIRED,
                assertThrows(YouTubeAuth.Failure.class, () -> f.client.identities(session)).problem);
        assertEquals(YouTubeAuth.Problem.AUTH_REQUIRED,
                assertThrows(YouTubeAuth.Failure.class, () -> f.client.refresh("refresh")).problem);
        assertEquals(Arrays.asList("/tv", "/s/tv/fixture/base.js", "/youtubei/v1/account/accounts_list", "/o/oauth2/token"), f.paths());
    }

    @Test public void providerDenialAndIoAreSanitized() throws Exception {
        Fixture f = new Fixture();
        f.add(200, DEVICE);
        f.add(400, "{\"error\":\"access_denied\",\"error_description\":\"private-marker\"}");
        var auth = f.client.begin("device-id");
        var denied = assertThrows(YouTubeAuth.Failure.class, () -> f.client.complete(auth));
        assertEquals(YouTubeAuth.Problem.DECLINED, denied.problem);
        assertFalse(denied.toString().contains("private-marker"));
        var io = assertThrows(YouTubeAuth.Failure.class, () -> f.client.refresh("refresh-fixture"));
        assertEquals(YouTubeAuth.Problem.NETWORK, io.problem);
        assertNull(io.getCause());
    }

    private record Request(URI uri, Map<String, String> headers, String body) {
        JSONObject json() throws Exception { return new JSONObject(body); }
        @Override public String toString() { return "Request[redacted]"; }
    }

    private static final class Fixture {
        final ArrayDeque<TvYouTubeAuth.Reply> replies = new ArrayDeque<>();
        final List<Request> requests = new ArrayList<>();
        final List<Long> waits = new ArrayList<>();
        final TvYouTubeAuth client;
        long now = 1_000;
        boolean interruptDelay;

        Fixture() {
            add(200, "<script id=\"base-js\" src=\"/s/tv/fixture/base.js\"></script>\"INNERTUBE_CLIENT_VERSION\":\"7.123.0\",\"visitorData\":\"visitor-fixture\"");
            add(200, "clientId:\"fixture.apps.googleusercontent.com\",key:\"syntheticSecret\"");
            client = new TvYouTubeAuth((uri, headers, body, limit) -> {
                assertEquals("https", uri.getScheme());
                assertEquals("www.youtube.com", uri.getHost());
                assertTrue(headers.get("User-Agent").contains("Cobalt/"));
                assertTrue(limit > 0);
                requests.add(new Request(uri, headers, body));
                if (replies.isEmpty()) throw new IOException("private-marker");
                return replies.removeFirst();
            }, () -> now, millis -> {
                if (interruptDelay) throw new InterruptedException();
                waits.add(millis);
                now += millis;
            });
        }

        void add(int status, String body) { replies.add(new TvYouTubeAuth.Reply(status, body)); }
        List<String> paths() {
            List<String> paths = new ArrayList<>();
            for (Request request : requests) paths.add(request.uri.getPath());
            return paths;
        }
    }
}
