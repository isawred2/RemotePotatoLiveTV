using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using System.IO;
using System.Threading;
using FatAttitude.MediaStreamer.HLS;
using System.Globalization;

namespace FatAttitude.MediaStreamer
{
    internal class FFHLSRunner
    {
        public string InputFile { get; set; }
        public string WorkingDirectory { get; set; }
        public string AdditionalArgsString { get; set; }
        public bool SettingsDefaultDebugAdvanced { get; set; }
        public bool Transcode { get; set; }
        public bool IsRunning;
        int StartAtSeconds = 0;

        // Private
        SegmentStore Store;
        private ShellCmdRunner shellRunner;
        private CommandArguments cmdArguments;

        private CommandArguments segmentArguments;
        public VideoEncodingParameters EncodingParameters;
        private MediaStreamingRequest request;
        public string MapArgumentsString;
        private string PathToTools;

        //private double CurrentPosition; //in seconds
        //private double CurrentOverallPosition; //in seconds

        public FFHLSRunner(string pathToTools, SegmentStore segStore)
        {
            // From parameters
            PathToTools = pathToTools;
            Store = segStore;

            // Defaults
            EncodingParameters = new VideoEncodingParameters();
            AudioSyncAmount = 1;  // 2 can create streaming issues
        }
        public FFHLSRunner(string pathToTools, SegmentStore store, MediaStreamingRequest mrq)
            : this(pathToTools, store)
        {
            request = mrq;
            EncodingParameters = mrq.CustomParameters;
            if (request.LiveTV)
            {
                EncodingParameters.SegmentDuration = request.ActualSegmentDuration;//LiveTV
            }
            else
            {
                EncodingParameters.SegmentDuration = 4;
            }
        }

        #region Top Level - Start/Stop/IsRunning
        public bool IsReStarting = false;
        int SegmentDuration;
        public bool Start(int _startAtSegment, ref string txtResult)
        {
            AwaitingSegmentNumber = _startAtSegment; // need to set this pretty sharpish
            if (request.LiveTV)
            {
                // Start up slowly
                SegmentDuration = Math.Min(EncodingParameters.SegmentDuration, request.InitialWaitTimeBeforeLiveTVStarts + _startAtSegment * request.SegmentIncreasingStepsLiveTV); //starting at 16 seconds make segments q second bigger untill 60 seconds reached
                int q = request.InitialWaitTimeBeforeLiveTVStarts;
                int r = request.SegmentIncreasingStepsLiveTV;
                int x = SegmentDuration;
                StartAtSeconds = (x - q) * (x + q - r) / (2 * r); //This is the general quadratic solution to the valuse x=4,y=0, x=4+a, y=4, x=4+2a, y=4+4+a etc. 
                double StartAtSegmentWhereCalculatedSegmentDurationIsMax = (EncodingParameters.SegmentDuration - q) / r;
                double StartAtSecondsWhereCalculatedSegmentDurationIsMax = (Math.Min(q + (int)StartAtSegmentWhereCalculatedSegmentDurationIsMax * r, EncodingParameters.SegmentDuration)
                    - q) * (Math.Min(q + (int)StartAtSegmentWhereCalculatedSegmentDurationIsMax * r, EncodingParameters.SegmentDuration) + q - r) / (2 * r);
                if (_startAtSegment >= StartAtSegmentWhereCalculatedSegmentDurationIsMax)
                {
                    StartAtSeconds = (int)(StartAtSecondsWhereCalculatedSegmentDurationIsMax + (_startAtSegment - (int)(StartAtSegmentWhereCalculatedSegmentDurationIsMax)) * EncodingParameters.SegmentDuration);
                }
                //                }
                SendDebugMessage("SegmentDuration: ********* " + SegmentDuration + "start at: " + StartAtSeconds);
            }
            else
            {
                SegmentDuration = EncodingParameters.SegmentDuration;
                StartAtSeconds = _startAtSegment * EncodingParameters.SegmentDuration;
            }

            if (IsRunning)
            {
                IsReStarting = true;
                SendDebugMessage("Aborting, then restarting");
                Abort();
            }

            Initialise(_startAtSegment);

            IsReStarting = false;
            SendDebugMessage("Running: " + shellRunner.FileName);
            SendDebugMessage("Arguments: " + shellRunner.Arguments);
            //if (_startAtSegment == 0) Thread.Sleep (500+request.InitialWaitTimeBeforeLiveTVStarts*1000);
            IsRunning = shellRunner.Start(ref txtResult);
            return IsRunning;
        }
        void Initialise(int startAtSegment)
        {
            shellRunner = new ShellCmdRunner(request.LiveTV);
            shellRunner.ProcessFinished += new EventHandler<GenericEventArgs<processfinishedEventArgs>>(shellRunner_ProcessFinished);
            shellRunner.FileName = Path.Combine(PathToTools, "ffmpeg.exe");
            shellRunner.StandardErrorReceivedLine += new EventHandler<GenericEventArgs<string>>(shellRunner_StandardErrorReceivedLine);
            shellRunner.StandardOutputReceived += new EventHandler<GenericEventArgs<byte[]>>(shellRunner_StandardOutputReceived);

            // Incoming data from ffmpeg STDOUt - mangements
            InitTempWriter();

            // Set up objects
            cmdArguments = new CommandArguments();
            segmentArguments = new CommandArguments();

            // Arguments
            ConstructArguments(startAtSegment);
            shellRunner.Arguments = cmdArguments.ToString();

        }
        public void Abort()
        {
            if (!IsRunning) return;

            // insert kill file into the directory to stop the streamer
            SendDebugMessage("FF Runner: Abort signalled.");

            if (shellRunner != null)
            {
                lock (shellRunner.StandardOutputReceivedLock)
                {
                    // Unhook events
                    shellRunner.StandardErrorReceivedLine -= new EventHandler<GenericEventArgs<string>>(shellRunner_StandardErrorReceivedLine);
                    shellRunner.StandardOutputReceived -= new EventHandler<GenericEventArgs<byte[]>>(shellRunner_StandardOutputReceived);
                    shellRunner.ProcessFinished -= new EventHandler<GenericEventArgs<processfinishedEventArgs>>(shellRunner_ProcessFinished);
                }

                // Kill the shell runner 
                shellRunner.KillNow();
            }

            shellRunner = null;
            IsRunning = false;
        }
        #endregion

