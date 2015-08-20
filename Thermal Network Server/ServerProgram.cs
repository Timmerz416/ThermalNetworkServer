using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Microsoft.SPOT;
using Microsoft.SPOT.Hardware;
using SecretLabs.NETMF.Hardware;
using SecretLabs.NETMF.Hardware.Netduino;
using NETMF.OpenSource.XBee;
using NETMF.OpenSource.XBee.Api;
using NETMF.OpenSource.XBee.Api.Zigbee;
using Toolbox.NETMF;
using System.Collections;

namespace ThermalNetworkServer {

	public class Program {
		//=====================================================================
		// PORT SETUP
		//=====================================================================
		// Digital Ports
		private static OutputPort onboardLED = new OutputPort(Pins.ONBOARD_LED, false);		// Turn off the onboard led

		//=====================================================================
		// SOCKET COMMUNICATION SETUP
		//=====================================================================
		// Addresses
		private const string DB_ADDRESS = "192.168.2.53";	// The address to the server with the MySQL server
		private const int DB_PORT = 80;						// The port to send the webserver request to
		private const int LISTENING_PORT = 5267;			// The port that the microcontroller will listen for commands
		private const int SERVER_PORT = 6232;				// The listening port of the source commands

		//=====================================================================
		// XBEE SETUP
		//=====================================================================
		// XBee sensor codes
		private enum XBeePortData { Temperature, Luminosity, Pressure, Humidity, LuminosityLux, HeatingOn, ThermoOn, Power }

		// XBee command codes
		const byte CMD_THERMO_POWER = 1;
		const byte CMD_OVERRIDE		= 2;
		const byte CMD_RULE_CHANGE	= 3;
		const byte CMD_SENSOR_DATA	= 4;
		const byte CMD_TIME_REQUEST	= 5;
		const byte CMD_STATUS		= 6;

		// XBee subcommand codes
		const byte CMD_NACK			= 0;
		const byte CMD_ACK			= 1;
		const byte STATUS_OFF		= 2;
		const byte STATUS_ON		= 3;
		const byte STATUS_GET		= 4;
		const byte STATUS_ADD		= 5;
		const byte STATUS_DELETE	= 6;
		const byte STATUS_MOVE		= 7;
		const byte STATUS_UPDATE	= 8;

		// XBee connection members
		private static XBeeApi xBee;				// The object controlling the XBee interface
		private static bool xbeeConnected = false;	// A flat to indicate the status of the xbee connection (true = connected)

		//=====================================================================
		// NETWORKING MEMBERS/CONSTANTS
		//=====================================================================
		// Addresses
		private const string RELAY_ADDRESS = "00 13 A2 00 40 AE B9 7F";	// The address of the xbee radio controlling the relay
		private const string CONTROL_ADDRESS = "40aeba93";	// The short address of this controller radio (attached to this netduino plus) for DB identification
		private const string XBEE_PORT = "COM1";

		// Messaging
		private static bool awaitingResponse = false;	// Indicates whether the response from the relay has been received
		private static byte lastXBeeCommand = CMD_NACK;	// Tracks the last message sent for checking responses
		private static IPAddress requestIP = null;		// Tracks the network location where the request originated
		private static int requestPort = 0;				// Tracks the port the request was sent from

		//=====================================================================
		// SENSOR SETUP
		//=====================================================================
		// Sensors
		private static TMP102BusSensor tempSensor = new TMP102BusSensor();

		// Timing
		//private const int SENSOR_DELAY = 600000;	// The delay in microseconds between sensor readings
		private const int SENSOR_DELAY = 60000;	// Debug delay

