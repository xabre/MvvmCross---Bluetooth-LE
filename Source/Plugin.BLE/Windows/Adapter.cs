using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Extensions;
using System.Collections.Concurrent;
using Windows.Devices.Enumeration;

namespace Plugin.BLE.Windows
{
    public class Adapter : AdapterBase
    {
        private readonly BluetoothAdapter _bluetoothAdapter;
        private BluetoothLEAdvertisementWatcher _bleWatcher;

		/// <summary>
        /// Registry used to store device instances for pending disconnect operations
        /// Helps to detect connection lost events.
        /// </summary>
        private readonly IDictionary<string, IDevice> disconnectingRegistry = new ConcurrentDictionary<string, IDevice>();

		private readonly DeviceWatcher _pairedDeviceWatcher;

		public Adapter(BluetoothAdapter adapter)
		{
			_bluetoothAdapter = adapter;

			ConcurrentDictionary<string, Device> bondedDevices = new();
			_pairedDeviceWatcher = DeviceInformation.CreateWatcher(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true));

			_pairedDeviceWatcher.Added += (_, args) =>
			{
                if ((Device)GetBondedDevices().First(dev => dev.Id == args.Id.ToBleDeviceGuidFromId()) is { } device)
					HandleDeviceBondStateChanged(new() { Device = bondedDevices[args.Id] = device, Address = args.Id.ToBleDeviceGuidFromId().ToBleAddress().ToHexBleAddress(), State = DeviceBondState.Bonded });
			};

			_pairedDeviceWatcher.Removed += (_, args) =>
			{
				if (bondedDevices.TryRemove(args.Id, out Device device))
					HandleDeviceBondStateChanged(new() { Device = device, Address = args.Id.ToBleDeviceGuidFromId().ToBleAddress().ToHexBleAddress(), State = DeviceBondState.NotBonded });
			};

			_pairedDeviceWatcher.Start();
		}

        public override async Task BondAsync(IDevice device)
        {
            var bleDevice = device.NativeDevice as BluetoothLEDevice;
            if (bleDevice is null)
            {
                Trace.Message($"BondAsync failed since NativeDevice is null with: {device.Name}: {device.Id} ");
                return;
            }
            DeviceInformation deviceInformation = await DeviceInformation.CreateFromIdAsync(bleDevice.DeviceId);
            if (deviceInformation.Pairing.IsPaired)
            {
                Trace.Message($"BondAsync is already paired with: {device.Name}: {device.Id}");
                return;
            }
            if (!deviceInformation.Pairing.CanPair)
            {
                Trace.Message($"BondAsync cannot pair with: {device.Name}: {device.Id}");
                return;
            }
            DeviceInformationCustomPairing p = deviceInformation.Pairing.Custom;
            p.PairingRequested += PairingRequestedHandler;
            var result = await p.PairAsync(DevicePairingKinds.ConfirmOnly);
            p.PairingRequested -= PairingRequestedHandler;
            Trace.Message($"BondAsync pairing result was {result.Status} with: {device.Name}: {device.Id}");
        }

        private static void PairingRequestedHandler(DeviceInformationCustomPairing sender, DevicePairingRequestedEventArgs args)
        {
            switch (args.PairingKind)
            {
                case DevicePairingKinds.ConfirmOnly:
                    args.Accept();
                    break;

                default:
                    Trace.Message("PairingKind " + args.PairingKind + " not supported");
                    break;
            }
        }

        protected override Task StartScanningForDevicesNativeAsync(ScanFilterOptions scanFilterOptions, bool allowDuplicatesKey, CancellationToken scanCancellationToken)
        {
            var serviceUuids = scanFilterOptions?.ServiceUuids;
            var hasFilter = serviceUuids?.Any() ?? false;

            _bleWatcher = new BluetoothLEAdvertisementWatcher { ScanningMode = ScanMode.ToNative(), AllowExtendedAdvertisements = true };

            Trace.Message("Starting a scan for devices.");
            if (hasFilter)
            {
                //adds filter to native scanner if serviceUuids are specified
                foreach (var uuid in serviceUuids)
                {
                    _bleWatcher.AdvertisementFilter.Advertisement.ServiceUuids.Add(uuid);
                }

                Trace.Message($"ScanFilters: {string.Join(", ", serviceUuids)}");
            }
            _bleWatcher.Received -= AdvertisementReceived;
            _bleWatcher.Received += AdvertisementReceived;
            _bleWatcher.Start();
            return Task.FromResult(true);
        }

        protected override void StopScanNative()
        {
            if (_bleWatcher != null)
            {
                Trace.Message("Stopping the scan for devices");
                _bleWatcher.Stop();
                _bleWatcher = null;
            }
        }

