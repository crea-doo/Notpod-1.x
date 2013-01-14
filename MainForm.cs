using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Text;
using System.IO;
using System.Windows.Forms;
using iTunesLib;
using System.Xml;
using System.Xml.Serialization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Diagnostics;
using Notpod.Configuration12;
using Notpod.Properties;
using System.Security.AccessControl;
using Common.Logging;
using WindowsPortableDevicesLib;
using WindowsPortableDevicesLib.Domain;

namespace Notpod
{
    public partial class MainForm : Form
    {

        private ILog l = LogManager.GetLogger(typeof(MainForm));

        //Constants used when intercepting system messages
        public const int WM_SYSCOMMAND = 0x112;
        public const int WM_DEVICECHANGE = 0x0219;
        public const int SC_MINIMIZE = 0xF020;
        public const int DBT_DEVICEARRIVAL = 0x8000;
        public const int DBT_DEVICEREMOVECOMPLETE = 0x8004;

        /// <summary>
        /// The path where the configuration is stored.
        /// </summary>
        public static readonly string DATA_PATH = ConfigurationHelper.GetAppDataPath();

        private ITunesAppFactory itunesAppFactory = new DefaultITunesAppFactory();

        private iTunesApp itunes;
        private IITUserPlaylist folderMyDevices;
        private IConnectedDevicesManager connectedDevices;

        private DeviceConfiguration deviceConfiguration;
        private SyncPatternCollection syncPatterns;
        private Configuration configuration;

        private ISynchronizeForm syncForm;

        private WindowsPortableDeviceService portableDevicesService;

        private Hashtable deviceInitialPlaylists = new Hashtable();

        /// <summary>
        /// Create a new instance of MainForm.
        /// </summary>
        public MainForm()
        {
            InitializeComponent();
            portableDevicesService = new StandardWindowsPortableDeviceService();
        }

