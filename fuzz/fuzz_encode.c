/*
 * libFuzzer harness for the ENCODER API (v4 modernization) - the companion to
 * fuzz_hip_decode.c. The decoder harness answers "can hostile bytes crash the decoder";
 * this one answers "can hostile PARAMETERS or PCM crash the encoder": every lame_set_*
 * value below comes straight from fuzz data with no sanitization, because the public API
 * takes plain ints from callers and must fail cleanly (lame_init_params returning an
 * error is a valid outcome; memory unsafety is not).
 *
 * Layout of an input: a 32-byte parameter block, then the rest is PCM fed as interleaved
 * 16-bit samples in a few chunks plus a flush. Sample count is capped so a single input
 * cannot dominate wall time.
 *
 * Build (clang only): same recipe as fuzz_hip_decode.c with this file instead.
 */
#include <stddef.h>
#include <stdint.h>
#include <string.h>

#include <lame.h>

#define MAX_SAMPLES_PER_CH 8192 /* cap per input: keeps exec/s useful */

static int
rd32(const uint8_t * p)
{
    /* little-endian, may produce any int value including negatives - intended */
    return (int) ((uint32_t) p[0] | ((uint32_t) p[1] << 8)
                  | ((uint32_t) p[2] << 16) | ((uint32_t) p[3] << 24));
}

int
LLVMFuzzerTestOneInput(const uint8_t *data, size_t size)
{
    static unsigned char mp3buf[LAME_MAXMP3BUFFER];
    static short pcm[MAX_SAMPLES_PER_CH * 2];
    lame_t  gf;
    size_t  off, nsamp, chunk_sz, fed;
    int     ret;

    if (size < 32)
        return 0;

    gf = lame_init();
    if (gf == NULL)
        return 0;

    /* Parameter soup: raw fuzz values into the public setters. Setters may clamp or
       reject; lame_init_params must validate whatever gets through. */
    lame_set_in_samplerate(gf, rd32(data + 0));
    lame_set_out_samplerate(gf, rd32(data + 4));
    lame_set_num_channels(gf, (int) (data[8] & 0x03));
    lame_set_mode(gf, (MPEG_mode) (data[9] & 0x07));
    lame_set_quality(gf, (int) (data[10] & 0x0F));
    switch (data[11] & 0x03) {
    case 0:
        lame_set_brate(gf, rd32(data + 12));
        break;
    case 1:
        lame_set_VBR(gf, vbr_abr);
        lame_set_VBR_mean_bitrate_kbps(gf, rd32(data + 12));
        break;
    default:
        lame_set_VBR(gf, vbr_mtrh);
        lame_set_VBR_quality(gf, (float) (data[12] % 12) - 1.0f);
        break;
    }
    lame_set_lowpassfreq(gf, rd32(data + 16));
    lame_set_highpassfreq(gf, rd32(data + 20));
    lame_set_scale(gf, (float) rd32(data + 24) / 65536.0f);
    lame_set_copyright(gf, data[28] & 1);
    lame_set_original(gf, (data[28] >> 1) & 1);
    lame_set_error_protection(gf, (data[28] >> 2) & 1);
    lame_set_free_format(gf, (data[28] >> 3) & 1);
    lame_set_findReplayGain(gf, (data[28] >> 4) & 1);
    lame_set_bWriteVbrTag(gf, (data[28] >> 5) & 1);
    lame_set_strict_ISO(gf, data[29] & 3);
    lame_set_quant_comp(gf, data[30] & 0x0F);
    lame_set_quant_comp_short(gf, data[31] & 0x0F);

    if (lame_init_params(gf) < 0) {
        lame_close(gf);
        return 0;       /* clean rejection is the correct behavior */
    }

    /* Feed the rest as interleaved 16-bit PCM in a few chunks, then flush. */
    off = 32;
    fed = 0;
    while (off + 4 <= size && fed < MAX_SAMPLES_PER_CH) {
        chunk_sz = (size - off) / 4;
        if (chunk_sz > 1152)
            chunk_sz = 1152;
        if (chunk_sz > MAX_SAMPLES_PER_CH - fed)
            chunk_sz = MAX_SAMPLES_PER_CH - fed;
        if (chunk_sz == 0)
            break;
        for (nsamp = 0; nsamp < chunk_sz * 2; nsamp++) {
            pcm[nsamp] = (short) ((uint16_t) data[off + nsamp * 2]
                                  | ((uint16_t) data[off + nsamp * 2 + 1] << 8));
        }
        ret = lame_encode_buffer_interleaved(gf, pcm, (int) chunk_sz,
                                             mp3buf, (int) sizeof(mp3buf));
        if (ret < 0)
            break;      /* encoder reported an error; also a valid outcome */
        off += chunk_sz * 4;
        fed += chunk_sz;
    }
    lame_encode_flush(gf, mp3buf, (int) sizeof(mp3buf));
    lame_close(gf);
    return 0;
}