		//=====================================================================
		// MAIN PROGRAM
		//=====================================================================
		public static void Main() {
			//-----------------------------------------------------------------
			// Initialize the socket communications
			//-----------------------------------------------------------------
			// Start the socked listening thread
			SocketListener network = new SocketListener(LISTENING_PORT);

			// Setup event handling
			network.thermoStatusChanged += network_thermoStatusChanged;
			network.programOverrideRequested += network_programOverride;
			network.thermoRuleChanged += network_thermoRuleChanged;
			network.dataRequested += network_dataRequested;
			network.timeRequestReceived += network_timeRequestReceived;
			network.statusRequest += network_statusRequest;

			//-----------------------------------------------------------------
			// Initialize the XBee communications
			//-----------------------------------------------------------------
			// Setup the xBee
			Debug.Print("Initializing XBee...");
			xBee = new XBeeApi(XBEE_PORT, 9600);
			xBee.EnableDataReceivedEvent();
			xBee.EnableAddressLookup();
			xBee.EnableModemStatusEvent();
			NETMF.OpenSource.XBee.Util.Logger.Initialize(Debug.Print, NETMF.OpenSource.XBee.Util.LogLevel.All);

			// Connect to the XBee
			ConnectToXBee();

			// Create the Zigbee IO Sample Listener for automated data packets
			ZigbeeIOSensorListener sensorListener = new ZigbeeIOSensorListener();	// Create listnener
			sensorListener.SensorDataReceived += sensorListener_DataReceived;		// Add event handling
			xBee.AddPacketListener(sensorListener);									// Connect listener

			// Add event handling for custom-built packets transmitted over Zigbee
			xBee.DataReceived += xBee_PacketReceived;

			//-----------------------------------------------------------------
			// Infinite loop to collect local sensor data
			//-----------------------------------------------------------------
			while(true) {
				// Get the sensor reading
				double temperature = tempSensor.readTemperature();

				// Update the database - Air temperature
				string sensorUpdate = "GET /db_test_upload.php?radio_id=" + CONTROL_ADDRESS + "&temperature=" + temperature.ToString("F2") + "&power=3.3\r\n";
				SendNetworkRequest(sensorUpdate, IPAddress.Parse(DB_ADDRESS), DB_PORT);

				// Sleep on this thread for the sensor period
				Thread.Sleep(SENSOR_DELAY);
			}
		}

		//=====================================================================
		// ConnectToXBee
		//=====================================================================
		/// <summary>
		/// The method to connect to the XBee trhough the API
		/// </summary>
		/// <returns>Whether the connection was successful</returns>
		private static bool ConnectToXBee() {
			//-----------------------------------------------------------------
			// Only connect if not already connected
			//-----------------------------------------------------------------
			if(!xbeeConnected) {
				try {
					// Connect to the XBee
					xBee.Open();
					xbeeConnected = true;	// Set connection status
					Debug.Print("XBee Connected...");
				} catch(Exception xbeeIssue) {	// Assumes the only exceptions are related to connecting to xbee
					Debug.Print("Caught the following trying to open the XBee connection: " + xbeeIssue.Message);
					return false;
				}
			}

			// If the code get here, the xbee is connected
			return true;
		}

		//=====================================================================
		// SendNetworkRequest - By address and port
		//=====================================================================
		/// <summary>
		/// Sends a html request to the webserver
		/// </summary>
		/// <param name="message">The request to send</param>
		/// <param name="address">The address to send to</param>
		/// <param name="port">The port to address</param>
		/// <returns>If the send was successful or not</returns>
		private static bool SendNetworkRequest(string message, IPAddress address, int port) {
			// Send request of the network
			using(Socket client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)) {
				try {
					// Connect to the database
//					throw new Exception();	// DEBUGGING TO AVOID GOING TO NETWORK
					client.Connect(new IPEndPoint(address, port));	// Connect to endpoint

					// Write the buffer
					using(NetworkStream netStream = new NetworkStream(client)) {
						byte[] buffer = System.Text.Encoding.UTF8.GetBytes(message);
						netStream.Write(buffer, 0, buffer.Length);
					}
				} catch(Exception issue) {	// This code assumes any exception results in incomplete transmission
					Debug.Print("Follow message returned when trying to send request: " + issue.Message);
					return false;
				}
			}

			// Print string to debug console
			Debug.Print("The following message was sent to " + address.ToString() + ":" + port.ToString() + " => " + message);
			return true;	// All assumed ok if at this point
		}

		//=====================================================================
		// SendNetworkRequest - By socket
		//=====================================================================
		/// <summary>
		/// Sends a html request to the webserver
		/// </summary>
		/// <param name="message">The request to send</param>
		/// <param name="endpoint">The socket to send the request through</param>
		/// <returns>If the send was successful or not</returns>
		private static bool SendNetworkRequest(string message, Socket endpoint) {
			// Send request through the provided socket
			try {
				using(NetworkStream netStream = new NetworkStream(endpoint)) {
					byte[] buffer = System.Text.Encoding.UTF8.GetBytes(message);
					netStream.Write(buffer, 0, buffer.Length);
				}
			} catch(Exception issue) {
				Debug.Print("Follow message returned when trying to send request: " + issue.Message);
				return false;
			}

			// Print the string to the debug console
			Debug.Print("Sent the following message to " + endpoint.RemoteEndPoint.ToString() + ": " + message);
			return true;	// All assumed ok if at this point
		}

