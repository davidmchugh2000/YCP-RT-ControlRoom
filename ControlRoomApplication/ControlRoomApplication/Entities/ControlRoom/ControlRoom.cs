﻿using System.Collections.Generic;
using ControlRoomApplication.Controllers;
using System.Net;

namespace ControlRoomApplication.Entities
{
    public class ControlRoom
    {
        public List<RadioTelescopeControllerManagementThread> RTControllerManagementThreads { get; }
        public AbstractWeatherStation WeatherStation { get; }
        private RemoteListener mobileControlServer { get; }

        public List<RadioTelescopeController> RadioTelescopeControllers
        {
            get
            {
                List<RadioTelescopeController> rtControllers = new List<RadioTelescopeController>();

                foreach (RadioTelescopeControllerManagementThread rtmt in RTControllerManagementThreads)
                {
                    rtControllers.Add(rtmt.RTController);
                }

                return rtControllers;
            }
        }

        public List<RadioTelescope> RadioTelescopes
        {
            get
            {
                List<RadioTelescope> RTList = new List<RadioTelescope>();

                foreach (RadioTelescopeControllerManagementThread rtmt in RTControllerManagementThreads)
                {
                    RTList.Add(rtmt.RTController.RadioTelescope);
                }

                return RTList;
            }
        }

        public ControlRoom(AbstractWeatherStation weatherStation)
        {
            RTControllerManagementThreads = new List<RadioTelescopeControllerManagementThread>();
            WeatherStation = weatherStation;
            mobileControlServer = new RemoteListener(25565, IPAddress.Parse("192.168.1.178"));
        }
    }
}