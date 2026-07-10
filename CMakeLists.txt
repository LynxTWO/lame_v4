# LAME v4 CMake build.
#
# Mirrors the canonical Makefile.MSVC Win64 configuration (see build.cmd) so the MSVC output
# is comparable against tests/baseline.json, and adds a portable gcc/clang path for CI and
# other platforms. The bit-exact gate (tests/regress.ps1) is per-toolchain: hashes recorded
# with one compiler+flags are only expected to reproduce with the same compiler+flags.
#
#   Windows (from a vcvars64 shell):  cmake -B build -G Ninja && cmake --build build
#   Linux/macOS:                      cmake -B build && cmake --build build -j
#
# Defines mirror Makefile.MSVC exactly: HAVE_CONFIG_H (config.h is the committed copy of
# configMS.h, which also carries the __GNUC__ branches), HAVE_MPGLIB (decode support),
# TAKEHIRO_IEEE754_HACK (IEEE-754 bit tricks; little-endian), FLOAT8=float REAL_IS_FLOAT=1
# (single-precision build, PREC=SINGLE), BS_FORMAT=BINARY. NDEBUG is deliberately NOT
# defined -- the canonical nmake build keeps asserts live ("temporary remove NDEBUG, see
# configure.in"), and matching it keeps the two builds comparable.
cmake_minimum_required(VERSION 3.15)
project(lame C)

option(LAME_FUZZ "Build the libFuzzer harnesses (requires clang)" OFF)

# Single-config generators default to no build type, which drags in Debug-ish flags that
# clash with the canonical /O2 set below. Everything here assumes Release semantics.
if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
    set(CMAKE_BUILD_TYPE Release CACHE STRING "" FORCE)
endif()

set(LAMELIB_SOURCES
    libmp3lame/bitstream.c
    libmp3lame/encoder.c
    libmp3lame/fft.c
    libmp3lame/gain_analysis.c
    libmp3lame/id3tag.c
    libmp3lame/lame.c
    libmp3lame/newmdct.c
    libmp3lame/psymodel.c
    libmp3lame/quantize.c
    libmp3lame/quantize_pvt.c
    libmp3lame/vector/xmm_quantize_sub.c
    libmp3lame/set_get.c
    libmp3lame/vbrquantize.c
    libmp3lame/reservoir.c
    libmp3lame/tables.c
    libmp3lame/takehiro.c
    libmp3lame/util.c
    libmp3lame/mpglib_interface.c
    libmp3lame/VbrTag.c
    libmp3lame/presets.c
    libmp3lame/version.c
)

# Despite the _i386 names these two are portable C (the names are historical).
set(MPGLIB_SOURCES
    mpglib/common.c
    mpglib/dct64_i386.c
    mpglib/decode_i386.c
    mpglib/layer1.c
    mpglib/layer2.c
    mpglib/layer3.c
    mpglib/tabinit.c
    mpglib/interface.c
)

set(FRONTEND_SOURCES
    frontend/main.c
    frontend/lame_main.c
    frontend/get_audio.c
    frontend/parse.c
    frontend/timestatus.c
    frontend/lametime.c
    frontend/console.c
    frontend/brhist.c
)

set(LAME_DEFS
    HAVE_CONFIG_H
    HAVE_MPGLIB
    TAKEHIRO_IEEE754_HACK
    FLOAT8=float
    REAL_IS_FLOAT=1
    BS_FORMAT=BINARY
    # v4: unlock the frontend's "internal" research options (--ns-bass/-alto/-treble/-sfb21,
    # --nsmsfix, --shortthreshold ...) -- without this they parse but are silently ignored,
    # which turned an entire auto-tuning campaign into a no-op (all 177 configs identical).
    # Frontend-only; encodes that don't pass these options are bit-exact (gate-verified).
    _ALLOW_INTERNAL_OPTIONS
)
if(MSVC)
    list(APPEND LAME_DEFS Win64)
endif()

if(MSVC)
    # Exact Makefile.MSVC Win64 flags: /O2b2ity /GAy /Gs1024 /Zp8 /GL /GS- /Zi /MT.
    # Wipe CMake's per-config defaults (they add /DNDEBUG and different /O levels) so the
    # flag set is deterministic and comparable to the nmake build.
    set(CMAKE_C_FLAGS_RELEASE "")
    set(CMAKE_C_FLAGS_RELWITHDEBINFO "")
    set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")
    set(LAME_C_OPTS /O2 /Ob2 /Oi /Ot /Oy /GA /Gy /Gs1024 /Zp8 /GL /GS- /Zi)
    add_link_options(/LTCG)
else()
    # Portable path: plain -O2, strict IEEE semantics (NO -ffast-math -- the encoder's
    # quality work is measured; changing FP contraction silently changes output).
    set(LAME_C_OPTS -O2)
endif()

add_library(mp3lame STATIC ${LAMELIB_SOURCES} ${MPGLIB_SOURCES})
target_include_directories(mp3lame
    PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}/include
    PRIVATE ${CMAKE_CURRENT_SOURCE_DIR} ${CMAKE_CURRENT_SOURCE_DIR}/libmp3lame
            ${CMAKE_CURRENT_SOURCE_DIR}/mpglib)
target_compile_definitions(mp3lame PRIVATE ${LAME_DEFS})
target_compile_options(mp3lame PRIVATE ${LAME_C_OPTS})

add_executable(lame ${FRONTEND_SOURCES})
target_include_directories(lame PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR} ${CMAKE_CURRENT_SOURCE_DIR}/libmp3lame)
target_compile_definitions(lame PRIVATE ${LAME_DEFS})
target_compile_options(lame PRIVATE ${LAME_C_OPTS})
target_link_libraries(lame PRIVATE mp3lame)
if(NOT MSVC)
    target_link_libraries(lame PRIVATE m)
endif()

# libFuzzer harnesses (clang only): fuzz_hip_decode feeds attacker-controlled MP3 bytes to
# the mpglib/hip decoder; fuzz_encode feeds attacker-controlled parameters and PCM to the
# public encoder API. Opt-in target so normal builds never depend on clang.
if(LAME_FUZZ)
    if(NOT CMAKE_C_COMPILER_ID MATCHES "Clang")
        message(FATAL_ERROR "LAME_FUZZ requires clang (libFuzzer)")
    endif()
    foreach(harness fuzz_hip_decode fuzz_encode)
        add_executable(${harness} fuzz/${harness}.c)
        target_include_directories(${harness} PRIVATE
            ${CMAKE_CURRENT_SOURCE_DIR} ${CMAKE_CURRENT_SOURCE_DIR}/include
            ${CMAKE_CURRENT_SOURCE_DIR}/libmp3lame)
        target_compile_definitions(${harness} PRIVATE ${LAME_DEFS})
        target_compile_options(${harness} PRIVATE -O1 -g -fsanitize=fuzzer,address)
        target_link_options(${harness} PRIVATE -fsanitize=fuzzer,address)
        target_link_libraries(${harness} PRIVATE mp3lame m)
    endforeach()
endif()