		//=====================================================================
		// SendXBeeTransmission
		//=====================================================================
		/// <summary>
		/// Sends a TxRequest over the XBee network
		/// </summary>
		/// <param name="payload">The data payload for the transmission</param>
		/// <param name="destination">The XBee radio to send the data to</param>
		/// <returns>If the transmission was successful</returns>
		private static bool SendXBeeTransmission(byte[] payload, XBeeAddress destination) {
			//-----------------------------------------------------------------
			// Send the packet to the destination
			//-----------------------------------------------------------------
			// Create the transmission object to the specified destination
			TxRequest request = new TxRequest(destination, payload);
			request.Option = TxRequest.Options.DisableAck;

			// Create debug console message
			string message = "Sending XBee message to " + destination.ToString() + " (";
			for(int i = 0; i < payload.Length; i++) message += payload[i].ToString("X") + (i == (payload.Length - 1) ? "" : "-");
			message += ") => ";

			// Connect to the XBee
			bool sentMessage = false;
			if(ConnectToXBee()) {
				try {
					xBee.Send(request).NoResponse();	// Send packet
					sentMessage = true;
					message += "Sent";
				} catch(XBeeTimeoutException) {
					message += "Timeout";
				}	// OTHER EXCEPTION TYPE TO INCLUDE?
			} else message += "XBee disconnected";

			Debug.Print(message);
			return sentMessage;
		}

		//=====================================================================
		// network_thermoStatusChanged
		//=====================================================================
		/// <summary>
		/// Event handler when receiving a network command to set thermostat status
		/// </summary>
		/// <param name="client">The network source of the request</param>
		/// <param name="request">The request that was made</param>
		static void network_thermoStatusChanged(Socket client, RequestArgs request) {
			// Cast the request args
			ThermoStatusArgs txCmd = (request is ThermoStatusArgs) ? request as ThermoStatusArgs : null;
			if(txCmd != null) {
				//-------------------------------------------------------------
				// Send the message
				//-------------------------------------------------------------
				// Create the payload
				byte[] payload = { CMD_THERMO_POWER, txCmd.TurnOn ? STATUS_ON : STATUS_OFF };

				// Send the command
				if(SendXBeeTransmission(payload, new XBeeAddress64(RELAY_ADDRESS))) {
					// Update the messaging status
					awaitingResponse = true;
					lastXBeeCommand = CMD_THERMO_POWER;

					// Get the address and port
					IPEndPoint remoteIP = client.RemoteEndPoint as IPEndPoint;
					requestIP = remoteIP.Address;
					requestPort = remoteIP.Port;

					return;	// All went well, so return and pick up the response through the event handler
				} // TODO - ERROR HANDLING IF THE REQUEST WAS NOT SENT
			} else Debug.Print("Incompatible RequestArgs sent to thermoStatusChanged: " + request.GetType().ToString());

			//-----------------------------------------------------------------
			// Send response to the network that the command failed
			//-----------------------------------------------------------------
			string response = "TS:NACK";
			SendNetworkRequest(response, client);
		}

		//=====================================================================
		// network_programOverride Event Handler
		//=====================================================================
		/// <summary>
		/// Network handler when receiving program override commands
		/// </summary>
		/// <param name="client">The network source of the request</param>
		/// <param name="request">The request that was made</param>
		static void network_programOverride(Socket client, RequestArgs request) {
			// Cast the request args
			ProgramOverrideArgs txCmd = (request is ProgramOverrideArgs) ? request as ProgramOverrideArgs : null;
			if(txCmd != null) {
				//-------------------------------------------------------------
				// Send the message
				//-------------------------------------------------------------
				// Create the xbee command packet
				byte[] tempArray = Converters.FloatToByte((float) txCmd.Temperature);
				byte[] payload = { CMD_OVERRIDE, txCmd.TurnOn ? STATUS_ON : STATUS_OFF, tempArray[0], tempArray[1], tempArray[2], tempArray[3] };

				// Send the command
				if(SendXBeeTransmission(payload, new XBeeAddress64(RELAY_ADDRESS))) {
					// Update the messaging status
					awaitingResponse = true;
					lastXBeeCommand = CMD_OVERRIDE;

					// Get the address and port
					IPEndPoint remoteIP = client.RemoteEndPoint as IPEndPoint;
					requestIP = remoteIP.Address;
					requestPort = remoteIP.Port;

					return;	// All went well, so return and pick up the response through the event handler
				} // TODO - ERROR HANDLING IF THE REQUEST WAS NOT SENT
			} else Debug.Print("Incompatible RequestArgs sent to programOverride: " + request.GetType().ToString());

			//-----------------------------------------------------------------
			// Send response to the network that the command failed
			//-----------------------------------------------------------------			
			string response = "PO:NACK";
			SendNetworkRequest(response, client);
		}

