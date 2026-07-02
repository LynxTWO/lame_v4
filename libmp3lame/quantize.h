/*
 * MP3 quantization
 *
 * Copyright (c) 1999 Mark Taylor
 *
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Library General Public
 * License as published by the Free Software Foundation; either
 * version 2 of the License, or (at your option) any later version.
 *
 * This library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Library General Public License for more details.
 *
 * You should have received a copy of the GNU Library General Public
 * License along with this library; if not, write to the
 * Free Software Foundation, Inc., 59 Temple Place - Suite 330,
 * Boston, MA 02111-1307, USA.
 */

#ifndef LAME_QUANTIZE_H
#define LAME_QUANTIZE_H

void    CBR_iteration_loop(lame_internal_flags * gfc, const FLOAT pe[2][2],
                           const FLOAT ms_ratio[2], const III_psy_ratio ratio[2][2]);

void    VBR_old_iteration_loop(lame_internal_flags * gfc, const FLOAT pe[2][2],
                               const FLOAT ms_ratio[2], const III_psy_ratio ratio[2][2]);

void    VBR_new_iteration_loop(lame_internal_flags * gfc, const FLOAT pe[2][2],
                               const FLOAT ms_ratio[2], const III_psy_ratio ratio[2][2]);

void    ABR_iteration_loop(lame_internal_flags * gfc, const FLOAT pe[2][2],
                           const FLOAT ms_ratio[2], const III_psy_ratio ratio[2][2]);

/* v4 channel-parallel quantization worker (bit-exact; see gfc->qnt_worker in util.h).
   start returns 1 and sets qnt_worker.running on success, 0 otherwise (the encoder then
   just runs sequentially). stop is safe to call whether or not start succeeded. */
int     lame_quantize_worker_start(lame_internal_flags * gfc);
void    lame_quantize_worker_stop(lame_internal_flags * gfc);


#endif /* LAME_QUANTIZE_H */
