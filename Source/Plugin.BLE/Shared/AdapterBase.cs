using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;
using Plugin.BLE.Abstractions.Exceptions;
using Plugin.BLE.Abstractions.Utils;

namespace Plugin.BLE.Abstractions
{
    /// <summary>
    /// Base class for all platform-specific Adapter classes.
    /// </summary>
    public abstract class AdapterBase : IAdapter
    {
        private CancellationTokenSource _scanCancellationTokenSource;
        private volatile bool _isScanning;
        private Func<IDevice, bool> _currentScanDeviceFilter;

        /// <summary>
        /// Occurs when the adapter receives an advertisement.
        /// </summary>
        public event EventHandler<DeviceEventArgs> DeviceAdvertised;
        /// <summary>
        /// Occurs when the adapter receives an advertisement for the first time of the current scan run.
        /// This means once per every <c>StartScanningForDevicesAsync</c> call.
        /// </summary>
        public event EventHandler<DeviceEventArgs> DeviceDiscovered;
        /// <summary>
        /// Occurs when a device has been connected.
        /// </summary>
        public event EventHandler<DeviceEventArgs> DeviceConnected;
        /// <summary>
        /// Occurs when a device has been disconnected. This occurs on intended disconnects after <see cref="DisconnectDeviceAsync"/>.
        /// </summary>
        public event EventHandler<DeviceEventArgs> DeviceDisconnected;
        /// <summary>
        /// Occurs when a device has been disconnected. This occurs on unintended disconnects (e.g. when the device exploded).
        /// </summary>
        public event EventHandler<DeviceErrorEventArgs> DeviceConnectionLost;
        /// <summary>
        /// Occurs when the connection to a device fails.
        /// </summary>
        public event EventHandler<DeviceErrorEventArgs> DeviceConnectionError;
        /// <summary>
        /// Occurs when the bonding state of a device changed.
        /// </summary>
        public event EventHandler<DeviceBondStateChangedEventArgs> DeviceBondStateChanged;
        /// <summary>
        /// Occurs when the scan has been stopped due the timeout after <see cref="ScanTimeout"/> ms.
        /// </summary>
        public event EventHandler ScanTimeoutElapsed;

        /// <summary>
        /// Indicates, if the adapter is scanning for devices.
        /// </summary>
        public bool IsScanning
        {
            get => _isScanning;
            private set => _isScanning = value;
        }

        /// <summary>
        /// Timeout for Ble scanning. Default is 10000.
        /// </summary>
        public int ScanTimeout { get; set; } = 10000;

        /// <summary>
        /// Specifies the scanning mode. Must be set before calling StartScanningForDevicesAsync().
        /// Changing it while scanning, will have no change the current scan behavior.
        /// Default: <see cref="ScanMode.LowPower"/> 
        /// </summary>
        public ScanMode ScanMode { get; set; } = ScanMode.LowPower;
        
        /// <summary>
        /// Scan match mode defines how agressively we look for adverts
        /// </summary>
        public ScanMatchMode ScanMatchMode { get; set; } = ScanMatchMode.STICKY;

        /// <summary>
        /// Dictionary of all discovered devices, indexed by Guid.
        /// </summary>
        protected ConcurrentDictionary<Guid, IDevice> DiscoveredDevicesRegistry { get; } = new ConcurrentDictionary<Guid, IDevice>();

        /// <summary>
        /// List of all discovered devices.
        /// </summary>
        public virtual IReadOnlyList<IDevice> DiscoveredDevices => DiscoveredDevicesRegistry.Values.ToList();

        /// <summary>
        /// Used to store all connected devices
        /// </summary>
        public ConcurrentDictionary<string, IDevice> ConnectedDeviceRegistry { get; } = new ConcurrentDictionary<string, IDevice>();

        /// <summary>
        /// List of all connected devices.
        /// </summary>
        public IReadOnlyList<IDevice> ConnectedDevices => ConnectedDeviceRegistry.Values.ToList();

        /// <summary>
        /// List of all bonded devices (or null if the device does not support this information).
        /// </summary>
        public IReadOnlyList<IDevice> BondedDevices => GetBondedDevices();