		//=====================================================================
		// network_thermoRuleChanged Event Handler
		//=====================================================================
		/// <summary>
		/// Event handler for thermo rule status change requests.
		/// </summary>
		/// <param name="client">The network source of the request</param>
		/// <param name="request">The request that was made</param>
		static void network_thermoRuleChanged(Socket client, RequestArgs request) {
			// Cast the request args
			RuleChangeArgs txCmd = (request is RuleChangeArgs) ? request as RuleChangeArgs : null;
			if(txCmd != null) {
				//-------------------------------------------------------------
				// Send the message
				//-------------------------------------------------------------
				// Create the xbee command packet
				byte[] payload = null;
				switch(txCmd.ChangeRequested) {
					case RuleChangeArgs.Operation.Get:
						payload = new byte[] { CMD_RULE_CHANGE, STATUS_GET };
						break;
					case RuleChangeArgs.Operation.Add:
						// Convert the floats
						byte[] timeArray = Converters.FloatToByte(txCmd.Rule.Time);
						byte[] tempArray = Converters.FloatToByte(txCmd.Rule.Temperature);

						// Create the payload
						payload = new byte[12];
						payload[0] = CMD_RULE_CHANGE;
						payload[1] = STATUS_ADD;
						payload[2] = txCmd.FirstPosition;
						payload[3] = (byte) txCmd.Rule.Days;
						for(int i = 0; i < 4; i++) {
							payload[4 + i] = timeArray[i];
							payload[8 + i] = tempArray[i];
						}
						break;
					case RuleChangeArgs.Operation.Delete:
						// Create the payload
						payload = new byte[] { CMD_RULE_CHANGE, STATUS_DELETE, txCmd.FirstPosition };
						break;
					case RuleChangeArgs.Operation.Move:
						// Create the payload
						payload = new byte[] { CMD_RULE_CHANGE, STATUS_MOVE, txCmd.FirstPosition, txCmd.SecondPosition };
						break;
					case RuleChangeArgs.Operation.Update:
						// Convert the floats
						timeArray = Converters.FloatToByte(txCmd.Rule.Time);
						tempArray = Converters.FloatToByte(txCmd.Rule.Temperature);

						// Create the payload
						payload = new byte[12];
						payload[0] = CMD_RULE_CHANGE;
						payload[1] = STATUS_UPDATE;
						payload[2] = txCmd.FirstPosition;
						payload[3] = (byte) txCmd.Rule.Days;
						for(int i = 0; i < 4; i++) {
							payload[4 + i] = timeArray[i];
							payload[8 + i] = tempArray[i];
						}
						break;
				}

				// Send the command
				if(SendXBeeTransmission(payload, new XBeeAddress64(RELAY_ADDRESS))) {
					// Update the messaging status
					awaitingResponse = true;
					lastXBeeCommand = CMD_RULE_CHANGE;

					// Get the address and port
					IPEndPoint remoteIP = client.RemoteEndPoint as IPEndPoint;
					requestIP = remoteIP.Address;
					requestPort = remoteIP.Port;

					return;	// All went well, so return and pick up the response through the event handler
				} // TODO - ERROR HANDLING IF THE REQUEST WAS NOT SENT
			} else Debug.Print("Incompatible RequestArgs sent to thermoRuleChanged: " + request.GetType().ToString());

			//-----------------------------------------------------------------
			// Send response to the network that the command failed
			//-----------------------------------------------------------------			
			string response = "PO:NACK";
			SendNetworkRequest(response, client);
		}

