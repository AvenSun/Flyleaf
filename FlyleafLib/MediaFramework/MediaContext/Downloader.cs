﻿using System;
using System.Threading;
using System.Threading.Tasks;

using FFmpeg.AutoGen;

using FlyleafLib.MediaFramework.MediaDemuxer;
using FlyleafLib.MediaFramework.MediaRemuxer;

/* TODO
 * Don't let audio go further than video (because of large duration without video)?
 * 
 */

namespace FlyleafLib.MediaFramework.MediaContext
{
    /// <summary>
    /// Downloads or remuxes to different format any ffmpeg valid input url
    /// </summary>
    public unsafe class Downloader : RunThreadBase
    {
        /// <summary>
        /// The backend demuxer. Access this to enable the required streams for downloading
        /// </summary>
        //public Demuxer      Demuxer             { get; private set; }

        public DecoderContext DecCtx { get; private set; }

        /// <summary>
        /// The backend remuxer. Normally you shouldn't access this
        /// </summary>
        public Remuxer      Remuxer             { get; private set; }

        /// <summary>
        /// The current timestamp of the frame starting from 0 (Ticks)
        /// </summary>
        public long         CurTime             { get => _CurTime; private set => Set(ref _CurTime,  value); }
        long _CurTime;

        /// <summary>
        /// The total duration of the input (Ticks)
        /// </summary>
        public long         Duration            { get => _Duration; private set => Set(ref _Duration, value); }
        long _Duration;

        /// <summary>
        /// The percentage of the current download process (0 for live streams)
        /// </summary>
        public double       DownloadPercentage  { get => _DownloadPercentage;     set => Set(ref _DownloadPercentage,  value); }
        double _DownloadPercentage;
        double downPercentageFactor;

        public Downloader(Config config, int uniqueId = -1) : base(uniqueId)
        {
            DecCtx = new DecoderContext(config, null, UniqueId, false);
            //DecCtx.VideoInputOpened += (o, e) => { if (!e.Success) OnDownloadCompleted(false); };
            Remuxer = new Remuxer(UniqueId);
            threadName = "Downloader";
        }

        /// <summary>
        /// Fires on download completed or failed
        /// </summary>
        public event EventHandler<bool> DownloadCompleted;
        protected virtual void OnDownloadCompleted(bool success)
        {
            Task.Run(() =>
            {
                Dispose();
                DownloadCompleted?.Invoke(this, success);
            });
        }

        /// <summary>
        /// Opens a new media file (audio/video) and prepares it for download (blocking)
        /// </summary>
        /// <param name="url">Media file's url</param>
        /// <param name="defaultInput">Whether to open the default input (in case of multiple inputs eg. from bitswarm/youtube-dl, you might want to choose yours)</param>
        /// <param name="defaultVideo">Whether to open the default video stream from plugin suggestions</param>
        /// <param name="defaultAudio">Whether to open the default audio stream from plugin suggestions</param>
        /// <returns></returns>
        public string Open(string url, bool defaultInput = true, bool defaultVideo = true, bool defaultAudio = true)
        {
            lock (lockActions)
            {
                Dispose();

                Disposed= false;
                Status  = Status.Opening;
                var ret =  DecCtx.OpenVideo(url, defaultInput, defaultVideo, defaultAudio, false);
                if (ret != null && ret.Error != null) return ret.Error;

                CurTime = 0;
                DownloadPercentage = 0;

                Demuxer Demuxer = !DecCtx.VideoDemuxer.Disposed ? DecCtx.VideoDemuxer : DecCtx.AudioDemuxer;
                Duration = Demuxer.IsLive ? 0 : Demuxer.Duration;
                downPercentageFactor = Duration / 100.0;

                return null;
            }
        }

        /// <summary>
        /// Downloads the currently configured AVS streams
        /// </summary>
        /// <param name="filename">The filename for the downloaded video. The file extension will let the demuxer to choose the output format (eg. mp4). If you useRecommendedExtension will be updated with the extension.</param>
        /// <param name="useRecommendedExtension">Will try to match the output container with the input container</param>
        public void Download(ref string filename, bool useRecommendedExtension = true)
        {
            lock (lockActions)
            {
                if (Status != Status.Opening || Disposed)
                    { OnDownloadCompleted(false); return; }

                if (useRecommendedExtension)
                    filename = $"{filename}.{(!DecCtx.VideoDemuxer.Disposed ? DecCtx.VideoDemuxer.Extension : DecCtx.AudioDemuxer.Extension)}";

                int ret = Remuxer.Open(filename);
                if (ret != 0)
                    { OnDownloadCompleted(false); return; }

                AddStreams(DecCtx.VideoDemuxer);
                AddStreams(DecCtx.AudioDemuxer);

                if (!Remuxer.HasStreams || Remuxer.WriteHeader() != 0)
                    { OnDownloadCompleted(false); return; }

                Start();
            }
        }