        /// <summary>
        /// Starts scanning for BLE devices that fulfill the <paramref name="deviceFilter"/>.
        /// DeviceDiscovered will only be called, if <paramref name="deviceFilter"/> returns <c>true</c> for the discovered device.
        /// </summary>
        public async Task StartScanningForDevicesAsync(ScanFilterOptions scanFilterOptions,
            Func<IDevice, bool> deviceFilter = null,
            bool allowDuplicatesKey = false,
            CancellationToken cancellationToken = default)
        {
            if (IsScanning)
            {
                Trace.Message("Adapter: Already scanning!");
                return;
            }

            IsScanning = true;
            _currentScanDeviceFilter = deviceFilter ?? (d => true);
            _scanCancellationTokenSource = new CancellationTokenSource();

            try
            {
                DiscoveredDevicesRegistry.Clear();

                using (cancellationToken.Register(() => _scanCancellationTokenSource?.Cancel()))
                {
                    await StartScanningForDevicesNativeAsync(scanFilterOptions, allowDuplicatesKey, _scanCancellationTokenSource.Token);
                    await Task.Delay(ScanTimeout, _scanCancellationTokenSource.Token);
                    Trace.Message("Adapter: Scan timeout has elapsed.");
                    CleanupScan();
                    ScanTimeoutElapsed?.Invoke(this, new System.EventArgs());
                }
            }
            catch (TaskCanceledException)
            {
                CleanupScan();
                Trace.Message("Adapter: Scan was cancelled.");
            }
        }

        /// <summary>
        /// Starts scanning for BLE devices that fulfill the <paramref name="deviceFilter"/>.
        /// DeviceDiscovered will only be called, if <paramref name="deviceFilter"/> returns <c>true</c> for the discovered device.
        /// This overload takes a list of service IDs and is only kept for backwards compatibility. Might be removed in a future version.
        /// </summary>
        public async Task StartScanningForDevicesAsync(Guid[] serviceUuids, Func<IDevice, bool> deviceFilter = null, bool allowDuplicatesKey = false,
            CancellationToken cancellationToken = default)
        {
            await StartScanningForDevicesAsync(new ScanFilterOptions { ServiceUuids = serviceUuids }, deviceFilter, allowDuplicatesKey, cancellationToken);
        }

        /// <summary>
        /// Stops scanning for BLE devices.
        /// </summary>
        public Task StopScanningForDevicesAsync()
        {
            if (_scanCancellationTokenSource != null && !_scanCancellationTokenSource.IsCancellationRequested)
            {
                _scanCancellationTokenSource.Cancel();
            }
            else
            {
                Trace.Message("Adapter: Already cancelled scan.");
            }

            return Task.FromResult(0);
        }

        /// <summary>
        /// Connects to the <paramref name="device"/>.
        /// </summary>
        /// <param name="device">Device to connect to.</param>
        /// <param name="connectParameters">Connection parameters. Contains platform specific parameters needed to achieved connection. The default value is None.</param>
        /// <param name="cancellationToken">The token to monitor for cancellation requests. The default value is None.</param>
        /// <returns>A task that represents the asynchronous read operation. The Task will finish after the device has been connected successfuly.</returns>
        public async Task ConnectToDeviceAsync(IDevice device, ConnectParameters connectParameters = default, CancellationToken cancellationToken = default)
        {
            if (device == null)
                throw new ArgumentNullException(nameof(device));

            if (device.State == DeviceState.Connected)
                return;

            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                await TaskBuilder.FromEvent<bool, EventHandler<DeviceEventArgs>, EventHandler<DeviceErrorEventArgs>>(
                    execute: () =>
                    {
                        ConnectToDeviceNativeAsync(device, connectParameters, cts.Token);
                    },

                    getCompleteHandler: (complete, reject) => (sender, args) =>
                    {
                        if (args.Device.Id == device.Id)
                        {
                            Trace.Message("ConnectToDeviceAsync Connected: {0} {1}", args.Device.Id, args.Device.Name);
                            complete(true);
                        }
                    },
                    subscribeComplete: handler => DeviceConnected += handler,
                    unsubscribeComplete: handler => DeviceConnected -= handler,

                    getRejectHandler: reject => (sender, args) =>
                    {
                        if (args.Device?.Id == device.Id)
                        {
                            Trace.Message("ConnectAsync Error: {0} {1}", args.Device?.Id, args.Device?.Name);
                            reject(new DeviceConnectionException((Guid)args.Device?.Id, args.Device?.Name,
                                args.ErrorMessage));
                        }
                    },

                    subscribeReject: handler => DeviceConnectionError += handler,
                    unsubscribeReject: handler => DeviceConnectionError -= handler,
                    token: cts.Token, mainthread: false);
            }
        }

