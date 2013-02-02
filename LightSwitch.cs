using MLS.HA.DeviceController.Common.ServicePlugin;
using MLS.HA.DeviceController.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net.Sockets;
using System.Net;
using System.Threading;
using System.Security.Cryptography;
using System.Data;
using System.ComponentModel;
using MLS.HA.DeviceController.Common.HaControllerInterface;
using MLS.HA.DeviceController.Common.Device;

namespace LightSwitch
{
    public class LightSwitch : ServicePlugin, IServicePlugin {

        private const string SETTING_PORT = "LS_PORT_CF";
        private const string SETTING_MAX_CON = "LS_MAXCONN_CF";
        private const string SETTING_VERBOSE = "LS_VERBOSE_CF";
        private const string SETTING_PASSWORD = "LS_PASSWORD_CF";
        private const string SETTING_SORTLIST = "LS_SORTLIST_CF";
        private const string SETTING_PUBLISH_ZERO = "LS_PUBLISHZEROCFG_CF";

        private Socket lightSwitchSocket;
        private readonly List<Socket> lightSwitchClients = new List<Socket>();
        public AsyncCallback pfnWorkerCallBack;
        private int m_cookie = new Random().Next(65536);
        public volatile bool isActive = false;
        //private NetService netservice = null;
        private bool _verbose = false;
        private bool _useBonjour = false;
        private bool _sort_list = true;
        private int _port = 6005;
        private int _max_conn = 50;
        private bool isReady;
        private string applicationNameAndVersion = "light_switch_plugin";

        public void initialize() {
            defineSetting(SETTING_PORT, (1337).ToString());
            defineSetting(SETTING_MAX_CON, (200).ToString());
            defineSetting(SETTING_VERBOSE, false.ToString());
            defineSetting(SETTING_PASSWORD, "1234");
            defineSetting(SETTING_SORTLIST, true.ToString());
            defineSetting(SETTING_PUBLISH_ZERO, false.ToString());

            bool.TryParse(getSetting(SETTING_VERBOSE), out _verbose);
            bool.TryParse(getSetting(SETTING_PUBLISH_ZERO), out _useBonjour);
            bool.TryParse(getSetting(SETTING_SORTLIST), out _sort_list);
            int.TryParse(getSetting(SETTING_PORT), out _port);
            int.TryParse(getSetting(SETTING_MAX_CON), out _max_conn);
        }

        public void startPlugin() {
            openLightSwitchSocket();
        }

        public void stopPlugin() {
            closeLightSwitchSocket();
        }

        public override void deviceChangedLevel(Guid deviceId, ChangeStateType changeType, int oldLevel, int newLevel) {
            BackgroundWorker bw = new BackgroundWorker();
            bw.DoWork += (s, a) => {
                var device = getDevice(deviceId);

                string updateString = deviceToString(device);
                if (!string.IsNullOrEmpty(updateString)) {
                    //broadcastMessage("UPDATE~" + updateString + Environment.NewLine);
                    broadcastMessage(updateString + Environment.NewLine);
                    //broadcastMessage("ENDLIST" + Environment.NewLine);
                }

                string device_name = string.Empty;
                device_name = device.name;
                //broadcastMessage("MSG~" + "'" + device_name + "' " + deviceId + " changed to " + newLevel + Environment.NewLine);
                broadcastMessage(device_name + "~" + deviceId + "~" + newLevel + Environment.NewLine);

            };
            bw.RunWorkerAsync();
        }

        /// <summary>
        /// Starts listening for LightSwitch clients. 
        /// </summary>
        public void openLightSwitchSocket() {
            writeLog("Opening light switch socket");
            if (lightSwitchSocket == null || !isActive) {
                try {
                    writeLog("Light switch using port " + _port);
                    isActive = true;
                    lightSwitchSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    lightSwitchSocket.Bind(new IPEndPoint(IPAddress.Any, _port));
                    lightSwitchSocket.Listen(_max_conn);
                    lightSwitchSocket.BeginAccept(new AsyncCallback(onLightSwitchClientConnect), null);                    
                    isReady = true;

                } catch (SocketException e) {
                    writeLog("Light switch socket failed to open", e);
                }
            }
        }

