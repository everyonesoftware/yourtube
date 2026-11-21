# Controlled quality fixture

`Fixtures/video-90p.mp4` is original synthetic H.264 Main video (160×90, 24 fps, 18 seconds). It differs from the 320×180 original in both decoded size and color. Both have keyframes every two seconds and use the existing shared AAC track. The alternate is validation-only; `ResolveAsync` still returns the original single-rendition manifest.

Generate the alternate from the `Fixtures` directory (write to a scratch copy if the file already exists):

```sh
ffmpeg -hide_banner -loglevel error -nostdin -n \
  -f lavfi -i 'testsrc2=size=160x90:rate=24,negate' -t 18 -an \
  -c:v libx264 -pix_fmt yuv420p -profile:v main -level:v 1.2 \
  -g 48 -keyint_min 48 -sc_threshold 0 -bf 0 -threads 1 -crf 26 \
  -video_track_timescale 12288 \
  -movflags +empty_moov+frag_keyframe+default_base_moof+global_sidx video-90p.mp4
```

The FFmpeg build `n9.0.1-11-ge47273f4d9-20260831` reproduced the checked-in bytes (SHA-256 `724189e5bcca49cf127bca003f2ae16a22d7e0f97ad47d6be562a87319f0f95a`). Other builds can change MP4 offsets or encoding. If regenerating, recompute the inclusive initialization and global `sidx` ranges in `representation-90p.xml` and rerun `dotnet test Tests/FixtureTests.csproj -c Release` from the native validation project directory. Decode both video files with the shared audio separately, for example:

```sh
ffmpeg -hide_banner -loglevel error -xerror -nostdin -i video-90p.mp4 \
  -i audio.mp4 -map 0:v:0 -map 1:a:0 -progress pipe:1 -nostats -f null -
```

These checks cover the embedded assets and resolver, not Windows playback or the active handoff proof.
