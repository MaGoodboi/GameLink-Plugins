﻿using ForzaHorizon4Plugin.Properties;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using YawGLAPI;

namespace YawVR_Game_Engine.Plugin
{
    [Export(typeof(Game))]
	[ExportMetadata("Name", "Forza Horizon 4")]
	[ExportMetadata("Version", "1.0")]
	public class ForzaHorizon4Plugin : Game {
		[StructLayout(LayoutKind.Sequential)]
		private class ForzaTelemetry {
			public uint IsRaceOn; // = 1 when race is on. = 0 when in menus/race stopped …

			public uint TimestampMS; //Can overflow to 0 eventually

			public float EngineMaxRpm;
			public float EngineIdleRpm;
			public float CurrentEngineRpm;

			public float AccelerationX; //In the car's local space; X = right, Y = up, Z = forward
			public float AccelerationY;
			public float AccelerationZ;

			public float VelocityX; //In the car's local space; X = right, Y = up, Z = forward
			public float VelocityY;
			public float VelocityZ;

			public float AngularVelocityX; //In the car's local space; X = pitch, Y = yaw, Z = roll
			public float AngularVelocityY;
			public float AngularVelocityZ;

			public float Yaw;
			public float Pitch;
			public float Roll;

			public float NormalizedSuspensionTravelFrontLeft; // Suspension travel normalized: 0.0f = max stretch; 1.0 = max compression
			public float NormalizedSuspensionTravelFrontRight;
			public float NormalizedSuspensionTravelRearLeft;
			public float NormalizedSuspensionTravelRearRight;

			public float TireSlipRatioFrontLeft; // Tire normalized slip ratio, = 0 means 100% grip and |ratio| > 1.0 means loss of grip.
			public float TireSlipRatioFrontRight;
			public float TireSlipRatioRearLeft;
			public float TireSlipRatioRearRight;

			public float WheelRotationSpeedFrontLeft; // Wheel rotation speed radians/sec.
			public float WheelRotationSpeedFrontRight;
			public float WheelRotationSpeedRearLeft;
			public float WheelRotationSpeedRearRight;

			public uint WheelOnRumbleStripFrontLeft; // = 1 when wheel is on rumble strip, = 0 when off.
			public uint WheelOnRumbleStripFrontRight;
			public uint WheelOnRumbleStripRearLeft;
			public uint WheelOnRumbleStripRearRight;

			public float WheelInPuddleDepthFrontLeft; // = from 0 to 1, where 1 is the deepest puddle
			public float WheelInPuddleDepthFrontRight;
			public float WheelInPuddleDepthRearLeft;
			public float WheelInPuddleDepthRearRight;

			public float SurfaceRumbleFrontLeft; // Non-dimensional surface rumble values passed to controller force feedback
			public float SurfaceRumbleFrontRight;
			public float SurfaceRumbleRearLeft;
			public float SurfaceRumbleRearRight;

			public float TireSlipAngleFrontLeft; // Tire normalized slip angle, = 0 means 100% grip and |angle| > 1.0 means loss of grip.
			public float TireSlipAngleFrontRight;
			public float TireSlipAngleRearLeft;
			public float TireSlipAngleRearRight;

			public float TireCombinedSlipFrontLeft; // Tire normalized combined slip, = 0 means 100% grip and |slip| > 1.0 means loss of grip.
			public float TireCombinedSlipFrontRight;
			public float TireCombinedSlipRearLeft;
			public float TireCombinedSlipRearRight;

			public float SuspensionTravelMetersFrontLeft; // Actual suspension travel in meters
			public float SuspensionTravelMetersFrontRight;
			public float SuspensionTravelMetersRearLeft;
			public float SuspensionTravelMetersRearRight;

			public uint CarOrdinal; //Unique ID of the car make/model
			public uint CarClass; //Between 0 (D -- worst cars) and 7 (X class -- best cars) inclusive
			public uint CarPerformanceIndex; //Between 100 (slowest car) and 999 (fastest car) inclusive
			public uint DrivetrainType; //Corresponds to EDrivetrainType; 0 = FWD, 1 = RWD, 2 = AWD
			public uint NumCylinders; //Number of cylinders in the engine


			//Position (meters)
			public float PositionX;
			public float PositionY;
			public float PositionZ;

			public float Speed; // meters per second
			public float Power; // watts
			public float Torque; // newton meter

			public float TireTempFrontLeft;
			public float TireTempFrontRight;
			public float TireTempRearLeft;
			public float TireTempRearRight;

			public float Boost;
			public float Fuel;
			public float DistanceTraveled;
			public float BestLap;
			public float LastLap;
			public float CurrentLap;
			public float CurrentRaceTime;

