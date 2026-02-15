using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using LiveTranscript.Models;

namespace LiveTranscript.Services
{
    /// <summary>
    /// Captures audio from microphone and/or system speaker (WASAPI loopback).
    /// Supports three modes: Mic only, Speaker only, or Both.
    /// Emits PCM16 mono 16kHz chunks for streaming.
    /// </summary>
    public class AudioCaptureService : IDisposable
    {
        private const int TargetSampleRate = 16000;
        private const int TargetChannels = 1;
        private const int TargetBitsPerSample = 16;

        private WaveInEvent? _micCapture;
        private WasapiLoopbackCapture? _loopbackCapture;
        private WaveFormat? _loopbackSourceFormat;
        private bool _isCapturing;

        /// <summary>Fired when microphone audio data is available.</summary>
        public event Action<byte[]>? MicDataAvailable;

        /// <summary>Fired when speaker/loopback audio data is available.</summary>
        public event Action<byte[]>? SpeakerDataAvailable;

        public event Action<string>? Error;

        public bool IsCapturing => _isCapturing;

        /// <summary>Returns available microphone device names.</summary>
        public static List<string> GetMicrophoneDevices()
        {
            var devices = new List<string>();
            for (int i = 0; i < WaveInEvent.DeviceCount; i++)
            {
                var caps = WaveInEvent.GetCapabilities(i);
                devices.Add(caps.ProductName);
            }
            return devices;
        }

        /// <summary>
        /// Starts capturing based on the selected audio source mode.
        /// </summary>
        public void Start(AudioSource source, int micDeviceIndex = 0)
        {
            Stop();

            if (source == AudioSource.Microphone || source == AudioSource.Both)
                StartMicrophone(micDeviceIndex);

            if (source == AudioSource.SystemSpeaker || source == AudioSource.Both)
                StartLoopback();

            _isCapturing = true;
        }

        private void StartMicrophone(int deviceIndex)
        {
            var targetFormat = new WaveFormat(TargetSampleRate, TargetBitsPerSample, TargetChannels);

            _micCapture = new WaveInEvent
            {
                DeviceNumber = deviceIndex,
                WaveFormat = targetFormat,
                BufferMilliseconds = 50
            };

            _micCapture.DataAvailable += (s, e) =>
            {
                if (e.BytesRecorded > 0)
                {
                    var buffer = new byte[e.BytesRecorded];
                    Array.Copy(e.Buffer, buffer, e.BytesRecorded);
                    MicDataAvailable?.Invoke(buffer);
                }
            };

            _micCapture.RecordingStopped += (s, e) =>
            {
                if (e.Exception != null)
                    Error?.Invoke($"Microphone error: {e.Exception.Message}");
            };

            try
            {
                _micCapture.StartRecording();
            }
            catch (Exception ex)
            {
                Error?.Invoke($"Failed to start microphone: {ex.Message}");
            }
        }

        private void StartLoopback()
        {
            _loopbackCapture = new WasapiLoopbackCapture();
            _loopbackSourceFormat = _loopbackCapture.WaveFormat;

            _loopbackCapture.DataAvailable += OnLoopbackDataAvailable;
            _loopbackCapture.RecordingStopped += (s, e) =>
            {
                if (e.Exception != null)
                    Error?.Invoke($"Loopback error: {e.Exception.Message}");
            };

            try
            {
                _loopbackCapture.StartRecording();
            }
            catch (Exception ex)
            {
                Error?.Invoke($"Failed to start loopback capture: {ex.Message}");
            }
        }

        private void OnLoopbackDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded == 0) return;

            try
            {
                var converted = ConvertToPcm16Mono(e.Buffer, e.BytesRecorded, _loopbackSourceFormat!);
                if (converted.Length > 0)
                    SpeakerDataAvailable?.Invoke(converted);
            }
            catch (Exception ex)
            {
                Error?.Invoke($"Audio conversion error: {ex.Message}");
            }
        }

        private byte[] ConvertToPcm16Mono(byte[] data, int length, WaveFormat sourceFormat)
        {
            int sourceSampleRate = sourceFormat.SampleRate;
            int sourceChannels = sourceFormat.Channels;
            int bytesPerSourceSample = sourceFormat.BitsPerSample / 8;
            int frameSize = bytesPerSourceSample * sourceChannels;

            int totalFrames = length / frameSize;
            double ratio = (double)TargetSampleRate / sourceSampleRate;
            int targetFrames = (int)(totalFrames * ratio);

            var result = new byte[targetFrames * 2];

            for (int i = 0; i < targetFrames; i++)
            {
                int sourceFrame = (int)(i / ratio);
                if (sourceFrame >= totalFrames) break;

                int offset = sourceFrame * frameSize;

                double sampleSum = 0;
                for (int ch = 0; ch < sourceChannels; ch++)
                {
                    int chOffset = offset + ch * bytesPerSourceSample;
                    if (chOffset + bytesPerSourceSample > length) break;

                    double sample;
                    if (sourceFormat.Encoding == WaveFormatEncoding.IeeeFloat && bytesPerSourceSample == 4)
                        sample = BitConverter.ToSingle(data, chOffset);
                    else if (bytesPerSourceSample == 2)
                        sample = BitConverter.ToInt16(data, chOffset) / 32768.0;
                    else if (bytesPerSourceSample == 4 && sourceFormat.Encoding != WaveFormatEncoding.IeeeFloat)
                        sample = BitConverter.ToInt32(data, chOffset) / 2147483648.0;
                    else
                        sample = 0;

                    sampleSum += sample;
                }

                double monoSample = Math.Max(-1.0, Math.Min(1.0, sampleSum / sourceChannels));
                short pcm16 = (short)(monoSample * 32767);

                int targetOffset = i * 2;
                if (targetOffset + 1 < result.Length)
                {
                    result[targetOffset] = (byte)(pcm16 & 0xFF);
                    result[targetOffset + 1] = (byte)((pcm16 >> 8) & 0xFF);
                }
            }

            return result;
        }

        public void Stop()
        {
            _isCapturing = false;

            if (_micCapture != null)
            {
                try { _micCapture.StopRecording(); } catch { }
                _micCapture.Dispose();
                _micCapture = null;
            }

            if (_loopbackCapture != null)
            {
                try { _loopbackCapture.StopRecording(); } catch { }
                _loopbackCapture.Dispose();
                _loopbackCapture = null;
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