        /// <summary>
        /// Disconnects from the <paramref name="device"/>.
        /// </summary>
        /// <param name="device">Device to connect from.</param>
        /// <param name="cancellationToken">The token to monitor for cancellation requests. The default value is None.</param>
        public Task DisconnectDeviceAsync(IDevice device, CancellationToken cancellationToken = default)
        {
            if (!ConnectedDevices.Contains(device))
            {
                Trace.Message("Disconnect async: device {0} not in the list of connected devices.", device.Name);
                return Task.FromResult(false);
            }

            return TaskBuilder.FromEvent<bool, EventHandler<DeviceEventArgs>, EventHandler<DeviceErrorEventArgs>>(
               execute: () => DisconnectDeviceNative(device),

               getCompleteHandler: (complete, reject) => ((sender, args) =>
               {
                   if (args.Device.Id == device.Id)
                   {
                       Trace.Message("DisconnectAsync Disconnected: {0} {1}", args.Device.Id, args.Device.Name);
                       complete(true);
                   }
               }),
               subscribeComplete: handler => DeviceDisconnected += handler,
               unsubscribeComplete: handler => DeviceDisconnected -= handler,

               getRejectHandler: reject => ((sender, args) =>
               {
                   if (args.Device.Id == device.Id)
                   {
                       Trace.Message("DisconnectAsync", "Disconnect Error: {0} {1}", args.Device?.Id, args.Device?.Name);
                       reject(new Exception("Disconnect operation exception"));
                   }
               }),
               subscribeReject: handler => DeviceConnectionError += handler,
               unsubscribeReject: handler => DeviceConnectionError -= handler,
               token: cancellationToken);
        }

        private void CleanupScan()
        {
            Trace.Message("Adapter: Stopping the scan for devices.");
            StopScanNative();

            if (_scanCancellationTokenSource != null)
            {
                _scanCancellationTokenSource.Dispose();
                _scanCancellationTokenSource = null;
            }

            IsScanning = false;
        }

        /// <summary>
        /// Handle discovery of a new device.
        /// </summary>
        public void HandleDiscoveredDevice(IDevice device)
        {
            if (_currentScanDeviceFilter != null && !_currentScanDeviceFilter(device))
                return;

            DeviceAdvertised?.Invoke(this, new DeviceEventArgs { Device = device });

            // TODO (sms): check equality implementation of device
            if (DiscoveredDevicesRegistry.ContainsKey(device.Id))
                return;

            DiscoveredDevicesRegistry[device.Id] = device;
            DeviceDiscovered?.Invoke(this, new DeviceEventArgs { Device = device });
        }

        /// <summary>
        /// Handle connection of a new device.
        /// </summary>
        public void HandleConnectedDevice(IDevice device)
        {
            DeviceConnected?.Invoke(this, new DeviceEventArgs { Device = device });
        }

		/// <summary>
		/// Handle disconnection of a device.
		/// </summary>
		public void HandleDisconnectedDevice(bool disconnectRequested, IDevice device, string message = "")
		{
			if (disconnectRequested)
			{
				Trace.Message("DisconnectedPeripheral by user: {0}", device.Name);
				DeviceDisconnected?.Invoke(this, new DeviceEventArgs { Device = device });
			}
			else
			{
				string m = !string.IsNullOrWhiteSpace(message) ? message : "DisconnectedPeripheral by lost signal";
				Trace.Message($"{m}: {device.Name}");
				DeviceConnectionLost?.Invoke(this, new DeviceErrorEventArgs { Device = device, ErrorMessage = m });

				if (DiscoveredDevicesRegistry.TryRemove(device.Id, out _))
					Trace.Message("Removed device from discovered devices list: {0}", device.Name);
			}
		}