        public void closeLightSwitchSocket() {
            if (lightSwitchSocket != null && isActive) {
                lightSwitchSocket.Close();

                foreach (Socket client in lightSwitchClients) {
                    if (client.Connected) {
                        client.Close();
                    }
                }
                //log.Info("LightSwitch server stopped");
                isActive = false;
                isReady = false;
            }
        }

        /// <summary>
        /// Welcomes client and opens individualized socket for them to clear main socket.
        /// </summary>
        /// <param name="asyn"></param>
        private void onLightSwitchClientConnect(IAsyncResult asyn) {
            try {
                //accept and create new socket
                Socket lightSwitchClientsSocket = lightSwitchSocket.EndAccept(asyn);

                lock (lightSwitchClients) {
                    lightSwitchClients.Add(lightSwitchClientsSocket);
                }

                //log.Info("Connection Attempt from: " + LightSwitchClientsSocket.RemoteEndPoint.ToString());

                // Send a welcome message to client                
                string msg = "LightSwitch zVirtualScenes Plug-in (Active Connections " + lightSwitchClients.Count + ")" + Environment.NewLine;
                sendMsgToLightSwitchClient(msg, lightSwitchClients.Count);

                // Let the worker Socket do the further processing for the just connected client
                waitForData(lightSwitchClientsSocket, lightSwitchClients.Count, false);

                // Since the main Socket is now free, it can go back and wait for other clients who are attempting to connect
                lightSwitchSocket.BeginAccept(new AsyncCallback(onLightSwitchClientConnect), null);
            } catch (ObjectDisposedException) {
            } catch (SocketException e) {
                //log.Error("Socket Exception: " + e);
            } catch (Exception) { }
        }

        /// <summary>
        /// Waits for client data and handles it when recieved
        /// </summary>
        /// <param name="soc"></param>
        /// <param name="clientNumber"></param>
        /// <param name="verified"></param>
        public void waitForData(System.Net.Sockets.Socket soc, int clientNumber, bool verified) {
            try {
                if (pfnWorkerCallBack == null) {
                    // Specify the call back function which is to be invoked when there is any write activity by the connected client
                    pfnWorkerCallBack = new AsyncCallback(onDataReceived);
                }
                SocketPacket theSocPkt = new SocketPacket(soc, clientNumber, verified);
                soc.BeginReceive(theSocPkt.dataBuffer, 0, theSocPkt.dataBuffer.Length, SocketFlags.None, pfnWorkerCallBack, theSocPkt);
            } catch (SocketException e) {
                //log.Error("Socket Exception: " + e);
            }
        }