        protected override async Task ConnectToDeviceNativeAsync(IDevice device, ConnectParameters connectParameters, CancellationToken cancellationToken)
        {
            var dev = device as Device;
            if (dev.NativeDevice == null)
            {
                await dev.RecreateNativeDevice();
            }
            var nativeDevice = device.NativeDevice as BluetoothLEDevice;
            Trace.Message("ConnectToDeviceNativeAsync {0} Named: {1} Connected: {2}", device.Id.ToHexBleAddress(), device.Name, nativeDevice.ConnectionStatus);


            bool success = await dev.ConnectInternal(connectParameters, cancellationToken);
            if (success)
            {
                if (!ConnectedDeviceRegistry.ContainsKey(device.Id.ToString()))
                {
                    ConnectedDeviceRegistry[device.Id.ToString()] = device;
                    nativeDevice.ConnectionStatusChanged += Device_ConnectionStatusChanged;
                    if (nativeDevice.ConnectionStatus == BluetoothConnectionStatus.Connected)
                    {
                        Device_ConnectionStatusChanged(nativeDevice, null);
                    }
                }
            }
            else
            {
                // use DisconnectDeviceNative to clean up resources otherwise windows won't disconnect the device
                // after a subsequent successful connection (#528, #536, #423)
                DisconnectDeviceNative(device);

                // fire a connection failed event
                HandleConnectionFail(device, "Failed connecting to device.");

                // this is normally done in Device_ConnectionStatusChanged but since nothing actually connected
                // or disconnect, ConnectionStatusChanged will not fire.                
                ConnectedDeviceRegistry.TryRemove(device.Id.ToString(), out _);
            }
        }

        private void Device_ConnectionStatusChanged(BluetoothLEDevice nativeDevice, object args)
        {
            Trace.Message("Device_ConnectionStatusChanged {0} {1} {2}",
                nativeDevice.BluetoothAddress.ToHexBleAddress(),
                nativeDevice.Name,
                nativeDevice.ConnectionStatus);
            var id = nativeDevice.BluetoothAddress.ParseDeviceId().ToString();

            if (nativeDevice.ConnectionStatus == BluetoothConnectionStatus.Connected
                && ConnectedDeviceRegistry.TryGetValue(id, out var connectedDevice))
            {
#if WINDOWS10_0_22000_0_OR_GREATER
                if (Environment.OSVersion.Version.Build >= 22000)
                {
                    var conpar = nativeDevice.GetConnectionParameters();
                    Trace.Message(
                        $"Connected with Latency = {conpar.ConnectionLatency}, "
                        + $"Interval = {conpar.ConnectionInterval}, Timeout = {conpar.LinkTimeout}");
                }
#endif
                HandleConnectedDevice(connectedDevice);
                return;
            }

            if (nativeDevice.ConnectionStatus == BluetoothConnectionStatus.Disconnected
                && ConnectedDeviceRegistry.TryRemove(id, out var disconnectedDevice))
            {
                bool disconnectRequested = disconnectingRegistry.Remove(id);
                if (!disconnectRequested)
                {
                    // Device was powered off or went out of range. Call DisconnectInternal to cleanup
                    // resources otherwise windows will not disconnect on a subsequent connect-disconnect.
                    ((Device)disconnectedDevice).DisconnectInternal();
                }
                ConnectedDeviceRegistry.Remove(id, out _);
                nativeDevice.ConnectionStatusChanged -= Device_ConnectionStatusChanged;
                // fire the correct event (DeviceDisconnected or DeviceConnectionLost)
                HandleDisconnectedDevice(disconnectRequested, disconnectedDevice);
            }
        }

        protected override void DisconnectDeviceNative(IDevice device)
        {
            // Windows doesn't support disconnecting, so currently just dispose of the device
            Trace.Message($"DisconnectDeviceNative from device with ID:  {device.Id.ToHexBleAddress()}");
            disconnectingRegistry[device.Id.ToString()] = device;
            ((Device)device).DisconnectInternal();
        }

        public override async Task<IDevice> ConnectToKnownDeviceNativeAsync(Guid deviceGuid, ConnectParameters connectParameters, CancellationToken cancellationToken)
        {
            var bleAddress = deviceGuid.ToBleAddress();
            var nativeDevice = await BluetoothLEDevice.FromBluetoothAddressAsync(bleAddress);
            if (nativeDevice == null)
                throw new Abstractions.Exceptions.DeviceConnectionException(deviceGuid, "", $"[Adapter] Device {deviceGuid} not found.");

            var knownDevice = new Device(this, nativeDevice, 0, deviceGuid);

            await ConnectToDeviceAsync(knownDevice, connectParameters, cancellationToken: cancellationToken);
            return knownDevice;
        }