			public ushort LapNumber;
			public byte RacePosition;

			public byte Accel;
			public byte Brake;
			public byte Clutch;
			public byte HandBrake;
			public byte Gear;
			public sbyte Steer;

			public sbyte NormalizedDrivingLine;
			public sbyte NormalizedAIBrakeDifference;

		}

		private bool stop = false;
		private Thread readthread;
		UdpClient receivingUdpClient;
		IPEndPoint RemoteIpEndPoint = new IPEndPoint(IPAddress.Any, 0);
		private Process process;
        private IMainFormDispatcher dispatcher;
        private IProfileManager controller;

        public string PROCESS_NAME => "ForzaHorizon4";
        public int STEAM_ID => 1293830;
		public string AUTHOR => "YawVR";
		public bool PATCH_AVAILABLE => true;

		public string Description => Resources.description;

		public Stream Logo => GetStream("logo.png");
		public Stream SmallLogo => GetStream("recent.png");
		public Stream Background => GetStream("wide.png");



		public LedEffect DefaultLED() {
			return new LedEffect(

		   EFFECT_TYPE.KNIGHT_RIDER_2,
		   4,
		   new YawColor[] {
				new YawColor(66, 135, 245),
				 new YawColor(80,80,80),
				new YawColor(128, 3, 117),
				new YawColor(110, 201, 12),
				},
		   25f);

		}

		public List<Profile_Component> DefaultProfile() {

			return dispatcher.JsonToComponents(Resources.defProfile);
			
		}

		public void Exit() {
			receivingUdpClient.Close();
			receivingUdpClient = null;
			stop = true;
			//readthread.Abort();

		}
		public string[] GetInputData() {

			Type t = typeof(ForzaTelemetry);
			FieldInfo[] fields = t.GetFields();

			string[] inputs = new string[fields.Length];

			for (int i = 0; i < fields.Length; i++) {
				inputs[i] = fields[i].Name;
			}
			return inputs;
		}


		public void SetReferences(IProfileManager controller, IMainFormDispatcher dispatcher)
		{
			this.dispatcher = dispatcher;
			this.controller = controller;
		}
		public void Init() {
			Addloopback();
			stop = false;
			receivingUdpClient = new UdpClient(20127);
			readthread = new Thread(new ThreadStart(ReadFunction));
			readthread.Start();
		}

		private void ReadFunction() {

			Console.WriteLine("ForzaRD");
			ForzaTelemetry obj = new ForzaTelemetry();
			FieldInfo[] fields = typeof(ForzaTelemetry).GetFields();
			while (!stop) {
				try
				{
					// Blocks until a message returns on this socket from a remote host.
					byte[] rawData = receivingUdpClient.Receive(ref RemoteIpEndPoint);
					Console.Write(BitConverter.ToSingle(rawData, 0));

					IntPtr unmanagedPointer = Marshal.AllocHGlobal(rawData.Length);
					Marshal.Copy(rawData, 0, unmanagedPointer, rawData.Length);
					// Call unmanaged code
					Marshal.FreeHGlobal(unmanagedPointer);
					Marshal.PtrToStructure(unmanagedPointer, obj);

					obj.Yaw *= 57.295f;
					obj.Pitch *= 57.295f;
					obj.Roll *= 57.295f;

					obj.AngularVelocityX *= 57.295f;
					obj.AngularVelocityY *= 57.295f;
					obj.AngularVelocityZ *= 57.295f;
					for (int i = 0; i < fields.Length; i++)
					{
						controller.SetInput(i, (float)Convert.ChangeType(fields[i].GetValue(obj), TypeCode.Single));
					}
				} catch(SocketException) { }
			}
		}
		public void PatchGame() {
			Addloopback();
		}

   
        public void Addloopback() {
			var proc1 = new ProcessStartInfo();
			proc1.UseShellExecute = true;

			proc1.WorkingDirectory = @"C:\Windows\System32";

			proc1.FileName = @"C:\Windows\System32\cmd.exe";
			proc1.Verb = "runas";
			proc1.Arguments = "/c " + "checknetisolation loopbackexempt -a -n=Microsoft.SunriseBaseGame_1.351.461.2_x64__8wekyb3d8bbwe";
			proc1.WindowStyle = ProcessWindowStyle.Normal;
			Process.Start(proc1);

		}

        public Dictionary<string, ParameterInfo[]> GetFeatures()
        {
            return null;
        }
        Stream GetStream(string resourceName)
        {
            var assembly = GetType().Assembly;
            var rr = assembly.GetManifestResourceNames();
            string fullResourceName = $"{assembly.GetName().Name}.Resources.{resourceName}";
            return assembly.GetManifestResourceStream(fullResourceName);
        }
    }
}