using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Android.App;
using Android.Bluetooth;
using Android.Content;
using Android.OS;

using Java.Util;

using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.Extensions;
using Plugin.BLE.Abstractions.Utils;
using Plugin.BLE.Android.CallbackEventArgs;
using Plugin.BLE.Extensions;

using Trace = Plugin.BLE.Abstractions.Trace;

namespace Plugin.BLE.Android;

public class Device : DeviceBase<BluetoothDevice>
{
	/// <summary>
	/// we have to keep a reference to this because Android's api is weird and requires
	/// the GattServer in order to do nearly anything, including enumerating services
	/// </summary>
	internal BluetoothGatt Gatt;

	/// <summary>
	/// we also track this because of google's weird API. the gatt callback is where
	/// we'll get notified when services are enumerated
	/// </summary>
	private readonly GattCallback _gattCallback;

	/// <summary>
	/// the registration must be disposed to avoid disconnecting after a connection
	/// </summary>
	private CancellationTokenRegistration _connectCancellationTokenRegistration;

	private TaskCompletionSource<bool> _bondCompleteTaskCompletionSource;

	/// <summary>
	/// the connect parameters used when connecting to this device
	/// </summary>
	public ConnectParameters ConnectParameters { get; private set; }

	public Device(Adapter adapter, BluetoothDevice nativeDevice, BluetoothGatt gatt, int rssi = 0, byte[] advertisementData = null, bool isConnectable = true) : base(adapter, nativeDevice)
	{
		Update(nativeDevice, gatt);
		Rssi = rssi;
		AdvertisementRecords = ParseScanRecord(advertisementData);
		IsConnectable = isConnectable;
		_gattCallback = new(adapter, this);
	}

	public void Update(BluetoothDevice nativeDevice, BluetoothGatt gatt)
	{
		_connectCancellationTokenRegistration.Dispose();
		_connectCancellationTokenRegistration = new();

		NativeDevice = nativeDevice;
		Gatt = gatt;

		Id = ParseDeviceId();
		Name = NativeDevice.Name;
	}

	internal bool IsOperationRequested { get; set; }

		protected override async Task<IReadOnlyList<IService>> GetServicesNativeAsync(CancellationToken cancellationToken)
		{
			if (_gattCallback == null || Gatt == null)
			{
				return new List<IService>();
			}

			// Gatt.Services is already populated if device service discovery was already done
			if (Gatt.Services.Any())
			{
				return Gatt.Services.Select(service => new Service(service, Gatt, _gattCallback, this)).ToList();
			}

			return await DiscoverServicesInternal(cancellationToken);
		}

		protected override async Task<IService> GetServiceNativeAsync(Guid id, CancellationToken cancellationToken)
		{
			if (_gattCallback == null || Gatt == null)
			{
				return null;
			}

		var uuid = UUID.FromString(id.ToString("d"));

		// Gatt.GetService will directly return if device service discovery was already done
		var nativeService = Gatt.GetService(uuid);
		if (nativeService != null)
			return new Service(nativeService, Gatt, _gattCallback, this);

			var services = await DiscoverServicesInternal(cancellationToken);
			return services?.FirstOrDefault(service => service.Id == id);
		}

		private async Task<IReadOnlyList<IService>> DiscoverServicesInternal(CancellationToken cancellationToken)
		{
			if (Gatt == null)
			{
				Trace.Message("[Warning]: Can't discover services {0}. Gatt is null.", Name);
			}
			
			return await TaskBuilder
				.FromEvent<IReadOnlyList<IService>, EventHandler<ServicesDiscoveredCallbackEventArgs>, EventHandler>(
					execute: () =>
					{
						if (!Gatt.DiscoverServices())
						{
							throw new Exception("Could not start service discovery");
						}
					},
					getCompleteHandler: (complete, reject) => (sender, args) =>
					{
						if (Gatt.Services == null)
						{
							complete(new List<IService>());
						}
						else
						{
							complete(Gatt.Services.Select(service => new Service(service, Gatt, _gattCallback, this)).ToList());
						}
					},
					subscribeComplete: handler => _gattCallback.ServicesDiscovered += handler,
					unsubscribeComplete: handler => _gattCallback.ServicesDiscovered -= handler,
					getRejectHandler: reject => (sender, args) =>
					{
						reject(new Exception($"Device {Name} disconnected while fetching services."));
					},
					subscribeReject: handler => _gattCallback.ConnectionInterrupted += handler,
					unsubscribeReject: handler => _gattCallback.ConnectionInterrupted -= handler,
					token: cancellationToken);
		}

