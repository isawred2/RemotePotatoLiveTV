using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;

namespace FatAttitude.MediaStreamer
{
    public class LiveTVHelpers
    {
        public LiveTVHelpers()
        {
        }
        public double GetMediaSyncDifference(string PathToTools, string store, string fileName)
        {
            //MediaInfoGrabber grabber = new MediaInfoGrabber(Functions.ToolkitFolder, Path.Combine(Functions.StreamBaseFolder, "probe_results"), FatAttitude.Functions.FileWriter.GetShortPathName(fileName));
            //grabber.DebugMessage += new EventHandler<FatAttitude.GenericEventArgs<string>>(grabber_DebugMessage);
            //grabber.GetInfo();
            //grabber.DebugMessage -= grabber_DebugMessage;
            //return (grabber.Info.Success) ? grabber.Info.AVSyncDifference : 0.0; //for LiveTV
            MediaInfoGrabber grabber = new MediaInfoGrabber(PathToTools, Path.Combine(store, "probe_results"), Functions.FileWriter.GetShortPathName(fileName));
            grabber.DebugMessage += new EventHandler<GenericEventArgs<string>>(grabber_DebugMessage);
            grabber.GetInfo();
            grabber.DebugMessage -= grabber_DebugMessage;
            return ((grabber.Info.Success) ? grabber.Info.AVSyncDifference : 0.0); //for LiveTV

        }
        void grabber_DebugMessage(object sender, FatAttitude.GenericEventArgs<string> e)
        {
            Functions2.WriteLineToLogFileIfSetting(true, e.Value);
        }

    }
}
