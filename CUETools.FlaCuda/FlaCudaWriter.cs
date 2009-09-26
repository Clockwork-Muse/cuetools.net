/**
 * CUETools.FlaCuda: FLAC audio encoder using CUDA
 * Copyright (c) 2009 Gregory S. Chudov
 *
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation; either
 * version 2.1 of the License, or (at your option) any later version.
 *
 * This library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public
 * License along with this library; if not, write to the Free Software
 * Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA 02110-1301 USA
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using CUETools.Codecs;
using CUETools.Codecs.FLAKE;
using GASS.CUDA;
using GASS.CUDA.Types;

namespace CUETools.Codecs.FlaCuda
{
	public class FlaCudaWriter : IAudioDest
	{
		Stream _IO = null;
		string _path;
		long _position;

		// number of audio channels
		// valid values are 1 to 8
		int channels, ch_code;

		// audio sample rate in Hz
		int sample_rate, sr_code0, sr_code1;

		// sample size in bits
		// only 16-bit is currently supported
		uint bits_per_sample;
		int bps_code;

		// total stream samples
		// if 0, stream length is unknown
		int sample_count;

		FlakeEncodeParams eparams;

		// maximum frame size in bytes
		// this can be used to allocate memory for output
		int max_frame_size;

		byte[] frame_buffer = null;
		BitWriter frame_writer = null;

		int frame_count = 0;

		long first_frame_offset = 0;

		TimeSpan _userProcessorTime;

		// header bytes
		// allocated by flake_encode_init and freed by flake_encode_close
		byte[] header;

		int[] residualBuffer;
		float[] windowBuffer;
		byte[] md5_buffer;
		int samplesInBuffer = 0;
		int max_frames = 0;

		int _compressionLevel = 5;
		int _blocksize = 0;
		int _totalSize = 0;
		int _windowsize = 0, _windowcount = 0;

		Crc8 crc8;
		Crc16 crc16;
		MD5 md5;

		FlakeReader verify;

		SeekPoint[] seek_table;
		int seek_table_offset = -1;

		bool inited = false;

		CUDA cuda;
		FlaCudaTask task1;
		FlaCudaTask task2;

		CUdeviceptr cudaWindow;

		bool encode_on_cpu = true;

		public const int MAX_BLOCKSIZE = 4608 * 16;
		internal const int maxFrames = 32;
		internal const int maxResidualParts = 64; // not (MAX_BLOCKSIZE + 255) / 256!! 64 is hardcoded in cudaEstimateResidual. It's per block.
		internal const int maxAutocorParts = (MAX_BLOCKSIZE + 255) / 256;

		public FlaCudaWriter(string path, int bitsPerSample, int channelCount, int sampleRate, Stream IO)
		{
			if (bitsPerSample != 16)
				throw new Exception("Bits per sample must be 16.");
			if (channelCount != 2)
				throw new Exception("ChannelCount must be 2.");

			channels = channelCount;
			sample_rate = sampleRate;
			bits_per_sample = (uint) bitsPerSample;

			// flake_validate_params

			_path = path;
			_IO = IO;

			residualBuffer = new int[FlaCudaWriter.MAX_BLOCKSIZE * (channels == 2 ? 10 : channels + 1)];
			windowBuffer = new float[FlaCudaWriter.MAX_BLOCKSIZE * lpc.MAX_LPC_WINDOWS];
			md5_buffer = new byte[FlaCudaWriter.MAX_BLOCKSIZE * channels * bits_per_sample / 8];

			eparams.flake_set_defaults(_compressionLevel);
			eparams.padding_size = 8192;

			crc8 = new Crc8();
			crc16 = new Crc16();
		}

		public int TotalSize
		{
			get
			{
				return _totalSize;
			}
		}

		public int PaddingLength
		{
			get
			{
				return eparams.padding_size;
			}
			set
			{
				eparams.padding_size = value;
			}
		}

		public int CompressionLevel
		{
			get
			{
				return _compressionLevel;
			}
			set
			{
				if (value < 0 || value > 11)
					throw new Exception("unsupported compression level");
				_compressionLevel = value;
				eparams.flake_set_defaults(_compressionLevel);
			}
		}

		//[DllImport("kernel32.dll")]
		//static extern bool GetThreadTimes(IntPtr hThread, out long lpCreationTime, out long lpExitTime, out long lpKernelTime, out long lpUserTime);
		//[DllImport("kernel32.dll")]
		//static extern IntPtr GetCurrentThread();

		void DoClose()
		{
			if (inited)
			{
				int nFrames = samplesInBuffer / eparams.block_size;
				if (nFrames > 0)
					do_output_frames(nFrames);
				if (samplesInBuffer > 0)
				{
					eparams.block_size = samplesInBuffer;
					do_output_frames(1);
				}
				if (task2.frameCount > 0)
				{
					cuda.SynchronizeStream(task2.stream);
					process_result(task2);
					task2.frameCount = 0;
				}

				if (_IO.CanSeek)
				{
					if (sample_count == 0 && _position != 0)
					{
						BitWriter bitwriter = new BitWriter(header, 0, 4);
						bitwriter.writebits(32, (int)_position);
						bitwriter.flush();
						_IO.Position = 22;
						_IO.Write(header, 0, 4);
					}

					if (md5 != null)
					{
						md5.TransformFinalBlock(frame_buffer, 0, 0);
						_IO.Position = 26;
						_IO.Write(md5.Hash, 0, md5.Hash.Length);
					}

					if (seek_table != null)
					{
						_IO.Position = seek_table_offset;
						int len = write_seekpoints(header, 0, 0);
						_IO.Write(header, 4, len - 4);
					}
				}
				_IO.Close();

				cuda.Free(cudaWindow);
				task1.Dispose();
				task2.Dispose();
				cuda.Dispose();
				inited = false;
			}
		}

		public void Close()
		{
			DoClose();
			if (sample_count != 0 && _position != sample_count)
				throw new Exception(string.Format("Samples written differs from the expected sample count. Expected {0}, got {1}.", sample_count, _position));
		}

		public void Delete()
		{
			if (inited)
			{
				_IO.Close();
				cuda.Free(cudaWindow);
				task1.Dispose();
				task2.Dispose();
				cuda.Dispose();
				inited = false;
			}

			if (_path != "")
				File.Delete(_path);
		}

		public long Position
		{
			get
			{
				return _position;
			}
		}

		public long FinalSampleCount
		{
			set { sample_count = (int)value; }
		}

		public long BlockSize
		{
			set {
				if (value < 256 || value > MAX_BLOCKSIZE )
					throw new Exception("unsupported BlockSize value");
				_blocksize = (int)value; 
			}
			get { return _blocksize == 0 ? eparams.block_size : _blocksize; }
		}

		public StereoMethod StereoMethod
		{
			get { return eparams.do_midside ? StereoMethod.Search : StereoMethod.Independent; }
			set { eparams.do_midside = value != StereoMethod.Independent; }
		}

		public int MinPrecisionSearch
		{
			get { return eparams.lpc_min_precision_search; }
			set
			{
				if (value < 0 || value > eparams.lpc_max_precision_search)
					throw new Exception("unsupported MinPrecisionSearch value");
				eparams.lpc_min_precision_search = value;
			}
		}

		public int MaxPrecisionSearch
		{
			get { return eparams.lpc_max_precision_search; }
			set
			{
				if (value < eparams.lpc_min_precision_search || value >= lpc.MAX_LPC_PRECISIONS)
					throw new Exception("unsupported MaxPrecisionSearch value");
				eparams.lpc_max_precision_search = value;
			}
		}

		public WindowFunction WindowFunction
		{
			get { return eparams.window_function; }
			set { eparams.window_function = value; }
		}

		public bool DoMD5
		{
			get { return eparams.do_md5; }
			set { eparams.do_md5 = value; }
		}

		public bool DoVerify
		{
			get { return eparams.do_verify; }
			set { eparams.do_verify = value; }
		}

		public bool DoSeekTable
		{
			get { return eparams.do_seektable; }
			set { eparams.do_seektable = value; }
		}

		public int VBRMode
		{
			get { return eparams.variable_block_size; }
			set { eparams.variable_block_size = value; }
		}

		public int MinLPCOrder
		{
			get
			{
				return eparams.min_prediction_order;
			}
			set
			{
				if (value < 1 || value > eparams.max_prediction_order)
					throw new Exception("invalid MinLPCOrder " + value.ToString());
				eparams.min_prediction_order = value;
			}
		}

		public int MaxLPCOrder
		{
			get
			{
				return eparams.max_prediction_order;
			}
			set
			{
				if (value > lpc.MAX_LPC_ORDER || value < eparams.min_prediction_order)
					throw new Exception("invalid MaxLPCOrder " + value.ToString());
				eparams.max_prediction_order = value;
			}
		}

		public int MinFixedOrder
		{
			get
			{
				return eparams.min_fixed_order;
			}
			set
			{
				if (value < 0 || value > eparams.max_fixed_order)
					throw new Exception("invalid MinFixedOrder " + value.ToString());
				eparams.min_fixed_order = value;
			}
		}

		public int MaxFixedOrder
		{
			get
			{
				return eparams.max_fixed_order;
			}
			set
			{
				if (value > 4 || value < eparams.min_fixed_order)
					throw new Exception("invalid MaxFixedOrder " + value.ToString());
				eparams.max_fixed_order = value;
			}
		}

		public int MinPartitionOrder
		{
			get { return eparams.min_partition_order; }
			set
			{
				if (value < 0 || value > eparams.max_partition_order)
					throw new Exception("invalid MinPartitionOrder " + value.ToString());
				eparams.min_partition_order = value;
			}
		}

		public int MaxPartitionOrder
		{
			get { return eparams.max_partition_order; }
			set
			{
				if (value > 8 || value < eparams.min_partition_order)
					throw new Exception("invalid MaxPartitionOrder " + value.ToString());
				eparams.max_partition_order = value;
			}
		}

		public TimeSpan UserProcessorTime
		{
			get { return _userProcessorTime; }
		}

		public int BitsPerSample
		{
			get { return 16; }
		}

		/// <summary>
		/// Copy channel-interleaved input samples into separate subframes
		/// </summary>
		/// <param name="samples"></param>
		/// <param name="pos"></param>
		/// <param name="block"></param>
 		unsafe void copy_samples(int[,] samples, int pos, int block, FlaCudaTask task)
		{
			int* s = ((int*)task.samplesBufferPtr) + samplesInBuffer;
			fixed (int* src = &samples[pos, 0])
			{
				short* dst = ((short*)task.samplesBytesPtr) + samplesInBuffer * channels;
				for (int i = 0; i < block * channels; i++)
					dst[i] = (short)src[i];
				if (channels == 2 && eparams.do_midside)
					channel_decorrelation(s, s + FlaCudaWriter.MAX_BLOCKSIZE,
						s + 2 * FlaCudaWriter.MAX_BLOCKSIZE, s + 3 * FlaCudaWriter.MAX_BLOCKSIZE, src, block);
				else
					for (int ch = 0; ch < channels; ch++)
					{
						int* psamples = s + ch * FlaCudaWriter.MAX_BLOCKSIZE;
						for (int i = 0; i < block; i++)
							psamples[i] = src[i * channels + ch];
					}
			}
			samplesInBuffer += block;
		}

		unsafe static void channel_decorrelation(int* leftS, int* rightS, int *leftM, int *rightM, int* src, int blocksize)
		{
			for (int i = 0; i < blocksize; i++)
			{
				int l = *(src++);
				int r = *(src++);
				leftS[i] = l;
				rightS[i] = r;
				leftM[i] = (l + r) >> 1;
				rightM[i] = l - r;
			}
		}

		unsafe void encode_residual_fixed(int* res, int* smp, int n, int order)
		{
			int i;
			int s0, s1, s2;
			switch (order)
			{
				case 0:
					AudioSamples.MemCpy(res, smp, n);
					return;
				case 1:
					*(res++) = s1 = *(smp++);
					for (i = n - 1; i > 0; i--)
					{
						s0 = *(smp++);
						*(res++) = s0 - s1;
						s1 = s0;
					}
					return;
				case 2:
					*(res++) = s2 = *(smp++);
					*(res++) = s1 = *(smp++);
					for (i = n - 2; i > 0; i--)
					{
						s0 = *(smp++);
						*(res++) = s0 - 2 * s1 + s2;
						s2 = s1;
						s1 = s0;
					}
					return;
				case 3:
					res[0] = smp[0];
					res[1] = smp[1];
					res[2] = smp[2];
					for (i = 3; i < n; i++)
					{
						res[i] = smp[i] - 3 * smp[i - 1] + 3 * smp[i - 2] - smp[i - 3];
					}
					return;
				case 4:
					res[0] = smp[0];
					res[1] = smp[1];
					res[2] = smp[2];
					res[3] = smp[3];
					for (i = 4; i < n; i++)
					{
						res[i] = smp[i] - 4 * smp[i - 1] + 6 * smp[i - 2] - 4 * smp[i - 3] + smp[i - 4];
					}
					return;
				default:
					return;
			}
		}

		static uint rice_encode_count(uint sum, uint n, uint k)
		{
			return n * (k + 1) + ((sum - (n >> 1)) >> (int)k);
		}

		//static unsafe uint find_optimal_rice_param(uint sum, uint n)
		//{
		//    uint* nbits = stackalloc uint[Flake.MAX_RICE_PARAM + 1];
		//    int k_opt = 0;

		//    nbits[0] = UINT32_MAX;
		//    for (int k = 0; k <= Flake.MAX_RICE_PARAM; k++)
		//    {
		//        nbits[k] = rice_encode_count(sum, n, (uint)k);
		//        if (nbits[k] < nbits[k_opt])
		//            k_opt = k;
		//    }
		//    return (uint)k_opt;
		//}

		static unsafe int find_optimal_rice_param(uint sum, uint n, out uint nbits_best)
		{
			int k_opt = 0;
			uint a = n;
			uint b = sum - (n >> 1);
			uint nbits = a + b;
			for (int k = 1; k <= Flake.MAX_RICE_PARAM; k++)
			{
				a += n;
				b >>= 1;
				uint nbits_k = a + b;
				if (nbits_k < nbits)
				{
					k_opt = k;
					nbits = nbits_k;
				}
			}
			nbits_best = nbits;
			return k_opt;
		}

		static unsafe uint calc_optimal_rice_params(ref RiceContext rc, int porder, uint* sums, uint n, uint pred_order)
		{
			uint part = (1U << porder);
			uint all_bits = 0;			
			rc.rparams[0] = find_optimal_rice_param(sums[0], (n >> porder) - pred_order, out all_bits);
			uint cnt = (n >> porder);
			for (uint i = 1; i < part; i++)
			{
				uint nbits;
				rc.rparams[i] = find_optimal_rice_param(sums[i], cnt, out nbits);
				all_bits += nbits;
			}
			all_bits += (4 * part);
			rc.porder = porder;
			return all_bits;
		}

		static unsafe void calc_sums(int pmin, int pmax, int* data, uint n, uint pred_order, uint* sums)
		{
			// sums for highest level
			int parts = (1 << pmax);
			int* res = data + pred_order;
			uint cnt = (n >> pmax) - pred_order;
			uint sum = 0;
			for (uint j = cnt; j > 0; j--)
			{
				int val = *(res++);
				sum += (uint)((val << 1) ^ (val >> 31));
			}
			sums[pmax * Flake.MAX_PARTITIONS + 0] = sum;
			cnt = (n >> pmax);
			for (int i = 1; i < parts; i++)
			{
				sum = 0;
				for (uint j = cnt; j > 0; j--)
				{
					int val = *(res++);
					sum += (uint)((val << 1) ^ (val >> 31));
				}
				sums[pmax * Flake.MAX_PARTITIONS + i] = sum;
			}
			// sums for lower levels
			for (int i = pmax - 1; i >= pmin; i--)
			{
				parts = (1 << i);
				for (int j = 0; j < parts; j++)
				{
					sums[i * Flake.MAX_PARTITIONS + j] = 
						sums[(i + 1) * Flake.MAX_PARTITIONS + 2 * j] + 
						sums[(i + 1) * Flake.MAX_PARTITIONS + 2 * j + 1];
				}
			}
		}

		static unsafe uint calc_rice_params(ref RiceContext rc, ref RiceContext tmp_rc, int pmin, int pmax, int* data, uint n, uint pred_order)
		{
			//uint* udata = stackalloc uint[(int)n];
			uint* sums = stackalloc uint[(pmax + 1) * Flake.MAX_PARTITIONS];
			//uint* bits = stackalloc uint[Flake.MAX_PARTITION_ORDER];

			//assert(pmin >= 0 && pmin <= Flake.MAX_PARTITION_ORDER);
			//assert(pmax >= 0 && pmax <= Flake.MAX_PARTITION_ORDER);
			//assert(pmin <= pmax);

			//for (uint i = 0; i < n; i++)
			//    udata[i] = (uint) ((2 * data[i]) ^ (data[i] >> 31));

			calc_sums(pmin, pmax, data, n, pred_order, sums);

			int opt_porder = pmin;
			uint opt_bits = AudioSamples.UINT32_MAX;
			for (int i = pmin; i <= pmax; i++)
			{
				uint bits = calc_optimal_rice_params(ref tmp_rc, i, sums + i * Flake.MAX_PARTITIONS, n, pred_order);
				if (bits <= opt_bits)
				{
					opt_porder = i;
					opt_bits = bits;
					RiceContext tmp_rc2 = rc;
					rc = tmp_rc;
					tmp_rc = tmp_rc2;
				}
			}

			return opt_bits;
		}

		static int get_max_p_order(int max_porder, int n, int order)
		{
			int porder = Math.Min(max_porder, BitReader.log2i(n ^ (n - 1)));
			if (order > 0)
				porder = Math.Min(porder, BitReader.log2i(n / order));
			return porder;
		}

		unsafe void output_frame_header(FlacFrame frame, BitWriter bitwriter)
		{
			bitwriter.writebits(15, 0x7FFC);
			bitwriter.writebits(1, eparams.variable_block_size > 0 ? 1 : 0);
			bitwriter.writebits(4, frame.bs_code0);
			bitwriter.writebits(4, sr_code0);
			if (frame.ch_mode == ChannelMode.NotStereo)
				bitwriter.writebits(4, ch_code);
			else
				bitwriter.writebits(4, (int) frame.ch_mode);
			bitwriter.writebits(3, bps_code);
			bitwriter.writebits(1, 0);
			bitwriter.write_utf8(frame_count);

			// custom block size
			if (frame.bs_code1 >= 0)
			{
				if (frame.bs_code1 < 256)
					bitwriter.writebits(8, frame.bs_code1);
				else
					bitwriter.writebits(16, frame.bs_code1);
			}

			// custom sample rate
			if (sr_code1 > 0)
			{
				if (sr_code1 < 256)
					bitwriter.writebits(8, sr_code1);
				else
					bitwriter.writebits(16, sr_code1);
			}

			// CRC-8 of frame header
			bitwriter.flush();
			byte crc = crc8.ComputeChecksum(frame_buffer, 0, bitwriter.Length);
			bitwriter.writebits(8, crc);
		}

		unsafe void output_residual(FlacFrame frame, BitWriter bitwriter, FlacSubframeInfo sub)
		{
			// rice-encoded block
			bitwriter.writebits(2, 0);

			// partition order
			int porder = sub.best.rc.porder;
			int psize = frame.blocksize >> porder;
			//assert(porder >= 0);
			bitwriter.writebits(4, porder);
			int res_cnt = psize - sub.best.order;

			// residual
			int j = sub.best.order;
			for (int p = 0; p < (1 << porder); p++)
			{
				int k = sub.best.rc.rparams[p];
				bitwriter.writebits(4, k);
				if (p == 1) res_cnt = psize;
				int cnt = Math.Min(res_cnt, frame.blocksize - j);
				bitwriter.write_rice_block_signed(k, sub.best.residual + j, cnt);
				j += cnt;
			}
		}

		unsafe void 
		output_subframe_constant(FlacFrame frame, BitWriter bitwriter, FlacSubframeInfo sub)
		{
			bitwriter.writebits_signed(sub.obits, sub.samples[0]);
		}

		unsafe void
		output_subframe_verbatim(FlacFrame frame, BitWriter bitwriter, FlacSubframeInfo sub)
		{
			int n = frame.blocksize;
			for (int i = 0; i < n; i++)
				bitwriter.writebits_signed(sub.obits, sub.samples[i]);
			// Don't use residual here, because we don't copy samples to residual for verbatim frames.
		}

		unsafe void
		output_subframe_fixed(FlacFrame frame, BitWriter bitwriter, FlacSubframeInfo sub)
		{
			// warm-up samples
			for (int i = 0; i < sub.best.order; i++)
				bitwriter.writebits_signed(sub.obits, sub.samples[i]);

			// residual
			output_residual(frame, bitwriter, sub);
		}

		unsafe void
		output_subframe_lpc(FlacFrame frame, BitWriter bitwriter, FlacSubframeInfo sub)
		{
			// warm-up samples
			for (int i = 0; i < sub.best.order; i++)
				bitwriter.writebits_signed(sub.obits, sub.samples[i]);

			// LPC coefficients
			bitwriter.writebits(4, sub.best.cbits - 1);
			bitwriter.writebits_signed(5, sub.best.shift);
			for (int i = 0; i < sub.best.order; i++)
				bitwriter.writebits_signed(sub.best.cbits, sub.best.coefs[i]);
			
			// residual
			output_residual(frame, bitwriter, sub);
		}

		unsafe void output_subframes(FlacFrame frame, BitWriter bitwriter)
		{
			for (int ch = 0; ch < channels; ch++)
			{
				FlacSubframeInfo sub = frame.subframes[ch];
				// subframe header
				int type_code = (int) sub.best.type;
				if (sub.best.type == SubframeType.Fixed)
					type_code |= sub.best.order;
				if (sub.best.type == SubframeType.LPC)
					type_code |= sub.best.order - 1;
				bitwriter.writebits(1, 0);
				bitwriter.writebits(6, type_code);
				bitwriter.writebits(1, sub.wbits != 0 ? 1 : 0);
				if (sub.wbits > 0)
					bitwriter.writebits((int)sub.wbits, 1);

				//if (frame_writer.Length >= frame_buffer.Length)
				//    throw new Exception("buffer overflow");

				// subframe
				switch (sub.best.type)
				{
					case SubframeType.Constant:
						output_subframe_constant(frame, bitwriter, sub);
						break;
					case SubframeType.Verbatim:
						output_subframe_verbatim(frame, bitwriter, sub);
						break;
					case SubframeType.Fixed:
						output_subframe_fixed(frame, bitwriter, sub);
						break;
					case SubframeType.LPC:
						output_subframe_lpc(frame, bitwriter, sub);
						break;
				}
				//if (frame_writer.Length >= frame_buffer.Length)
				//    throw new Exception("buffer overflow");
			}
		}

		void output_frame_footer(BitWriter bitwriter)
		{
			bitwriter.flush();
			ushort crc = crc16.ComputeChecksum(frame_buffer, 0, bitwriter.Length);
			bitwriter.writebits(16, crc);
			bitwriter.flush();
		}

		unsafe delegate void window_function(float* window, int size);

		unsafe void calculate_window(float* window, window_function func, WindowFunction flag)
		{
			if ((eparams.window_function & flag) == 0 || _windowcount == lpc.MAX_LPC_WINDOWS)
				return;

			func(window + _windowcount * FlaCudaWriter.MAX_BLOCKSIZE, _windowsize);
			//int sz = _windowsize;
			//float* pos = window + _windowcount * FlaCudaWriter.MAX_BLOCKSIZE * 2;
			//do
			//{
			//    func(pos, sz);
			//    if ((sz & 1) != 0)
			//        break;
			//    pos += sz;
			//    sz >>= 1;
			//} while (sz >= 32);
			_windowcount++;
		}

		unsafe void initialize_autocorTasks(int blocksize, int channelsCount, int nFrames, FlaCudaTask task)
		{
			computeAutocorTaskStruct* autocorTasks = (computeAutocorTaskStruct*)task.autocorTasksPtr;
			task.nAutocorTasks = 0;
			task.nResidualTasks = 0;
			task.nResidualTasksPerChannel = (_windowcount * eparams.max_prediction_order + 1 + (eparams.do_constant ? 1 : 0) + eparams.max_fixed_order - eparams.min_fixed_order + 7) & ~7;
			task.nAutocorTasksPerChannel = _windowcount;
			for (int iFrame = 0; iFrame < nFrames; iFrame++)
			{
				for (int ch = 0; ch < channelsCount; ch++)
				{
					for (int iWindow = 0; iWindow < _windowcount; iWindow++)
					{
						// Autocorelation task
						autocorTasks[task.nAutocorTasks].samplesOffs = ch * FlaCudaWriter.MAX_BLOCKSIZE + iFrame * blocksize;
						autocorTasks[task.nAutocorTasks].windowOffs = iWindow * FlaCudaWriter.MAX_BLOCKSIZE;
						autocorTasks[task.nAutocorTasks].residualOffs = eparams.max_prediction_order * iWindow + task.nResidualTasksPerChannel * (ch + iFrame * channelsCount);
						autocorTasks[task.nAutocorTasks].blocksize = blocksize;
						task.nAutocorTasks++;
						// LPC tasks
						for (int order = 1; order <= eparams.max_prediction_order; order++)
						{
							task.ResidualTasks[task.nResidualTasks].type = (int)SubframeType.LPC;
							task.ResidualTasks[task.nResidualTasks].channel = ch;
							task.ResidualTasks[task.nResidualTasks].obits = (int)bits_per_sample + (channels == 2 && ch == 3 ? 1 : 0);
							task.ResidualTasks[task.nResidualTasks].blocksize = blocksize;
							task.ResidualTasks[task.nResidualTasks].residualOrder = order;
							task.ResidualTasks[task.nResidualTasks].samplesOffs = ch * FlaCudaWriter.MAX_BLOCKSIZE + iFrame * blocksize;
							task.ResidualTasks[task.nResidualTasks].residualOffs = task.ResidualTasks[task.nResidualTasks].samplesOffs;
							task.nResidualTasks++;
						}
					}
					// Constant frames
					if (eparams.do_constant)
					{
						task.ResidualTasks[task.nResidualTasks].type = (int)SubframeType.Constant;
						task.ResidualTasks[task.nResidualTasks].channel = ch;
						task.ResidualTasks[task.nResidualTasks].obits = (int)bits_per_sample + (channels == 2 && ch == 3 ? 1 : 0);
						task.ResidualTasks[task.nResidualTasks].blocksize = blocksize;
						task.ResidualTasks[task.nResidualTasks].samplesOffs = ch * FlaCudaWriter.MAX_BLOCKSIZE + iFrame * blocksize;
						task.ResidualTasks[task.nResidualTasks].residualOffs = task.ResidualTasks[task.nResidualTasks].samplesOffs;
						task.ResidualTasks[task.nResidualTasks].residualOrder = 1;
						task.ResidualTasks[task.nResidualTasks].shift = 0;
						task.ResidualTasks[task.nResidualTasks].coefs[0] = 1;
						task.nResidualTasks++;
					}
					// Fixed prediction
					for (int order = eparams.min_fixed_order; order <= eparams.max_fixed_order; order++)
					{
						task.ResidualTasks[task.nResidualTasks].type = (int)SubframeType.Fixed;
						task.ResidualTasks[task.nResidualTasks].channel = ch;
						task.ResidualTasks[task.nResidualTasks].obits = (int)bits_per_sample + (channels == 2 && ch == 3 ? 1 : 0);
						task.ResidualTasks[task.nResidualTasks].blocksize = blocksize;
						task.ResidualTasks[task.nResidualTasks].residualOrder = order;
						task.ResidualTasks[task.nResidualTasks].samplesOffs = ch * FlaCudaWriter.MAX_BLOCKSIZE + iFrame * blocksize;
						task.ResidualTasks[task.nResidualTasks].residualOffs = task.ResidualTasks[task.nResidualTasks].samplesOffs;
						task.ResidualTasks[task.nResidualTasks].shift = 0;
						switch (order)
						{
							case 0:
								break;
							case 1:
								task.ResidualTasks[task.nResidualTasks].coefs[0] = 1;
								break;
							case 2:
								task.ResidualTasks[task.nResidualTasks].coefs[1] = 2;
								task.ResidualTasks[task.nResidualTasks].coefs[0] = -1;
								break;
							case 3:
								task.ResidualTasks[task.nResidualTasks].coefs[2] = 3;
								task.ResidualTasks[task.nResidualTasks].coefs[1] = -3;
								task.ResidualTasks[task.nResidualTasks].coefs[0] = 1;
								break;
							case 4:
								task.ResidualTasks[task.nResidualTasks].coefs[3] = 4;
								task.ResidualTasks[task.nResidualTasks].coefs[2] = -6;
								task.ResidualTasks[task.nResidualTasks].coefs[1] = 4;
								task.ResidualTasks[task.nResidualTasks].coefs[0] = -1;
								break;
						}
						task.nResidualTasks++;
					}
					// Filler
					while ((task.nResidualTasks % task.nResidualTasksPerChannel) != 0)
					{
						task.ResidualTasks[task.nResidualTasks].type = (int)SubframeType.Verbatim;
						task.ResidualTasks[task.nResidualTasks].channel = ch;
						task.ResidualTasks[task.nResidualTasks].obits = (int)bits_per_sample + (channels == 2 && ch == 3 ? 1 : 0);
						task.ResidualTasks[task.nResidualTasks].blocksize = blocksize;
						task.ResidualTasks[task.nResidualTasks].residualOrder = 0;
						task.ResidualTasks[task.nResidualTasks].samplesOffs = ch * FlaCudaWriter.MAX_BLOCKSIZE + iFrame * blocksize;
						task.ResidualTasks[task.nResidualTasks].residualOffs = task.ResidualTasks[task.nResidualTasks].samplesOffs;
						task.ResidualTasks[task.nResidualTasks].shift = 0;
						task.nResidualTasks++;
					}
				}
			}
			if (sizeof(encodeResidualTaskStruct) * task.nResidualTasks > task.residualTasksLen)
				throw new Exception("oops");
			if (sizeof(computeAutocorTaskStruct) * task.nAutocorTasks > task.autocorTasksLen)
				throw new Exception("oops");
			cuda.CopyHostToDeviceAsync(task.cudaAutocorTasks, task.autocorTasksPtr, (uint)(sizeof(computeAutocorTaskStruct) * task.nAutocorTasks), task.stream);
			cuda.CopyHostToDeviceAsync(task.cudaResidualTasks, task.residualTasksPtr, (uint)(sizeof(encodeResidualTaskStruct) * task.nResidualTasks), task.stream);
			task.frameSize = blocksize;
		}

		unsafe void encode_residual(FlacFrame frame)
		{
			for (int ch = 0; ch < channels; ch++)
			{
				switch (frame.subframes[ch].best.type)
				{
					case SubframeType.Constant:
						break;
					case SubframeType.Verbatim:
						break;
					case SubframeType.Fixed:
						{
							encode_residual_fixed(frame.subframes[ch].best.residual, frame.subframes[ch].samples,
								frame.blocksize, frame.subframes[ch].best.order);

							int pmin = get_max_p_order(eparams.min_partition_order, frame.blocksize, frame.subframes[ch].best.order);
							int pmax = get_max_p_order(eparams.max_partition_order, frame.blocksize, frame.subframes[ch].best.order);
							uint bits = (uint)frame.subframes[ch].best.order * frame.subframes[ch].obits + 6;
							frame.subframes[ch].best.size = bits + calc_rice_params(ref frame.subframes[ch].best.rc, ref frame.current.rc, pmin, pmax, frame.subframes[ch].best.residual, (uint)frame.blocksize, (uint)frame.subframes[ch].best.order);
						}
						break;
					case SubframeType.LPC:
						fixed (int* coefs = frame.subframes[ch].best.coefs)
						{
							ulong csum = 0;
							for (int i = frame.subframes[ch].best.order; i > 0; i--)
								csum += (ulong)Math.Abs(coefs[i - 1]);
							if ((csum << (int)frame.subframes[ch].obits) >= 1UL << 32)
								lpc.encode_residual_long(frame.subframes[ch].best.residual, frame.subframes[ch].samples, frame.blocksize, frame.subframes[ch].best.order, coefs, frame.subframes[ch].best.shift);
							else if (encode_on_cpu)
								lpc.encode_residual(frame.subframes[ch].best.residual, frame.subframes[ch].samples, frame.blocksize, frame.subframes[ch].best.order, coefs, frame.subframes[ch].best.shift);

							int pmin = get_max_p_order(eparams.min_partition_order, frame.blocksize, frame.subframes[ch].best.order);
							int pmax = get_max_p_order(eparams.max_partition_order, frame.blocksize, frame.subframes[ch].best.order);
							uint bits = (uint)frame.subframes[ch].best.order * frame.subframes[ch].obits + 4 + 5 + (uint)frame.subframes[ch].best.order * (uint)frame.subframes[ch].best.cbits + 6;
							frame.subframes[ch].best.size = bits + calc_rice_params(ref frame.subframes[ch].best.rc, ref frame.current.rc, pmin, pmax, frame.subframes[ch].best.residual, (uint)frame.blocksize, (uint)frame.subframes[ch].best.order);
						}
						break;
				}
				if (frame.subframes[ch].best.size > frame.subframes[ch].obits * (uint)frame.blocksize)
				{
#if DEBUG
					throw new Exception("larger than verbatim");
#endif
					frame.subframes[ch].best.type = SubframeType.Verbatim;
					frame.subframes[ch].best.size = frame.subframes[ch].obits * (uint)frame.blocksize;
				}
			}
		}

		unsafe void select_best_methods(FlacFrame frame, int channelsCount, int iFrame, FlaCudaTask task)
		{
			if (channelsCount == 4 && channels == 2)
			{
				if (task.BestResidualTasks[iFrame * 2].channel == 0 && task.BestResidualTasks[iFrame * 2 + 1].channel == 1)
					frame.ch_mode = ChannelMode.LeftRight;
				else if (task.BestResidualTasks[iFrame * 2].channel == 0 && task.BestResidualTasks[iFrame * 2 + 1].channel == 3)
					frame.ch_mode = ChannelMode.LeftSide;
				else if (task.BestResidualTasks[iFrame * 2].channel == 3 && task.BestResidualTasks[iFrame * 2 + 1].channel == 1)
					frame.ch_mode = ChannelMode.RightSide;
				else if (task.BestResidualTasks[iFrame * 2].channel == 2 && task.BestResidualTasks[iFrame * 2 + 1].channel == 3)
					frame.ch_mode = ChannelMode.MidSide;
				else
					throw new Exception("internal error: invalid stereo mode");
				frame.SwapSubframes(0, task.BestResidualTasks[iFrame * 2].channel);
				frame.SwapSubframes(1, task.BestResidualTasks[iFrame * 2 + 1].channel);
			}
			else
				frame.ch_mode = channels != 2 ? ChannelMode.NotStereo : ChannelMode.LeftRight;

			for (int ch = 0; ch < channels; ch++)
			{
				frame.subframes[ch].best.type = SubframeType.Verbatim;
				frame.subframes[ch].best.size = frame.subframes[ch].obits * (uint)frame.blocksize;
				frame.subframes[ch].wbits = 0;

				int index = ch + iFrame * channels;
				if (task.BestResidualTasks[index].size < 0)
					throw new Exception("internal error");
				if (frame.blocksize > 4 && frame.subframes[ch].best.size > task.BestResidualTasks[index].size)
				{
					frame.subframes[ch].best.type = (SubframeType)task.BestResidualTasks[index].type;
					frame.subframes[ch].best.size = (uint)task.BestResidualTasks[index].size;
					frame.subframes[ch].best.order = task.BestResidualTasks[index].residualOrder;
					frame.subframes[ch].best.cbits = task.BestResidualTasks[index].cbits;
					frame.subframes[ch].best.shift = task.BestResidualTasks[index].shift;
					frame.subframes[ch].obits -= (uint)task.BestResidualTasks[index].wbits;
					frame.subframes[ch].wbits = (uint)task.BestResidualTasks[index].wbits;
					if (frame.subframes[ch].wbits != 0)
						for (int i = 0; i < frame.blocksize; i++)
							frame.subframes[ch].samples[i] >>= (int)frame.subframes[ch].wbits;
					for (int i = 0; i < task.BestResidualTasks[index].residualOrder; i++)
						frame.subframes[ch].best.coefs[i] = task.BestResidualTasks[index].coefs[task.BestResidualTasks[index].residualOrder - 1 - i];
					if (!encode_on_cpu)
						AudioSamples.MemCpy(frame.subframes[ch].best.residual + frame.subframes[ch].best.order, (int*)task.residualBufferPtr + task.BestResidualTasks[index].residualOffs, frame.blocksize - frame.subframes[ch].best.order);
				}
			}
		}

		unsafe void estimate_residual(FlaCudaTask task, int channelsCount)
		{
			if (task.frameSize <= 4)
				return;

			//int autocorPartSize = (2 * 256 - eparams.max_prediction_order) & ~15;
			int autocorPartSize = 256 + 128;
			int autocorPartCount = (task.frameSize + autocorPartSize - 1) / autocorPartSize;
			if (autocorPartCount > maxAutocorParts)
				throw new Exception("internal error");

			int threads_y;
			if (task.nResidualTasksPerChannel >= 4 && task.nResidualTasksPerChannel <= 8)
				threads_y = task.nResidualTasksPerChannel;
			else if ((task.nResidualTasksPerChannel % 8) == 0)
				threads_y = 8;
			else if ((task.nResidualTasksPerChannel % 7) == 0)
				threads_y = 7;
			else if ((task.nResidualTasksPerChannel % 6) == 0)
				threads_y = 6;
			else if ((task.nResidualTasksPerChannel % 5) == 0)
				threads_y = 5;
			else if ((task.nResidualTasksPerChannel % 4) == 0)
				threads_y = 4;
			else
				throw new Exception("invalid LPC order");
			int residualPartSize = 32 * threads_y;
			int residualPartCount = (task.frameSize + residualPartSize - 1) / residualPartSize;

			if (residualPartCount > maxResidualParts)
				throw new Exception("invalid combination of block size and LPC order");

			CUfunction cudaChannelDecorr = channels == 2 ? (channelsCount == 4 ? task.cudaStereoDecorr : task.cudaChannelDecorr2) : task.cudaChannelDecorr;

			cuda.SetParameter(cudaChannelDecorr, 0 * sizeof(uint), (uint)task.cudaSamples.Pointer);
			cuda.SetParameter(cudaChannelDecorr, 1 * sizeof(uint), (uint)task.cudaSamplesBytes.Pointer);
			cuda.SetParameter(cudaChannelDecorr, 2 * sizeof(uint), (uint)MAX_BLOCKSIZE);
			cuda.SetParameterSize(cudaChannelDecorr, sizeof(uint) * 3U);
			cuda.SetFunctionBlockShape(cudaChannelDecorr, 256, 1, 1);

			cuda.SetParameter(task.cudaFindWastedBits, 0 * sizeof(uint), (uint)task.cudaResidualTasks.Pointer);
			cuda.SetParameter(task.cudaFindWastedBits, 1 * sizeof(uint), (uint)task.cudaSamples.Pointer);
			cuda.SetParameter(task.cudaFindWastedBits, 2 * sizeof(uint), (uint)task.nResidualTasksPerChannel);
			cuda.SetParameter(task.cudaFindWastedBits, 3 * sizeof(uint), (uint)task.frameSize);
			cuda.SetParameterSize(task.cudaFindWastedBits, sizeof(uint) * 4U);
			cuda.SetFunctionBlockShape(task.cudaFindWastedBits, 256, 1, 1);

			cuda.SetParameter(task.cudaComputeAutocor, 0, (uint)task.cudaAutocorOutput.Pointer);
			cuda.SetParameter(task.cudaComputeAutocor, sizeof(uint), (uint)task.cudaSamples.Pointer);
			cuda.SetParameter(task.cudaComputeAutocor, sizeof(uint) * 2, (uint)cudaWindow.Pointer);
			cuda.SetParameter(task.cudaComputeAutocor, sizeof(uint) * 3, (uint)task.cudaAutocorTasks.Pointer);
			cuda.SetParameter(task.cudaComputeAutocor, sizeof(uint) * 4, (uint)eparams.max_prediction_order);
			cuda.SetParameter(task.cudaComputeAutocor, sizeof(uint) * 4 + sizeof(uint), (uint)task.frameSize);
			cuda.SetParameter(task.cudaComputeAutocor, sizeof(uint) * 4 + sizeof(uint) * 2, (uint)autocorPartSize);
			cuda.SetParameterSize(task.cudaComputeAutocor, sizeof(uint) * 7U);
			cuda.SetFunctionBlockShape(task.cudaComputeAutocor, 32, 8, 1);

			cuda.SetParameter(task.cudaComputeLPC, 0, (uint)task.cudaResidualTasks.Pointer);
			cuda.SetParameter(task.cudaComputeLPC, sizeof(uint), (uint)task.cudaAutocorOutput.Pointer);
			cuda.SetParameter(task.cudaComputeLPC, sizeof(uint) * 2, (uint)task.cudaAutocorTasks.Pointer);
			cuda.SetParameter(task.cudaComputeLPC, sizeof(uint) * 3, (uint)eparams.max_prediction_order);
			cuda.SetParameter(task.cudaComputeLPC, sizeof(uint) * 3 + sizeof(uint), (uint)autocorPartCount);
			cuda.SetParameterSize(task.cudaComputeLPC, (uint)(sizeof(uint) * 3) + sizeof(uint) * 2);
			cuda.SetFunctionBlockShape(task.cudaComputeLPC, (autocorPartCount + 31) & ~31, 1, 1);

			cuda.SetParameter(task.cudaEstimateResidual, sizeof(uint) * 0, (uint)task.cudaResidualOutput.Pointer);
			cuda.SetParameter(task.cudaEstimateResidual, sizeof(uint) * 1, (uint)task.cudaSamples.Pointer);
			cuda.SetParameter(task.cudaEstimateResidual, sizeof(uint) * 2, (uint)task.cudaResidualTasks.Pointer);
			cuda.SetParameter(task.cudaEstimateResidual, sizeof(uint) * 3, (uint)eparams.max_prediction_order);
			cuda.SetParameter(task.cudaEstimateResidual, sizeof(uint) * 4, (uint)task.frameSize);
			cuda.SetParameter(task.cudaEstimateResidual, sizeof(uint) * 5, (uint)residualPartSize);
			cuda.SetParameterSize(task.cudaEstimateResidual, sizeof(uint) * 6);
			cuda.SetFunctionBlockShape(task.cudaEstimateResidual, 32, threads_y, 1);

			cuda.SetParameter(task.cudaChooseBestMethod, 0 * sizeof(uint), (uint)task.cudaResidualTasks.Pointer);
			cuda.SetParameter(task.cudaChooseBestMethod, 1 * sizeof(uint), (uint)task.cudaResidualOutput.Pointer);
			cuda.SetParameter(task.cudaChooseBestMethod, 2 * sizeof(uint), (uint)residualPartCount);
			cuda.SetParameter(task.cudaChooseBestMethod, 3 * sizeof(uint), (uint)task.nResidualTasksPerChannel);
			cuda.SetParameterSize(task.cudaChooseBestMethod, sizeof(uint) * 4U);
			cuda.SetFunctionBlockShape(task.cudaChooseBestMethod, 32, 16, 1);

			cuda.SetParameter(task.cudaCopyBestMethod, 0, (uint)task.cudaBestResidualTasks.Pointer);
			cuda.SetParameter(task.cudaCopyBestMethod, 1 * sizeof(uint), (uint)task.cudaResidualTasks.Pointer);
			cuda.SetParameter(task.cudaCopyBestMethod, 2 * sizeof(uint), (uint)task.nResidualTasksPerChannel);
			cuda.SetParameterSize(task.cudaCopyBestMethod, sizeof(uint) * 3U);
			cuda.SetFunctionBlockShape(task.cudaCopyBestMethod, 64, 1, 1);

			cuda.SetParameter(task.cudaCopyBestMethodStereo, 0, (uint)task.cudaBestResidualTasks.Pointer);
			cuda.SetParameter(task.cudaCopyBestMethodStereo, 1 * sizeof(uint), (uint)task.cudaResidualTasks.Pointer);
			cuda.SetParameter(task.cudaCopyBestMethodStereo, 2 * sizeof(uint), (uint)task.nResidualTasksPerChannel);
			cuda.SetParameterSize(task.cudaCopyBestMethodStereo, sizeof(uint) * 3U);
			cuda.SetFunctionBlockShape(task.cudaCopyBestMethodStereo, 64, 1, 1);

			cuda.SetParameter(task.cudaEncodeResidual, 0, (uint)task.cudaResidual.Pointer);
			cuda.SetParameter(task.cudaEncodeResidual, 1 * sizeof(uint), (uint)task.cudaSamples.Pointer);
			cuda.SetParameter(task.cudaEncodeResidual, 2 * sizeof(uint), (uint)task.cudaBestResidualTasks.Pointer);
			cuda.SetParameterSize(task.cudaEncodeResidual, sizeof(uint) * 3U);
			cuda.SetFunctionBlockShape(task.cudaEncodeResidual, residualPartSize, 1, 1);

			// issue work to the GPU
			cuda.LaunchAsync(cudaChannelDecorr, (task.frameCount * task.frameSize + 255) / 256, channels == 2 ? 1 : channels, task.stream);
			if (eparams.do_wasted)
				cuda.LaunchAsync(task.cudaFindWastedBits, channelsCount * task.frameCount, 1, task.stream);
			cuda.LaunchAsync(task.cudaComputeAutocor, autocorPartCount, task.nAutocorTasksPerChannel * channelsCount * task.frameCount, task.stream);
			cuda.LaunchAsync(task.cudaComputeLPC, 1, task.nAutocorTasksPerChannel * channelsCount * task.frameCount, task.stream);
			cuda.LaunchAsync(task.cudaEstimateResidual, residualPartCount, task.nResidualTasksPerChannel * channelsCount * task.frameCount / threads_y, task.stream);
			cuda.LaunchAsync(task.cudaChooseBestMethod, 1, channelsCount * task.frameCount, task.stream);
			if (channels == 2 && channelsCount == 4)
				cuda.LaunchAsync(task.cudaCopyBestMethodStereo, 1, task.frameCount, task.stream);
			else
				cuda.LaunchAsync(task.cudaCopyBestMethod, 1, channels * task.frameCount, task.stream);
			if (!encode_on_cpu)
				cuda.LaunchAsync(task.cudaEncodeResidual, residualPartCount, channels * task.frameCount, task.stream);
			cuda.CopyDeviceToHostAsync(task.cudaBestResidualTasks, task.bestResidualTasksPtr, (uint)(sizeof(encodeResidualTaskStruct) * channels * task.frameCount), task.stream);
			if (!encode_on_cpu)
				cuda.CopyDeviceToHostAsync(task.cudaResidual, task.residualBufferPtr, (uint)(sizeof(int) * MAX_BLOCKSIZE * channels), task.stream);
		}

		unsafe int encode_frame(bool doMidside, int channelCount, int iFrame, FlaCudaTask task)
		{
			fixed (int* r = residualBuffer)
			{
				FlacFrame frame = task.frame;
				frame.InitSize(task.frameSize, eparams.variable_block_size != 0);
				for (int ch = 0; ch < channelCount; ch++)
				{
					int* s = ((int*)task.samplesBufferPtr) + ch * FlaCudaWriter.MAX_BLOCKSIZE + iFrame * task.frameSize;
					frame.subframes[ch].Init(s, r + ch * FlaCudaWriter.MAX_BLOCKSIZE,
						bits_per_sample + (doMidside && ch == 3 ? 1U : 0U), 0);
				}

				select_best_methods(frame, channelCount, iFrame, task);

				encode_residual(frame);

				frame_writer.Reset();

				output_frame_header(frame, frame_writer);
				output_subframes(frame, frame_writer);
				output_frame_footer(frame_writer);
				if (frame_writer.Length >= frame_buffer.Length)
					throw new Exception("buffer overflow");

				if (frame_buffer != null)
				{
					if (eparams.variable_block_size > 0)
						frame_count += frame.blocksize;
					else
						frame_count++;
				}
				return frame_writer.Length;
			}
		}

		unsafe void send_to_GPU(FlaCudaTask task, int nFrames, int blocksize)
		{
			bool doMidside = channels == 2 && eparams.do_midside;
			int channelsCount = doMidside ? 2 * channels : channels;
			if (blocksize != task.frameSize)
				task.nResidualTasks = 0;
			task.frameCount = nFrames;
			task.frameSize = blocksize;
			cuda.CopyHostToDeviceAsync(task.cudaSamplesBytes, task.samplesBytesPtr, (uint)(sizeof(short) * channels * blocksize * nFrames), task.stream);
		}

		unsafe void run_GPU_task(FlaCudaTask task)
		{
			bool doMidside = channels == 2 && eparams.do_midside;
			int channelsCount = doMidside ? 2 * channels : channels;

			if (task.frameSize != _windowsize && task.frameSize > 4)
				fixed (float* window = windowBuffer)
				{
					_windowsize = task.frameSize;
					_windowcount = 0;
					calculate_window(window, lpc.window_welch, WindowFunction.Welch);
					calculate_window(window, lpc.window_tukey, WindowFunction.Tukey);
					calculate_window(window, lpc.window_hann, WindowFunction.Hann);
					calculate_window(window, lpc.window_flattop, WindowFunction.Flattop);
					calculate_window(window, lpc.window_bartlett, WindowFunction.Bartlett);
					if (_windowcount == 0)
						throw new Exception("invalid windowfunction");
					cuda.CopyHostToDevice<float>(cudaWindow, windowBuffer);
				}
			if (task.nResidualTasks == 0)
				initialize_autocorTasks(task.frameSize, channelsCount, max_frames, task);

			if (verify != null)
			{
				int* r = (int*)task.samplesBufferPtr;
				fixed (int* s = task.verifyBuffer)
					for (int ch = 0; ch < channels; ch++)
						AudioSamples.MemCpy(s + ch * FlaCudaWriter.MAX_BLOCKSIZE, r + ch * FlaCudaWriter.MAX_BLOCKSIZE, task.frameSize * task.frameCount);
			}

			estimate_residual(task, channelsCount);
		}

		unsafe int process_result(FlaCudaTask task)
		{
			bool doMidside = channels == 2 && eparams.do_midside;
			int channelCount = doMidside ? 2 * channels : channels;

			int bs = 0;
			for (int iFrame = 0; iFrame < task.frameCount; iFrame++)
			{
				//if (0 != eparams.variable_block_size && 0 == (task.blocksize & 7) && task.blocksize >= 128)
				//    fs = encode_frame_vbs();
				//else
				int fs = encode_frame(doMidside, channelCount, iFrame, task);
				bs += task.frameSize;

				if (seek_table != null && _IO.CanSeek)
				{
					for (int sp = 0; sp < seek_table.Length; sp++)
					{
						if (seek_table[sp].framesize != 0)
							continue;
						if (seek_table[sp].number > (ulong)_position + (ulong)task.frameSize)
							break;
						if (seek_table[sp].number >= (ulong)_position)
						{
							seek_table[sp].number = (ulong)_position;
							seek_table[sp].offset = (ulong)(_IO.Position - first_frame_offset);
							seek_table[sp].framesize = (uint)task.frameSize;
						}
					}
				}

				_position += task.frameSize;
				_IO.Write(frame_buffer, 0, fs);
				_totalSize += fs;

				if (verify != null)
				{
					int decoded = verify.DecodeFrame(frame_buffer, 0, fs);
					if (decoded != fs || verify.Remaining != (ulong)task.frameSize)
						throw new Exception("validation failed! frame size mismatch");
					fixed (int* s = task.verifyBuffer, r = verify.Samples)
					{
						for (int ch = 0; ch < channels; ch++)
							if (AudioSamples.MemCmp(s + iFrame * task.frameSize + ch * FlaCudaWriter.MAX_BLOCKSIZE, r + ch * Flake.MAX_BLOCKSIZE, task.frameSize))
								throw new Exception("validation failed!");
					}
				}
			}
			return bs;
		}

		public unsafe void Write(int[,] buff, int pos, int sampleCount)
		{
			bool doMidside = channels == 2 && eparams.do_midside;
			int channelCount = doMidside ? 2 * channels : channels;

			if (!inited)
			{
				cuda = new CUDA(true, InitializationFlags.None);
				cuda.CreateContext(0, CUCtxFlags.SchedAuto);
				using (Stream cubin = GetType().Assembly.GetManifestResourceStream(GetType(), "flacuda.cubin"))
				using (StreamReader sr = new StreamReader(cubin))
					cuda.LoadModule(new ASCIIEncoding().GetBytes(sr.ReadToEnd()));
				//cuda.LoadModule(System.IO.Path.Combine(Environment.CurrentDirectory, "flacuda.cubin"));
				task1 = new FlaCudaTask(cuda, channelCount);
				task2 = new FlaCudaTask(cuda, channelCount);
				cudaWindow = cuda.Allocate((uint)sizeof(float) * FlaCudaWriter.MAX_BLOCKSIZE * 2 * lpc.MAX_LPC_WINDOWS);
				if (_IO == null)
					_IO = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.Read);
				int header_size = flake_encode_init();
				_IO.Write(header, 0, header_size);
				if (_IO.CanSeek)
					first_frame_offset = _IO.Position;
				inited = true;
			}
			int len = sampleCount;
			while (len > 0)
			{
				int block = Math.Min(len, eparams.block_size * max_frames - samplesInBuffer);

				copy_samples(buff, pos, block, task1);

				if (md5 != null)
				{
					AudioSamples.FLACSamplesToBytes(buff, pos, md5_buffer, 0, block, channels, (int)bits_per_sample);
					md5.TransformBlock(md5_buffer, 0, block * channels * ((int)bits_per_sample >> 3), null, 0);
				}

				len -= block;
				pos += block;

				int nFrames = samplesInBuffer / eparams.block_size;
				if (nFrames >= max_frames)
					do_output_frames(nFrames);
			}
			//if (task2.frameCount > 0)
			//{
			//    cuda.SynchronizeStream(task2.stream);
			//    process_result(task2);
			//    task2.frameCount = 0;
			//}
		}

		public unsafe void do_output_frames(int nFrames)
		{
			bool doMidside = channels == 2 && eparams.do_midside;
			int channelCount = doMidside ? 2 * channels : channels;

			send_to_GPU(task1, nFrames, eparams.block_size);
			if (task2.frameCount > 0)
				cuda.SynchronizeStream(task2.stream);
			run_GPU_task(task1);
			if (task2.frameCount > 0)
				process_result(task2);
			int bs = eparams.block_size * nFrames;
			if (bs < samplesInBuffer)
			{
				int* s1 = (int*)task1.samplesBufferPtr;
				int* s2 = (int*)task2.samplesBufferPtr;
				for (int ch = 0; ch < channelCount; ch++)
					AudioSamples.MemCpy(s2 + ch * FlaCudaWriter.MAX_BLOCKSIZE, s1 + bs + ch * FlaCudaWriter.MAX_BLOCKSIZE, samplesInBuffer - bs);
				AudioSamples.MemCpy(((short*)task2.samplesBytesPtr), ((short*)task1.samplesBytesPtr) + bs * channels, (samplesInBuffer - bs) * channels);
			}
			samplesInBuffer -= bs;
			FlaCudaTask tmp = task1;
			task1 = task2;
			task2 = tmp;
			task1.frameCount = 0;
		}

		public string Path { get { return _path; } }

		string vendor_string = "FlaCuda#0.5";

		int select_blocksize(int samplerate, int time_ms)
		{
			int blocksize = Flake.flac_blocksizes[1];
			int target = (samplerate * time_ms) / 1000;
			if (eparams.variable_block_size > 0)
			{
				blocksize = 1024;
				while (target >= blocksize)
					blocksize <<= 1;
				return blocksize >> 1;
			}

			for (int i = 0; i < Flake.flac_blocksizes.Length; i++)
				if (target >= Flake.flac_blocksizes[i] && Flake.flac_blocksizes[i] > blocksize)
				{
					blocksize = Flake.flac_blocksizes[i];
				}
			return blocksize;
		}

		void write_streaminfo(byte[] header, int pos, int last)
		{
			Array.Clear(header, pos, 38);
			BitWriter bitwriter = new BitWriter(header, pos, 38);

			// metadata header
			bitwriter.writebits(1, last);
			bitwriter.writebits(7, (int)MetadataType.StreamInfo);
			bitwriter.writebits(24, 34);

			if (eparams.variable_block_size > 0)
				bitwriter.writebits(16, 0);
			else
				bitwriter.writebits(16, eparams.block_size);

			bitwriter.writebits(16, eparams.block_size);
			bitwriter.writebits(24, 0);
			bitwriter.writebits(24, max_frame_size);
			bitwriter.writebits(20, sample_rate);
			bitwriter.writebits(3, channels - 1);
			bitwriter.writebits(5, bits_per_sample - 1);

			// total samples
			if (sample_count > 0)
			{
				bitwriter.writebits(4, 0);
				bitwriter.writebits(32, sample_count);
			}
			else
			{
				bitwriter.writebits(4, 0);
				bitwriter.writebits(32, 0);
			}
			bitwriter.flush();
		}

		/**
		 * Write vorbis comment metadata block to byte array.
		 * Just writes the vendor string for now.
	     */
		int write_vorbis_comment(byte[] comment, int pos, int last)
		{
			BitWriter bitwriter = new BitWriter(comment, pos, 4);
			Encoding enc = new ASCIIEncoding();
			int vendor_len = enc.GetBytes(vendor_string, 0, vendor_string.Length, comment, pos + 8);

			// metadata header
			bitwriter.writebits(1, last);
			bitwriter.writebits(7, (int)MetadataType.VorbisComment);
			bitwriter.writebits(24, vendor_len + 8);

			comment[pos + 4] = (byte)(vendor_len & 0xFF);
			comment[pos + 5] = (byte)((vendor_len >> 8) & 0xFF);
			comment[pos + 6] = (byte)((vendor_len >> 16) & 0xFF);
			comment[pos + 7] = (byte)((vendor_len >> 24) & 0xFF);
			comment[pos + 8 + vendor_len] = 0;
			comment[pos + 9 + vendor_len] = 0;
			comment[pos + 10 + vendor_len] = 0;
			comment[pos + 11 + vendor_len] = 0;
			bitwriter.flush();
			return vendor_len + 12;
		}

		int write_seekpoints(byte[] header, int pos, int last)
		{
			seek_table_offset = pos + 4;

			BitWriter bitwriter = new BitWriter(header, pos, 4 + 18 * seek_table.Length);

			// metadata header
			bitwriter.writebits(1, last);
			bitwriter.writebits(7, (int)MetadataType.Seektable);
			bitwriter.writebits(24, 18 * seek_table.Length);
			for (int i = 0; i < seek_table.Length; i++)
			{
				bitwriter.writebits64(Flake.FLAC__STREAM_METADATA_SEEKPOINT_SAMPLE_NUMBER_LEN, seek_table[i].number);
				bitwriter.writebits64(Flake.FLAC__STREAM_METADATA_SEEKPOINT_STREAM_OFFSET_LEN, seek_table[i].offset);
				bitwriter.writebits(Flake.FLAC__STREAM_METADATA_SEEKPOINT_FRAME_SAMPLES_LEN, seek_table[i].framesize);
			}
			bitwriter.flush();
			return 4 + 18 * seek_table.Length;
		}

		/**
		 * Write padding metadata block to byte array.
		 */
		int
		write_padding(byte[] padding, int pos, int last, int padlen)
		{
			BitWriter bitwriter = new BitWriter(padding, pos, 4);

			// metadata header
			bitwriter.writebits(1, last);
			bitwriter.writebits(7, (int)MetadataType.Padding);
			bitwriter.writebits(24, padlen);

			return padlen + 4;
		}

		int write_headers()
		{
			int header_size = 0;
			int last = 0;

			// stream marker
			header[0] = 0x66;
			header[1] = 0x4C;
			header[2] = 0x61;
			header[3] = 0x43;
			header_size += 4;

			// streaminfo
			write_streaminfo(header, header_size, last);
			header_size += 38;

			// seek table
			if (_IO.CanSeek && seek_table != null)
				header_size += write_seekpoints(header, header_size, last);

			// vorbis comment
			if (eparams.padding_size == 0) last = 1;
			header_size += write_vorbis_comment(header, header_size, last);

			// padding
			if (eparams.padding_size > 0)
			{
				last = 1;
				header_size += write_padding(header, header_size, last, eparams.padding_size);
			}

			return header_size;
		}

		int flake_encode_init()
		{
			int i, header_len;

			//if(flake_validate_params(s) < 0)

			ch_code = channels - 1;

			// find samplerate in table
			for (i = 4; i < 12; i++)
			{
				if (sample_rate == Flake.flac_samplerates[i])
				{
					sr_code0 = i;
					break;
				}
			}

			// if not in table, samplerate is non-standard
			if (i == 12)
				throw new Exception("non-standard samplerate");

			for (i = 1; i < 8; i++)
			{
				if (bits_per_sample == Flake.flac_bitdepths[i])
				{
					bps_code = i;
					break;
				}
			}
			if (i == 8)
				throw new Exception("non-standard bps");
			// FIXME: For now, only 16-bit encoding is supported
			if (bits_per_sample != 16)
				throw new Exception("non-standard bps");

			if (_blocksize == 0)
			{
				if (eparams.block_size == 0)
					eparams.block_size = select_blocksize(sample_rate, eparams.block_time_ms);
				_blocksize = eparams.block_size;
			}
			else
				eparams.block_size = _blocksize;

			max_frames = Math.Min(maxFrames, FlaCudaWriter.MAX_BLOCKSIZE / eparams.block_size);

			// set maximum encoded frame size (if larger, re-encodes in verbatim mode)
			if (channels == 2)
				max_frame_size = 16 + ((eparams.block_size * (int)(bits_per_sample + bits_per_sample + 1) + 7) >> 3);
			else
				max_frame_size = 16 + ((eparams.block_size * channels * (int)bits_per_sample + 7) >> 3);

			if (_IO.CanSeek && eparams.do_seektable && sample_count != 0)
			{
				int seek_points_distance = sample_rate * 10;
				int num_seek_points = 1 + sample_count / seek_points_distance; // 1 seek point per 10 seconds
				if (sample_count % seek_points_distance == 0)
					num_seek_points--;
				seek_table = new SeekPoint[num_seek_points];
				for (int sp = 0; sp < num_seek_points; sp++)
				{
					seek_table[sp].framesize = 0;
					seek_table[sp].offset = 0;
					seek_table[sp].number = (ulong)(sp * seek_points_distance);
				}
			}

			// output header bytes
			header = new byte[eparams.padding_size + 1024 + (seek_table == null ? 0 : seek_table.Length * 18)];
			header_len = write_headers();

			// initialize CRC & MD5
			if (_IO.CanSeek && eparams.do_md5)
				md5 = new MD5CryptoServiceProvider();

			if (eparams.do_verify)
				verify = new FlakeReader(channels, bits_per_sample);

			frame_buffer = new byte[max_frame_size + 1];
			frame_writer = new BitWriter(frame_buffer, 0, max_frame_size + 1);

			encode_on_cpu = eparams.lpc_max_precision_search <= 1;

			return header_len;
		}
	}

	struct FlakeEncodeParams
	{
		// compression quality
		// set by user prior to calling flake_encode_init
		// standard values are 0 to 8
		// 0 is lower compression, faster encoding
		// 8 is higher compression, slower encoding
		// extended values 9 to 12 are slower and/or use
		// higher prediction orders
		public int compression;

		// stereo decorrelation method
		// set by user prior to calling flake_encode_init
		// if set to less than 0, it is chosen based on compression.
		// valid values are 0 to 2
		// 0 = independent L+R channels
		// 1 = mid-side encoding
		public bool do_midside;

		// block size in samples
		// set by the user prior to calling flake_encode_init
		// if set to 0, a block size is chosen based on block_time_ms
		// can also be changed by user before encoding a frame
		public int block_size;

		// block time in milliseconds
		// set by the user prior to calling flake_encode_init
		// used to calculate block_size based on sample rate
		// can also be changed by user before encoding a frame
		public int block_time_ms;

		// padding size in bytes
		// set by the user prior to calling flake_encode_init
		// if set to less than 0, defaults to 4096
		public int padding_size;

		// minimum LPC order
		// set by user prior to calling flake_encode_init
		// if set to less than 0, it is chosen based on compression.
		// valid values are 1 to 32
		public int min_prediction_order;

		// maximum LPC order
		// set by user prior to calling flake_encode_init
		// if set to less than 0, it is chosen based on compression.
		// valid values are 1 to 32 
		public int max_prediction_order;

		// minimum fixed prediction order
		// set by user prior to calling flake_encode_init
		// if set to less than 0, it is chosen based on compression.
		// valid values are 0 to 4
		public int min_fixed_order;

		// maximum fixed prediction order
		// set by user prior to calling flake_encode_init
		// if set to less than 0, it is chosen based on compression.
		// valid values are 0 to 4 
		public int max_fixed_order;

		// minimum partition order
		// set by user prior to calling flake_encode_init
		// if set to less than 0, it is chosen based on compression.
		// valid values are 0 to 8
		public int min_partition_order;

		// maximum partition order
		// set by user prior to calling flake_encode_init
		// if set to less than 0, it is chosen based on compression.
		// valid values are 0 to 8
		public int max_partition_order;

		// whether to use variable block sizes
		// set by user prior to calling flake_encode_init
		// 0 = fixed block size
		// 1 = variable block size
		public int variable_block_size;

		// whether to try various lpc_precisions
		// 0 - use only one precision
		// 1 - try two precisions
		public int lpc_max_precision_search;

		public int lpc_min_precision_search;

		public bool do_wasted;

		public bool do_constant;

		public WindowFunction window_function;

		public bool do_md5;
		public bool do_verify;
		public bool do_seektable;

		public int flake_set_defaults(int lvl)
		{
			compression = lvl;

			if ((lvl < 0 || lvl > 12) && (lvl != 99))
			{
				return -1;
			}

			// default to level 5 params
			window_function = WindowFunction.Flattop | WindowFunction.Tukey;
			do_midside = true;
			block_size = 0;
			block_time_ms = 105;
			min_fixed_order = 0;
			max_fixed_order = 4;
			min_prediction_order = 1;
			max_prediction_order = 12;
			min_partition_order = 0;
			max_partition_order = 6;
			variable_block_size = 0;
			lpc_min_precision_search = 1;
			lpc_max_precision_search = 1;
			do_md5 = true;
			do_verify = false;
			do_seektable = true;
			do_wasted = true;
			do_constant = true;

			// differences from level 7
			switch (lvl)
			{
				case 0:
					do_constant = false;
					do_wasted = false;
					do_midside = false;
					max_partition_order = 4;
					max_prediction_order = 4;
					min_fixed_order = 3;
					max_fixed_order = 2;
					break;
				case 1:
					do_wasted = false;
					do_midside = false;
					max_partition_order = 4;
					max_prediction_order = 5;
					break;
				case 2:
					window_function = WindowFunction.Bartlett;
					max_partition_order = 4;
					min_fixed_order = 2;
					max_fixed_order = 2;
					max_prediction_order = 6;
					break;
				case 3:
					window_function = WindowFunction.Bartlett;
					max_partition_order = 4;
					min_fixed_order = 2;
					max_fixed_order = 1;
					max_prediction_order = 7;
					break;
				case 4:
					max_partition_order = 4;
					max_prediction_order = 8;
					break;
				case 5:
					max_prediction_order = 9;
					break;
				case 6:
					max_fixed_order = 2;
					max_prediction_order = 10;
					break;
				case 7:
					min_fixed_order = 2;
					max_fixed_order = 2;
					max_prediction_order = 11;
					break;
				case 8:
					break;
				case 9:
					max_prediction_order = 16;
					break;
				case 10:
					max_prediction_order = 24;
					break;
				case 11:
					max_prediction_order = 32;
					break;
			}

			return 0;
		}
	}

	unsafe struct computeAutocorTaskStruct
	{
		public int samplesOffs;
		public int windowOffs;
		public int residualOffs;
		public int blocksize;
		public fixed int reserved[12];
	};
	
	unsafe struct encodeResidualTaskStruct
	{
		public int residualOrder;
		public int samplesOffs;
		public int shift;
		public int cbits;
		public int size;
		public int type;
		public int obits;
		public int blocksize;
		public int best_index;
		public int channel;
		public int residualOffs;
		public int wbits;
		public fixed int reserved[4];
		public fixed int coefs[32];
	};

	internal class FlaCudaTask
	{
		CUDA cuda;
		public CUfunction cudaStereoDecorr;
		public CUfunction cudaChannelDecorr;
		public CUfunction cudaChannelDecorr2;
		public CUfunction cudaFindWastedBits;
		public CUfunction cudaComputeAutocor;
		public CUfunction cudaComputeLPC;
		public CUfunction cudaEstimateResidual;
		public CUfunction cudaChooseBestMethod;
		public CUfunction cudaCopyBestMethod;
		public CUfunction cudaCopyBestMethodStereo;
		public CUfunction cudaEncodeResidual;
		public CUdeviceptr cudaSamplesBytes;
		public CUdeviceptr cudaSamples;
		public CUdeviceptr cudaResidual;
		public CUdeviceptr cudaAutocorTasks;
		public CUdeviceptr cudaAutocorOutput;
		public CUdeviceptr cudaResidualTasks;
		public CUdeviceptr cudaResidualOutput;
		public CUdeviceptr cudaBestResidualTasks;
		public IntPtr samplesBytesPtr = IntPtr.Zero;
		public IntPtr samplesBufferPtr = IntPtr.Zero;
		public IntPtr residualBufferPtr = IntPtr.Zero;
		public IntPtr autocorTasksPtr = IntPtr.Zero;
		public IntPtr residualTasksPtr = IntPtr.Zero;
		public IntPtr bestResidualTasksPtr = IntPtr.Zero;
		public CUstream stream;
		public int[] verifyBuffer;
		public int frameSize = 0;
		public int frameCount = 0;
		public FlacFrame frame;
		public int autocorTasksLen;
		public int residualTasksLen;
		public int bestResidualTasksLen;
		public int samplesBufferLen;
		public int nResidualTasks = 0;
		public int nAutocorTasks = 0;
		public int nResidualTasksPerChannel = 0;
		public int nAutocorTasksPerChannel = 0;

		unsafe public FlaCudaTask(CUDA _cuda, int channelCount)
		{
			cuda = _cuda;

			autocorTasksLen = sizeof(computeAutocorTaskStruct) * channelCount * lpc.MAX_LPC_WINDOWS * FlaCudaWriter.maxFrames;
			residualTasksLen = sizeof(encodeResidualTaskStruct) * channelCount * (lpc.MAX_LPC_ORDER * lpc.MAX_LPC_WINDOWS + 8) * FlaCudaWriter.maxFrames;
			bestResidualTasksLen = sizeof(encodeResidualTaskStruct) * channelCount * FlaCudaWriter.maxFrames;
			samplesBufferLen = sizeof(int) * FlaCudaWriter.MAX_BLOCKSIZE * channelCount;

			cudaSamplesBytes = cuda.Allocate((uint)samplesBufferLen / 2);
			cudaSamples = cuda.Allocate((uint)samplesBufferLen);
			cudaResidual = cuda.Allocate((uint)samplesBufferLen);
			cudaAutocorTasks = cuda.Allocate((uint)autocorTasksLen);
			cudaAutocorOutput = cuda.Allocate((uint)(sizeof(float) * channelCount * lpc.MAX_LPC_WINDOWS * (lpc.MAX_LPC_ORDER + 1) * (FlaCudaWriter.maxAutocorParts + FlaCudaWriter.maxFrames)));
			cudaResidualTasks = cuda.Allocate((uint)residualTasksLen);
			cudaBestResidualTasks = cuda.Allocate((uint)bestResidualTasksLen);
			cudaResidualOutput = cuda.Allocate((uint)(sizeof(int) * channelCount * (lpc.MAX_LPC_WINDOWS * lpc.MAX_LPC_ORDER + 8) * 64 /*FlaCudaWriter.maxResidualParts*/ * FlaCudaWriter.maxFrames));
			CUResult cuErr = CUResult.Success;
			if (cuErr == CUResult.Success)
				cuErr = CUDADriver.cuMemAllocHost(ref samplesBytesPtr, (uint)samplesBufferLen/2);
			if (cuErr == CUResult.Success)
				cuErr = CUDADriver.cuMemAllocHost(ref samplesBufferPtr, (uint)samplesBufferLen);
			if (cuErr == CUResult.Success)
				cuErr = CUDADriver.cuMemAllocHost(ref residualBufferPtr, (uint)samplesBufferLen);			
			if (cuErr == CUResult.Success)
				cuErr = CUDADriver.cuMemAllocHost(ref autocorTasksPtr, (uint)autocorTasksLen);
			if (cuErr == CUResult.Success)
				cuErr = CUDADriver.cuMemAllocHost(ref residualTasksPtr, (uint)residualTasksLen);
			if (cuErr == CUResult.Success)
				cuErr = CUDADriver.cuMemAllocHost(ref bestResidualTasksPtr, (uint)bestResidualTasksLen);
			if (cuErr != CUResult.Success)
			{
				if (samplesBytesPtr != IntPtr.Zero) CUDADriver.cuMemFreeHost(samplesBytesPtr); samplesBytesPtr = IntPtr.Zero;
				if (samplesBufferPtr != IntPtr.Zero) CUDADriver.cuMemFreeHost(samplesBufferPtr); samplesBufferPtr = IntPtr.Zero;
				if (residualBufferPtr != IntPtr.Zero) CUDADriver.cuMemFreeHost(residualBufferPtr); residualBufferPtr = IntPtr.Zero;
				if (autocorTasksPtr != IntPtr.Zero) CUDADriver.cuMemFreeHost(autocorTasksPtr); autocorTasksPtr = IntPtr.Zero;
				if (residualTasksPtr != IntPtr.Zero) CUDADriver.cuMemFreeHost(residualTasksPtr); residualTasksPtr = IntPtr.Zero;
				if (bestResidualTasksPtr != IntPtr.Zero) CUDADriver.cuMemFreeHost(bestResidualTasksPtr); bestResidualTasksPtr = IntPtr.Zero;
				throw new CUDAException(cuErr);
			}

			cudaComputeAutocor = cuda.GetModuleFunction("cudaComputeAutocor");
			cudaStereoDecorr = cuda.GetModuleFunction("cudaStereoDecorr");
			cudaChannelDecorr = cuda.GetModuleFunction("cudaChannelDecorr");
			cudaChannelDecorr2 = cuda.GetModuleFunction("cudaChannelDecorr2");
			cudaFindWastedBits = cuda.GetModuleFunction("cudaFindWastedBits");
			cudaComputeLPC = cuda.GetModuleFunction("cudaComputeLPC");
			cudaEstimateResidual = cuda.GetModuleFunction("cudaEstimateResidual");
			cudaChooseBestMethod = cuda.GetModuleFunction("cudaChooseBestMethod");
			cudaCopyBestMethod = cuda.GetModuleFunction("cudaCopyBestMethod");
			cudaCopyBestMethodStereo = cuda.GetModuleFunction("cudaCopyBestMethodStereo");
			cudaEncodeResidual = cuda.GetModuleFunction("cudaEncodeResidual");

			stream = cuda.CreateStream();
			verifyBuffer = new int[FlaCudaWriter.MAX_BLOCKSIZE * channelCount]; // should be channels, not channelCount. And should null if not doing verify!
			frame = new FlacFrame(channelCount);
		}

		public void Dispose()
		{
			cuda.Free(cudaSamples);
			cuda.Free(cudaSamplesBytes);
			cuda.Free(cudaResidual);
			cuda.Free(cudaAutocorTasks);
			cuda.Free(cudaAutocorOutput);
			cuda.Free(cudaResidualTasks);
			cuda.Free(cudaResidualOutput);
			cuda.Free(cudaBestResidualTasks);
			CUDADriver.cuMemFreeHost(samplesBytesPtr);
			CUDADriver.cuMemFreeHost(samplesBufferPtr);
			CUDADriver.cuMemFreeHost(residualBufferPtr);
			CUDADriver.cuMemFreeHost(residualTasksPtr);
			CUDADriver.cuMemFreeHost(bestResidualTasksPtr);
			CUDADriver.cuMemFreeHost(autocorTasksPtr);
			cuda.DestroyStream(stream);
		}

		public unsafe encodeResidualTaskStruct* ResidualTasks
		{
			get
			{
				return (encodeResidualTaskStruct*)residualTasksPtr;
			}
		}

		public unsafe encodeResidualTaskStruct* BestResidualTasks
		{
			get
			{
				return (encodeResidualTaskStruct*)bestResidualTasksPtr;
			}
		}
	}
}