        /// <summary>
        /// Event handler for the form load event.
        /// </summary>
        /// <param name="sender">Sender</param>
        /// <param name="e">Event arguments</param>
        private void MainForm_Load(object sender, EventArgs e)
        {

            l.Info("Notpod is initializing...");

            this.Visible = false;

            this.ShowInTaskbar = true;

            // Check application configuration
            if (!File.Exists(MainForm.DATA_PATH + "\\.ita-convert"))
            {
                if (!ConfigurationHelper.MovePreNotpodConfiguration())
                {
                    Application.Exit();
                    return;
                }

            }

            //Try loading the configuration
            StreamReader stream = null;
            XmlTextReader reader = null;
            try
            {
                XmlSerializer serializer = new XmlSerializer(typeof(Configuration));
                stream = new StreamReader(MainForm.DATA_PATH + "\\notpod-config.xml");
                reader = new XmlTextReader(stream);

                configuration = (Configuration)serializer.Deserialize(reader);

            }
            catch (Exception ex)
            {
                string message = "I could not locate 'notpod-config.xml' which contains configuration data,"
                    + " or the file exists, but is invalid. Please make sure this file is "
                    + "valid and restart me.\n\nError message: " + ex.Message;

                l.Error(message, ex);

                MessageBox.Show(message, "Configuration not available",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                Application.Exit();
                return;
            }
            finally
            {
                if (stream != null)
                    stream.Close();
                if (reader != null)
                    reader.Close();
            }

            //Load devices that the agent recognizes
            StreamReader dcStream = null;
            XmlTextReader dcReader = null;
            try
            {
                XmlSerializer serializer = new XmlSerializer(typeof(DeviceConfiguration));
                dcStream = new StreamReader(MainForm.DATA_PATH + "\\device-config.xml");
                dcReader = new XmlTextReader(dcStream);

                deviceConfiguration = (DeviceConfiguration)serializer.Deserialize(dcReader);

            }
            catch (Exception ex)
            {
                l.Error(ex);

                MessageBox.Show("Unable to locate list of known devices. The agent needs this list.\n\nReason for failure: " + ex.Message,
                    "Missing list of devices", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Application.Exit();
            }
            finally
            {
                if (dcStream != null)
                    dcStream.Close();
                if (dcReader != null)
                    dcReader.Close();
            }

            LoadSyncPatterns();
            deviceConfiguration.SyncPattern = syncPatterns.SyncPatterns;


            if (!CreateITunesInstance())
            {
                Application.Exit();
                return;
            }

            if (configuration.UseListFolder)
                CreateMyDevicesFolder();

            //Create ConnectedDevice instance which maintains a list of connected devices.
            connectedDevices = new ConnectedDevicesManagerImpl();
            connectedDevices.DeviceConfig = deviceConfiguration;
            connectedDevices.DeviceConnected += new DeviceConnectedEventHandler(OnDeviceConnected);
            connectedDevices.DeviceDisconnected += new DeviceDisconnectedEventHandler(OnDeviceDisconnect);


            connectedDevices.Synchronize(portableDevicesService.Devices);           
        }

        private void LoadSyncPatterns()
        {

            //Load devices that the agent recognizes
            StreamReader stream = null;
            XmlTextReader reader = null;
            try
            {
                XmlSerializer serializer = new XmlSerializer(typeof(SyncPatternCollection));
                stream = new StreamReader(Application.StartupPath + "\\Resources\\syncpatterns.xml");
                reader = new XmlTextReader(stream);

                syncPatterns = (SyncPatternCollection)serializer.Deserialize(reader);

            }
            catch (Exception ex)
            {
                l.Error(ex);

                MessageBox.Show("Unable to load available synchronize patterns.\n\nReason for failure: " + ex.Message,
                    "Missing synchronize patterns", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Application.Exit();
            }
            finally
            {
                if (stream != null)
                    stream.Close();
                if (reader != null)
                    reader.Close();
            }
        }
        /// <summary>
        /// Check if the My Devices folder exists, if not create it.
        /// </summary>
        private void CreateMyDevicesFolder()
        {
            try
            {
                foreach (IITPlaylist p in itunes.LibrarySource.Playlists)
                {
                    if (p.Name == "My Devices" && p.Kind == ITPlaylistKind.ITPlaylistKindUser)
                    {
                        folderMyDevices = (IITUserPlaylist)p;
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                l.Error(ex);
            }

            try
            {
                object src = itunes.LibrarySource;
                folderMyDevices = (IITUserPlaylist)itunes.CreateFolderInSource("My Devices", ref src);
            }
            catch (COMException ex)
            {
                l.Error(ex);

                MessageBox.Show(this, "Unable to create 'My Devices' folder. This may "
                + "be due to iTunes being busy with an other operation. If this error "
                + "continues to occur, please turn the option off in the Preferences dialog.\n\n(" + ex.Message + ")",
                "Failed to create My Devices folder", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

        }

        /// <summary>
        /// Creates connection to iTunes.
        /// </summary>
        /// <returns>True if an instance of iTunes interface was successfully made, 
        /// false otherwise.</returns>
        public bool CreateITunesInstance()
        {
            //Try to create a COM connection to iTunes.
            bool retry = true;
            while (retry)
            {

                try
                {
                    SetITunesInstance();
                    string version = itunes.Version;
                    SetStatusMessage("Notpod", "I have found iTunes (" + version
                        + ") on your computer and I am ready to synchronize your devices.",
                        ToolTipIcon.Info);
                    SetEventHandlers();
                    retry = false;
                }
                catch (Exception ex)
                {
                    l.Warn(ex);

                    if (MessageBox.Show(this, "Unable to communicate with iTunes. Do you want to retry?\n\n("
                        + ex.Message + ")", "Communication error", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                    {
                        retry = true;
                    }
                    else
                    {
                        SetStatusMessage("Notpod", "An error occured while communicating with iTunes. Please "
                            + "make sure iTunes is properly installed and running before restarting the application.",
                            ToolTipIcon.Error);
                        return false;
                    }
                }
            }

            return true;
        }

        public void SetITunesInstance()
        {

            itunes = itunesAppFactory.GetNewInstance();
        }

        /// <summary>
        /// Event handler for iTunes quit events.
        /// </summary>
        protected void OniTunesQuitEvent()
        {
            Console.Out.WriteLine("OniTunesQuitEvent");

            CancelEventArgs e = new CancelEventArgs(false);
            this.OnClosing(e);
            Application.Exit();
        }

        /// <summary>
        /// Event handler for connected devices.
        /// </summary>
        /// <param name="sender">Sender of the event.</param>
        /// <param name="args">Event arguments.</param>
        private void OnDeviceConnected(object sender, CDMEventArgs args)
        {

            WindowsPortableDevice device = args.PortableDevice;
            Device deviceConfig = args.Device;
            

            if (configuration.ShowNotificationPopups)
            {
                string message = "'" + deviceConfig.Name + "' has been connected. "
                    + "You may now synchronize the device "
                    + "with the playlist for this device.\n\nDevices currently connected:";


                foreach (Device d in connectedDevices.GetConnectedDevices().Values)
                    message += "\n - " + d.Name;


                itaTray.ShowBalloonTip(5, "Device connected!", message, ToolTipIcon.Info);
            }

            IITPlaylist playlist = PlaylistExists(deviceConfig);
            //Delete playlist if it exists.
            //if (playlist != null)
            //    playlist.Delete();
            if (playlist == null)
            {
                try
                {
                    if (configuration.UseListFolder)
                    {
                        CreateMyDevicesFolder();
                        playlist = folderMyDevices.CreatePlaylist(deviceConfig.Name);
                    }
                    else
                        playlist = itunes.CreatePlaylist(deviceConfig.Name);

                }
                catch (Exception e)
                {
                    l.Error(e);

                    MessageBox.Show("Failed to create list for device '" + deviceConfig.Name
                        + "'. You will not be able to synchronize this device with iTunes.",
                        "Playlist error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }

            }
            else
            {
                //If the option to use "My Devices" folder is set, move the playlist to that folder.
                if (configuration.UseListFolder && (playlist.Kind == ITPlaylistKind.ITPlaylistKindUser)
                    && (deviceConfig.Playlist == null || deviceConfig.Playlist.Length == 0))
                {
                    CreateMyDevicesFolder();
                    object parent = (object)folderMyDevices;
                    ((IITUserPlaylist)playlist).set_Parent(ref parent);
                }
            }

        }

        /// <summary>
        /// Checks if an iTunes playlist exists.
        /// </summary>
        /// <param name="deviceConfig">Device to check playlist for.</param>
        /// <returns>The playlist if it exists, null otherwise.</returns>
        private IITPlaylist PlaylistExists(Device deviceConfig)
        {

            string name = (deviceConfig.Playlist == null || deviceConfig.Playlist.Length == 0) ? deviceConfig.Name : deviceConfig.Playlist;

            bool retry = true;
            while (retry)
            {
                try
                {

                    foreach (IITPlaylist playlist in itunes.LibrarySource.Playlists)
                    {
                        if (playlist.Name.Equals(name))
                            return playlist;
                    }

                    return null;
                }
                catch (Exception comex)
                {
                    l.Warn(comex);

                    retry = MessageBox.Show("Failed to get playlists from iTunes. This is most likely due to iTunes being busy or an open dialog. Do you want to try again?\n\n(" + comex.Message + ")", "Communication error", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
                   
                    continue;
                }
            }

            return null;
        }

        /// <summary>
        /// Event handler for disconnected devices.
        /// </summary>
        /// <param name="sender">Sender of the event.</param>
        /// <param name="driveName">Name of drive where the device was connected.</param>
        /// <param name="args">Event arguments.</param>
        private void OnDeviceDisconnect(object sender, CDMEventArgs args)
        {
            Device device = args.Device;

            if (configuration.ShowNotificationPopups)
                itaTray.ShowBalloonTip(5, "Device disconnected", "'" + device.Name + "' was disconnected.", ToolTipIcon.Info);

        }

        /// <summary>
        /// Overriding OnClosing to remove playlists for devices.
        /// </summary>
        /// <param name="e"></param>
        protected override void OnClosing(CancelEventArgs e)
        {
            //Hashtable cd = connectedDevices.GetConnectedDevicesWithDrives();
            //IDictionaryEnumerator cdenum = cd.GetEnumerator();
            //while (cdenum.MoveNext())
            //{
            //    CDMEventArgs args = new CDMEventArgs(null, (Device)cdenum.Value);
            //    OnDeviceDisconnect(this, (string)cdenum.Key, args);
            //}

            itunes = null;

            base.OnClosing(e);
        }

        /// <summary>
        /// Set event handlers for iTunes events.
        /// </summary>
        private void SetEventHandlers()
        {
            itunes.OnQuittingEvent += new _IiTunesEvents_OnQuittingEventEventHandler(OniTunesQuitEvent);
            itunes.OnAboutToPromptUserToQuitEvent
                    += new _IiTunesEvents_OnAboutToPromptUserToQuitEventEventHandler(OniTunesQuitEvent);
        }

        /// <summary>
        /// Set the status message in the main window.
        /// </summary>
        /// <param name="message">Status message to set.</param>
        /// <param name="icon">The type of icon to use on the baloon.</param>
        public void SetStatusMessage(string title, string message, ToolTipIcon icon)
        {
            if (configuration.ShowNotificationPopups)
                itaTray.ShowBalloonTip(5, title, message, icon);
        }

        /// <summary>
        /// WndProc override to enable minimize to tray.
        /// </summary>
        /// <param name="m"></param>
        protected override void WndProc(ref Message m)
        {
            switch(m.Msg) {
                case WM_SYSCOMMAND: 
                    {
            
                        switch (m.WParam.ToInt32())
                        {
                            case SC_MINIMIZE:
                                {
                                    Hide();
                                    return;
                                }
                            default:
                                {
                                    break;
                                }
                        }
                        break;
                    }
                case WM_DEVICECHANGE:
                    {
                        switch (m.WParam.ToInt32())
                        {
                            case DBT_DEVICEARRIVAL:
                                {
                                    l.Debug("A new device was connected to the system. Triggering device check...");
                                    while (portableDevicesService.Devices.Count == 0)
                                    {
                                        Thread.Sleep(100);
                                    }
                                    connectedDevices.Synchronize(portableDevicesService.Devices);
                                    break;
                                }
                            case DBT_DEVICEREMOVECOMPLETE:
                                {
                                    l.Debug("A device was removed. Triggering device check...");
                                    connectedDevices.Synchronize(portableDevicesService.Devices);
                                    break;
                                }

                            default:
                                {
                                    break;
                                }
                        }
                        break;
                    }

                default:
                    {
                        break;
                    }

            }

            //Let the base class handle the rest.
            base.WndProc(ref m);
        }

        private void ctxTraySynchronize_Click(object sender, EventArgs e)
        {

            // There are no devices connected.
            if (connectedDevices.GetConnectedDevices().Count == 0)
            {
                if (configuration.ShowNotificationPopups)
                    itaTray.ShowBalloonTip(3, "No devices", "There are no connected devices to synchronize.", ToolTipIcon.Info);

                return;
            }

            syncForm = new StandardSynchronizerForm();
            syncForm.Show();
            Thread thread = new Thread(new ThreadStart(PerformSynchronize));
            thread.Start();
        }

        private bool GetMusicLocationConfirmation(string path)
        {
            DialogResult confirmedMediaRoot = MessageBox.Show("Please confirm that the path below represents the folder on your portable "
                + "media player where you want Notpod to copy your music to:\n\n" + path + "\n\nConfirm the path above by clicking OK, "
                + "otherwise click Cancel and reconfigure your device under Preferences.", "Confirm music location", 
                MessageBoxButtons.OKCancel, MessageBoxIcon.Exclamation);
            return confirmedMediaRoot == DialogResult.OK;
          
        }
                
        public void PerformSynchronize()
        {


            IDictionary<WindowsPortableDevice, Device> deviceinfo = connectedDevices.GetConnectedDevices();
            IEnumerator<WindowsPortableDevice> keys = deviceinfo.Keys.GetEnumerator();
            try
            {
                // Create synchronizer and form.
                ISynchronizer synchronizer = new StandardSynchronizer();
                synchronizer.Form = syncForm;

                while (keys.MoveNext())
                {

                    WindowsPortableDevice drive = keys.Current;
                    Device device = (Device)deviceinfo[keys.Current];

                    if (configuration.ConfirmMusicLocation && !GetMusicLocationConfirmation(device.MediaLocation.LocationName))
                    {
                        syncForm.CloseSafe();
                        syncForm = null;
                        synchronizer = null;
                        return;
                    }

                    IITPlaylist playlist = PlaylistExists(device);
                    if (playlist == null)
                    {
                        MessageBox.Show("I could not synchronize '" + device.Name + "' because "
                            + "the playlist does not exist! Try reconnecting the device. If the problem continues"
                            + " please report the problem to the Notpod developers at http://notpod.com/support.html.",
                            "Internal synchronization error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        continue;
                    }

                    synchronizer.Configuration = deviceConfiguration;
                    synchronizer.SynchronizeError += new SynchronizeErrorEventHandler(OnSynchronizeError);
                    synchronizer.SynchronizeComplete += new SynchronizeCompleteEventHandler(OnSynchronizeComplete);
                    synchronizer.SynchronizeCancelled += new SynchronizeCancelledEventHandler(OnSynchronizeCancelled);
                    synchronizer.SynchronizeDevice((IITUserPlaylist)playlist, drive, device);

                }

            }
            catch (Exception ex)
            {
                l.Error(ex);

                string message = "I encountered an unexpected error while synchronizing your device(s). "
                    + "\n\nPlease make sure your devices are properly "
                    + "connected and try again.";

                itaTray.ShowBalloonTip(7, "Synchronize error", message, ToolTipIcon.Error);
            }

            if (syncForm != null && configuration.CloseSyncWindowOnSuccess)
                syncForm.CloseSafe();
        }

        private void OnSynchronizeError(object sender, SyncErrorArgs args)
        {
            itaTray.ShowBalloonTip(5, "Synchronize error", args.ErrorMessage, ToolTipIcon.Error);
        }

        private void OnSynchronizeComplete(object sender)
        {
            if (configuration.ShowNotificationPopups)
                itaTray.ShowBalloonTip(5, "Synchronize complete", "Your device was successfully synchronized with iTunes.", ToolTipIcon.Info);
        }

        private void OnSynchronizeCancelled(object sender)
        {
            if (configuration.ShowNotificationPopups)
                itaTray.ShowBalloonTip(5, "Synchronization cancelled", "The synchronization was cancelled.", ToolTipIcon.Warning);
        }


        /// <summary>
        /// Event handler for the exit menu item.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void menuAgentExit_Click(object sender, EventArgs e)
        {
            Close();
        }

        /// <summary>
        /// Event handler for Help->About
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void menuHelpAbout_Click(object sender, EventArgs e)
        {

        }

        /// <summary>
        /// Event handler for the Help->Online documentation menu item.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void menuHelpOnlineDoc_Click(object sender, EventArgs e)
        {

        }

        private void menuAgentConfigure_Click(object sender, EventArgs e)
        {

        }

        private void menuAgentFormat_Click(object sender, EventArgs e)
        {
            DialogResult dr = MessageBox.Show("WARNING! This will delete all music on the device and will synchronize your device with the current playlist for that device in iTunes.\n\nContinue?", "Warning!", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        }

        private void ctxTrayExit_Click(object sender, EventArgs e)
        {
            menuAgentExit_Click(sender, e);
        }

        private void linkJaranweb_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {

            //Open URL in default browser
            ProcessStartInfo info = new ProcessStartInfo("http://notpod.com/");
            info.Verb = "open";
            Process.Start(info);

        }

        /// <summary>
        /// Event handler for the tray context menu "About Notpod..."
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ctxTrayAbout_Click(object sender, EventArgs e)
        {
            AboutBox about = new AboutBox();
            about.ShowDialog();
        }

        /// <summary>
        /// Event handler for the tray context menu "Online help..."
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ctxTrayHelp_Click(object sender, EventArgs e)
        {
            //Open URL in default browser
            ProcessStartInfo info = new ProcessStartInfo("http://notpod.com/support.html");
            info.Verb = "open";
            Process.Start(info);
        }

        private void ctxTrayReportBug_Click(object sender, EventArgs e)
        {
            //Open URL in default browser
            ProcessStartInfo info = new ProcessStartInfo("https://github.com/notpod/Notpod-1.x/issues/new");
            info.Verb = "open";
            Process.Start(info);
        }

        /// <summary>
        /// Event handler for the tray context menu "Preferences..."
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ctxTrayPreferences_Click(object sender, EventArgs e)
        {
            ConfigurationForm conf = new ConfigurationForm(ref configuration, ref deviceConfiguration, ref itunes);
            conf.ConnectedDevicesManager = connectedDevices;
            conf.ShowDialog();

            connectedDevices.Synchronize(portableDevicesService.Devices);

        }

        /// <summary>
        /// Check the provided path against the list of potential system drives
        /// </summary>
        /// <param name="path">Path to check.</param>
        /// <returns>False if all is good, true if the provided path might be a system drive.</returns>
        private bool CheckIfSystemDrive(string drive)
        {
            string[] warnOnPatterns = new string[] { "WINDOWS\\system32", "WINNT\\system32", "Users", 
                "Documents and Settings"};

            foreach (string pattern in warnOnPatterns)
            {
                DirectoryInfo di = new DirectoryInfo(drive + "\\" + pattern);
                if (di.Exists)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Check if device drive may be iTunes library folder. This could happen in case 
        /// the user misconfigure the device and this check should help prevent loss of data.
        /// </summary>
        /// <param name="drive"></param>
        /// <returns></returns>
        public bool CheckIfiTunesLibrary(string drive)
        {
            string libraryPath = itunes.LibraryXMLPath;

            l.Debug("LibraryXMLPath: " + libraryPath);

            if (libraryPath == null)
            {
                return false;
            }

            if (libraryPath.StartsWith("\\\\"))  // network drive
            {
                libraryPath = libraryPath.Substring(2);
            }
            else
            {
                libraryPath = libraryPath.Substring(3);
            }

            FileInfo fi = new FileInfo(drive + "\\" + libraryPath);
            return fi.Exists;

        }

        public ITunesAppFactory ITunesAppFactory
        {

            set { this.itunesAppFactory = value; }
            get { return this.itunesAppFactory; }

        }

    }
}