        private void AddStreams(Demuxer demuxer)
        {
            for(int i=0; i<demuxer.EnabledStreams.Count; i++)
                if (Remuxer.AddStream(demuxer.AVStreamToStream[demuxer.EnabledStreams[i]].AVStream, demuxer.Type == MediaType.Audio) != 0)
                    Log($"Failed to add stream {demuxer.AVStreamToStream[demuxer.EnabledStreams[i]].Type} {demuxer.AVStreamToStream[demuxer.EnabledStreams[i]].StreamIndex}");
        }

        /// <summary>
        /// Stops and disposes the downloader
        /// </summary>
        public void Dispose()
        {
            if (Disposed) return;

            lock (lockActions)
            {
                if (Disposed) return;

                Stop();

                DecCtx.Dispose();
                Remuxer.Dispose();
                
                Status = Status.Stopped;
                Disposed = true;
            }
        }

        protected override void RunInternal()
        {
            if (!Remuxer.HasStreams) { OnDownloadCompleted(false); return; }

            // Don't allow audio to change our duration without video (TBR: requires timestamp of videodemuxer to wait)
            if (!DecCtx.VideoDemuxer.Disposed && !DecCtx.AudioDemuxer.Disposed)
            {
                DecCtx.AudioDemuxer.Config = DecCtx.VideoDemuxer.Config.Clone();
                DecCtx.AudioDemuxer.Config.BufferDuration = 100 * 10000;
            }

            DecCtx.Start();

            Demuxer Demuxer = !DecCtx.VideoDemuxer.Disposed ? DecCtx.VideoDemuxer : DecCtx.AudioDemuxer;
            long startTime = Demuxer.hlsCtx == null ? Demuxer.StartTime : Demuxer.hlsCtx->first_timestamp * 10;
            Duration = Demuxer.IsLive ? 0 : Demuxer.Duration;
            downPercentageFactor = Duration / 100.0;

            long startedAt = DateTime.UtcNow.Ticks;
            long secondTicks = startedAt;

            do
            {
                if (Demuxer.Packets.Count == 0 && DecCtx.AudioDemuxer.Packets.Count == 0)
                {
                    lock (lockStatus)
                        if (Status == Status.Running) Status = Status.QueueEmpty;

                    while (Demuxer.Packets.Count == 0 && Status == Status.QueueEmpty)
                    {
                        if (Demuxer.Status == Status.Ended)
                        {
                            Status = Status.Ended;
                            if (Demuxer.Interrupter.Interrupted == 0)
                            { 
                                CurTime = _Duration;
                                DownloadPercentage = 100;
                            }
                            break;
                        }
                        else if (!Demuxer.IsRunning)
                        {
                            Log($"Demuxer is not running [Demuxer Status: {Demuxer.Status}]");

                            lock (Demuxer.lockStatus)
                            lock (lockStatus)
                            {
                                if (Demuxer.Status == Status.Pausing || Demuxer.Status == Status.Paused)
                                    Status = Status.Pausing;
                                else if (Demuxer.Status != Status.Ended)
                                    Status = Status.Stopping;
                                else
                                    continue;
                            }

                            break;
                        }
                        
                        Thread.Sleep(20);
                    }

                    lock (lockStatus)
                    {
                        if (Status != Status.QueueEmpty) break;
                        Status = Status.Running;
                    }
                }

                bool isAudio = false;
                IntPtr pktPtr;

                if (Demuxer.Type == MediaType.Video)
                {
                    Demuxer.Packets.TryDequeue(out pktPtr);
                    if (pktPtr == IntPtr.Zero)
                    {
                        DecCtx.AudioDemuxer.Packets.TryDequeue(out pktPtr);
                        isAudio = true;
                    }
                }
                else
                {
                    Demuxer.Packets.TryDequeue(out pktPtr);
                    isAudio = true;
                }

                AVPacket* packet = (AVPacket*) pktPtr;

                long curDT = DateTime.UtcNow.Ticks;
                if (curDT - secondTicks > 1000 * 10000 && (!isAudio || DecCtx.VideoDemuxer.Disposed))
                {
                    secondTicks = curDT;

                    if (Demuxer.hlsCtx != null)
                        CurTime = (long) ((packet->dts * Demuxer.AVStreamToStream[packet->stream_index].Timebase) - startTime);
                    else
                        CurTime = Demuxer.CurTime + Demuxer.BufferedDuration;

                    if (_Duration > 0) DownloadPercentage = CurTime / downPercentageFactor;
                }

                Remuxer.Write(packet, isAudio);

            } while (Status == Status.Running);

            if (Status != Status.Pausing && Status != Status.Paused)
                OnDownloadCompleted(Remuxer.WriteTrailer() == 0);
            else
                Demuxer.Pause();
        }
    }
}