        #region Incoming Events

        void shellRunner_ProcessFinished(object sender, GenericEventArgs<processfinishedEventArgs> e)
        {
            if (sender != shellRunner) return;
            if (request.LiveTV)
            {

                //if (VideoEncodingParameters.EOSDetected)

                //                VideoEncodingParameters.EOSDetected = false;
                SendDebugMessage("FF Runner: Shell process is finished in live TV.  (");
                shellRunner = null;
                IsRunning = false;
                if (!e.Value.WasAborted) //finished normally
                {
                    string txtResult = "";
                    SendDebugMessage("FF Runner: Restart runner for the final segment and following segments");

                    beginNextSegment();
                    this.Start(incomingSegment.Number, ref txtResult);
                }
            }
            else
            {
                if (!e.Value.WasAborted) // If finished normally, write the [final] segment to disk
                {
                    processRemainingBytes();
                    finaliseCurrentSegment();
                }
                SendDebugMessage("FF Runner: Shell process is finished in non-live tv.  (");
                shellRunner = null;
                IsRunning = false;
            }

        }
        #endregion


        #region Incoming Segments
        // Members
        BinaryWriter bw;
        List<byte> byteHoldingBuffer;
        int delimiterLength;
        public int AwaitingSegmentNumber { get; set; }
        byte[] Delimiter = null;

