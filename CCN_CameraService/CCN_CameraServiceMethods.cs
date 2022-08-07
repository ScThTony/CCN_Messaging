using System;
using System.Collections.Generic;
using System.Linq;
using VideoOS.Platform;
using VideoOS.Platform.SDK.Config;
using VideoOS.Platform.SDK.StatusClient;
using VideoOS.Platform.SDK.StatusClient.StatusEventArgs;
using System.Security;
using System.Data.Linq;
using System.Data;
using System.Timers;
using System.ServiceProcess;
using System.Net;
using NLog;

namespace CCN_CameraService
{
    public partial class CCN_CameraService
    {
        private static Logger logger = LogManager.GetCurrentClassLogger();
       // Data Source = metasys; Initial Catalog = CCN_BMS; Persist Security Info=True;User ID = ccn_bms; Password=ccn_bms

        private static readonly string XptServer = Properties.Settings.Default.XptServer;
        private static readonly string XptUsername = Properties.Settings.Default.XptUsername;
        private static readonly string XptPass = Properties.Settings.Default.XptPass;

        private static bool PreviouslyTried = false;

        private static Uri uri;
        private StatusSession statusApi;

        private static readonly int TimerInterval = Properties.Settings.Default.DefaultTimerInterval * 1000;
        private static readonly Timer CancelAlarmTimer = new Timer();

        void Login(string server, string user, string unsecPass)
        {
            VideoOS.Platform.SDK.Environment.Initialize();

            //SecureString secPass = new SecureString();
            //Array.ForEach(unsecPass.ToArray(), secPass.AppendChar);
            //uri = new UriBuilder(server).Uri;
            //VideoOS.Platform.SDK.Environment.AddServer(uri, new System.Net.NetworkCredential(user, secPass));


            //Comment out above 4 lines and uncomment next 3 lines to use basic auth login to XProtect
            uri = new UriBuilder(server).Uri;
            var cc = VideoOS.Platform.Login.Util.BuildCredentialCache(uri, user, XptPass, "Basic");
            VideoOS.Platform.SDK.Environment.AddServer(uri, cc);

            try
            { VideoOS.Platform.SDK.Environment.Login(uri); }
            catch (Exception exception)
            {
                logger.Error(exception, "Initial login to XProtect failed. CCN_Camera Service is stopping."); 
                //This Initial login will only run once ever, unless the pc reboots. All other reconnects handled by StatusClient library. 
                base.Stop();   //user present at initial login/service start
            }
        }

        //Note: assumes single recording server without checking for recorder name. Specify recorder name for multiple recorder setup.
        static Item GetRecordingServer()
        {
            Item serverItem = Configuration.Instance.GetItem(EnvironmentManager.Instance.CurrentSite);
            List<Item> serverItemsList = serverItem.GetChildren();
            return serverItemsList.FirstOrDefault(item => item.FQID.Kind == Kind.Server && 
                                                          item.FQID.ServerId.ServerType == ServerId.CorporateRecordingServerType);
        }


        IEnumerable<Item> FindAllCameras(IEnumerable<Item> items, Guid recorderGuid)
        {
            foreach (Item item in items)
            {
                if (item.FQID.Kind == Kind.Camera && item.FQID.ParentId == recorderGuid && item.FQID.FolderType == FolderType.No)
                {
                    yield return item;
                }
                else if (item.FQID.FolderType != FolderType.No)
                {
                    foreach (var camera in FindAllCameras(item.GetChildren(), recorderGuid))
                    {
                        yield return camera;
                    }
                }
            }
        }