		//=====================================================================
		// network_timeRequestReceived
		//=====================================================================
		/// <summary>
		/// Event handler for timekeeping requests.
		/// </summary>
		/// <param name="client">The network source of the request</param>
		/// <param name="request">The request that was made</param>
		static void network_timeRequestReceived(Socket client, RequestArgs request) {
			// Cast the request args
			TimeRequestArgs txCmd = (request is TimeRequestArgs) ? request as TimeRequestArgs : null;
			if(txCmd != null) {
				//-------------------------------------------------------------
				// Send the message
				//-------------------------------------------------------------
				// Create the xbee command packet
				byte[] payload = null;
				switch(txCmd.Request) {
					case TimeRequestArgs.Operations.Get:	// Get the current time
						payload = new byte[] { CMD_TIME_REQUEST, STATUS_GET };
						break;
					case TimeRequestArgs.Operations.Set:	// Set the relay time
						payload = new byte[] { CMD_TIME_REQUEST, STATUS_UPDATE, txCmd.Seconds, txCmd.Minutes, txCmd.Hours, txCmd.Weekday, txCmd.Day, txCmd.Month, txCmd.Year };
						break;
					default:	// TODO - ERROR HANDLING BY SENDING RESPONSE TO THE SOURCE
						break;
				}

				// Send the command
				if(SendXBeeTransmission(payload, new XBeeAddress64(RELAY_ADDRESS))) {
					// Update the messaging status
					awaitingResponse = true;
					lastXBeeCommand = CMD_TIME_REQUEST;

					// Get the address and port
					IPEndPoint remoteIP = client.RemoteEndPoint as IPEndPoint;
					requestIP = remoteIP.Address;
					requestPort = remoteIP.Port;

					return;	// All went well, so return and pick up the response through the event handler
				} // TODO - ERROR HANDLING IF THE REQUEST WAS NOT SENT
			} else Debug.Print("Incompatible RequestArgs sent to timeRequestReceived: " + request.GetType().ToString());

			//-----------------------------------------------------------------
			// Send response to the network that the command failed
			//-----------------------------------------------------------------			
			string response = "CR:NACK";
			SendNetworkRequest(response, client);
		}

		//=====================================================================
		// network_statusRequest Event Handler
		//=====================================================================
		static void network_statusRequest(Socket client, RequestArgs request) {
			// Cast the request args
			StatusRequestArgs txCmd = (request is StatusRequestArgs) ? request as StatusRequestArgs : null;
			if(txCmd != null) {
				//-------------------------------------------------------------
				// Send the message
				//-------------------------------------------------------------
				// Create the xbee command packet
				byte[] payload = new byte[] { CMD_STATUS };

				// Send the command
				if(SendXBeeTransmission(payload, new XBeeAddress64(RELAY_ADDRESS))) {
					// Update the messaging status
					awaitingResponse = true;
					lastXBeeCommand = CMD_STATUS;

					// Get the address and port
					IPEndPoint remoteIP = client.RemoteEndPoint as IPEndPoint;
					requestIP = remoteIP.Address;
					requestPort = remoteIP.Port;

					return;	// All went well, so return and pick up the response through the event handler
				} // TODO - ERROR HANDLING IF THE REQUEST WAS NOT SENT
			} else Debug.Print("Incompatible RequestArgs sent to statusRequest: " + request.GetType().ToString());

			//-----------------------------------------------------------------
			// Send response to the network that the command failed
			//-----------------------------------------------------------------			
			string response = "ST:NACK";
			SendNetworkRequest(response, client);
		}

