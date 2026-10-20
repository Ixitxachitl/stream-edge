#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
#
# Builds a slim libmpv-2.dll for Windows x64: only what StreamEdge plays.
#
# The general-purpose builds (shinchiro's, ~115 MB) carry every codec,
# demuxer, protocol and filter FFmpeg has, plus Lua, JavaScript, Blu-ray,
# DVD, Vulkan and more. StreamEdge hands mpv one thing: a local HTTP stream
# (MPEG-TS or fragmented MP4) of H.264, HEVC or AV1 with AAC audio, from its
# own server (LocalStreamServer), drawn with Direct3D 11, heard through
# WASAPI, with CEA-608 captions and an ASS on-screen strip. This keeps that
# and drops the rest.
#
# Runs in an MSYS2 UCRT64 shell (locally, or in CI via msys2/setup-msys2).
# Every dependency is linked in statically: the DLL needs only Windows.
#
#   scripts/build-libmpv.sh [work-dir]
#
# Output: third_party/libmpv/win-x64/libmpv-2.dll (and VERSION).
set -euo pipefail

# Pinned sources. mpv is the commit the previous (shinchiro) build used, so
# playback behaves the same.
FFMPEG_TAG=n9.0.2
MPV_COMMIT=492bae0768f5e826991bf9fb222fa303371e8498
LIBPLACEBO_TAG=v7.360.1
LIBASS_TAG=0.17.5
HARFBUZZ_TAG=14.6.0
FREETYPE_TAG=VER-2-15-0

# MSYS2 packages: the toolchain, plus libraries taken as static archives
# (the shader compilers, fribidi, dav1d).
PACKAGES=(git make diffutils
  mingw-w64-ucrt-x86_64-{gcc,meson,ninja,nasm,pkgconf,python}
  mingw-w64-ucrt-x86_64-{shaderc,glslang,spirv-tools,spirv-cross,fribidi,dav1d})

if [[ "${MSYSTEM:-}" != UCRT64 ]]; then
  echo "Run this in an MSYS2 UCRT64 shell (MSYSTEM=UCRT64)." >&2
  exit 1
fi

# Meson's CMake lookups need a home folder, which Python finds on Windows
# through USERPROFILE: not always passed into an MSYS2 shell.
export USERPROFILE=${USERPROFILE:-$(cygpath -w "$HOME")}

repo=$(cd "$(dirname "$0")/.." && pwd)
# Unix form: a Windows path's "D:" would split PKG_CONFIG_PATH at the colon.
work=$(cygpath -u "${1:-$repo/third_party/libmpv/.build}")
src=$work/src
prefix=$work/prefix
out=$(cygpath -u "${LIBMPV_OUT:-$repo/third_party/libmpv/win-x64}")
mkdir -p "$src" "$prefix/lib/pkgconfig" "$out"

