using System;
using Microsoft.SPOT;

namespace ThermalNetworkServer {

	//=========================================================================
	// Converters STATIC CLASS
	//=========================================================================
	/// <summary>
	/// A static class for converting between different data types
	/// </summary>
	class Converters {
		//=====================================================================
		// ByteToFloat
		//=====================================================================
		/// <summary>
		/// Covert a 4-byte array into a single precision floating point
		/// </summary>
		/// <param name="byte_array">A 4-byte array to be converted</param>
		/// <returns>The float representation of the 4-byte array</returns>
		public static unsafe float ByteToFloat(byte[] byte_array) {
			uint ret = (uint) (byte_array[0] << 0 | byte_array[1] << 8 | byte_array[2] << 16 | byte_array[3] << 24);
			float r = *((float*) &ret);
			return r;
		}

		//=====================================================================
		// FloatToByte
		//=====================================================================
		/// <summary>
		/// Convert a single precision floatin point to a 4-byte array
		/// </summary>
		/// <param name="value">The floating point value to be converted</param>
		/// <returns>The 4-byte represenation of the floating point</returns>
		public static unsafe byte[] FloatToByte(float value) {
			if(sizeof(uint) != 4) throw new Exception("uint is not a 4-byte variable on this system!");

			uint asInt = *((uint*) &value);
			byte[] byte_array = new byte[sizeof(uint)];

			byte_array[0] = (byte) (asInt & 0xFF);
			byte_array[1] = (byte) ((asInt >> 8) & 0xFF);
			byte_array[2] = (byte) ((asInt >> 16) & 0xFF);
			byte_array[3] = (byte) ((asInt >> 24) & 0xFF);

			return byte_array;
		}
	}
}