        // Methods
        void InitTempWriter()
        {
            byteHoldingBuffer = new List<byte>();

            initDelimiter();

            beginNextSegment();
        }
        void initDelimiter()
        {
            //if (Delimiter == null) Delimiter = System.Text.UTF8Encoding.UTF8.GetBytes(  "-------SEGMENT-BREAK-------");
            if (Delimiter == null) Delimiter = System.Text.UTF8Encoding.UTF8.GetBytes("-SEGBREAK-");
            delimiterLength = 10;
        }
        void shellRunner_StandardOutputReceived(object sender, GenericEventArgs<byte[]> e)
        {
            if (sender != shellRunner) return;  // an old shell runner

            processByteBuffer(e.Value);
        }
        void processByteBuffer(byte[] bytes)
        {
            foreach (byte b in bytes)
            {
                processByte(b);
            }
        }
        bool startedPumping = false;
        void processByte(byte b)
        {
            lock (byteHoldingBuffer)
            {
                byteHoldingBuffer.Add(b);
                if (byteHoldingBuffer.Count == delimiterLength)
                {
                    if (!startedPumping) startedPumping = true;

                    // Check for a match - if it matches then dump the byte buffer and start a new file
                    if (byteHoldingBuffer.SequenceEqual(Delimiter))
                    {
                        byteHoldingBuffer.Clear();
                        AwaitingSegmentNumber++;
                        switchToNextSegment();
                    }
                    else
                    {
                        if (bw.BaseStream.Position < MaxIncomingDataSize)
                        {
                            // dequeue the first byte (FIFO) and write it to disk
                            bw.Write(byteHoldingBuffer[0]);
                            byteHoldingBuffer.RemoveAt(0);
                        }
                        else
                        {
                            SendDebugMessage("WARNING: Data spill; segment exceeded max (" + MaxIncomingDataSize.ToString() + ") size.");
                            return;
                        }
                    }
                }

            }
        }
        void processRemainingBytes()
        {
            while (byteHoldingBuffer.Count > 0)
            {
                if (bw.BaseStream.Position < MaxIncomingDataSize)
                {
                    // dequeue the first byte (FIFO) and write it to disk
                    bw.Write(byteHoldingBuffer[0]);
                    byteHoldingBuffer.RemoveAt(0);
                }
                else
                    SendDebugMessage("WARNING: Data spill; segment exceeded max (" + MaxIncomingDataSize.ToString() + ") size.");
            }
        }
        void switchToNextSegment()
        {
            finaliseCurrentSegment();

            beginNextSegment();

        }
        void finaliseCurrentSegment()
        {
            // Transfer the data across from the buffer
            incomingSegment.Data = new byte[bw.BaseStream.Position];
            bw.Flush();
            bw.Close();

            Array.Copy(incomingSegmentDataBuffer, incomingSegment.Data, incomingSegment.Data.Length);
            incomingSegmentDataBuffer = null;  // clear/dispose

            StoreSegment();
        }
        private void StoreSegment()
        {
            // Store the segment
            Store.StoreSegment(incomingSegment);
        }
        Segment incomingSegment = null;
        const int MaxIncomingDataSize = 20000000;  // 20Mb per segment maximum size
        byte[] incomingSegmentDataBuffer;
        void beginNextSegment()
        {
            incomingSegment = new Segment();
            incomingSegment.Number = AwaitingSegmentNumber;

            incomingSegmentDataBuffer = new byte[MaxIncomingDataSize];
            MemoryStream ms = new MemoryStream(incomingSegmentDataBuffer);
            bw = new BinaryWriter(ms);
        }
        #endregion

        #region Parameters
        public int AudioSyncAmount { get; set; }