        //Updates db.Cameras table from live Cameras on XProtect
        static void UpdateCamerasDb(IEnumerable<Item> items)
        {

            using (var db = new bmsContext())
            {
                //Add new live cameras
                var NewCams = items.Where(i => !db.Cameras.Select(c => c.ObjectId).Any(c => c.Equals(i.FQID.ObjectId))).ToList();
                //linq returns empty if no result
                foreach (var newCam in NewCams)
                {
                    var toCamerasDb = new Camera
                    {
                        MessagingEnabled = false,
                        NormalStateInterval = Properties.Settings.Default.NormalStateInterval,
                        CameraName = newCam.Name,
                        ObjectId = newCam.FQID.ObjectId,
                    };
                    db.Cameras.InsertOnSubmit(toCamerasDb);
                }
                if (NewCams.Any())
                {
                    try { db.SubmitChanges(); }
                    catch (DataException de)
                    { logger.Warn(de, "Failed to insert new Cameras from XProtect to db.Cameras"); }
                }
                
                

                //Delete non-existant cameras
                var toDeletes = db.Cameras.Where(c => !items.Select(i => i.FQID.ObjectId).Contains(c.ObjectId));

                foreach (var toDelete in toDeletes)
                {
                    db.Cameras.DeleteOnSubmit(toDelete);
                };
                if (toDeletes.Any())
                {
                    try { db.SubmitChanges(); }
                    catch (DataException de)
                    { logger.Warn(de, "Failed to delete non-existant cameras on db.Cameras"); }
                }


                //Update those camera names that changed
                bool changed = false;
                foreach (var toupdate in db.Cameras)
                {
                    var newName = items.Where(i => toupdate.ObjectId.Equals(i.FQID.ObjectId)).Select(i => i.Name).FirstOrDefault();

                    if (!String.IsNullOrEmpty(newName))
                    {
                        toupdate.CameraName = newName;
                        changed = true;
                    }
                }
                if (changed)
                {
                    try { db.SubmitChanges(); }
                    catch (DataException de)
                    { logger.Warn(de, "Failed to update db.Cameras"); }
                }
                

                
            }
        }



        //Updates db.MonitoringPoints from db.Cameras + live cameras on XProtect 
        static void UpdateBms(IEnumerable<Item> items)
        {
            using (var db = new bmsContext())
            {
                //Inserts all newly enabled cameras on db.Cameras into db.MonitoringPoints
                var options = new DataLoadOptions();
                options.LoadWith<Camera>(c => c.MonitoringPoints);          //force join
                db.LoadOptions = options;
                var newEnableds = db.Cameras.Where(c => !c.MonitoringPoints.Any(m => m.CameraKey.Equals(c.PK)) && c.MessagingEnabled == true)
                                            .ToDictionary(c => c.CameraName, c => c.PK);

                if (!newEnableds.Any())
                {
                    return;
                }

                var sources = items.Where(i => newEnableds.Keys.Any(n => n.Equals(i.Name))).ToList();

                foreach (var source in sources)
                {
                    var toAdd = new MonitoringPoint()
                    {
                        itemFullyQualifiedReference = source.FQID.ObjectId.ToString(),
                        AllowedDowntime = 0,
                        Enabled = true,
                        Priority = 1,
                        Location = "None",
                        Equipment = source.Name,
                        ParentLocation = source.FQID.ParentId.ToString(),
                        CameraKey = newEnableds[source.Name]
                    };
                    db.MonitoringPoints.InsertOnSubmit(toAdd);
                }
                try { db.SubmitChanges(); }
                catch (DataException de)
                { logger.Warn(de, "Failed to insert new enabled Cameras to db.Monitoringpoints"); }
                    
                
            }

            using (var db = new bmsContext())
            {
                //Deletes all newly disabled cameras on db.MonitoringPoints
                var options = new DataLoadOptions();
                options.LoadWith<MonitoringPoint>(c => c.Camera);             //force join         
                db.LoadOptions = options;
                var toDeletes = db.MonitoringPoints.Where(m => m.Camera.PK.Equals(m.CameraKey) && m.Camera.MessagingEnabled == false);

                foreach (var toDelete in toDeletes)
                {
                    db.MonitoringPoints.DeleteOnSubmit(toDelete);
                }
                if (toDeletes.Any())
                {
                    try { db.SubmitChanges(); }
                    catch (DataException de)
                    { logger.Warn(de, "Failed to Delete disabled Cameras on db.Monitoringpoints"); }
                }

                //Updates all cameras on db.MonitoringPoints
                var toUpdates = db.MonitoringPoints.Where(m => m.Camera.PK.Equals(m.CameraKey));
                foreach (var toUpdate in toUpdates)
                {
                    toUpdate.itemFullyQualifiedReference = toUpdate.Camera.ObjectId.ToString();
                    toUpdate.AllowedDowntime = 0;
                    toUpdate.Enabled = true;
                    toUpdate.Priority = 1;
                    toUpdate.Equipment = toUpdate.Camera.CameraName;
                    toUpdate.CameraKey = toUpdate.Camera.PK;
                }
                if (toUpdates.Any())
                {
                    try { db.SubmitChanges(); }
                    catch (DataException de)
                    { logger.Warn(de, "Failed to update db.Monitoringpoints"); }
                }
                
            }
        }

