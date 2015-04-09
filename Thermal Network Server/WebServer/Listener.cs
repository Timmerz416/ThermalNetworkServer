using System;
using Microsoft.SPOT;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Threading;

namespace ThermalNetworkServer {

	public delegate void RequestReceivedHandler(Socket client, RequestArgs request);

	public class SocketListener : IDisposable {
		// Local constants
		const int MAX_REQUEST_SIZE = 1024;

		// Members
		readonly int _portNumber;
		private Socket _listeningSocket = null;
		private IPEndPoint _client;

		// Events
		public event RequestReceivedHandler thermoStatusChanged;
		public event RequestReceivedHandler programOverrideRequested;
		public event RequestReceivedHandler thermoRuleChanged;
		public event RequestReceivedHandler dataRequested;
		public event RequestReceivedHandler timeRequestReceived;

		// Constructor
		public SocketListener(int PortNumber) {
			// Setup the socket and initialize it for listening
			_portNumber = PortNumber;
			_listeningSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
			_listeningSocket.Bind(new IPEndPoint(IPAddress.Any, _portNumber));
			_listeningSocket.Listen(10);

			// Listen for connections in another thread
			new Thread(StartListening).Start();
		}

		// Destructor
		~SocketListener() {
			Dispose();
		}

		// Parmeters
		public IPEndPoint ClientIP {
			get { return _client; }
		}

		// Listening thread
		public void StartListening() {
			// Infinite loop looking for connections
			while(true) {
				using(Socket clientSocket = _listeningSocket.Accept()) {
					// Get the client IP
					_client = clientSocket.RemoteEndPoint as IPEndPoint;
					Debug.Print("Received request from " + _client.ToString());

					// Determine the size of the transmission
					int availableBytes = clientSocket.Available;
					int bytesReceived = (availableBytes > MAX_REQUEST_SIZE ? MAX_REQUEST_SIZE : availableBytes);
					Debug.Print(DateTime.Now.ToString() + " " + availableBytes.ToString() + " request bytes available; " + bytesReceived + " bytes to try and receive.");

					// Process the request
					if(bytesReceived > 2) {
						byte[] buffer = new byte[bytesReceived];
						int readByteCount = clientSocket.Receive(buffer, bytesReceived, SocketFlags.None);
						Debug.Print("Read " + readByteCount + " bytes from the client socket.");

						// Get the first two characters, and check the codes
						string code = new string(Encoding.UTF8.GetChars(buffer, 0, 2));
						char[] cmd = Encoding.UTF8.GetChars(buffer);
						if(code == "TS") {	// Thermo status command
							if(thermoStatusChanged != null) thermoStatusChanged(clientSocket, new ThermoStatusArgs(cmd));
						} else if(code == "PO") {	// Program override command
							if(programOverrideRequested != null) programOverrideRequested(clientSocket, new ProgramOverrideArgs(cmd));
						} else if(code == "TR") {	// Thermo rule command
							if(thermoRuleChanged != null) thermoRuleChanged(clientSocket, new RuleChangeArgs(cmd));
						} else if(code == "DR") {	// Data request
							if(dataRequested != null) dataRequested(clientSocket, new DataRequestArgs(cmd));
						} else if(code == "CR") {	// Time request
							if(timeRequestReceived != null) timeRequestReceived(clientSocket, new TimeRequestArgs(cmd));
						}
					} else Debug.Print("Number of identified bytes is less than needed for this hardware.");
				}
				Thread.Sleep(10);	// Provide some delay to help prevent lock-ups
			}
		}

		#region IDisposable Members
		/// <summary>
		/// Closes the listening socket
		/// </summary>
		public void Dispose() {
			if(_listeningSocket != null) _listeningSocket.Close();
		}
		#endregion
	}
}
