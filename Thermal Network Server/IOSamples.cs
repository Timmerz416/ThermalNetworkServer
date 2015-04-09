using System;
using Microsoft.SPOT;
using NETMF.OpenSource.XBee.Api;
using NETMF.OpenSource.XBee.Api.Zigbee;

namespace ThermalNetworkServer {

	//=========================================================================
	// DELEGATES
	//=========================================================================
	/// <summary>
	/// Delegate to handle transmissions involving IO samples sent over the Zigbee network
	/// </summary>
	/// <param name="ioPacket">The sensor sample response sent by the remote radio</param>
	public delegate void IoSampleDataHandler(IoSampleResponse ioPacket);

	//=========================================================================
	// ZigbeeIOSensorListner
	//=========================================================================
	/// <summary>
	/// A class to listen for Zigbee sensor respnoses sent by radios on the network
	/// </summary>
	public class ZigbeeIOSensorListener : IPacketListener {
		// This class was taken from https://xbee.codeplex.com/discussions/440465 and modified for use

		public event IoSampleDataHandler SensorDataReceived;

		#region IPacketListener Implementation
		public bool Finished { get { return false; } }

		public void ProcessPacket(XBeeResponse packet) {
			if((packet is IoSampleResponse) && (SensorDataReceived != null)) SensorDataReceived(packet as IoSampleResponse);
		}

		public XBeeResponse[] GetPackets(int timeout) {
			throw new System.NotSupportedException();
		}
		#endregion IPacketListener Implementation
	}
}
