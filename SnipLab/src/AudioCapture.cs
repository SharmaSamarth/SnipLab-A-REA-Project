using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace SnipLab
{
    // Records one audio endpoint with WASAPI: the default speakers in loopback
    // mode (system audio) or the default microphone. Windows converts whatever the
    // device uses to 48 kHz stereo 16-bit PCM, so two sources can be mixed by
    // simply adding samples.
    internal sealed class AudioCapture : IDisposable
    {
        public const int SampleRate = 48000;
        public const int Channels = 2;
        public const int BytesPerFrame = Channels * 2;

        private const int AUDCLNT_SHAREMODE_SHARED = 0;
        private const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
        private const int AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;
        private const int AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = unchecked((int)0x80000000);
        private const int AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
        private const int CLSCTX_ALL = 0x17;
        private const int eRender = 0, eCapture = 1, eConsole = 0;
        private const long BufferDuration = 5000000; // 500 ms in 100-ns units, enough to ride out a pause in this process
        private const int MaxBacklogBytes = SampleRate * BytesPerFrame; // 1 s

        private readonly object sync = new object();
        private readonly Queue<byte[]> chunks = new Queue<byte[]>();
        private readonly IAudioClient client;
        private readonly IAudioCaptureClient capture;
        private readonly Thread thread;
        private byte[] partial;
        private int partialOffset;
        private int buffered;
        private long lastPacketTimestamp;
        private volatile bool running = true;

        public AudioCapture(bool systemAudio)
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            IMMDevice device;
            Check(enumerator.GetDefaultAudioEndpoint(systemAudio ? eRender : eCapture, eConsole, out device));

            Guid clientId = typeof(IAudioClient).GUID;
            object clientObject;
            Check(device.Activate(ref clientId, CLSCTX_ALL, IntPtr.Zero, out clientObject));
            client = (IAudioClient)clientObject;

            var format = new WAVEFORMATEX
            {
                wFormatTag = 1, // PCM
                nChannels = Channels,
                nSamplesPerSec = SampleRate,
                nAvgBytesPerSec = SampleRate * BytesPerFrame,
                nBlockAlign = BytesPerFrame,
                wBitsPerSample = 16
            };
            IntPtr formatPtr = Marshal.AllocHGlobal(Marshal.SizeOf(format));
            try
            {
                Marshal.StructureToPtr(format, formatPtr, false);
                int flags = AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
                if (systemAudio) flags |= AUDCLNT_STREAMFLAGS_LOOPBACK;
                Check(client.Initialize(AUDCLNT_SHAREMODE_SHARED, flags, BufferDuration, 0, formatPtr, IntPtr.Zero));
            }
            finally
            {
                Marshal.FreeHGlobal(formatPtr);
            }

            Guid captureId = typeof(IAudioCaptureClient).GUID;
            object captureObject;
            Check(client.GetService(ref captureId, out captureObject));
            capture = (IAudioCaptureClient)captureObject;

            Check(client.Start());
            thread = new Thread(Run)
            {
                IsBackground = true,
                Priority = ThreadPriority.Highest,
                Name = systemAudio ? "System audio" : "Microphone"
            };
            thread.Start();
        }

        // Audio that arrives while recording is paused is thrown away.
        public bool Paused { get; set; }

        // True while the device is delivering packets. A microphone always does;
        // loopback goes quiet when nothing is playing.
        public bool IsActive
        {
            get { return Stopwatch.GetTimestamp() - Interlocked.Read(ref lastPacketTimestamp) < Stopwatch.Frequency / 5; }
        }

        // Copies up to count bytes of captured audio into buffer at offset and
        // returns how many were copied.
        public int Read(byte[] buffer, int offset, int count)
        {
            lock (sync)
            {
                int copied = 0;
                while (copied < count)
                {
                    if (partial == null)
                    {
                        if (chunks.Count == 0) break;
                        partial = chunks.Dequeue();
                        partialOffset = 0;
                    }
                    int take = Math.Min(count - copied, partial.Length - partialOffset);
                    Buffer.BlockCopy(partial, partialOffset, buffer, offset + copied, take);
                    copied += take;
                    partialOffset += take;
                    if (partialOffset == partial.Length) partial = null;
                }
                buffered -= copied;
                return copied;
            }
        }

        private void Run()
        {
            while (running)
            {
                uint packet;
                while (running && capture.GetNextPacketSize(out packet) == 0 && packet > 0)
                {
                    IntPtr data;
                    uint frames, flags;
                    ulong devicePosition, qpcPosition;
                    if (capture.GetBuffer(out data, out frames, out flags, out devicePosition, out qpcPosition) != 0) break;

                    var chunk = new byte[frames * BytesPerFrame];
                    if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) == 0) Marshal.Copy(data, chunk, 0, chunk.Length);
                    capture.ReleaseBuffer(frames);
                    Interlocked.Exchange(ref lastPacketTimestamp, Stopwatch.GetTimestamp());
                    if (!Paused) Enqueue(chunk);
                }
                Thread.Sleep(10);
            }
        }

        private void Enqueue(byte[] chunk)
        {
            lock (sync)
            {
                chunks.Enqueue(chunk);
                buffered += chunk.Length;
                // If nobody is reading fast enough, drop the oldest audio so it cannot drift.
                while (buffered > MaxBacklogBytes && chunks.Count > 1)
                {
                    buffered -= chunks.Dequeue().Length;
                }
            }
        }

        public void Dispose()
        {
            running = false;
            thread.Join(500);
            client.Stop();
            Marshal.ReleaseComObject(capture);
            Marshal.ReleaseComObject(client);
        }

        private static void Check(int hr)
        {
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        }

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        private struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorComObject
        {
        }

        // Only the methods up to the last one used are declared; COM calls go by vtable order.
        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        }

        [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioClient
        {
            [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr audioSessionGuid);
            [PreserveSig] int GetBufferSize(out uint frames);
            [PreserveSig] int GetStreamLatency(out long latency);
            [PreserveSig] int GetCurrentPadding(out uint padding);
            [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
            [PreserveSig] int GetMixFormat(out IntPtr format);
            [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
            [PreserveSig] int Start();
            [PreserveSig] int Stop();
            [PreserveSig] int Reset();
            [PreserveSig] int SetEventHandle(IntPtr eventHandle);
            [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
        }

        [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioCaptureClient
        {
            [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
            [PreserveSig] int ReleaseBuffer(uint frames);
            [PreserveSig] int GetNextPacketSize(out uint frames);
        }
    }
}
