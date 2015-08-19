using System;
using Microsoft.SPOT;
using System.Net.Sockets;
using System.Text;
using System.Net;
using System.IO;

namespace ThermalNetworkServer {

	//=========================================================================
	// RequestArgs Class
	//=========================================================================
	/// <summary>
	/// Base class for handling socket commands
	/// </summary>
	public class RequestArgs {
		// Private members
		protected string _command;	// Contains the received message from the socket
		protected string[] _args;	// Parsed arguments for the command

		//=====================================================================
		// Constructor
		//=====================================================================
		/// <summary>
		/// Constructor to initialize members
		/// </summary>
		/// <param name="Data">The message sent over the network</param>
		public RequestArgs(char[] Data) {
			_command = new string(Data);
			_args = _command.Split(':');
		}
	}

	//=========================================================================
	// ThermoStatusArgs Class
	//=========================================================================
	/// <summary>
	/// Class for tracking a command to turn on/off the thermostat
	/// </summary>
	public class ThermoStatusArgs : RequestArgs {
		// Private members
		private bool _turnOn;	// Indicates if the command was to turn on the thermostat

		//=====================================================================
		// Constructor
		//=====================================================================
		/// <summary>
		/// Constructor to initialize members
		/// </summary>
		/// <param name="Data">The message sent over the network</param>
		public ThermoStatusArgs(char[] Data)
			: base(Data) {
			// Check that there are two args
			if(_args.Length != 2) throw new ArgumentException("Thermostat Command requires 2 arguments");

			// Set the status
			if(_args[1].ToUpper() == "ON") _turnOn = true;
			else if(_args[1].ToUpper() == "OFF") _turnOn = false;
			else throw new ArgumentException("Thermostat Status Command '" + _command + "' Not Currently Handled.");
		}

		//=====================================================================
		// Parameters
		//=====================================================================
		// TurnOn
		/// <summary>
		/// Indicates if the command was to turn on (true) or off (false) the thermostat
		/// </summary>
		public bool TurnOn {
			get { return _turnOn; }
		}
	}

	public class ProgramOverrideArgs : RequestArgs {
		private bool _turnOn;
		private double _setting;

		public ProgramOverrideArgs(char[] Data) : base(Data) {
			// Check that there are 3 args
			if(_args.Length != 3) throw new ArgumentException("Program Override Command requires 3 arguments");

			// Check the args
			if(_args[1].ToUpper() == "ON") {
				_turnOn = true;
				_setting = double.Parse(_args[2]);
			} else if(_args[1].ToUpper() == "OFF") {
				_turnOn = false;
				_setting = 0.0;
			} else throw new ArgumentException("Program Override Command '" + _command + "' Not Currently Handled.");
		}

		public bool TurnOn {
			get { return _turnOn; }
		}

		public double Temperature {
			get { return _setting; }
		}
	}

	public class RuleChangeArgs : RequestArgs {
		public enum Operation { Get, Add, Delete, Move, Update }

		private Operation _operation;
		private byte _pos1;
		private byte _pos2;
		private TemperatureRule _rule;

		public RuleChangeArgs(char[] Data) : base(Data) {
			// The number of args depends on the requested operation
			string CMD = _args[1].ToUpper();
			switch(CMD) {
				case "GET":	// Get the current list of rules
					_operation = Operation.Get;
					_pos1 = _pos2 = 0;	// Meaningless
					_rule = null;	// Meaningless
					break;
				case "ADD":	// Add a new rule before the insert position (-1 corresponds to the last item)
					if(_args.Length != 6) throw new ArgumentException("Adding a new rule requires 6 arguments");
					_operation = Operation.Add;
					_pos1 = byte.Parse(_args[2]);	// Lists the insert position
					_pos2 = 0;	// Meaningless
					_rule = new TemperatureRule((TemperatureRule.DayType) int.Parse(_args[3]), (float) double.Parse(_args[4]), (float) double.Parse(_args[5]));
					break;
				case "DELETE":	// Delete the rule at the specified index
					if(_args.Length != 3) throw new ArgumentException("Deleting a rule requires 3 arguments.");
					_operation = Operation.Delete;
					_pos1 = byte.Parse(_args[2]);	// Lists the element to delete
					_pos2 = 0;	// Meaningless
					_rule = null;	// Meaningless
					break;
				case "MOVE":	// Move the specified rule to the new index
					if(_args.Length != 4) throw new ArgumentException("Moving a rule requires 4 arguments.");
					_operation = Operation.Move;
					_pos1 = byte.Parse(_args[2]);	// The index of the rule to move
					_pos2 = byte.Parse(_args[3]);	// The index of the new position
					_rule = null;	// Meaningless
					break;
				case "UPDATE":	// Update the rule at the specified index
					if(_args.Length != 6) throw new ArgumentException("Updating a rule requires 6 arguments");
					_operation = Operation.Update;
					_pos1 = byte.Parse(_args[2]);	// Lists the update index
					_pos2 = 0;	// Meaningless
					_rule = new TemperatureRule((TemperatureRule.DayType) int.Parse(_args[3]), (float) double.Parse(_args[4]), (float) double.Parse(_args[5]));
					break;
				default:
					throw new ArgumentException("Rule Change Command '" + CMD + "' Not Currently Handled.");
			}
		}