		//=====================================================================
		// xBee_PacketReceived
		//=====================================================================
		/// <summary>
		/// This method responds to an XBee packet and takes appropriate actions
		/// </summary>
		/// <param name="receiver">The XBee API instance receiving the packet</param>
		/// <param name="data">The data packet</param>
		/// <param name="sender">The XBee radio sending the data</param>
		static void xBee_PacketReceived(XBeeApi receiver, byte[] data, XBeeAddress sender) {
			//-----------------------------------------------------------------
			// Check and convert the payload
			//-----------------------------------------------------------------
			// Format the data packet to remove 0x7d instances (assuming API mode is 2)
			byte[] payload = FormatApiMode(data, true);

			// Check to see if the payload contains the entire message
			bool isRemoteSensor = false;
			byte[] packet = null;
			if((payload[0] == 0x7E) && (payload[3] == 0x10)) {
				// Data is from remote sensor that passes the entire message
				int payloadSize = payload.Length - 18;	// Calculate the packet size
				packet = new byte[payloadSize];	// Create the packet array
				for(int i = 0; i < payloadSize; i++) packet[i] = payload[17+i];	// Copy the contents
				isRemoteSensor = true;	// Signal this is a simple sensor reading
			} else {
				// Copy the entire message
				packet = new byte[payload.Length];
				for(int i = 0; i < payload.Length; i++) packet[i] = payload[i];
			}

			// Print the received request to the debug console
			string message = "Received XBee message from " + sender.ToString() + ": ";
			for(int i = 0; i < packet.Length; i++) message += packet[i].ToString("X") + (i == (packet.Length - 1) ? "" : "-");
			Debug.Print(message);

			// Check the current status of communications
			if((packet[0] == CMD_SENSOR_DATA) || isRemoteSensor) {
				//-------------------------------------------------------------
				// Process the sensor data and send acknowledgement
				//-------------------------------------------------------------
				UpdateSensorData(packet, sender, isRemoteSensor);
				byte[] response = { CMD_SENSOR_DATA, CMD_ACK };
				if(!isRemoteSensor) SendXBeeTransmission(response, sender);	// Only send acknowledgement to relay-type sensor
			} else if(awaitingResponse) {
				//-------------------------------------------------------------
				// Send a response to the network based on the request
				//-------------------------------------------------------------
				// Make sure the response type matches the request
				if(packet[0] == lastXBeeCommand) {
					// Create response string
					string response = "";

					// Check the packet type
					switch(packet[0]) {
						//-----------------------------------------------------
						case CMD_THERMO_POWER:
							response = "TS:" + (packet[1] == CMD_ACK ? "ACK" : "NACK");
							break;
						//-----------------------------------------------------
						case CMD_OVERRIDE:
							response = "PO:" + (packet[1] == CMD_ACK ? "ACK" : "NACK");
							break;
						//-----------------------------------------------------
						case CMD_RULE_CHANGE:
							// Create the response based on the rule command
							switch(packet[1]) {
								case STATUS_GET:
									response = "TR:GET:" + ProcessGetRuleResults(packet);
									break;
								case STATUS_ADD:
									response = "TR:ADD:" + (packet[2] == CMD_ACK ? "ACK" : "NACK");
									break;
								case STATUS_DELETE:
									response = "TR:DELETE:" + (packet[2] == CMD_ACK ? "ACK" : "NACK");
									break;
								case STATUS_MOVE:
									response = "TR:MOVE:" + (packet[2] == CMD_ACK ? "ACK" : "NACK");
									break;
								case STATUS_UPDATE:
									response = "TR:UPDATE:" + (packet[2] == CMD_ACK ? "ACK" : "NACK");
									break;
								default:
									Debug.Print("This rule request type has not been implemented yet.");
									response = "TR:NACK";
									break;
							}
							break;
						//-----------------------------------------------------
						case CMD_TIME_REQUEST:
							// Create message based on command type
							switch(packet[1]) {
								case STATUS_GET:
									response = "CR";
									for(int i = 2; i < packet.Length; i++) response += ":" + packet[i].ToString();
									break;
								case STATUS_UPDATE:
									response = "CR:" + (packet[2] == CMD_ACK ? "ACK" : "NACK");
									break;
								default:
									Debug.Print("This type of Time Request command (" + packet[1] + ") does not exist!");
									response = "CR:NACK";
									break;
							}
							break;
						//-----------------------------------------------------
						case CMD_STATUS:
							// Check the length of the response
							if(packet.Length == 11) {
								// Get the float values
								float temperature = BitConverter.ToSingle(packet, 3);
								float target = BitConverter.ToSingle(packet, 7);

								// Create the string message
								response = "ST:";
								response += (packet[1] == 0 ? "OFF" : "ON") + ":";	// Sets thermostat status
								response += (packet[2] == 0 ? "OFF" : "ON") + ":";	// Sets relay status
								response += temperature.ToString("F2") + ":" + target.ToString("F2");
							} else response = "ST:NACK";
							break;
						//-----------------------------------------------------
						default:
							// This command doesn't exist
							Debug.Print("Command " + packet[0] + " not implemented - this shouldn't have happened!");
							break;
					}

					// Send the response
					SendNetworkRequest(response, requestIP, SERVER_PORT);

					// Reset the messaging tracking
					awaitingResponse = false;
					lastXBeeCommand = CMD_NACK;
					requestIP = null;
					requestPort = 0;
				} else {
					// MAJOR TIME DELAY, OR CONFLICTING COMMUNICATIONS
					Debug.Print("Didn't receive a response that was expected?");
				}
			} else {
				//-------------------------------------------------------------
				// Handle unexpected packet
				//-------------------------------------------------------------
				Debug.Print("Don't know how we got here?");
			}
		}

