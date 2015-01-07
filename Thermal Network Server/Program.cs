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

namespace NetduinoThermalNetworkServer {

	public delegate void IoDataHandler(IoSampleResponse ioPacket);

	public class Program {

		private const string DB_ADDRESS = "192.168.2.53";	// The address to the server with the MySQL server

		private enum XBeePortData { Temperature, Luminosity, Pressure, Humidity, LuminosityLux, HeatingOn, ThermoOn, Power }

		private static XBeeApi xBee;

		public static void Main() {
			// Create the Zigbee IO Sample Listener
			IoSampleListener dataListener = new IoSampleListener();
			dataListener.IoDataReceived += dataListener_IoDataReceived;

			// Setup the xBee
			Debug.Print("Initializing XBee...");
			xBee = new XBeeApi("COM1", 9600);
			xBee.EnableDataReceivedEvent();
			xBee.EnableAddressLookup();
			xBee.EnableModemStatusEvent();

			// Add event handling
			xBee.AddPacketListener(dataListener);
			xBee.DataReceived += xBee_DataReceived;

			// Connect to the XBee
			xBee.Open();
			Debug.Print("XBee Connected...");

			// Set the 2 Wire communication arrays
			byte[] RxBuffer = new byte[2];

			// Initialize the TMP102
			I2CDevice TMP102Device = new I2CDevice(new I2CDevice.Configuration(0x48, 100));
			I2CDevice.I2CReadTransaction[] ReadTemperature = new I2CDevice.I2CReadTransaction[] {
				I2CDevice.CreateReadTransaction(RxBuffer)
			};

			// Connect the ethernet to the network
/*			Socket client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
			client.Connect(new IPEndPoint(IPAddress.Parse(DB_ADDRESS), 80));	// Connect to server*/

			// Infinite loop, but read the sensor every 10 minutes
			double airTempSum;
			while(true) {
				// Check to see if 

				// Get the sensor reading - perform an average over 20 readings
				airTempSum = 0.0;
				for(int i = 0; i < 20; i++) {
					// Get the air reading
					TMP102Device.Execute(ReadTemperature, 1000);
					int TemperatureSum = ((RxBuffer[0] << 8) | RxBuffer[1]) >> 4;
					airTempSum += 0.0625*TemperatureSum;

					Thread.Sleep(100);
				}
				double airTemperature = airTempSum/20.0;

				// Update the database - Air temperature
				string airUpdate = "GET /db_sensor_upload.php?radio_id=40b0ad63&temperature=" + airTemperature.ToString("F2") + "&power=3.3\r\n";
				DBSendData(airUpdate);

				// Sleep for the rest of the 10 minutes
				Thread.Sleep(598000);

/*				// TESTING REMOTE SWITCH
				bool isOn = false;
				for(int sec = 0; sec < 598; sec++) {
					// Send the signal to turn on the remote switch
					// Set the address and data
					byte[] address = new byte[] { 0x00, 0x13, 0xA2, 0x00, 0x40, 0xAE, 0xB8, 0x8C };
					float sendData;
					if(isOn) {
						sendData = -1.0f;
						isOn = false;
					} else {
						sendData = 1.0f;
						isOn = true;
					}
					byte[] dataArray = floatToByte(sendData);

					// Create the payload - by hand
					byte[] data_array = new byte[22];
					data_array[0] = 0x7e;
					data_array[1] = 0x00;
					data_array[2] = 0x12;
					data_array[3] = 0x10;
					data_array[4] = 0x01;
					data_array[5] = address[0];
					data_array[6] = address[1];
					data_array[7] = address[2];
					data_array[8] = address[3];
					data_array[9] = address[4];
					data_array[10] = address[5];
					data_array[11] = address[6];
					data_array[12] = address[7];
					data_array[13] = 0xff;
					data_array[14] = 0xfe;
					data_array[15] = 0x00;
					data_array[16] = 0x00;
					data_array[17] = dataArray[0];
					data_array[18] = dataArray[1];
					data_array[19] = dataArray[2];
					data_array[20] = dataArray[3];
					data_array[21] = Checksum.Compute(data_array, 3, 18);

					// Send the data over the XBee
					XBeeResponse result = xBee.Send(data_array).To(new XBeeAddress64("0013a20040aeb88c")).GetResponse();
					Debug.Print(result.ToString());
					Thread.Sleep(1000);
				}*/
			}
		}

