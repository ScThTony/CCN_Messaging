using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Threading.Tasks;

namespace CCN_CameraService
{
    using VideoOS.Platform.SDK.Config;

    public partial class CCN_CameraService : ServiceBase
    {
        public CCN_CameraService()
        {
            InitializeComponent();
        }

        protected override void OnStart(string[] args)
        {
            Login(XptServer, XptUsername, XptPass);
            Initialize();
            
            

        }

        //Gets called from a tray icon app.
        protected override void OnCustomCommand(int command)
        {
            base.OnCustomCommand(command);
            if (command != 222 && statusApi == null)
            {
                return;
            }
            if (CancelAlarmTimer.Enabled)
            {
                CancelAlarmTimer.Stop();
            }

            statusApi.EventFired -= EventFiredHandler;
            statusApi.ConnectionStateChanged -= ConnectionStateChangedHandler;
            statusApi.Dispose();
            statusApi.WaitForSessionCompletion();   //block
            if (!VideoOS.Platform.SDK.Environment.IsLoggedIn(uri))
            {
                Login(XptServer, XptUsername, XptPass);
            }
            Initialize();

       
        }
        

        protected override void OnStop()
        {
            using (var db = new bmsContext())
            {
                var tolog = new StatusEventsLog();
                LogEvent(ref tolog, "ShutDown", "CCN_CameraService");
                db.StatusEventsLogs.InsertOnSubmit(tolog);
                try { db.SubmitChanges(); }
                catch (DataException de)
                { logger.Error(de, "EventFiredHandler data error"); }
            }

            if (CancelAlarmTimer.Enabled)  
            {
                CancelAlarmTimer.Elapsed -= OnElapsed;
                CancelAlarmTimer.Dispose();
            }
            if (statusApi != null)
            {

                statusApi.EventFired -= EventFiredHandler;
                statusApi.ConnectionStateChanged -= ConnectionStateChangedHandler;
                statusApi.Dispose();
                statusApi.WaitForSessionCompletion();
            }
            if (VideoOS.Platform.SDK.Environment.IsLoggedIn(uri))
            {
                VideoOS.Platform.SDK.Environment.RemoveAllServers();
                VideoOS.Platform.SDK.Environment.Logout(uri);
            }
            
          

        }

        public void OnDebug()
        {
            OnStart(null);
        }

    }
}
