/*
 * libFuzzer harness for the hip/mpglib MP3 decoder (v4 modernization).
 *
 * mpglib is 25-year-old C that parses attacker-controlled bytes whenever LAME decodes
 * (--decode, mp3 input files, CUETools' decode paths). This harness feeds arbitrary data
 * through the same streaming API the frontend uses (hip_decode1_headers), in small chunks to
 * exercise the frame-reassembly buffering, under ASan.
 *
 * Build (clang only):  cmake -B build-fuzz -DLAME_FUZZ=ON -DCMAKE_C_COMPILER=clang
 * Run:                 ./build-fuzz/fuzz_hip_decode seeds/ -max_total_time=60
 * Seeds: any small MP3s (CI generates them with the freshly built encoder).
 */
#include <stddef.h>
#include <stdint.h>
#include <string.h>

#include <lame.h>

/* mpglib emits at most 1152 samples per channel per call; leave generous headroom anyway
   (a too-small PCM buffer would itself be the vulnerability we are hunting). */
#define PCM_MAX (1152 * 8)

int
LLVMFuzzerTestOneInput(const uint8_t *data, size_t size)
{
    static short pcm_l[PCM_MAX];
    static short pcm_r[PCM_MAX];
    mp3data_struct mp3data;
    hip_t   hip;
    size_t  off = 0;

    hip = hip_decode_init();
    if (hip == NULL)
        return 0;
    memset(&mp3data, 0, sizeof(mp3data));

    while (off < size) {
        size_t  chunk = size - off > 512 ? 512 : size - off;
        int     ret;
        int     drain_guard = 0;

        ret = hip_decode1_headers(hip, (unsigned char *) data + off, chunk,
                                  pcm_l, pcm_r, &mp3data);
        /* Drain: len=0 calls return buffered frames until 0 (need more) or -1 (error).
           The guard bounds pathological inputs that keep "producing" without consuming. */
        while (ret > 0 && drain_guard++ < 64) {
            ret = hip_decode1_headers(hip, (unsigned char *) data + off, 0,
                                      pcm_l, pcm_r, &mp3data);
        }
        if (ret == -1)
            break;      /* decoder rejected the stream; that is a valid outcome */
        off += chunk;
    }

    hip_decode_exit(hip);
    return 0;
}
