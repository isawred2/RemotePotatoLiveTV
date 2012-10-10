using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace FatAttitude.LiveTV
{
    public class LiveTVParameters
    {
        public bool LiveTV { get; set; }
        public Int32 DurationLiveTVBlocks { get; set; }
        //public double AVSyncDifference { get; set; } // for livetv
        //public static bool EOSDetected { get; set; }

        public LiveTVParameters()
        {
            // Defaults

            DurationLiveTVBlocks = 1;

            //AVSyncDifference = 0.0; // for livetv

            //EOSDetected = false;
        }

    }    

}
