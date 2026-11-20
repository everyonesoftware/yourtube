# Third-party notices

## MediaServiceCore / SmartTube HTTP behavior reference

YourTube's experimental Windows C# YouTube core adapts HTTP request behavior and parsing patterns from yuliskov/MediaServiceCore, pinned at commit `c1d202fc21bd97d800a5f92d058cd8f5fbbd65d8`.

Upstream repository: https://github.com/yuliskov/MediaServiceCore

Relevant reference files include `youtubeapi/src/main/java/com/liskovsoft/youtubeapi/app/AppApi.java`, `app/models/AppInfo.java`, `app/models/ClientData.java`, `auth/V2/AuthApi.java`, `auth/V2/AuthApiHelper.java`, `common/helpers/AppClient.kt`, `common/models/gen/CommonHelper.kt`, `browse/v2/BrowseApiHelper.kt`, and `googlecommon/common/helpers/DefaultHeaders.kt`.

No upstream Android binaries or runtime dependencies are bundled. Public TV client credentials are discovered at runtime, not distributed as source constants. Google and YouTube do not endorse this proof of concept.

The upstream MIT license is retained below:

MIT License

Copyright (c) 2020-present yuliskov

Permission is hereby granted, free of charge, to any person obtaining
a copy of this software and associated documentation files (the
"Software"), to deal in the Software without restriction, including
without limitation the rights to use, copy, modify, merge, publish,
distribute, sublicense, and/or sell copies of the Software, and to
permit persons to whom the Software is furnished to do so, subject to
the following conditions:

The above copyright notice and this permission notice shall be
included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