		public Operation ChangeRequested {
			get { return _operation; }
		}

		public byte FirstPosition {
			get { return _pos1; }
		}

		public byte SecondPosition {
			get { return _pos2; }
		}

		public TemperatureRule Rule {
			get { return _rule; }
		}
	}

	//=========================================================================
	// DataRequestArgs Class
	//=========================================================================
	/// <summary>
	/// This class handles any requests for data from the relay
	/// </summary>
	public class DataRequestArgs : RequestArgs {
		public DataRequestArgs(char[] Data) : base(Data) { }
	}

	//=========================================================================
	// TimeRequestArgs Class
	//=========================================================================
	/// <summary>
	/// This class handles any requests for time settings from the relay
	/// </summary>
	public class TimeRequestArgs : RequestArgs {
		//=====================================================================
		// CLASS ENUMERATIONS
		//=====================================================================
		public enum Operations { Get, Set };

		//=====================================================================
		// CLASS MEMBERS
		//=====================================================================
		public Operations Request { get; private set; }
		public byte Seconds { get; private set; }
		public byte Minutes { get; private set; }
		public byte Hours { get; private set; }
		public byte Day { get; private set; }
		public byte Month { get; private set; }
		public byte Year { get; private set; }
		public byte Weekday { get; private set; }

		//=====================================================================
		// Default Constructor
		//=====================================================================
		/// <summary>
		/// This class will read the request and covert the data correcting into the object
		/// </summary>
		/// <param name="Data">The received request</param>
		public TimeRequestArgs(char[] Data) : base(Data) {
			// Check the command
			string CMD = _args[1].ToUpper();
			switch(CMD) {
				case "GET":	// Get the current time from the relay clock
					Request = Operations.Get;
					break;
				case "SET":	// Set the time on the relay clock
					// Check number of arguments
					if(_args.Length != 9) throw new ArgumentException("Incorrect number of arguments sent to time request");

					// Get the data
					Request = Operations.Set;
					Seconds = byte.Parse(_args[2]);
					Minutes = byte.Parse(_args[3]);
					Hours = byte.Parse(_args[4]);
					Weekday = byte.Parse(_args[5]);
					Day = byte.Parse(_args[6]);
					Month = byte.Parse(_args[7]);
					Year = byte.Parse(_args[8]);
					break;
				default:	// Something else sent
					throw new ArgumentException("Time Command '" + CMD + "' Not Currently Handled.");
			}
		}
	}

	//=========================================================================
	// StatusRequestArgs Class
	//=========================================================================
	/// <summary>
	/// This class handles status requests
	/// </summary>
	public class StatusRequestArgs : RequestArgs {
		public StatusRequestArgs(char[] Data) : base(Data) { }
	}

	//=========================================================================
	// TemperatureRule Class
	//=========================================================================
	/// <summary>
	/// 
	/// </summary>
	public class TemperatureRule {
		private const int PACKET_LENGTH	= 9;
		private const int FLOAT_LENGTH	= 4;

		public enum DayType { Sunday, Monday, Tuesday, Wednesday, Thursday, Friday, Saturday, Weekdays, Weekends, Everyday }

		private DayType _days;
		private float _time;
		private float _temp;
	
		public TemperatureRule(DayType setRuleDay, float setRuleTime, float setRuleTemp) {
			_days = setRuleDay;
			_time = setRuleTime;
			_temp = setRuleTemp;
		}

		public DayType Days {
			get { return _days; }
		}

		public float Time {
			get { return _time; }
		}

		public float Temperature {
			get { return _temp; }
		}

		public TemperatureRule(byte[] packetData) {
			// Confirm the size of the packet
			if(packetData.Length != PACKET_LENGTH) throw new ArgumentException("TemperatureRule trying to instantiate based on incorrect byte packet length.");
			Debug.Assert(sizeof(float) == FLOAT_LENGTH);	// Debuggin check on the hardware

			// Get the float arrays
			byte[] time = new byte[FLOAT_LENGTH];
			byte[] temp = new byte[FLOAT_LENGTH];
			for(int i = 0; i < FLOAT_LENGTH; i++) {
				time[i] = packetData[i + 1];
				temp[i] = packetData[i + FLOAT_LENGTH + 1];
			}

			// Set the values
			_days = (DayType) packetData[0];
			_time = Converters.ByteToFloat(time);
			_temp = Converters.ByteToFloat(temp);
		}

		public byte[] toByteArray() {
			Debug.Assert(sizeof(float) == FLOAT_LENGTH);	// Debugging check on the hardware

			// Get the byte bits
			byte day = (byte) _days;
			byte[] time = Converters.FloatToByte(_time);
			byte[] temp = Converters.FloatToByte(_temp);

			// Create the packet
			byte[] packet = new byte[PACKET_LENGTH];
			packet[0] = day;
			for(int i = 0; i < FLOAT_LENGTH; i++) {
				packet[i + 1] = time[i];
				packet[i + FLOAT_LENGTH + 1] = temp[i];
			}

			return packet;
		}
	}
}