        protected override IReadOnlyList<IDevice> GetBondedDevices()
        {
            string pairedSelector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
            DeviceInformationCollection pairedDevices = DeviceInformation.FindAllAsync(pairedSelector).GetAwaiter().GetResult();
            List<IDevice> devlist = new List<IDevice>();
            foreach (var dev in pairedDevices)
            {
                Guid id = dev.Id.ToBleDeviceGuidFromId();
                ulong bleaddress = id.ToBleAddress();
                var bluetoothLeDevice = BluetoothLEDevice.FromBluetoothAddressAsync(bleaddress).AsTask().Result;
                if (bluetoothLeDevice != null)
                {
                    var device = new Device(
                        this,
                        bluetoothLeDevice,
                        0, id);
                    devlist.Add(device);
                    Trace.Message("GetBondedDevices: {0}: {1}", dev.Id, dev.Name);
                }
                else
                {
                    Trace.Message("GetBondedDevices: {0}: {1}, BluetoothLEDevice == null", dev.Id, dev.Name);
                }
            }
            return devlist;
        }

        public override IReadOnlyList<IDevice> GetSystemConnectedOrPairedDevices(Guid[] services = null)
        {
            string pairedSelector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
            DeviceInformationCollection pairedDevices = DeviceInformation.FindAllAsync(pairedSelector).GetAwaiter().GetResult();
            List<IDevice> devlist = ConnectedDevices.ToList();
            List<Guid> ids = ConnectedDevices.Select(d => d.Id).ToList();
            foreach (var dev in pairedDevices)
            {
                Guid id = dev.Id.ToBleDeviceGuidFromId();
                ulong bleaddress = id.ToBleAddress();
                if (!ids.Contains(id))
                {
                    var bluetoothLeDevice = BluetoothLEDevice.FromBluetoothAddressAsync(bleaddress).AsTask().Result;
                    if (bluetoothLeDevice != null)
                    {
                        var device = new Device(
                            this,
                            bluetoothLeDevice,
                            0, id);
                        devlist.Add(device);
                        ids.Add(id);
                        Trace.Message("GetSystemConnectedOrPairedDevices: {0}: {1}", dev.Id, dev.Name);
                    }
                    else
                    {
                        Trace.Message("GetSystemConnectedOrPairedDevices: {0}: {1}, BluetoothLEDevice == null", dev.Id, dev.Name);
                    }

                }
            }
            return devlist;
        }

        /// <summary>
        /// Parses a given advertisement for various stored properties
        /// Currently only parses the manufacturer specific data
        /// </summary>
        /// <param name="adv">The advertisement to parse</param>
        /// <returns>List of generic advertisement records</returns>
        public static List<AdvertisementRecord> ParseAdvertisementData(BluetoothLEAdvertisement adv)
        {
            var advList = adv.DataSections;
            return advList.Select(data => new AdvertisementRecord((AdvertisementRecordType)data.DataType, data.Data?.ToArray())).ToList();
        }

        /// <summary>
        /// Handler for devices found when duplicates are not allowed
        /// </summary>
        /// <param name="watcher">The bluetooth advertisement watcher currently being used</param>
        /// <param name="btAdv">The advertisement recieved by the watcher</param>
        private void AdvertisementReceived(BluetoothLEAdvertisementWatcher watcher, BluetoothLEAdvertisementReceivedEventArgs btAdv)
        {
            var deviceId = btAdv.BluetoothAddress.ParseDeviceId();

            if (DiscoveredDevicesRegistry.TryGetValue(deviceId, out var device))
            {
                // This deviceId has been discovered
                Trace.Message("AdvReceived - Old: {0}", btAdv.ToDetailedString(device.Name));
                (device as Device)?.Update(btAdv.RawSignalStrengthInDBm, ParseAdvertisementData(btAdv.Advertisement));
                HandleDiscoveredDevice(device);
            }
            else
            {
                var bluetoothLeDevice = BluetoothLEDevice.FromBluetoothAddressAsync(btAdv.BluetoothAddress).AsTask().Result;
                if (bluetoothLeDevice != null) //make sure advertisement bluetooth address actually returns a device
                {
                    device = new Device(
                        this,
                        bluetoothLeDevice,
                        btAdv.RawSignalStrengthInDBm,
                        deviceId,
                        ParseAdvertisementData(btAdv.Advertisement),
                        btAdv.IsConnectable);
                    Trace.Message("AdvReceived - New: {0}", btAdv.ToDetailedString(device.Name));
                    HandleDiscoveredDevice(device);
                }
            }
        }

        public override IReadOnlyList<IDevice> GetKnownDevicesByIds(Guid[] ids)
        {
            // TODO: implement this
            return new List<IDevice>();
        }

        public override bool SupportsExtendedAdvertising()
        {
            return _bluetoothAdapter.IsExtendedAdvertisingSupported;
        }
    }
}