        //Does not check live status. Simple cleanup and alarm reset.
        //Only call this on service start, reboots and after extended downtime. 
        static void CancelOldAlarms() 
        {
            using (var db = new bmsContext())
            {
                var options = new DataLoadOptions();
                options.LoadWith<MonitoringPoint>(m => m.Camera);
                db.LoadOptions = options;

                //Checks first if there any OldAlarms. If yes, loads the relevant rows from both db.MonitoringPoints and db.Cameras.
                var OldAlarms = db.MonitoringPoints.Where(m => m.IsAlarmState == true && m.CameraKey != null && !m.itemFullyQualifiedReference.StartsWith("Metasys")).ToList();

                //Checks if the OldAlarms have expired, and if yes, cancels them. 
                if (!OldAlarms.Any())
                {
                    return;
                }

                var ExpiredAlarms = OldAlarms.Where(m => DateTime.Now.ToUniversalTime() > Convert.ToDateTime(m.LastEventDate).AddSeconds(m.Camera.NormalStateInterval));
                foreach (var OldAlarm in OldAlarms)
                {
                    OldAlarm.IsAlarmState = false;
                    //leave LastActionId unchanged
                }
                try { db.SubmitChanges(); }
                catch (DataException de)
                { logger.Warn(de, "Failed to cancel old alarms on db.MonitoringPoints"); }
            }
        }


        static IEnumerable<Item> ReturnEnabled(IEnumerable<Item> items)
        {
            var db = new bmsContext();
            var msgEnabled = db.Cameras.Where(c => c.MessagingEnabled == true).Select(o => o.ObjectId).ToList();
            var enabledCams = items.Where(i => msgEnabled.Any(m => m.Equals(i.FQID.ObjectId)));

            foreach (var cam in enabledCams)
            {
                yield return cam;
            }
        }

        void StartStatusSession(Item recorder,  List<Item> allcams)
        {
            ISet<Guid> subscribedEvents = new HashSet<Guid>();
            subscribedEvents.Add(VideoOS.Platform.SDK.StatusClient.KnownStatusEvents.MotionStarted);
            
            ISet<Guid> subscribedCameras = new HashSet<Guid>(ReturnEnabled(allcams).ToDictionary(i => i.FQID.ObjectId).Keys);
            if (subscribedCameras.Count == 0)
            {
                logger.Fatal("No subscribed cameras found!");
                base.Stop(); //CameraService assumes -> no cameras enabled = want to stop (stops normally)
                return;
            }

            statusApi = new StatusSession(recorder);
            
            statusApi.ConnectionStateChanged += ConnectionStateChangedHandler;                
            statusApi.EventFired += EventFiredHandler;  
            
            statusApi.StartSession();
            statusApi.SetSubscribedEvents(subscribedEvents);
            statusApi.SetSubscribedDevicesForStateChanges(subscribedCameras);
            StartCancelTimer();
            
        }

        
        private static void ConnectionStateChangedHandler(object sender, ConnectionStateChangedEventArgs e)
        {
            using (var db = new bmsContext())
            {   
                var toLog = new StatusEventsLog();
                LogEvent(ref toLog, e.ConnectionState.ToString() , "CCN_CameraService");
                db.StatusEventsLogs.InsertOnSubmit(toLog);
                try { db.SubmitChanges(); }
                catch (DataException de)
                { logger.Error(de, "Statusapi connection handler failed to log an event"); }                
            }


            //New Code to test 28/10/2015
            if (e.ConnectionState == ConnectStates.TryingToLogOn && PreviouslyTried == false)
            {
                PreviouslyTried = true;
            }
            if (e.ConnectionState == ConnectStates.Connected && PreviouslyTried == true)
            {
                PreviouslyTried = false;
            }
            if (e.ConnectionState == ConnectStates.NotConnected && PreviouslyTried == false)
            {
                CancelOldAlarms();
            }

        }
   

