using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

using Crestron.SimplSharp;
using Crestron.SimplSharp.CrestronSockets;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PepperDash.Core
{
    public static class UdpManager
    {
        public static Dictionary<int, SharedUdpServer> UdpServers = new Dictionary<int, SharedUdpServer>();
        public static CMutex ServerCreationMutex = new CMutex();

        public static SharedUdpServer GetServerForPort(int port, int bufferSize)
        {
            ServerCreationMutex.WaitForMutex();
            try
            {
                if (UdpServers.ContainsKey(port))
                {
                    return UdpServers[port];
                }
                else
                {
                    var server = new SharedUdpServer(port, bufferSize);
                    UdpServers.Add(port, server);
                    return server;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError(Debug.ErrorLogLevel.Error, string.Format("Exception creating UDP Server at port {0}: {1}", port, ex.Message));
                return null;
            }
            finally
            {
                ServerCreationMutex.ReleaseMutex();
            }
        }
    }

    public class SharedUdpServerDevice : Device, ISocketStatusWithStreamDebugging
    {
        public CommunicationStreamDebugging StreamDebugging { get; private set; }

        /// <summary>
        /// 
        /// </summary>
        public event EventHandler<GenericCommMethodReceiveBytesArgs> BytesReceived;

        /// <summary>
        /// 
        /// </summary>
        public event EventHandler<GenericCommMethodReceiveTextArgs> TextReceived;

        /// <summary>
        /// 
        /// </summary>
        public event EventHandler<GenericSocketStatusChageEventArgs> ConnectionChange;

        private SharedUdpServer Server;
        private string _address;
        private int _port;

        /// <summary>
        /// 
        /// </summary>
        public SocketStatus ClientStatus
        {
            get
            {
                return Server.ServerStatus;
            }
        }

        /// <summary>
        /// Indicates that the UDP Server is enabled
        /// </summary>
        public bool IsConnected
        {
            get
            {
                return Server.IsConnected;
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="key"></param>
        /// <param name="address"></param>
        /// <param name="port"></param>
        /// <param name="bufferSize"></param>
        public SharedUdpServerDevice(string key, string address, int port, int bufferSize)
            : base(key)
        {
            StreamDebugging = new CommunicationStreamDebugging(key);
            _address = address;
            _port = port;
            Server = UdpManager.GetServerForPort(port, bufferSize);
            Server.ServerDevices.Add(address, this);
        }


        /// <summary>
        /// Enables the UDP Server
        /// </summary>
        public void Connect()
        {
            if (Server != null)
            {
                Server.Connect();
            }
        }

        /// <summary>
        /// Disabled the UDP Server
        /// </summary>
        public void Disconnect()
        {
            if (Server != null)
                Server.Disconnect();
        }

        public void ReceiveData(byte[] bytes)
        {
            var str = Encoding.GetEncoding(28591).GetString(bytes, 0, bytes.Length);
            var bytesHandler = BytesReceived;
            if (bytesHandler != null)
            {
                if (StreamDebugging.RxStreamDebuggingIsEnabled)
                {
                    Debug.Console(0, this, "Received {1} bytes: '{0}'", ComTextHelper.GetEscapedText(bytes), bytes.Length);
                }
                bytesHandler(this, new GenericCommMethodReceiveBytesArgs(bytes));
            }
            var textHandler = TextReceived;
            if (textHandler != null)
            {
                if (StreamDebugging.RxStreamDebuggingIsEnabled)
                    Debug.Console(0, this, "Received {1} characters of text: '{0}'", ComTextHelper.GetDebugText(str), str.Length);
                textHandler(this, new GenericCommMethodReceiveTextArgs(str));
            }
        }


        /// <summary>
        /// General send method
        /// </summary>
        /// <param name="text"></param>
        public void SendText(string text)
        {
            var bytes = Encoding.GetEncoding(28591).GetBytes(text);

            if (Server != null)
            {
                if (StreamDebugging.TxStreamDebuggingIsEnabled)
                    Debug.Console(0, this, "Sending {0} characters of text: '{1}'", text.Length, ComTextHelper.GetDebugText(text));

                Server.SendData(bytes, bytes.Length, _address, _port);
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="bytes"></param>
        public void SendBytes(byte[] bytes)
        {
            if (StreamDebugging.TxStreamDebuggingIsEnabled)
                Debug.Console(0, this, "Sending {0} bytes: '{1}'", bytes.Length, ComTextHelper.GetEscapedText(bytes));

            if (Server != null)
                Server.SendData(bytes, bytes.Length, _address, _port);
        }
    }

    public class SharedUdpServer
    {
        public Dictionary<string, SharedUdpServerDevice> ServerDevices = new Dictionary<string, SharedUdpServerDevice>();
        
        /// <summary>
        /// 
        /// </summary>
        public SocketStatus ServerStatus
        {
            get
            {
                return Server.ServerStatus;
            }
        }

        /// <summary>
        /// Port on server
        /// </summary>
        public int Port { get; set; }

        private bool isConnected;
        /// <summary>
        /// Indicates that the UDP Server is enabled
        /// </summary>
        public bool IsConnected
        {
            get
            {
                return isConnected;
            }
        }

        /// <summary>
        /// Defaults to 2000
        /// </summary>
        public int BufferSize { get; set; }

        private UDPServer Server;
       
        /// <summary>
        /// 
        /// </summary>
        /// <param name="port"></param>
        /// <param name="bufferSize"></param>
        public SharedUdpServer(int port, int bufferSize)
        {             
            Port = port;
            BufferSize = bufferSize;

            if (Port < 1 || Port > 65535 || UdpManager.UdpServers.ContainsKey(port))
            {
                {
                    Debug.Console(1, Debug.ErrorLogLevel.Warning, "SharedUdpServer: Invalid port {0}", Port);
                    return;
                }
            }

            Server = new UDPServer();
            Server.RemotePortNumber = port;
            Server.EthernetAdapterToBindTo = EthernetAdapterType.EthernetLANAdapter;

            CrestronEnvironment.ProgramStatusEventHandler += new ProgramStatusEventHandler(CrestronEnvironment_ProgramStatusEventHandler);
            CrestronEnvironment.EthernetEventHandler += new EthernetEventHandler(CrestronEnvironment_EthernetEventHandler);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="ethernetEventArgs"></param>
        void CrestronEnvironment_EthernetEventHandler(EthernetEventArgs ethernetEventArgs)
        {
            // Re-enable the server if the link comes back up and the status should be connected
            if (ethernetEventArgs.EthernetEventType == eEthernetEventType.LinkUp)
            {
                Server.HandleLinkUp();
                Connect();
            }
            else if (ethernetEventArgs.EthernetEventType == eEthernetEventType.LinkDown)
            {
                Server.HandleLinkLoss();
                isConnected = false;
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="programEventType"></param>
        void CrestronEnvironment_ProgramStatusEventHandler(eProgramStatusEventType programEventType)
        {
            if (programEventType != eProgramStatusEventType.Stopping) 
                return;

            Debug.Console(1, "Program stopping. Disabling UDP Server");
            Disconnect();
        }

        /// <summary>
        /// Enables the UDP Server
        /// </summary>
        public void Connect()
        {
            if (Server == null || IsConnected)
            {
                return;
            }
            
            var status = Server.EnableUDPServer("0.0.0.0", Port, Port);
            Debug.Console(2, "UDP SocketErrorCode: {0}", status);

            if (status == SocketErrorCodes.SOCKET_OK)
            {
                isConnected = true;
                // Start receiving data
                Server.ReceiveDataAsync(Receive);
            }
        }

        /// <summary>
        /// Disabled the UDP Server
        /// </summary>
        public void Disconnect()
        {
            if (Server != null)
            {
                isConnected = false;
                Server.DisableUDPServer();
            }
        }


        /// <summary>
        /// Recursive method to receive data
        /// </summary>
        /// <param name="server"></param>
        /// <param name="numBytes"></param>
        void Receive(UDPServer server, int numBytes)
        {
            try
            {                
                var sourceIp = Server.IPAddressLastMessageReceivedFrom;
                var sourcePort = Server.IPPortLastMessageReceivedFrom;
                Debug.Console(1, "GenericUdpServer receive port: {0} address: {1}", sourcePort, sourceIp);

                if (numBytes <= 0)
                    return;
                var bytes = server.IncomingDataBuffer.Take(numBytes).ToArray();

                if (ServerDevices.ContainsKey(sourceIp))
                {
                    var device = ServerDevices[sourceIp];
                    device.ReceiveData(bytes);
                }
            }
            catch (Exception ex)
            {
                Debug.Console(0, "GenericUdpServer Receive error: {0}{1}", ex.Message, ex.StackTrace);
            }
            finally
            {
                server.ReceiveDataAsync(Receive);
            }
        }

        /// <summary>
        /// Method to send data
        /// </summary>
        /// <param name="bytes"></param>
        /// <param name="length"></param>
        /// <param name="address"></param>
        /// <param name="port"></param>
        public void SendData(byte[] bytes, int length, string address, int port)
        {           
            var status = Server.SendData(bytes, bytes.Length, address, port);
            Debug.Console(1, "UDP SendData SocketErrorCode: {0}, address: {1}, port: {2}", status.ToString(), address, port.ToString());
        }
    }
}