        /*
         * C:\Program Files (x86)\AirVideoServer\ffmpeg.exe" 
         * --segment-length 4
         * --segment-offset 188
         * --conversion-id 548cf790-c04f-488a-96be-aae2968f272bdefd0e1d-2bdf-457d-ab15-3eb6c51ccf85 
         * --port-number 46631 
         * -threads 4 
         * -flags +loop 
         * -g 30 -keyint_min 1 
         * -bf 0 
         * -b_strategy 0 
         * -flags2 -wpred-dct8x8 -cmp +chroma -deblockalpha 0 -deblockbeta 0 -refs 1 -coder 0 -me_range 16 -subq 5 -partitions +parti4x4+parti8x8+partp8x8 
         * -trellis 0 
         * -sc_threshold 40 -i_qfactor 0.71 -qcomp 0.6 -map 0.1:0.1 -map 0.0:0.0 -ss 188.0 
         * -vf "crop=720:572:0:2, scale=568:320"
         * -aspect 720:576
         * -y
         * -async 1
         * -f mpegts
         * -vcodec libx264
         * -bufsize 1024k
         * -b 1200k
         * -bt 1300k
         * -qmax 48
         * -qmin 2
         * -r 25.0
         * -acodec libmp3lame
         * -ab 192k
         * -ar 48000
         * -ac 2 
         */
        void ConstructArguments(int startAtSegment)
        {
            string args;
            if (request.LiveTV)
                args = @"{THREADS} {H264PROFILE} {H264LEVEL} -flags +loop -g 30 -keyint_min 1 -b_strategy 0 -flags2 -wpred-dct8x8 -cmp +chroma -deblockalpha 0 -deblockbeta 0 -refs 1 {MOTIONSEARCHRANGE} {SUBQ} {PARTITIONS} -trellis 0 -coder 0 -sc_threshold 40 -i_qfactor 0.71 -qcomp 0.6 -qdiff 4 -rc_eq 'blurCplx^(1-qComp)' {MAPPINGS} {STARTTIME} {INPUTFILE} {AUDIOSYNC} {ASPECT} {FRAMESIZE} {DEINTERLACE} -y -f mpegts -vcodec libx264 {VIDEOBITRATE} {MINVIDEOBITRATE} {MAXVIDEOBITRATE} {VIDEOBITRATEDEVIATION} -qmax 48 -qmin 2 -r 25 {AUDIOCODEC} {AUDIOBITRATE} {AUDIOSAMPLERATE} {AUDIOCHANNELS} {VOLUMELEVEL}";
            // see for efficiency: http://smorgasbork.com/component/content/article/35-linux/98-high-bitrate-real-time-mpeg-2-encoding-with-ffmpeg as well
            else // never change a winning team:
                args = @"{THREADS} {H264PROFILE} {H264LEVEL} -flags +loop -g 30 -keyint_min 1 -bf 0 -b_strategy 0 -flags2 -wpred-dct8x8 -cmp +chroma -deblockalpha 0 -deblockbeta 0 -refs 1 {MOTIONSEARCHRANGE} {SUBQ} {PARTITIONS} -trellis 0 -coder 0 -sc_threshold 40 -i_qfactor 0.71 -qcomp 0.6 -qdiff 4 -rc_eq 'blurCplx^(1-qComp)' {MAPPINGS} {STARTTIME} {INPUTFILE} {AUDIOSYNC} {ASPECT} {FRAMESIZE} {DEINTERLACE} -y -f mpegts -vcodec libx264 {VIDEOBITRATE} {VIDEOBITRATEDEVIATION} -qmax 48 -qmin 2 -r 25 {AUDIOCODEC} {AUDIOBITRATE} {AUDIOSAMPLERATE} {AUDIOCHANNELS} {VOLUMELEVEL}";

            // Use either the standard ffmpeg template or a custom one
            string strFFMpegTemplate = (string.IsNullOrWhiteSpace(EncodingParameters.CustomFFMpegTemplate)) ? args : EncodingParameters.CustomFFMpegTemplate;

            // Segment length and offset
            if (request.LiveTV)
            {// start with 4 seconds first then gradually increase up to 1 minute of segmentlength
                segmentArguments.AddArgCouple("--segment-length", SegmentDuration.ToString());
            }
            else
            {
                segmentArguments.AddArgCouple("--segment-length", EncodingParameters.SegmentDuration.ToString());
            }
            segmentArguments.AddArgCouple("--segment-offset", StartAtSeconds.ToString());
            cmdArguments.AddArg(segmentArguments.ToString());

            // Multi threads
            if (request.LiveTV)
                strFFMpegTemplate = strFFMpegTemplate.Replace("{THREADS}", "-threads 8");
            else // never change a winning team:
                strFFMpegTemplate = strFFMpegTemplate.Replace("{THREADS}", "-threads 4");

            // Me Range
            strFFMpegTemplate = strFFMpegTemplate.Replace("{MOTIONSEARCHRANGE}", ("-me_range " + EncodingParameters.MotionSearchRange.ToString()));

            // SUBQ - important as setting it too high can slow things down
            strFFMpegTemplate = strFFMpegTemplate.Replace("{SUBQ}", ("-subq " + EncodingParameters.X264SubQ.ToString()));

            // Partitions
            string strPartitions = (EncodingParameters.PartitionsFlags.Length > 0) ?
                "-partitions " + EncodingParameters.PartitionsFlags : "";
            strFFMpegTemplate = strFFMpegTemplate.Replace("{PARTITIONS}", strPartitions);

            // Add Mappings
            string strMapArgs = (string.IsNullOrEmpty(MapArgumentsString)) ? "" : MapArgumentsString;
            strFFMpegTemplate = strFFMpegTemplate.Replace("{MAPPINGS}", strMapArgs);

            // Start at : MUST BE BEFORE INPUT FILE FLAG -i *** !!! 
            string strStartTime = (StartAtSeconds <= 0) ? "" : ("-ss " + StartAtSeconds.ToString());
            strFFMpegTemplate = strFFMpegTemplate.Replace("{STARTTIME}", strStartTime);

            //// for liveTV, -async 1 alone does not seem to work, so:
            //string strVideoAudioSync = (EncodingParameters.AVSyncDifference <= 0) ? "" : ("-itsoffset " + EncodingParameters.AVSyncDifference.ToString(CultureInfo.InvariantCulture));
            //strFFMpegTemplate = strFFMpegTemplate.Replace("{ITOFFSET}", strVideoAudioSync);

            // Input file - make short to avoid issues with UTF-8 in batch files  IT IS VERY IMPORTANT WHERE THIS GOES; AFTER SS BUT BEFORE VCODEC AND ACODEC
            string shortInputFile = Functions.FileWriter.GetShortPathName(InputFile);
            // Quotes around file
            string quotedInputFile = "\"" + shortInputFile + "\"";
            strFFMpegTemplate = strFFMpegTemplate.Replace("{INPUTFILE}", ("-i " + quotedInputFile));

            // Aspect ratio and frame size
            string strAspectRatio = (EncodingParameters.OutputSquarePixels) ? "-aspect 1:1" : "-aspect " + EncodingParameters.AspectRatio;
            strFFMpegTemplate = strFFMpegTemplate.Replace("{ASPECT}", strAspectRatio);
            string strFrameSize = "-s " + EncodingParameters.ConstrainedSize;
            strFFMpegTemplate = strFFMpegTemplate.Replace("{FRAMESIZE}", strFrameSize);


            // Deinterlace (experimental)
            string strDeinterlace = (EncodingParameters.DeInterlace) ? "-deinterlace" : "";
            strFFMpegTemplate = strFFMpegTemplate.Replace("{DEINTERLACE}", strDeinterlace);

            // OPTIONAL FOR LATER:  -vf "crop=720:572:0:2, scale=568:320"
            // Think this means crop to the aspect ratio, then scale to the normal frame

            // Audio sync amount
            string strAudioSync = "-async " + AudioSyncAmount.ToString();
            strFFMpegTemplate = strFFMpegTemplate.Replace("{AUDIOSYNC}", strAudioSync);

            int maxDuration = EncodingParameters.SegmentDuration;
            int currentSegmentDuration = Math.Min(EncodingParameters.SegmentDuration, request.InitialWaitTimeBeforeLiveTVStarts + startAtSegment * request.SegmentIncreasingStepsLiveTV); ;
            int videobitrate = 0;
            int audiobitrate = 0;
            // Video bitrate
            if (request.LiveTV)
            {
                if (maxDuration == 0)
                {
                }
                else
                {
                    videobitrate = (int)(toInteger(EncodingParameters.VideoBitRate) * (Math.Sin((Math.PI) * currentSegmentDuration / (2 * maxDuration))));//sin seems to be a nice fast climbing function
//                    audiobitrate = (int)(toInteger(EncodingParameters.AudioBitRate) * (Math.Sin((Math.PI) * currentSegmentDuration / (2 * maxDuration))));
                    audiobitrate = toInteger(EncodingParameters.AudioBitRate);
                    //videobitrate = (int)(toInteger(EncodingParameters.VideoBitRate) * (currentSegmentDuration / maxDuration)); //linear
                    //audiobitrate = (int)(toInteger(EncodingParameters.AudioBitRate) * (currentSegmentDuration / maxDuration));
                    SendDebugMessage("VideoBitRate now:  " + videobitrate);
                }
                string strVideoBitRateOptions = "-bufsize " + "50Mi -b " + videobitrate;  //cmdArguments.AddArgCouple("-maxrate", VideoBitRate);
                strFFMpegTemplate = strFFMpegTemplate.Replace("{VIDEOBITRATE}", strVideoBitRateOptions);
            }
            else
            {
                string strVideoBitRateOptions = "-bufsize " + EncodingParameters.VideoBitRate + " -b " + EncodingParameters.VideoBitRate;  //cmdArguments.AddArgCouple("-maxrate", VideoBitRate);
                strFFMpegTemplate = strFFMpegTemplate.Replace("{VIDEOBITRATE}", strVideoBitRateOptions);
            }

            if (request.LiveTV)
            {
                // Max video bitrate (optional)
                string strMaxVideoBitRate = "-maxrate " + videobitrate;
                strFFMpegTemplate = strFFMpegTemplate.Replace("{MAXVIDEOBITRATE}", strMaxVideoBitRate);
                string strMinVideoBitRate = "-minrate " + videobitrate;
                strFFMpegTemplate = strFFMpegTemplate.Replace("{MINVIDEOBITRATE}", strMinVideoBitRate);
                string strMaxAudioBitRate = "-maxrate " + audiobitrate;
                strFFMpegTemplate = strFFMpegTemplate.Replace("{MAXAUDIOBITRATE}", strMaxAudioBitRate);
                string strMinAudioBitRate = "-maxrate " + audiobitrate;
                strFFMpegTemplate = strFFMpegTemplate.Replace("{MINAUDIOBITRATE}", strMinAudioBitRate);

                string strVideoBitRateDeviation = "";
                strFFMpegTemplate = strFFMpegTemplate.Replace("{VIDEOBITRATEDEVIATION}", strVideoBitRateDeviation);
            }
            else
            {
                // Max video bitrate (optional)
                string strMaxVideoBitRate = "-maxrate " + EncodingParameters.VideoBitRate;
                strFFMpegTemplate = strFFMpegTemplate.Replace("{MAXVIDEOBITRATE}", strMaxVideoBitRate);

                string strVideoBitRateDeviation = "-bt " + EncodingParameters.BitRateDeviation;
                strFFMpegTemplate = strFFMpegTemplate.Replace("{VIDEOBITRATEDEVIATION}", strVideoBitRateDeviation);
            }


            // Restrict H264 encoding level (e.g. for iPhone 3G)
            string strH264Level = (EncodingParameters.X264Level > 0) ? ("-level " + EncodingParameters.X264Level.ToString()) : "";
            strFFMpegTemplate = strFFMpegTemplate.Replace("{H264LEVEL}", strH264Level);
            string strH264Profile = (string.IsNullOrWhiteSpace(EncodingParameters.X264Profile)) ? "" : "-profile " + EncodingParameters.X264Profile;
            strFFMpegTemplate = strFFMpegTemplate.Replace("{H264PROFILE}", strH264Profile);

            // Audio: MP3 - must be after input file flag -i  //
            string strAudioCodecOptions = "";
            switch (EncodingParameters.AudioCodec)
            {
                case VideoEncodingParameters.AudioCodecTypes.AAC:
                    strAudioCodecOptions = "-acodec aac -strict experimental";
                    break;

                default:
                    strAudioCodecOptions = "-acodec libmp3lame";
                    break;

                //// "libfaac");
            }
            strFFMpegTemplate = strFFMpegTemplate.Replace("{AUDIOCODEC}", strAudioCodecOptions);

            // Audio Bitrate
            if (request.LiveTV)
            {
                string strAudioBitRate = "-ab " + audiobitrate;
                strFFMpegTemplate = strFFMpegTemplate.Replace("{AUDIOBITRATE}", strAudioBitRate);
            }
            else
            {
                string strAudioBitRate = "-ab " + EncodingParameters.AudioBitRate;
                strFFMpegTemplate = strFFMpegTemplate.Replace("{AUDIOBITRATE}", strAudioBitRate);
            }

            // Audio sample rate
            string strAudioSampleRate = "-ar " + EncodingParameters.AudioSampleRate;
            strFFMpegTemplate = strFFMpegTemplate.Replace("{AUDIOSAMPLERATE}", strAudioSampleRate);

            // Force stereo
            string strAudioChannels = "-ac 2";
            strFFMpegTemplate = strFFMpegTemplate.Replace("{AUDIOCHANNELS}", strAudioChannels);

            // Volume Level
            string strVolumeBoost = "";
            if (EncodingParameters.AudioVolumePercent != 100)
            {
                double fVolumeBytes = (256.0 * (EncodingParameters.AudioVolumePercent / 100.0));
                int iVolumeBytes = Convert.ToInt32(fVolumeBytes);
                strVolumeBoost = "-vol " + iVolumeBytes.ToString();
            }
            strFFMpegTemplate = strFFMpegTemplate.Replace("{VOLUMELEVEL}", strVolumeBoost);

            // Pipe to segmenter (ie send to standard output now)
            strFFMpegTemplate = strFFMpegTemplate + " -";


            // Commit - add to the arguments
            cmdArguments.AddArg(strFFMpegTemplate);

        }
        string croppedSizeForAspectRatio()
        {
            return "";
        }
        #endregion