        private static void LogEvent (ref StatusEventsLog log, string name, string state)
        {
            log.DateTime = DateTime.Now;
            log.Event = state;
            log.Source = name;
            log.MessageSent = false;

        }


        private static void LogEvent(ref StatusEventsLog log, EventFiredEventArgs fe, string name, bool msg)
        {            
            log.DateTime = fe.Time.ToLocalTime();
            log.Event = KnownStatusEvents.GetEventName(fe.EventId);
            log.Source = name;
            log.MessageSent = msg;
        }

        private static void LogPoint(ref MonitoringPoint mp, bool alarm, int actionId, DateTime dt)
        {
            mp.IsAlarmState = alarm;
            mp.LastActionID = actionId;
            mp.LastEventDate = dt;
        }


        private static bool DelayLapsed(int normalInterval, DateTime last)
        {
            last = last.ToLocalTime();

            return DateTime.Now > last.AddSeconds(normalInterval);
        }

        private static void EventFiredHandler(object sender, EventFiredEventArgs e)
        {
            if (e.EventId != KnownStatusEvents.MotionStarted)
            {
                return;
            }

            using (var db = new bmsContext())
            {
                var currentCam = db.MonitoringPoints.FirstOrDefault(m => m.itemFullyQualifiedReference.Equals(e.SourceId.ToString()));
                //Select on SourceId as the live camera name might have been changed on XProtect Server and CustomUpdateCommand has not yet been run.

                if (currentCam == null)
                {
                    return;
                }
                if (currentCam.IsAlarmState != true)
                {
                    LogPoint(ref currentCam, true, 1, e.Time);

                    var tolog = new StatusEventsLog();
                    LogEvent(ref tolog, e, currentCam.Equipment, true);
                    db.StatusEventsLogs.InsertOnSubmit(tolog);
                    try { db.SubmitChanges(); }
                    catch (DataException de)
                    { logger.Error(de, "EventFiredHandler data error"); }
                }
                else
                {
                    currentCam.LastEventDate = e.Time;

                    var tolog = new StatusEventsLog();
                    LogEvent(ref tolog, e, currentCam.Equipment, false);
                    db.StatusEventsLogs.InsertOnSubmit(tolog);
                    try { db.SubmitChanges(); }
                    catch (DataException de)
                    { logger.Error(de, "EventFiredHandler data error"); }
                }
            }
        }

        private static void StartCancelTimer()
        {
            CancelAlarmTimer.Interval = TimerInterval;
            CancelAlarmTimer.Elapsed += OnElapsed;
            CancelAlarmTimer.AutoReset = true;

            CancelAlarmTimer.Start();
        }


        private static void OnElapsed(object sender, ElapsedEventArgs e)
        {
            try
            {
                CancelAlarmTimer.Stop();
                using (var db = new bmsContext())
                {
                    var options = new DataLoadOptions();
                    options.LoadWith<MonitoringPoint>(m => m.Camera);         //force join
                    db.LoadOptions = options;

                    var allCams = db.MonitoringPoints.Where(m => m.CameraKey != null && !m.itemFullyQualifiedReference.StartsWith("Metasys"));

                    foreach (var cam in allCams)
                    {

                        if (cam.IsAlarmState == true &&
                            DelayLapsed(cam.Camera.NormalStateInterval, Convert.ToDateTime(cam.LastEventDate)))
                        {
                            cam.IsAlarmState = false;
                        }

                    }
                    try { db.SubmitChanges(); }
                    catch (DataException de)
                    { logger.Error(de, "Timer handler failed to cancel alarm state on db.MonitoringPoints"); }

                }

            }
            finally
            {
                CancelAlarmTimer.Start();
            }
        }

        void Initialize()
        {
            Item recordingServer = GetRecordingServer();
            List<Item> AllCameras = FindAllCameras(recordingServer.GetChildren(), recordingServer.FQID.ServerId.Id).ToList();

            UpdateBms(AllCameras);
            UpdateCamerasDb(AllCameras);
            CancelOldAlarms();
            StartStatusSession(recordingServer, AllCameras);
        }
    }
}   