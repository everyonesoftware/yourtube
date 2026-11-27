package com.everyonesoftware.yourtube;

import java.net.URI;
import java.util.List;

/** Blocking provider operations for a worker thread. The caller owns session storage and UI. */
interface YouTubeAuth {
    record Authorization(String userCode, URI verificationUri, String deviceCode,
                         long expiresAtMs, int intervalSeconds) {
        @Override public String toString() { return "Authorization[redacted]"; }
    }

    record Session(String accessToken, String refreshToken, long expiresAtMs) {
        @Override public String toString() { return "Session[redacted]"; }
    }

    record Identity(String name, String byline, String pageId) {
        @Override public String toString() { return "Identity[redacted]"; }
    }

    record Video(String title, String channel, Integer percentWatched) {}

    enum Problem { EXPIRED, DECLINED, AUTH_REQUIRED, HISTORY_DENIED, NETWORK, INVALID_RESPONSE }

    final class Failure extends Exception {
        final Problem problem;

        Failure(Problem problem) {
            super(switch (problem) {
                case EXPIRED -> "The activation code expired. Start sign-in again.";
                case DECLINED -> "YouTube sign-in was declined.";
                case AUTH_REQUIRED -> "Sign in to YouTube again.";
                case HISTORY_DENIED -> "YouTube did not allow access to this identity’s History.";
                case NETWORK -> "YouTube could not be reached. Try again later.";
                case INVALID_RESPONSE -> "YouTube returned an unsupported response.";
            });
            this.problem = problem;
        }
    }

    Authorization begin(String deviceId) throws Failure, InterruptedException;
    Session complete(Authorization authorization) throws Failure, InterruptedException;
    Session refresh(String token) throws Failure, InterruptedException;
    List<Identity> identities(Session session) throws Failure, InterruptedException;
    List<Video> history(Session session, Identity identity) throws Failure, InterruptedException;

    static boolean allowedActivation(URI uri) {
        if (uri == null || !"https".equalsIgnoreCase(uri.getScheme()) || uri.getHost() == null
                || (uri.getPort() != -1 && uri.getPort() != 443) || uri.getRawUserInfo() != null
                || uri.getRawQuery() != null || uri.getRawFragment() != null) return false;
        String host = uri.getHost();
        return "/activate".equals(uri.getRawPath())
                    && ("youtube.com".equalsIgnoreCase(host) || "www.youtube.com".equalsIgnoreCase(host))
                || "/device".equals(uri.getRawPath())
                    && ("google.com".equalsIgnoreCase(host) || "www.google.com".equalsIgnoreCase(host)
                        || "accounts.google.com".equalsIgnoreCase(host));
    }
}