	public void Connect(ConnectParameters connectParameters, CancellationToken cancellationToken)
	{
		IsOperationRequested = true;
		ConnectParameters = connectParameters;

		if (connectParameters.ForceBleTransport)
			ConnectToGattForceBleTransportApi(connectParameters.AutoConnect, cancellationToken);
		else
		{
			var connectGatt = NativeDevice.ConnectGatt(Application.Context, connectParameters.AutoConnect, _gattCallback);
			_connectCancellationTokenRegistration.Dispose();
			_connectCancellationTokenRegistration = cancellationToken.Register(() => DisconnectAndClose(connectGatt));
		}
	}

	public Task BondAsync()
	{
		_bondCompleteTaskCompletionSource = new();
		NativeDevice.CreateBond();
		return _bondCompleteTaskCompletionSource.Task;
	}

	private void ConnectToGattForceBleTransportApi(bool autoConnect, CancellationToken cancellationToken)
	{
		//This parameter is present from API 18 but only public from API 23
		//So reflection is used before API 23
		#if NET6_0_OR_GREATER
		if (OperatingSystem.IsAndroidVersionAtLeast(23))
		#else
		if (Build.VERSION.SdkInt >= BuildVersionCodes.M)
		#endif
		{
			var connectGatt = NativeDevice.ConnectGatt(Application.Context, autoConnect, _gattCallback, BluetoothTransports.Le);
			_connectCancellationTokenRegistration.Dispose();
			_connectCancellationTokenRegistration = cancellationToken.Register(() => DisconnectAndClose(connectGatt));
		}
		#if NET6_0_OR_GREATER
		else if (OperatingSystem.IsAndroidVersionAtLeast(21))
		#else
		else if (Build.VERSION.SdkInt >= BuildVersionCodes.Lollipop)
		#endif
		{
			var m = NativeDevice.Class.GetDeclaredMethod("connectGatt",
			[
				Java.Lang.Class.FromType(typeof(Context)),
				Java.Lang.Boolean.Type,
				Java.Lang.Class.FromType(typeof(BluetoothGattCallback)),
				Java.Lang.Integer.Type
			]);

			var transport = NativeDevice.Class.GetDeclaredField("TRANSPORT_LE").GetInt(null); // LE = 2, BREDR = 1, AUTO = 0
			m.Invoke(NativeDevice, Application.Context, false, _gattCallback, transport);
		}
		else
		{
			//no transport mode before lollipop, it will probably not work... gattCallBackError 133 again alas
			var connectGatt = NativeDevice.ConnectGatt(Application.Context, autoConnect, _gattCallback);
			_connectCancellationTokenRegistration.Dispose();
			_connectCancellationTokenRegistration = cancellationToken.Register(() => DisconnectAndClose(connectGatt));
		}
	}

	private void DisconnectAndClose(BluetoothGatt gatt)
	{
		gatt?.Disconnect();
		gatt?.Close();
	}

	/// <summary>
	/// This method is only called by a user triggered disconnect.
	/// A user will first trigger Gatt.Disconnect -> which in turn will trigger Gatt.Close() via the gattCallback
	/// </summary>
	public void Disconnect()
	{
		if (Gatt != null)
		{
			IsOperationRequested = true;
			ClearServices();
			Gatt.Disconnect();
		}
		else
			Trace.Message("[Warning]: Can't disconnect {0}. Gatt is null.", Name);
	}

	/// <summary>
	/// CloseGatt is called by the gattCallback in case of user disconnect or a disconnect by signal loss or a connection error.
	/// Clears all cached services.
	/// </summary>
	public void CloseGatt()
	{
		Gatt?.Close();
		Gatt = null;

		// CloseGatt might will get called on signal loss without Disconnect being called we have to make sure we clear the services
		// Clear services & characteristics otherwise we will get gatt operation return FALSE when connecting to the same IDevice instace at a later time
		ClearServices();
	}