		static void xBee_DataReceived(XBeeApi receiver, byte[] data, XBeeAddress sender) {
			// Format the data packet to remove 0x7d instances (assuming API mode is 2)
			byte[] packet = Program.FormatApiMode(data);

			// Create the http request string
			string dataUpdate = "GET /db_sensor_upload.php?radio_id=";

			// Get the sensor
			for(int i = 4; i < sender.Address.Length; i++) dataUpdate += sender.Address[i].ToString("x").ToLower();

			// Iterate through the data
			int data_length = packet.Length - 18;
			if(data_length % 5 != 0) return;	// Something funny happened
			else {
				int num_sensors = data_length/5;
				int byte_pos = 17;	// The starting point in the data to read the sensor data
				for(int cur_sensor = 0; cur_sensor < num_sensors; cur_sensor++) {
					// Determine the type of reading
					bool isPressure = false;
					if(packet[byte_pos] == 0x01) dataUpdate += "&temperature=";
					else if(packet[byte_pos] == 0x02) dataUpdate += "&luminosity=";
					else if(packet[byte_pos] == 0x04) {
						dataUpdate += "&pressure=";
						isPressure = true;
					} else if(packet[byte_pos] == 0x08) dataUpdate += "&humidity=";
					else if(packet[byte_pos] == 0x10) dataUpdate += "&power=";
					else if(packet[byte_pos] == 0x20) dataUpdate += "&luminosity_lux=";
					else if(packet[byte_pos] == 0x40) dataUpdate += "&heating_on=";
					else if(packet[byte_pos] == 0x80) dataUpdate += "&thermo_on=";
					else return;	// Something funny happened
					++byte_pos;

					// Convert the data
					byte[] fdata = { packet[byte_pos+0], packet[byte_pos+1], packet[byte_pos+2], packet[byte_pos+3] };
					float fvalue = byteToFloat(fdata);
					if(isPressure) {
						// Convert station pressure to altimiter pressure
						double Pmb = 0.01*fvalue;
						double hm = 167.64;
						fvalue = (float)(System.Math.Pow(1 + 8.422881e-5*(hm/System.Math.Pow(Pmb - 0.3, 0.190284)), 1/0.190284)*(Pmb - 0.3));
					}
					dataUpdate += fvalue.ToString("F2");
					byte_pos += 4;
				}

				// Send data
				dataUpdate += "\r\n";
				DBSendData(dataUpdate);
			}
		}

		private static unsafe float byteToFloat(byte[] byte_array) {
			uint ret = (uint)(byte_array[0] << 0 | byte_array[1] << 8 | byte_array[2] << 16 | byte_array[3] << 24);
//			float r = *(((float*)&ret));
			float r = *((float*) &ret);	// This should work?
			return r;
		}

		private static unsafe byte[] floatToByte(float value) {
			if(sizeof(uint) != 4) throw new Exception("uint is not a 4-byte variable on this system!");

			uint asInt = *((uint*) &value);
			byte[] byte_array = new byte[sizeof(uint)];

			byte_array[0] = (byte) (asInt & 0xFF);
			byte_array[1] = (byte) ((asInt >> 8) & 0xFF);
			byte_array[2] = (byte) ((asInt >> 16) & 0xFF);
			byte_array[3] = (byte) ((asInt >> 24) & 0xFF);

			return byte_array;
		}

