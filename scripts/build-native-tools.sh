#!/bin/sh
set -eu
work=$1
output=$2
jobs=$3
src="$work/sources"
prefix="$work/prefix"
mkdir -p "$prefix" "$output/ffmpeg"
cmake_build() {
    name=$1
    shift
    cmake -S "$src/$name" -B "$work/build-$name" -G 'MinGW Makefiles' \
        -DCMAKE_BUILD_TYPE=Release -DCMAKE_INSTALL_PREFIX="$prefix" \
        -DCMAKE_POLICY_VERSION_MINIMUM=3.10 -DBUILD_SHARED_LIBS=OFF "$@"
    cmake --build "$work/build-$name" --parallel "$jobs"
    cmake --install "$work/build-$name"
}
cmake_build zlib -DZLIB_BUILD_TESTING=OFF -DZLIB_BUILD_SHARED=OFF
# zlib 1.4 names its static archive libzs.a; expose the conventional name to consumers.
cp "$prefix/lib/libzs.a" "$prefix/lib/libz.a"
cmake_build aom -DENABLE_TESTS=OFF -DENABLE_EXAMPLES=OFF -DENABLE_TOOLS=OFF -DENABLE_DOCS=OFF \
    -DCONFIG_AV1_ENCODER=0 -DENABLE_NASM=ON
mkdir -p "$work/build-ffmpeg"
cd "$work/build-ffmpeg"
export PKG_CONFIG_PATH="$prefix/lib/pkgconfig"
"$src/ffmpeg/configure" --prefix="$prefix" --target-os=mingw32 --arch=x86_64 \
    --cc=gcc --cxx=g++ --pkg-config=pkg-config --pkg-config-flags=--static \
    --disable-autodetect --disable-network --disable-doc --disable-debug --disable-ffplay \
    --enable-libaom --disable-encoder=libaom_av1 --enable-zlib --extra-cflags="-I$prefix/include" --extra-ldflags="-L$prefix/lib -static" \
    --extra-libs=-lpthread
# mingw32-make and the POSIX FFmpeg recipes agree on command handling in verbose mode.
make -j"$jobs" V=1
cp ffmpeg.exe ffprobe.exe "$output/ffmpeg/"
strip "$output/ffmpeg/"*.exe
