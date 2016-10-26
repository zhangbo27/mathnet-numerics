﻿// <copyright file="MklFourierTransformProvider.cs" company="Math.NET">
// Math.NET Numerics, part of the Math.NET Project
// http://mathnet.opensourcedotnet.info
//
// Copyright (c) 2009-2016 Math.NET
//
// Permission is hereby granted, free of charge, to any person
// obtaining a copy of this software and associated documentation
// files (the "Software"), to deal in the Software without
// restriction, including without limitation the rights to use,
// copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the
// Software is furnished to do so, subject to the following
// conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES
// OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
// HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
// WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
// OTHER DEALINGS IN THE SOFTWARE.
// </copyright>

#if NATIVE

using System;
using System.Numerics;
using System.Threading;
using MathNet.Numerics.Providers.Common.Mkl;

namespace MathNet.Numerics.Providers.FourierTransform.Mkl
{
    public class MklFourierTransformProvider : IFourierTransformProvider, IDisposable
    {
        class Kernel
        {
            public IntPtr Handle;
            public int[] Dimensions;
            public FourierTransformScaling Scaling;
        }

        Kernel _kernel;

        /// <summary>
        /// Try to find out whether the provider is available, at least in principle.
        /// Verification may still fail if available, but it will certainly fail if unavailable.
        /// </summary>
        public bool IsAvailable()
        {
            return MklProvider.IsAvailable(minRevision: 11);
        }

        /// <summary>
        /// Initialize and verify that the provided is indeed available. If not, fall back to alternatives like the managed provider
        /// </summary>
        public void InitializeVerify()
        {
            MklProvider.Load(minRevision: 11);

            // we only support exactly one major version, since major version changes imply a breaking change.
            int fftMajor = SafeNativeMethods.query_capability((int) ProviderCapability.FourierTransformMajor);
            int fftMinor = SafeNativeMethods.query_capability((int) ProviderCapability.FourierTransformMinor);
            if (!(fftMajor == 1 && fftMinor >= 0))
            {
                throw new NotSupportedException(string.Format("MKL Native Provider not compatible. Expecting fourier transform v1 but provider implements v{0}.", fftMajor));
            }
        }

        /// <summary>
        /// Frees the memory allocated to the MKL memory pool.
        /// </summary>
        public void FreeBuffers()
        {
            Kernel kernel = Interlocked.Exchange(ref _kernel, null);
            if (kernel != null)
            {
                SafeNativeMethods.x_fft_free(ref kernel.Handle);
            }

            MklProvider.FreeBuffers();
        }

        /// <summary>
        /// Frees the memory allocated to the MKL memory pool on the current thread.
        /// </summary>
        public void ThreadFreeBuffers()
        {
            MklProvider.ThreadFreeBuffers();
        }

        /// <summary>
        /// Disable the MKL memory pool. May impact performance.
        /// </summary>
        public void DisableMemoryPool()
        {
            MklProvider.DisableMemoryPool();
        }

        /// <summary>
        /// Retrieves information about the MKL memory pool.
        /// </summary>
        /// <param name="allocatedBuffers">On output, returns the number of memory buffers allocated.</param>
        /// <returns>Returns the number of bytes allocated to all memory buffers.</returns>
        public long MemoryStatistics(out int allocatedBuffers)
        {
            return MklProvider.MemoryStatistics(out allocatedBuffers);
        }

        /// <summary>
        /// Enable gathering of peak memory statistics of the MKL memory pool.
        /// </summary>
        public void EnablePeakMemoryStatistics()
        {
            MklProvider.EnablePeakMemoryStatistics();
        }

        /// <summary>
        /// Disable gathering of peak memory statistics of the MKL memory pool.
        /// </summary>
        public void DisablePeakMemoryStatistics()
        {
            MklProvider.DisablePeakMemoryStatistics();
        }

        /// <summary>
        /// Measures peak memory usage of the MKL memory pool.
        /// </summary>
        /// <param name="reset">Whether the usage counter should be reset.</param>
        /// <returns>The peak number of bytes allocated to all memory buffers.</returns>
        public long PeakMemoryStatistics(bool reset = true)
        {
            return MklProvider.PeakMemoryStatistics(reset);
        }

        public override string ToString()
        {
            return MklProvider.Describe();
        }