		/// <summary>
		/// Handle connection failure.
		/// </summary>
		public void HandleConnectionFail(IDevice device, string errorMessage)
        {
            Trace.Message("Failed to connect peripheral {0}: {1}", device.Id, device.Name);
            DeviceConnectionError?.Invoke(this, new DeviceErrorEventArgs
            {
                Device = device,
                ErrorMessage = errorMessage
            });
        }
        
        /// <inheritdoc/>
        public abstract Task BondAsync(IDevice device);

        /// <summary>
        /// Handle bond state changed information.
        /// </summary>
        /// <param name="args"></param>
        protected void HandleDeviceBondStateChanged(DeviceBondStateChangedEventArgs args)
        {
            DeviceBondStateChanged?.Invoke(this, args);
        }

        /// <summary>
        /// Connects to a device with a known GUID without scanning and if in range. Does not scan for devices.
        /// </summary>
        public async Task<IDevice> ConnectToKnownDeviceAsync(Guid deviceGuid, ConnectParameters connectParameters = default, CancellationToken cancellationToken = default)
        {
            if (DiscoveredDevicesRegistry.TryGetValue(deviceGuid, out IDevice discoveredDevice))
            {
                await ConnectToDeviceAsync(discoveredDevice, connectParameters, cancellationToken);
                return discoveredDevice;
            }

            var connectedDevice = await ConnectToKnownDeviceNativeAsync(deviceGuid, connectParameters, cancellationToken);
            if (!DiscoveredDevicesRegistry.ContainsKey(deviceGuid)) 
                DiscoveredDevicesRegistry.TryAdd(deviceGuid, connectedDevice);

            return connectedDevice;
        }

        /// <summary>
        /// Native implementation of StartScanningForDevicesAsync.
        /// </summary>
        protected abstract Task StartScanningForDevicesNativeAsync(ScanFilterOptions scanFilterOptions, bool allowDuplicatesKey, CancellationToken scanCancellationToken);
        /// <summary>
        /// Stopping the scan (native implementation).
        /// </summary>
        protected abstract void StopScanNative();
        /// <summary>
        /// Native implementation of ConnectToDeviceAsync.
        /// </summary>
        protected abstract Task ConnectToDeviceNativeAsync(IDevice device, ConnectParameters connectParameters, CancellationToken cancellationToken);
        /// <summary>
        /// Native implementation of DisconnectDeviceAsync.
        /// </summary>
        protected abstract void DisconnectDeviceNative(IDevice device);

        /// <summary>
        /// Native implementation of ConnectToKnownDeviceAsync.
        /// </summary>
        public abstract Task<IDevice> ConnectToKnownDeviceNativeAsync(Guid deviceGuid, ConnectParameters connectParameters = default, CancellationToken cancellationToken = default);
        /// <summary>
        /// Returns all BLE devices connected to the system.
        /// </summary>
        public abstract IReadOnlyList<IDevice> GetSystemConnectedOrPairedDevices(Guid[] services = null);
        /// <summary>
        /// Returns a list of paired BLE devices for the given UUIDs.
        /// </summary>
        public abstract IReadOnlyList<IDevice> GetKnownDevicesByIds(Guid[] ids);
        /// <summary>
        /// Returns all BLE device bonded to the system.
        /// </summary>
        protected abstract IReadOnlyList<IDevice> GetBondedDevices();

        /// <summary>
        /// Indicates whether extended advertising (BLE5) is supported.
        /// </summary>
        public virtual bool SupportsExtendedAdvertising() => false;

        /// <summary>
        /// Indicates whether the Coded PHY feature (BLE5) is supported.
        /// </summary>
        public virtual bool SupportsCodedPHY() => false;
    }
}