		//=====================================================================
		// ProcessGetRuleResults
		//=====================================================================
		/// <summary>
		/// This method takes the data packet containing a list of thermostat rules and passes it on to the requesting client
		/// </summary>
		/// <param name="packetData">The data packet from the XBee transmission</param>
		/// <returns>The message to send out to the network</returns>
		private static string ProcessGetRuleResults(byte[] packetData) {
			// Determine the number of rules
			byte num_rules = packetData[2];
			Debug.Print("A total of " + num_rules + " rules sent with a packet length of " + packetData.Length);

			//-----------------------------------------------------------------
			// Convert the packet into a string with the data
			//-----------------------------------------------------------------
			string dataStr = num_rules.ToString();
			for(int i = 0; i < num_rules; i++) {
				// Collect the byte arrays for the rule
				byte[] tempArray = new byte[4];
				byte[] timeArray = new byte[4];
				for(int j = 0; j < 4; j++) {
					timeArray[j] = packetData[9*i + j + 4];
					tempArray[j] = packetData[9*i + j + 8];
				}

				// Convert the arrays to floats and add to the string
				double time = Converters.ByteToFloat(timeArray);
				double temperature = Converters.ByteToFloat(tempArray);
				dataStr += ":" + packetData[9 * i + 3] + "-" + time.ToString("F") + "-" + temperature.ToString("F");
			}

			return dataStr;
		}

		//=====================================================================
		// UpdateSensorData
		//=====================================================================
		/// <summary>
		/// Converts a custom sensor data packet into a format for uploading to
		/// the database and dispatches this update over the network.
		/// </summary>
		/// <param name="packetData">The received sensor data payload</param>
		/// <param name="sender">The XBee sending the data</param>
		private static void UpdateSensorData(byte[] packetData, XBeeAddress sender, bool onlySensorData) {
			// Create the http request string
			string dataUpdate = "GET /db_test_upload.php?radio_id=";

			// Get the sensor
			for(int i = 4; i < sender.Address.Length; i++) dataUpdate += sender.Address[i].ToString("x").ToLower();

			// Iterate through the data
			int data_length = packetData.Length - (onlySensorData ? 0 : 1);	// Determine the length of the data to check
			int byte_pos = onlySensorData ? 0 : 1;	// Determine the starting point
			if(data_length % 5 != 0) return;	// Something funny happened
			else {
				int num_sensors = data_length/5;
				for(int cur_sensor = 0; cur_sensor < num_sensors; cur_sensor++) {
					// Determine the type of reading
					bool isPressure = false;
					if(packetData[byte_pos] == 0x01) dataUpdate += "&temperature=";
					else if(packetData[byte_pos] == 0x02) dataUpdate += "&luminosity=";
					else if(packetData[byte_pos] == 0x04) {
						dataUpdate += "&pressure=";
						isPressure = true;
					} else if(packetData[byte_pos] == 0x08) dataUpdate += "&humidity=";
					else if(packetData[byte_pos] == 0x10) dataUpdate += "&power=";
					else if(packetData[byte_pos] == 0x20) dataUpdate += "&luminosity_lux=";
					else if(packetData[byte_pos] == 0x40) dataUpdate += "&heating_on=";
					else if(packetData[byte_pos] == 0x80) dataUpdate += "&thermo_on=";
					else return;	// Something funny happened
					++byte_pos;

					// Convert the data
					byte[] fdata = { packetData[byte_pos+0], packetData[byte_pos+1], packetData[byte_pos+2], packetData[byte_pos+3] };
					float fvalue = Converters.ByteToFloat(fdata);
					if(isPressure) {
						// Convert station pressure to altimiter pressure
						double Pmb = 0.01*fvalue;
						double hm = 167.64;
						fvalue = (float) (System.Math.Pow(1 + 8.422881e-5*(hm/System.Math.Pow(Pmb - 0.3, 0.190284)), 1/0.190284)*(Pmb - 0.3));
					}
					dataUpdate += fvalue.ToString("F2");
					byte_pos += 4;
				}

				// Try sending the data
				dataUpdate += "\r\n";
				if(!SendNetworkRequest(dataUpdate, IPAddress.Parse(DB_ADDRESS), DB_PORT)) {	// An error occurred
					Debug.Print("Could not send the sensor data to the database");
					// TODO - ADD ERROR HANDLING
				}
			}
		}