	protected override DeviceState GetState()
	{
		var manager = (BluetoothManager)Application.Context.GetSystemService(Context.BluetoothService);
		var state = manager.GetConnectionState(NativeDevice, ProfileType.Gatt);

		return state switch
		{
			ProfileState.Connected =>
				// if the device does not have a gatt instance we can't use it in the app, so we need to explicitly be able to connect it
				// even if the profile state is connected
				Gatt != null ? DeviceState.Connected : DeviceState.Limited,
			ProfileState.Connecting => DeviceState.Connecting,
			ProfileState.Disconnected or ProfileState.Disconnecting => DeviceState.Disconnected,
			_ => DeviceState.Disconnected
		};
	}

	private Guid ParseDeviceId()
	{
		var deviceGuid = new byte[16];
		var macWithoutColons = NativeDevice.Address.Replace(":", "");
		var macBytes = Enumerable.Range(0, macWithoutColons.Length)
			.Where(x => x % 2 == 0)
			.Select(x => Convert.ToByte(macWithoutColons.Substring(x, 2), 16))
			.ToArray();
		macBytes.CopyTo(deviceGuid, 10);
		return new(deviceGuid);
	}

	public List<AdvertisementRecord> ParseScanRecord(byte[] scanRecord)
	{
		var records = new List<AdvertisementRecord>();

		if (scanRecord == null)
			return records;

		try
		{
			var index = 0;
			while (index < scanRecord.Length)
			{
				var length = scanRecord[index++];
				//Done once we run out of records
				// 1 byte for type and length-1 bytes for data
				if (length == 0) break;

				int type = scanRecord[index];
				//Done if our record isn't a valid type
				if (type == 0) break;

				if (!Enum.IsDefined(typeof(AdvertisementRecordType), type))
				{
					Trace.Message("Advertisement record type not defined: {0}", type);
					break;
				}

				//data length is length -1 because type takes the first byte
				var data = new byte[length - 1];
				Array.Copy(scanRecord, index + 1, data, 0, length - 1);

				// don't forget that data is little endian so reverse
				// Supplement to Bluetooth Core Specification 1
				// NOTE: all relevant devices are already little endian, so this is not necessary for any type except UUIDs
				//var record = new AdvertisementRecord((AdvertisementRecordType)type, data.Reverse().ToArray());

				switch ((AdvertisementRecordType)type)
				{
					case AdvertisementRecordType.ServiceDataUuid32Bit:
					case AdvertisementRecordType.SsUuids128Bit:
					case AdvertisementRecordType.SsUuids16Bit:
					case AdvertisementRecordType.SsUuids32Bit:
					case AdvertisementRecordType.UuidCom32Bit:
					case AdvertisementRecordType.UuidsComplete128Bit:
					case AdvertisementRecordType.UuidsComplete16Bit:
					case AdvertisementRecordType.UuidsIncomplete16Bit:
					case AdvertisementRecordType.UuidsIncomplete128Bit:
						Array.Reverse(data);
						break;
				}

				var record = new AdvertisementRecord((AdvertisementRecordType)type, data);
				Trace.Message(record.ToString());
				records.Add(record);

				//Advance
				index += length;
			}
		}
		catch(Exception)
		{
			//There may be a situation where scanRecord contains incorrect data.
			Trace.Message($"Failed to parse advertisementData. Device address: {NativeDevice.Address}, Data: {scanRecord.ToHexString()}");
		}
		return records;
	}

