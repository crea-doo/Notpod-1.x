using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using Notpod.Configuration12;
using WindowsPortableDevicesLib.Domain;

namespace Notpod
{
    /// <summary>
    /// Event arguments class for Connected Device Managers.
    /// </summary>
    public class CDMEventArgs : EventArgs
    {
        private WindowsPortableDevice portableDevice;
        private Device device;

        /// <summary>
        /// Create a new instance of CDMEventArgs.
        /// </summary>
        public CDMEventArgs()
            : this(null, null)
        {
        }

        /// <summary>
        /// Create a new instance of CDMEventArgs.
        /// </summary>
        /// <param name="drive">Information on the drive for this event.</param>
        /// <param name="device">Informatioon on the device for this event.</param>
        public CDMEventArgs(WindowsPortableDevice drive, Device device)
            : base()
        {
            this.device = device;
            this.portableDevice = drive;
        }


        /// <summary>
        /// Accessor for the DriveInfo describing the drive involved in the event.
        /// </summary>
        public WindowsPortableDevice PortableDevice
        {
            get { return portableDevice; }
            set { portableDevice = value; }
        }

        /// <summary>
        /// Accessor for the device involved in the event.
        /// </summary>
        public Device Device
        {
            get { return device; }
            set { device = value; }

        }

    }
}