		static void dataListener_IoDataReceived(IoSampleResponse ioPacket) {
			// Print the data
			Debug.Print(ioPacket.ToString());

			// Create the http request to add the data to the database
			string dataUpdate = "GET /db_sensor_upload.php?radio_id=";

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
//				double voltage = 1.2*ioPacket.GetAnalog(IoSampleResponse.Pin.SupplyVoltage)/1023.0;
				double voltage = 1.2*ioPacket.GetSupplyVoltage()/1023.0;
				dataUpdate += "&power=" + voltage.ToString("F2");
			} else dataUpdate += "&power=3.3";
			dataUpdate += "\r\n";

			// Send the data
			DBSendData(dataUpdate);
		}

		private static void DBSendData(string sendURL) {
			// Send data over the ethernet
			using(Socket client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)) {
				client.Connect(new IPEndPoint(IPAddress.Parse(DB_ADDRESS), 80));	// Connect to server
				using(NetworkStream netStream = new NetworkStream(client)) {
					byte[] buffer = System.Text.Encoding.UTF8.GetBytes(sendURL);
					netStream.Write(buffer, 0, buffer.Length);
				}
			}

			// Print string to debug console
			Debug.Print(sendURL);
		}

		private static void XBeeSendData(byte[] address, float[] data) {
			// Check the address length
			if(address.Length != 8) throw new Exception("Address has to be 8 bytes.");

			// Create the payload - by hand
			byte[] data_array = new byte[33];
			data_array[0] = 0x7e;
			data_array[1] = 0x00;
			data_array[3] = 0x10;
			data_array[4] = 0x01;
			data_array[5] = address[0];
			data_array[6] = address[1];
			data_array[7] = address[2];
			data_array[8] = address[3];
			data_array[9] = address[4];
			data_array[10] = address[5];
			data_array[11] = address[6];
			data_array[12] = address[7];
			data_array[13] = 0xff;
			data_array[14] = 0xfe;
			data_array[15] = 0x00;
			data_array[16] = 0x00;

			// Iterate through each item of data
			int curPos = 17;
			byte length = 18;
			for(int i = 0; i < data.Length; i++) {
				byte[] data_item = floatToByte(data[i]);
				length += 4;
				for(int j = curPos; j < length; j++) data_array[j] = data_item[j-curPos];
				curPos += 4;
			}

			// Set the length and checksum
			data_array[2] = length;
			data_array[curPos] = Checksum.Compute(data_array, 3, length-3);

			// Send the data over the XBee
			XBeeResponse result = xBee.Send(data_array).To(new XBeeAddress64("0000000000000000")).GetResponse();
			Debug.Print(result.ToString());
		}

		private static void UpdateTime() {
			// Set the local time - assuming daylight savings time
			bool updated = Ntp.UpdateTimeFromNtpServer("time.nist.gov", -4);
			Debug.Print(updated ? "Time updated" : "Error updating time");
		}

		private static byte[] FormatApiMode(byte[] input) {
			// Determine the size of the array
			const byte marker = 0x7d;
			int count = 0;
			foreach(byte b in input) if(b != marker) ++count;

			// Check if any markers need to be removed
			if(count == input.Length) return input;
			else {
				// Iterate through each item
				byte[] copy_array = new byte[count];
				int n = 0;
				foreach(byte b in input)
					if(b != marker) copy_array[n++] = b;

				return copy_array;
			}
		}
	}

	public class IoSampleListener : IPacketListener {
		// This class was taken from https://xbee.codeplex.com/discussions/440465 and modified for use

		public event IoDataHandler IoDataReceived;

		#region IPacketListener Implementation
		public bool Finished { get { return false; } }

		public void ProcessPacket(XBeeResponse packet) {
			if((packet is IoSampleResponse) && (IoDataReceived != null)) IoDataReceived(packet as IoSampleResponse);
		}

		public XBeeResponse[] GetPackets(int timeout) {
			throw new System.NotSupportedException();
		}
		#endregion IPacketListener Implementation
	}
}