		//=====================================================================
		// sensorListener_DataReceived Event Handler
		//=====================================================================
		/// <summary>
		/// Receives and sensor data packet sent directly from the analog inputs
		/// on the XBee radio, creates the database command and issues the data
		/// over the network
		/// </summary>
		/// <param name="ioPacket">The IOSamplePacket received by the XBee</param>
		static void sensorListener_DataReceived(IoSampleResponse ioPacket) {
			// Print the data
			//Debug.Print(ioPacket.ToString());

			// Create the http request to add the data to the database
			string dataUpdate = "GET /db_test_upload.php?radio_id=";

			// Get the sensor sending the data
			dataUpdate += ioPacket.SourceSerial.ToString().Substring(8, 8).ToLower();

			// Temperature reading
			if(ioPacket.IsAnalogEnabled(IoSampleResponse.Pin.A0)) {
				double voltage = 1.2*ioPacket.GetAnalog(IoSampleResponse.Pin.A0)/1023.0;
				double temperature = 100.0*(voltage - 0.5);
				dataUpdate += "&temperature=" + temperature.ToString("F2");
			}

			// Luminosity reading
			if(ioPacket.IsAnalogEnabled(IoSampleResponse.Pin.A1)) {
				double voltage = 1.2*ioPacket.GetAnalog(IoSampleResponse.Pin.A1)/1023.0;
				dataUpdate += "&luminosity=" + voltage.ToString("F2");
			}

			// Power reading
			if(ioPacket.IsAnalogEnabled(IoSampleResponse.Pin.SupplyVoltage)) {
				// Until I have a battery pack in the system, this will remain at 3.3V
				double voltage = 1.2*ioPacket.GetSupplyVoltage()/1023.0;
				dataUpdate += "&power=" + voltage.ToString("F2");
			} else dataUpdate += "&power=3.3";
			dataUpdate += "\r\n";

			// Try sending the data
			if(!SendNetworkRequest(dataUpdate, IPAddress.Parse(DB_ADDRESS), DB_PORT)) {	// Error occurred
				Debug.Print("Could not send the sensor data to the database");
				// TODO - ADD ERROR HANDLING
			}
		}

		//=====================================================================
		// network_dataRequested Event Handler
		//=====================================================================
		static void network_dataRequested(Socket client, RequestArgs request) {
			throw new NotImplementedException();
		}

		//=====================================================================
		// FormatApiMode
		//=====================================================================
		/// <summary>
		/// Formats escape characters in the XBee payload data
		/// </summary>
		/// <param name="packet">The payload data</param>
		/// <param name="filterIncoming">Payload from an incoming tranmission with escape characters</param>
		/// <returns>The filtered payload data</returns>
		private static byte[] FormatApiMode(byte[] packet, bool filterIncoming) {
			// Local variables and constants
			byte[] escapeChars = { 0x7d, 0x7e, 0x11, 0x13 };	// The bytes requiring escaping
			const byte filter = 0x20;	// The XOR filter
			byte[] output;	// Contains the formatted packet
			int outSize = packet.Length;	// Contains the size of the outgoing packet

			if(filterIncoming) {	// Removed any escaping sequences
				//-------------------------------------------------------------
				// REMOVE ESCAPING CHARACTERS FROM PACKET FROM XBEE
				//-------------------------------------------------------------
				// Count the outgoing packet size
				foreach(byte b in packet) if(b == escapeChars[0]) outSize--;

				// Iterate through each byte and adjust
				output = new byte[outSize];
				int pos = 0;
				for(int i = 0; i < packet.Length; i++) {
					if(packet[i] == escapeChars[0]) output[pos++] = (byte) (packet[++i]^filter);	// Cast needed as XOR works on ints
					else output[pos++] = packet[i];
				}
			} else {
				//-------------------------------------------------------------
				// ADD ESCAPING CHARACTERS TO PACKET SENT FROM XBEE
				//-------------------------------------------------------------
				// Determine the new size
				foreach(byte b in packet) if(Array.IndexOf(escapeChars, b) > -1) outSize++;

				// Iterate through each byte and adjust
				output = new byte[outSize];
				int pos = 0;
				for(int i = 0; i < packet.Length; i++) {
					if(Array.IndexOf(escapeChars, packet[i]) > -1) {
						output[pos++] = escapeChars[0];
						output[pos++] = (byte) (packet[i]^filter);
					} else output[pos++] = packet[i];
				}
			}

			return output;
		}
	}
}