		public override async Task<bool> UpdateRssiAsync(CancellationToken cancellationToken)
		{
			if (Gatt == null || _gattCallback == null)
			{
				Trace.Message("You can't read the RSSI value for disconnected devices except on discovery on Android. Device is {0}", State);
				return false;
			}

			return await TaskBuilder.FromEvent<bool, EventHandler<RssiReadCallbackEventArgs>, EventHandler>(
			  execute: () => Gatt.ReadRemoteRssi(),
			  getCompleteHandler: (complete, reject) => (sender, args) =>
			  {
				  if (args.Error == null)
				  {
					  Trace.Message("Read RSSI for {0} {1}: {2}", Id, Name, args.Rssi);
					  Rssi = args.Rssi;
					  complete(true);
				  }
				  else
				  {
					  Trace.Message($"Failed to read RSSI for device {Id}-{Name}. {args.Error.Message}");
					  complete(false);
				  }
			  },
			  subscribeComplete: handler => _gattCallback.RemoteRssiRead += handler,
			  unsubscribeComplete: handler => _gattCallback.RemoteRssiRead -= handler,
			  getRejectHandler: reject => (sender, args) =>
			  {
				  reject(new Exception($"Device {Name} disconnected while updating rssi."));
			  },
			  subscribeReject: handler => _gattCallback.ConnectionInterrupted += handler,
			  unsubscribeReject: handler => _gattCallback.ConnectionInterrupted -= handler,
			  token: cancellationToken);
		}

		protected override async Task<int> RequestMtuNativeAsync(int requestValue, CancellationToken cancellationToken)
		{
			if (Gatt == null || _gattCallback == null)
			{
				Trace.Message("You can't request a MTU for disconnected devices. Device is {0}", State);
				return -1;
			}

		if (Build.VERSION.SdkInt < BuildVersionCodes.Lollipop)
		{
			Trace.Message($"Request MTU not supported in this Android API level");
			return -1;
		}

			return await TaskBuilder.FromEvent<int, EventHandler<MtuRequestCallbackEventArgs>, EventHandler>(
			  execute: () => Gatt.RequestMtu(requestValue),
			  getCompleteHandler: (complete, reject) => (sender, args) =>
			  {
				   if (args.Error != null)
				   {
					   Trace.Message($"Failed to request MTU ({requestValue}) for device {Id}-{Name}. {args.Error.Message}");
					   reject(new Exception($"Request MTU error: {args.Error.Message}"));
				   }
				   else
				   {
					   complete(args.Mtu);
				   }
			  },
			  subscribeComplete: handler => _gattCallback.MtuRequested += handler,
			  unsubscribeComplete: handler => _gattCallback.MtuRequested -= handler,
			  getRejectHandler: reject => (sender, args) =>
			  {
				   reject(new Exception($"Device {Name} disconnected while requesting MTU."));
			  },
			  subscribeReject: handler => _gattCallback.ConnectionInterrupted += handler,
			  unsubscribeReject: handler => _gattCallback.ConnectionInterrupted -= handler,
			  token: cancellationToken
			);
		}

	protected override bool UpdateConnectionIntervalNative(ConnectionInterval interval)
	{
		if (Gatt == null || _gattCallback == null)
		{
			Trace.Message($"You can't update a connection interval for disconnected devices. Device is {State}");
			return false;
		}

		if (Build.VERSION.SdkInt < BuildVersionCodes.Lollipop)
		{
			Trace.Message("Update connection interval parameter in this Android API level");
			return false;
		}

		try
		{
			// map to android gattConnectionPriorities
			// https://developer.android.com/reference/android/bluetooth/BluetoothGatt.html#CONNECTION_PRIORITY_BALANCED
			return Gatt.RequestConnectionPriority((GattConnectionPriority)(int)interval);
		}
		catch (Exception ex)
		{
			throw new($"Update Connection Interval fails with error. {ex.Message}");
		}
	}


	public override bool IsConnectable { get; protected set; }

	public override bool SupportsIsConnectable
	{
		get =>
		#if NET6_0_OR_GREATER
			OperatingSystem.IsAndroidVersionAtLeast(26);
		#else
				Build.VERSION.SdkInt >= BuildVersionCodes.O;
		#endif
	}

	protected override DeviceBondState GetBondState()
	{
		if (NativeDevice == null)
		{
			Trace.Message($"[Warning]: Can't get bond state of {Name}. NativeDevice is null.");
			return DeviceBondState.NotSupported;
		}

		return NativeDevice.BondState.XPlatformBondState();
	}

	public override bool UpdateConnectionParameters(ConnectParameters connectParameters = default) => throw new NotImplementedException();
}