        #region Debug
        public event EventHandler<GenericEventArgs<string>> DebugMessage;
        void shellRunner_StandardErrorReceivedLine(object sender, GenericEventArgs<string> e)
        {
            if (sender != shellRunner) return;
            if (e.Value == null) return; // this is actually required.  wow.

            //            VideoEncodingParameters.EOSDetected = (e.Value.Contains("Warning MVs not available"));

            //if (e.Value.Contains("time=") && e.Value.Contains("bitrate=")) //keep track of duration of media being played, does not work when user has used seek function!!!, e.g. in case of livetv?
            //{  //problem: debug info is not unique, can be made unique by putting streamid in debuginfo????????
            //    string part = e.Value;
            //    part = part.Substring(part.IndexOf("time=")+5);
            //    part = part.Substring(0,part.IndexOf(" "));
            //    double.TryParse(part, System.Globalization.NumberStyles.Any, CultureInfo.InvariantCulture, out CurrentPosition);//extract the time
            //    SendDebugMessage("^^&^^&*&*(&(*^^(*" + CurrentPosition.ToString());
            //}


            if (SettingsDefaultDebugAdvanced)
                SendDebugMessage("[C]" + e.Value);
        }

        void SendDebugMessage(string txtDebug)
        {
            if (DebugMessage != null)
                DebugMessage(this, new GenericEventArgs<string>(txtDebug));
        }
        void prober_DebugMessage(object sender, GenericEventArgs<string> e)
        {
            SendDebugMessage(e.Value);
        }
        //void grabber_DebugMessage(object sender, GenericEventArgs<string> e)
        //{
        //    WriteLineToLogFile(e.Value);
        //}
        //static object writeLogLock = new object();
        //public static string DebugLogFileFN;
        //static List<string> StoredLogEntries;
        //public static void WriteLineToLogFile(string txtLine)
        //{
        //    Monitor.Enter(writeLogLock);
        //    string logLine = System.String.Format("{0:G}: {1}.", System.DateTime.Now, txtLine);