        /// <summary>
        /// This the call back function which will be invoked when the socket detects any client writing of data on the stream
        /// </summary>
        /// <param name="asyn">Object containing Socket Packet</param>
        public void onDataReceived(IAsyncResult asyn) {
            SocketPacket socketData = (SocketPacket)asyn.AsyncState;
            Socket lightSwitchClientSocket = (Socket)socketData.m_currentSocket;

            if (!lightSwitchClientSocket.Connected)
                return;

            try {

                // Complete the BeginReceive() asynchronous call by EndReceive() method which will return the number of characters written to the stream  by the client
                int iRx = lightSwitchClientSocket.EndReceive(asyn);

                //this socket was closed
                if (iRx == 0) {
                    disconnectClientSocket(socketData);
                    return;
                }

                if (iRx > 2) {
                    char[] chars = new char[iRx + 1];
                    // Extract the characters as a buffer
                    System.Text.Decoder d = System.Text.Encoding.UTF8.GetDecoder();
                    int charLen = d.GetChars(socketData.dataBuffer, 0, iRx, chars, 0);
                    string data = new string(chars);

                    //if (_verbose)
                    //log.Info("Received [" + LightSwitchClientSocket.RemoteEndPoint.ToString() + "] " + data);

                    string[] commands = data.Split('\n');

                    string version = "VER~" + applicationNameAndVersion;

                    foreach (string command in commands) {
                        if (command.Length > 0) {
                            if (command.Length <= 2)
                                continue;

                            string cmd = command.TrimEnd(Environment.NewLine.ToCharArray());

                            if (cmd.ToUpper().StartsWith("IPHONE")) {
                                //Send salt to phone
                                socketData.m_verified = false;
                                sendMessagetoClientsSocket(lightSwitchClientSocket, "COOKIE~" + Convert.ToString(m_cookie) + Environment.NewLine);
                            }

                            if (!socketData.m_verified) {
                                //If not verified attept to verify                            
                                if (cmd.ToUpper().StartsWith("PASSWORD")) {
                                    string[] values = cmd.Split('~');
                                    string inputPassword = values[1];
                                    string hashedPassword = encodePassword(Convert.ToString(m_cookie) + ":" + getSetting(SETTING_PASSWORD));
                                    //hashedPassword = "638831F3AF6F32B25D6F1C961CBBA393"

                                    if (inputPassword.StartsWith(hashedPassword)) {
                                        socketData.m_verified = true;
                                        sendMessagetoClientsSocket(lightSwitchClientSocket, version + Environment.NewLine);
                                        //log.Info("[" + LightSwitchClientSocket.RemoteEndPoint.ToString() + "] User Authenticated.");
                                    } else {
                                        socketData.m_verified = false;
                                        throw new Exception("Passwords do not match");
                                    }
                                }
                            } else {
                                //CLIENT IS VERIFIED
                                if (cmd.ToUpper().StartsWith("VERSION"))
                                    sendMessagetoClientsSocket(lightSwitchClientSocket, version + Environment.NewLine);

                                else if (cmd.ToUpper().StartsWith("SERVER"))
                                    sendMessagetoClientsSocket(lightSwitchClientSocket, version + Environment.NewLine);

                                else if (cmd.ToUpper().StartsWith("TERMINATE"))
                                    throw new Exception("Terminating client socket...");

                                else if (cmd.ToUpper().StartsWith("ALIST")) {
                                    //DEVICES, SCENES AND ZONES.

                                    //log.Info("[" + LightSwitchClientSocket.RemoteEndPoint.ToString() + "] User requested device list.");

                                    sendDeviceList(lightSwitchClientSocket);
                                    sendSceneList(lightSwitchClientSocket);
                                    sendZoneList(lightSwitchClientSocket);

                                    sendMessagetoClientsSocket(lightSwitchClientSocket, "ENDLIST" + Environment.NewLine);
                                } else if (cmd.ToUpper().StartsWith("LIST")) {
                                    //DEVICES
                                    //log.Info("[" + LightSwitchClientSocket.RemoteEndPoint.ToString() + "] User requested device list.");

                                    sendDeviceList(lightSwitchClientSocket);
                                    sendMessagetoClientsSocket(lightSwitchClientSocket, "ENDLIST" + Environment.NewLine);
                                } else if (cmd.ToUpper().StartsWith("SLIST")) {
                                    //SCENES
                                    sendSceneList(lightSwitchClientSocket);

                                    sendMessagetoClientsSocket(lightSwitchClientSocket, "ENDLIST" + Environment.NewLine);
                                } else if (cmd.ToUpper().StartsWith("ZLIST")) {
                                    //ZONES
                                    //log.Info("[" + LightSwitchClientSocket.RemoteEndPoint.ToString() + "] User requested zone/group list.");

                                    sendZoneList(lightSwitchClientSocket);
                                    sendMessagetoClientsSocket(lightSwitchClientSocket, "ENDLIST" + Environment.NewLine);
                                } else if (cmd.ToUpper().StartsWith("DEVICE")) {
                                    string[] values = cmd.Split('~');
                                    //NOTIFY ALL CLIENTS
                                    ExecuteZVSCommand(Guid.Parse(values[1]), Convert.ToByte(values[2]), lightSwitchClientSocket);
                                } else if (cmd.ToUpper().StartsWith("SCENE")) {
                                    string[] values = cmd.Split('~');
                                    //NOTIFY ALL CLIENTS
                                    ExecuteZVSCommand(Guid.Parse(values[1]), lightSwitchClientSocket);
                                } else if (cmd.ToUpper().StartsWith("ZONE")) {
                                    //string[] values = cmd.Split('~');
                                    //if (values.Length > 1)
                                    //{
                                    //    int groupId = int.TryParse(values[1], out groupId) ? groupId : 0;
                                    //    string cmdUniqId = (values[2].Equals("255") ? "GROUP_ON" : "GROUP_OFF");

                                    //    Group g = context.Groups.FirstOrDefault(o => o.Id == groupId);
                                    //    if (g != null)
                                    //    {
                                    //        BuiltinCommand zvs_cmd = context.BuiltinCommands.FirstOrDefault(c => c.UniqueIdentifier == cmdUniqId);
                                    //        if (zvs_cmd != null)
                                    //        {
                                    //            string result = string.Format("[{0}] Ran {1} on group '{2}'", LightSwitchClientSocket.RemoteEndPoint.ToString(), zvs_cmd.Name, g.Name);
                                    //            log.Info(result);
                                    //            BroadcastMessage("MSG~" + result + Environment.NewLine);

                                    //            CommandProcessor cp = new CommandProcessor(Core);
                                    //            cp.RunCommandAsync(zvs_cmd.Id, g.Id.ToString());
                                    //        }
                                    //    }

                                    //}

                                } else if (cmd.ToUpper().StartsWith("THERMMODE")) {
                                    string[] values = cmd.Split('~');
                                    //NOTIFY ALL CLIENTS
                                    ExecuteZVSThermostatCommand(Guid.Parse(values[1]), Convert.ToByte(values[2]), lightSwitchClientSocket);
                                } else if (cmd.ToUpper().StartsWith("THERMTEMP")) {
                                    string[] values = cmd.Split('~');
                                    //NOTIFY ALL CLIENTS
                                    ExecuteZVSThermostatCommand(Guid.Parse(values[1]), Convert.ToByte(values[2]), Convert.ToInt32(values[3]), lightSwitchClientSocket);
                                } else {
                                    throw new Exception("Terminating Due To Unknown Socket Command: " + data);
                                }
                            }
                        }
                    }

                }
            } catch (ObjectDisposedException) {
                writeLog("OnDataReceived - Socket has been closed");
            } catch (SocketException se) {
                if (se.ErrorCode == 10054) {
                    // Error code for Connection reset by peer
                    writeLog("Client " + socketData.m_clientNumber + " Disconnected.");
                } else {
                    writeLog("SocketException", se);
                }

                disconnectClientSocket(socketData);
            } catch (Exception e) {
                //log.Error("[" + LightSwitchClientSocket.RemoteEndPoint.ToString() + "] Server Exception: " + e);

                //SEND ERROR TO CLIENT
                sendMessagetoClientsSocket(lightSwitchClientSocket, "ERR~" + e.Message + Environment.NewLine);

                Thread.Sleep(3000);
                disconnectClientSocket(socketData);
            }

            if (socketData.m_currentSocket.Connected) {
                // Continue the waiting for data on the Socket
                waitForData(socketData.m_currentSocket, socketData.m_clientNumber, socketData.m_verified);
            }
        }

