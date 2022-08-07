using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Threading.Tasks;
using VideoOS.Platform.SDK.StatusClient;

namespace CCN_CameraService
{
    static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>

        static void Main()
        {
#if DEBUG
            
            var DebugService = new CCN_CameraService();
            DebugService.OnDebug();
            

#else
            ServiceBase[] ServicesToRun = new ServiceBase[] {new CCN_CameraService()};
            ServiceBase.Run(ServicesToRun);
#endif
        }
    }
}