        Kernel Configure(int length, FourierTransformScaling scaling)
        {
            Kernel kernel = Interlocked.Exchange(ref _kernel, null);

            if (kernel == null)
            {
                kernel = new Kernel
                {
                    Dimensions = new[] {length},
                    Scaling = scaling
                };

                SafeNativeMethods.z_fft_create(out kernel.Handle, length, ForwardScaling(scaling, length), BackwardScaling(scaling, length));

                return kernel;
            }

            if (kernel.Dimensions.Length != 1 || kernel.Dimensions[0] != length || kernel.Scaling != scaling)
            {
                SafeNativeMethods.x_fft_free(ref kernel.Handle);
                SafeNativeMethods.z_fft_create(out kernel.Handle, length, ForwardScaling(scaling, length), BackwardScaling(scaling, length));
                kernel.Dimensions = new[] {length};
                kernel.Scaling = scaling;
                return kernel;
            }

            return kernel;
        }

        Kernel Configure(int[] dimensions, FourierTransformScaling scaling)
        {
            if (dimensions.Length == 1)
            {
                return Configure(dimensions[0], scaling);
            }

            Kernel kernel = Interlocked.Exchange(ref _kernel, null);

            if (kernel == null)
            {
                kernel = new Kernel
                {
                    Dimensions = dimensions,
                    Scaling = scaling
                };

                long length = 1;
                for (int i = 0; i < dimensions.Length; i++)
                {
                    length *= dimensions[i];
                }

                SafeNativeMethods.z_fft_create_multidim(out kernel.Handle, dimensions.Length, dimensions, ForwardScaling(scaling, length), BackwardScaling(scaling, length));
                return kernel;
            }

            bool mismatch = kernel.Dimensions.Length != dimensions.Length || kernel.Scaling != scaling;
            if (!mismatch)
            {
                for (int i = 0; i < dimensions.Length; i++)
                {
                    if (dimensions[i] != kernel.Dimensions[i])
                    {
                        mismatch = true;
                        break;
                    }
                }
            }

            if (mismatch)
            {
                long length = 1;
                for (int i = 0; i < dimensions.Length; i++)
                {
                    length *= dimensions[i];
                }

                SafeNativeMethods.x_fft_free(ref kernel.Handle);
                SafeNativeMethods.z_fft_create_multidim(out kernel.Handle, dimensions.Length, dimensions, ForwardScaling(scaling, length), BackwardScaling(scaling, length));
                kernel.Dimensions = dimensions;
                kernel.Scaling = scaling;
                return kernel;
            }

            return kernel;
        }

        void Release(Kernel kernel)
        {
            Kernel existing = Interlocked.Exchange(ref _kernel, kernel);
            if (existing != null)
            {
                SafeNativeMethods.x_fft_free(ref existing.Handle);
            }
        }

        public void ForwardInplace(Complex[] complex, FourierTransformScaling scaling)
        {
            Kernel kernel = Configure(complex.Length, scaling);
            SafeNativeMethods.z_fft_forward(kernel.Handle, complex);
            Release(kernel);
        }

        public void BackwardInplace(Complex[] complex, FourierTransformScaling scaling)
        {
            Kernel kernel = Configure(complex.Length, scaling);
            SafeNativeMethods.z_fft_backward(kernel.Handle, complex);
            Release(kernel);
        }

        public Complex[] Forward(Complex[] complexTimeSpace, FourierTransformScaling scaling)
        {
            Complex[] work = new Complex[complexTimeSpace.Length];
            complexTimeSpace.Copy(work);
            ForwardInplace(work, scaling);
            return work;
        }

        public Complex[] Backward(Complex[] complexFrequenceSpace, FourierTransformScaling scaling)
        {
            Complex[] work = new Complex[complexFrequenceSpace.Length];
            complexFrequenceSpace.Copy(work);
            BackwardInplace(work, scaling);
            return work;
        }

        public void ForwardInplaceMultidim(Complex[] complex, int[] dimensions, FourierTransformScaling scaling)
        {
            throw new NotImplementedException();
        }

        public void BackwardInplaceMultidim(Complex[] complex, int[] dimensions, FourierTransformScaling scaling)
        {
            throw new NotImplementedException();
        }

        static double ForwardScaling(FourierTransformScaling scaling, long length)
        {
            switch (scaling)
            {
                case FourierTransformScaling.SymmetricScaling:
                    return Math.Sqrt(1.0/length);
                case FourierTransformScaling.ForwardScaling:
                    return 1.0/length;
                default:
                    return 1.0;
            }
        }

        static double BackwardScaling(FourierTransformScaling scaling, long length)
        {
            switch (scaling)
            {
                case FourierTransformScaling.SymmetricScaling:
                    return Math.Sqrt(1.0/length);
                case FourierTransformScaling.BackwardScaling:
                    return 1.0/length;
                default:
                    return 1.0;
            }
        }

        public void Dispose()
        {
            FreeBuffers();
        }
    }
}

#endif