        /// <summary>
        /// Sends the scene list to the client.
        /// </summary>
        /// <param name="lightSwitchClientSocket"></param>
        private void sendSceneList(Socket lightSwitchClientSocket) {

            foreach (var scene in getScenes()) {
                //bool show = false;
                //bool.TryParse(ScenePropertyValue.GetPropertyValue(context, scene, "SHOWSCENEINLSLIST"), out show);

                //if (show)
                sendMessagetoClientsSocket(lightSwitchClientSocket, "SCENE~" + scene.sceneName + "~" + scene.sceneId + Environment.NewLine);
            }

        }

        private void sendZoneList(Socket LightSwitchClientSocket) {
            //using (zvsContext context = new zvsContext())
            //{
            //    foreach (Group g in context.Groups)
            //    {
            //        SendMessagetoClientsSocket(LightSwitchClientSocket, "ZONE~" + g.Name + "~" + g.Id + Environment.NewLine);
            //    }
            //}
        }

        private void sendDeviceList(Socket LightSwitchClientSocket) {
            List<string> lS_devices = new List<string>();

            //Get Devices
            foreach (var d in getDevices()) {
                bool show = true;
                //bool.TryParse(DevicePropertyValue.GetPropertyValue(d, "SHOWINLSLIST"), out show);

                if (show) {
                    string device_str = deviceToString(d);
                    if (!string.IsNullOrEmpty(device_str)) {
                        lS_devices.Add(device_str);
                    }
                }
            }

            if (_sort_list) {
                lS_devices.Sort();
            }

            // Send to Client
            foreach (string d_str in lS_devices) {
                //sendMessagetoClientsSocket(LightSwitchClientSocket, "DEVICE~" + d_str + Environment.NewLine);
                sendMessagetoClientsSocket(LightSwitchClientSocket, d_str + Environment.NewLine);
            }


        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="d"></param>
        /// <returns></returns>
        private string deviceToString(HaDevice d) {
            switch (d.deviceType) {
                case DeviceType.PowerOutlet:
                case DeviceType.StandardSwitch:
                    return d.deviceName + "~" + d.deviceId + "~" + (d.level > 0 ? "255" : "0") + "~" + "BinarySwitch";
                case DeviceType.DimmerSwitch:
                    return d.deviceName + "~" + d.deviceId + "~" + (int)d.level + "~" + "MultiLevelSwitch";
                case DeviceType.Thermostat:
                    return d.deviceName + "~" + d.deviceId + "~" + (int)d.level + "~" + "Thermostat";
                case DeviceType.MotionSensor:
                case DeviceType.MultiLevelSensor:
                case DeviceType.LevelDisplayer:
                    return d.deviceName + "~" + d.deviceId + "~" + (int)d.level + "~" + "Sensor";
                default:
                    return d.deviceName + "~" + d.deviceId + "~" + (int)d.level + "~" + "Sensor";
            }

        }

        //Light Switch Socket Format 
        //byData = System.Text.Encoding.UTF8.GetBytes("DEVICE~Bedroom Lights~0~60~MultiLevelSceneSwitch" + Environment.NewLine);
        //workerSocket.Send(byData);
        //byData = System.Text.Encoding.UTF8.GetBytes("DEVICE~Garage Light~1~0~BinarySwitch" + Environment.NewLine);
        //workerSocket.Send(byData);
        //byData = System.Text.Encoding.UTF8.GetBytes("DEVICE~Thermostat~3~75~Thermostat" + Environment.NewLine);
        //workerSocket.Send(byData);
        //byData = System.Text.Encoding.UTF8.GetBytes("DEVICE~Electric Blinds~4~100~WindowCovering" + Environment.NewLine);
        //workerSocket.Send(byData);
        //byData = System.Text.Encoding.UTF8.GetBytes("DEVICE~Motion Detector~5~0~Sensor" + Environment.NewLine);
        //workerSocket.Send(byData);
        //byData = System.Text.Encoding.UTF8.GetBytes("DEVICE~House (AWAY MODE)~6~0~Status" + Environment.NewLine);
        //workerSocket.Send(byData);

        /// <summary>
        /// Set levels for devices when a Lightswitch action level string is recieved.
        /// </summary>
        /// <param name="Node"></param>
        /// <param name="Level"></param>
        /// <param name="Client"></param>
        /// <returns></returns>
        private void ExecuteZVSCommand(Guid device_id, byte Level, Socket Client) {
            var d = getDevice(device_id);

            if (d != null) {
                if (Level == 0) {
                    setPower(device_id, false);
                } else {
                    setLevel(device_id, Level);
                }
                return;
            }
            broadcastMessage("ERR~Error setting device # " + device_id + ". Try Again");

        }

        /// <summary>
        /// Runs a zVirtualScene Scene
        /// </summary>
        /// <param name="SceneID">Scene ID</param>
        /// <param name="Client">Clients Socket.</param>
        private void ExecuteZVSCommand(Guid SceneID, Socket Client) {
            activateScene(SceneID);
            //BuiltinCommand cmd = context.BuiltinCommands.FirstOrDefault(c => c.UniqueIdentifier == "RUN_SCENE");
            //if (cmd != null)
            //{
            //    CommandProcessor cp = new CommandProcessor(Core);
            //    CommandProcessorResult args = await cp.RunCommandAsync(cmd.Id, SceneID.ToString());
            //    BroadcastMessage("MSG~" + args.Details + Environment.NewLine);
            //}
        }

        // TODO: Not supported by InControl
        //private bool ExecuteDynamicCMD(zvsContext context, Device d, string cmdUniqueId, string arg, Socket Client)
        //{
        //    DeviceCommand cmd = d.Commands.FirstOrDefault(c => c.UniqueIdentifier == cmdUniqueId);
        //    if (cmd != null)
        //    {
        //        string result = string.Format("[{0}] Executed command '{1}{2}' on '{3}'.", Client.RemoteEndPoint.ToString(), cmd.Name, string.IsNullOrEmpty(arg) ? arg : " to " + arg, d.Name);
        //        log.Info(result);
        //        CommandProcessor cp = new CommandProcessor(Core);
        //        cp.RunCommandAsync(cmd.Id, arg);
        //        return true;
        //    }
        //    return false;
        //}

        private Dictionary<string, string> ThermoTempCommandTranslations = new Dictionary<string, string>()
        {
            {"THINKSTICK2", "DYNAMIC_SP_R207_Heating1"},
            {"OPENZWAVE2", "DYNAMIC_CMD_HEATING 1"},
            {"THINKSTICK3", "DYNAMIC_SP_R207_Cooling1"},
            {"OPENZWAVE3", "DYNAMIC_CMD_COOLING 1"} 
        };

        private void ExecuteZVSThermostatCommand(Guid deviceID, byte Mode, int Temp, Socket Client) {
            //dm.setZWaveThermostatFanMode(deviceID, 

            switch (Mode) {
                case 0:
                    setZWaveThermostatSystemState(deviceID, ThermoSystemMode.Off);
                    break;
                case 1:
                    //dm.setZWaveThermostatSystemState(deviceID, MLS.Common.ThermoSystemMode.
                    break;
                case 2:
                    setZWaveThermostatSetPoint(deviceID, "Heating1", Temp);
                    setZWaveThermostatSystemState(deviceID, ThermoSystemMode.Heat);
                    break;
                case 3:
                    setZWaveThermostatSystemState(deviceID, ThermoSystemMode.Cool);
                    setZWaveThermostatSetPoint(deviceID, "Cooling1", Temp);
                    break;
                case 4:
                    setZWaveThermostatFanMode(deviceID, ThermoFanMode.On);
                    break;
                case 5:
                    setZWaveThermostatFanMode(deviceID, ThermoFanMode.Auto);
                    break;
            }

            broadcastMessage("ERR~Error setting device # " + deviceID + ". Try Again");
        }

        private class zvsCMD {
            public string CmdName;
            public string arg;
        }

        private void ExecuteZVSThermostatCommand(Guid deviceID, byte Mode, Socket Client) {
            broadcastMessage("ERR~Error setting device # " + deviceID + ". Try Again");
        }

        /// <summary>
        /// Sends a message to ALL connected clients.
        /// </summary>
        /// <param name="msg">the message to send</param>
        public void broadcastMessage(string msg) {
            if (msg.Length > 0) {
                // Convert the reply to byte array
                byte[] byData = System.Text.Encoding.UTF8.GetBytes(msg);

                foreach (Socket workerSocket in lightSwitchClients)
                    if (workerSocket != null && workerSocket.Connected) {
                        try {
                            workerSocket.Send(byData);
                        } catch (SocketException se) {
                            if (_verbose) {
                                writeLog("Error during BroadcastMessage", se);
                            }
                            return;
                        }
                    }

            }
        }

        /// <summary>
        /// Sends a message to ONE client by socket
        /// </summary>
        /// <param name="msg">the message to send</param>
        public void sendMessagetoClientsSocket(Socket lightSwitchClientSocket, string msg) {
            if (msg.Length > 0) {
                // Convert the reply to byte array
                byte[] byData = System.Text.Encoding.UTF8.GetBytes(msg);
                if (lightSwitchClientSocket != null && lightSwitchClientSocket.Connected) {
                    try {
                        lightSwitchClientSocket.Send(byData);
                    } catch (SocketException se) {
                        if (_verbose) {
                            writeLog("Error during BroadcastMessage", se);
                        }
                    }
                }

                if (_verbose) {
                    writeLog("SENT - " + msg);
                }

            }
        }

        /// <summary>
        /// Sends a message to ONE client by client number
        /// </summary>
        private void sendMsgToLightSwitchClient(string msg, int clientNumber) {

            // Convert the reply to byte array
            byte[] byData = System.Text.Encoding.UTF8.GetBytes(msg);
            Socket lightSwitchClientsSocket = (Socket)lightSwitchClients[clientNumber - 1];

            if (lightSwitchClientsSocket != null && lightSwitchClientsSocket.Connected) {
                try {
                    lightSwitchClientsSocket.Send(byData);
                } catch (SocketException se) {
                    if (_verbose) {
                        writeLog("Socket Exception: ", se);
                    }
                }
            }


            if (_verbose) {
                writeLog("SENT " + msg);
            }

        }

        private void disconnectClientSocket(SocketPacket socketData) {
            Socket lightSwitchClientsSocket = (Socket)socketData.m_currentSocket;
            try {
                // Remove the reference to the worker socket of the closed client so that this object will get garbage collected
                lock (lightSwitchClients) {
                    lightSwitchClients.Remove(lightSwitchClientsSocket);
                }

                lightSwitchClientsSocket.Close();
                lightSwitchClientsSocket = null;
            } catch (Exception e) {
                writeLog("Socket Disconnect: ", e);
            }
        }

        public string encodePassword(string originalPassword) {
            // Instantiate MD5CryptoServiceProvider, get bytes for original password and compute hash (encoded password)
            Byte[] originalBytes = ASCIIEncoding.Default.GetBytes(originalPassword);
            Byte[] encodedBytes = new MD5CryptoServiceProvider().ComputeHash(originalBytes);

            StringBuilder result = new StringBuilder();
            for (int i = 0; i < encodedBytes.Length; i++) {
                result.Append(encodedBytes[i].ToString("x2"));
            }

            return result.ToString().ToUpper();
        }


        #region ZeroConf/Bonjour

        //private void PublishZeroconf() {
        //    using (zvsContext context = new zvsContext()) {
        //        int port = 9909;
        //        int.TryParse(getSettingValue("PORT", context), out port);

        //        string domain = "";
        //        String type = "_lightswitch._tcp.";
        //        String name = "Lightswitch " + Environment.MachineName;
        //        netservice = new NetService(domain, type, name, port);
        //        netservice.AllowMultithreadedCallbacks = true;
        //        netservice.DidPublishService += new NetService.ServicePublished(publishService_DidPublishService);
        //        netservice.DidNotPublishService += new NetService.ServiceNotPublished(publishService_DidNotPublishService);

        //        /* HARDCODE TXT RECORD */
        //        System.Collections.Hashtable dict = new System.Collections.Hashtable();
        //        dict = new System.Collections.Hashtable();
        //        dict.Add("txtvers", "1");
        //        dict.Add("ServiceName", name);
        //        dict.Add("MachineName", Environment.MachineName);
        //        dict.Add("OS", Environment.OSVersion.ToString());
        //        dict.Add("IPAddress", "127.0.0.1");
        //        dict.Add("Version", Utils.ApplicationNameAndVersion);
        //        netservice.TXTRecordData = NetService.DataFromTXTRecordDictionary(dict);
        //        netservice.Publish();
        //    }

        //}

        //void publishService_DidPublishService(NetService service) {
        //    log.Info(String.Format("Published Service: domain({0}) type({1}) name({2})", service.Domain, service.Type, service.Name));
        //}

        //void publishService_DidNotPublishService(NetService service, DNSServiceException ex) {
        //    log.Error(ex.Message);
        //}

        #endregion
        //private void publishZeroConf() {
        //    if (_useBonjour) {
        //        try {
        //            if (netservice == null) {
        //                //PublishZeroconf();
        //            } else {
        //                netservice.Dispose();
        //                //PublishZeroconf();
        //            }

        //        } catch (Exception ex) {
        //            Logger.writeLog("Fatal Error with publishZeroConf", ex);
        //        }
        //    } else {
        //        if (netservice == null) {
        //            netservice.Dispose();
        //        }
        //    }
        //}
    }

    public class SocketPacket {
        // holds a reference to the socket
        public Socket m_currentSocket;

        // holds the client number for identification
        public int m_clientNumber;

        //Is a IViewer Client?
        public bool iViewerClient;

        // Buffer to store the data sent by the client
        public byte[] dataBuffer = new byte[1024];

        // flag whether this has passed authentication
        public bool m_verified = false;

        // Constructor which takes a Socket and a client number
        public SocketPacket(Socket socket, int clientNumber, bool verified) {
            m_currentSocket = socket;
            m_clientNumber = clientNumber;
            m_verified = verified;
            iViewerClient = false;
        }
    }
}