        //    System.IO.StreamWriter sw;
        //    try
        //    {
        //        sw = System.IO.File.AppendText(DebugLogFileFN);
        //    }
        //    catch
        //    {
        //        // Store the log entry for later
        //        if (StoredLogEntries.Count < 150)  // limit
        //            StoredLogEntries.Add(logLine);

        //        Monitor.Exit(writeLogLock);
        //        return;
        //    }

        //    try
        //    {
        //        // Write any pending log entries
        //        if (StoredLogEntries.Count > 0)
        //        {
        //            foreach (string s in StoredLogEntries)
        //            {
        //                sw.WriteLine(s);
        //            }
        //            StoredLogEntries.Clear();
        //        }

        //        sw.WriteLine(logLine);
        //    }
        //    finally
        //    {
        //        sw.Close();
        //    }

        //    Monitor.Exit(writeLogLock);
        //}

        #endregion

        #region helpers
        private static int toInteger(string input)
        {
            if (input.IndexOf("k") == -1)
            {
                return Convert.ToInt32(input);
            }
            else
            {
                input = input.Replace("k", "");
                return Convert.ToInt32(input) * 1024;
            }
        }
        #endregion
        #region probing

        public TimeSpan GetMediaDuration(string fileName)
        {
            MediaInfoGrabber grabber = new MediaInfoGrabber(Functions2.ToolkitFolder, Path.Combine(Functions2.StreamBaseFolder, "probe_results"), fileName);
            grabber.DebugMessage += new EventHandler<FatAttitude.GenericEventArgs<string>>(grabber_DebugMessage);
            grabber.GetInfo();
            grabber.DebugMessage -= grabber_DebugMessage;

            TimeSpan duration = (grabber.Info.Success) ? grabber.Info.Duration : new TimeSpan(0);

            return duration;
        }

        void grabber_DebugMessage(object sender, FatAttitude.GenericEventArgs<string> e)
        {
            Functions2.WriteLineToLogFile(e.Value);
        }

        #endregion
    }


}
