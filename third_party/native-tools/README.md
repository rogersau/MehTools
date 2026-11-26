# Bundled media tools

Meh Tools ships FFmpeg/ffprobe for Windows x64. These app-local tools require no PATH changes or separate installation. Custom media tool paths remain available. WARDOGS detection uses the English OCR feature in Windows.

Run `pwsh -File scripts/Prepare-NativeTools.ps1` from a checkout before publishing. This downloads checksum-pinned sources and a portable build-only toolchain into `artifacts/native-tools`, builds into `third_party/native-tools/win-x64`, and creates `artifacts/MehTools-native-sources.zip`. PowerShell 7, Git and Windows tar are the build prerequisites. The compiler, CMake and assembler are bootstrapped without administrator access. No build tools are needed by end users.

Versions, upstream URLs and SHA-256 hashes are recorded in `manifest.json`. FFmpeg is built without GPL or nonfree components. libaom provides AV1 decoding. H.264, HEVC and other built-in decoders remain enabled. Media exports use stream copy.

The app's `tools/licenses` directory contains the FFmpeg LGPL-2.1-or-later notice, libaom's license and patent grant, and the zlib and MinGW runtime notices. **MehTools-native-sources.zip** provides the corresponding FFmpeg, libaom and zlib sources, download manifest and build recipe alongside the installer and portable ZIP.

Source archives retain their original bytes under the filenames in the manifest; use `tar -xf <file> -C <destination> --strip-components <strip>` to unpack each source (`aom` uses zero components). To rebuild offline, populate `artifacts/native-tools/cache` with those files plus the pinned build-only downloads and run the preparation script. To update, change a reviewed version/URL/checksum together, rebuild, and run published-tool smoke checks. Release packaging must fail if tools or source materials are missing.

Run `dotnet run --project tests/JamesTools.UiTests -c Release -- --bundled-tools artifacts/publish` to relocate a self-contained publish into a path with spaces and check its default resolvers with PATH limited to System32. The synthetic checks cover probing, Windows OCR availability, thumbnails, multitrack trimming, montage verification and source hash preservation. CI caches tools and source materials using the manifest and build recipe hashes.