# Only what's missing: "pacman -S --needed" also upgrades what's installed,
# and upgrading gcc alone (without the C runtime it was built against)
# leaves a toolchain that can't link libstdc++ statically.
mapfile -t missing < <(pacman -T "${PACKAGES[@]}" || true)
if (( ${#missing[@]} )); then pacman -S --noconfirm "${missing[@]}"; fi

fetch() { # url ref dir [submodules...]
  local url=$1 ref=$2 dir=$src/$3
  shift 3
  if [[ ! -d $dir/.git ]]; then
    git init -q "$dir"
    git -C "$dir" fetch -q --depth 1 "$url" "$ref"
    git -C "$dir" checkout -q FETCH_HEAD
    for sub in "$@"; do git -C "$dir" submodule update -q --init --depth 1 "$sub"; done
  fi
}
fetch https://github.com/FFmpeg/FFmpeg "$FFMPEG_TAG" ffmpeg
fetch https://github.com/mpv-player/mpv "$MPV_COMMIT" mpv
fetch https://github.com/haasn/libplacebo "$LIBPLACEBO_TAG" libplacebo \
  3rdparty/jinja 3rdparty/markupsafe 3rdparty/fast_float 3rdparty/Vulkan-Headers
fetch https://github.com/libass/libass "$LIBASS_TAG" libass
fetch https://github.com/harfbuzz/harfbuzz "$HARFBUZZ_TAG" harfbuzz
fetch https://gitlab.freedesktop.org/freetype/freetype.git "$FREETYPE_TAG" freetype

export PKG_CONFIG_PATH=$prefix/lib/pkgconfig:/ucrt64/lib/pkgconfig
export CFLAGS="-O2" CXXFLAGS="-O2"

# MSYS2's shaderc_combined isn't: name glslang and SPIRV-Tools too. And
# spirv-cross under the name mpv and libplacebo look for, statically. Paths
# in Windows form: the compiler is a native one, blind to /ucrt64.
ucrt=$(cygpath -m /ucrt64)
for name in shaderc shaderc_combined; do # libplacebo asks for both
  cat > "$prefix/lib/pkgconfig/$name.pc" <<EOF
Name: $name
Description: shaderc with glslang and SPIRV-Tools, static
Version: 2026.3
Libs: -L$ucrt/lib -lshaderc_combined -lglslang -lMachineIndependent -lGenericCodeGen -lOSDependent -lSPIRV -lglslang-default-resource-limits -lSPIRV-Tools-opt -lSPIRV-Tools -lstdc++
Cflags: -I$ucrt/include
EOF
done
cat > "$prefix/lib/pkgconfig/spirv-cross-c-shared.pc" <<EOF
Name: spirv-cross-c-shared
Description: SPIRV-Cross C API, static
Version: 0.68.0
Libs: -L$ucrt/lib -lspirv-cross-c -lspirv-cross-glsl -lspirv-cross-hlsl -lspirv-cross-msl -lspirv-cross-cpp -lspirv-cross-reflect -lspirv-cross-util -lspirv-cross-core -lstdc++
Cflags: -I$ucrt/include/spirv_cross
EOF

meson_static() { # name [options...]
  local name=$1
  shift
  meson setup --reconfigure "$work/build/$name" "$src/$name" --prefix="$prefix" --buildtype=release \
    --default-library=static --prefer-static --wrap-mode=nodownload "$@"
  meson install -C "$work/build/$name" --quiet
}

# Fonts for captions and the on-screen strip: freetype, harfbuzz and libass,
# found through DirectWrite (no fontconfig, no glib).
meson_static freetype -Dbrotli=disabled -Dbzip2=disabled -Dharfbuzz=disabled -Dpng=disabled \
  -Dzlib=internal -Dtests=disabled
meson_static harfbuzz --auto-features=disabled -Dfreetype=enabled -Dtests=disabled -Ddocs=disabled \
  -Dutilities=disabled -Dbenchmark=disabled -Dintrospection=disabled \
  -Dsubset=disabled -Draster=disabled -Dvector=disabled -Dgpu=disabled
meson_static libass --auto-features=disabled -Ddirectwrite=enabled -Drequire-system-font-provider=true \
  -Dasm=enabled

# FFmpeg: the formats Twitch serves, decoded on the GPU (D3D11VA, DXVA2)
# or in software, from the app's own HTTP server.
ffmpeg_args=(--prefix="$prefix"
  --enable-static --disable-shared --disable-programs --disable-doc --disable-debug
  --disable-autodetect --disable-everything
  --enable-w32threads --enable-network --enable-d3d11va --enable-dxva2 --enable-libdav1d
  --enable-protocol=http,tcp,file
  --enable-demuxer=mpegts,mov,aac
  --enable-decoder=h264,hevc,av1,libdav1d,aac,aac_latm,mp3float,opus,ccaption
  --enable-parser=h264,hevc,av1,aac,aac_latm,mpegaudio,opus
  --enable-hwaccel=h264_d3d11va,h264_d3d11va2,h264_dxva2,hevc_d3d11va,hevc_d3d11va2,hevc_dxva2,av1_d3d11va,av1_d3d11va2,av1_dxva2
  --enable-bsfs
  --enable-filter=abuffer,abuffersink,buffer,buffersink,aformat,format,anull,null,aresample,scale
  --pkg-config-flags=--static)
# Configuring again rebuilds all of FFmpeg: only when something changed.
stamp="$FFMPEG_TAG ${ffmpeg_args[*]}"
if [[ "$(cat "$work/build/ffmpeg/.stamp" 2>/dev/null)" != "$stamp" ]]; then
  mkdir -p "$work/build/ffmpeg"
  (cd "$work/build/ffmpeg" && "$src/ffmpeg/configure" "${ffmpeg_args[@]}")
  make -C "$work/build/ffmpeg" -j"$(nproc)"
  make -C "$work/build/ffmpeg" install
  echo "$stamp" > "$work/build/ffmpeg/.stamp"
fi

meson_static libplacebo --auto-features=disabled -Dd3d11=enabled -Dshaderc=enabled \
  -Ddemos=false -Dtests=false -Dbench=false -Dfuzz=false

# libmpv itself: a DLL with everything above linked in.
meson setup --reconfigure "$work/build/mpv" "$src/mpv" --prefix="$prefix" --buildtype=release \
  --default-library=shared --prefer-static --wrap-mode=nodownload --auto-features=disabled \
  -Dlibmpv=true -Dcplayer=false -Dgpl=true -Dlua=disabled -Dgl=disabled \
  -Dwin32-threads=enabled -Dwasapi=enabled -Dd3d11=enabled -Dshaderc=enabled -Dspirv-cross=enabled \
  -Dd3d-hwaccel=enabled \
  -Dc_link_args="-static -static-libgcc -static-libstdc++"
meson compile -C "$work/build/mpv"

dll=$(find "$work/build/mpv" -maxdepth 1 -name 'libmpv-2.dll' | head -1)
strip -s "$dll" -o "$out/libmpv-2.dll"
echo "slim mpv $MPV_COMMIT, FFmpeg $FFMPEG_TAG (scripts/build-libmpv.sh)" > "$out/VERSION"

# Anything but Windows' own DLLs here means a dependency slipped in dynamically.
echo "Imports:"
objdump -p "$out/libmpv-2.dll" | grep 'DLL Name' | sort -u
ls -l "$out/libmpv-2.dll"
