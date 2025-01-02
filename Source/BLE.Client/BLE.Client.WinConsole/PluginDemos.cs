using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

using Plugin.BLE;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.Contracts.Pairing;
using Plugin.BLE.Abstractions.EventArgs;
using Plugin.BLE.Abstractions.Utils;
using Plugin.BLE.Extensions;
using Plugin.BLE.Windows;


namespace BLE.Client.WinConsole
{
	internal class PluginDemos
	{
		private readonly IBluetoothLE _bluetoothLe;
		private readonly Action<string, object[]>? _writer;
		private readonly List<IDevice> _discoveredDevices;
		private bool _isScanning;
		private ConsoleKey _consoleKey = ConsoleKey.None;
		private IDevice? _reconnectDevice;
		private readonly CancellationTokenSource _escKeyCancellationTokenSource = new();
		private readonly IAdapter _adapter;
		private IDevice? _selectedDevice;
		private ProtectionLevel _protectionLevel;

		public PluginDemos(Action<string, object[]>? writer = null)
		{
			_discoveredDevices = [];
			_bluetoothLe = CrossBluetoothLE.Current;
			_adapter = CrossBluetoothLE.Current.Adapter;
			_adapter.DeviceConnected += Adapter_DeviceConnected;
			_adapter.DeviceDisconnected += Adapter_DeviceDisconnected;
			_adapter.DeviceConnectionLost += Adapter_DeviceConnectionLost;
			_adapter.DeviceConnectionError += Adapter_DeviceConnectionError;
			_writer = writer;
		}

		private void Adapter_DeviceConnectionError(object? sender, DeviceErrorEventArgs e) => Write($"Adapter_DeviceConnectionError {e.Device.Id.ToHexBleAddress()} with name: {e.Device.Name}");

		private void Adapter_DeviceDisconnected(object? sender, DeviceEventArgs e) => Write($"Adapter_DeviceDisconnected {e.Device.Id.ToHexBleAddress()} with name: {e.Device.Name}");

		private void Adapter_DeviceConnected(object? sender, DeviceEventArgs e) => Write($"Adapter_DeviceConnected {e.Device.Id.ToHexBleAddress()} with name:  {e.Device.Name}");

		private void Write(string format, params object[] args) => _writer?.Invoke(format, args);

		public async Task TurnBluetoothOn() => await _bluetoothLe.TrySetStateAsync(true);

		public async Task TurnBluetoothOff() => await _bluetoothLe.TrySetStateAsync(false);

		public IDevice ConnectToKnown(Guid id) => _adapter.ConnectToKnownDeviceAsync(id).Result;

		public async Task Connect_Disconnect()
		{
			string bleAddress = BleAddressSelector.GetBleAddress();
			var id = bleAddress.ToBleDeviceGuid();
			var connectParameters = new ConnectParameters(connectionParameterSet: ConnectionParameterSet.ThroughputOptimized);
			IDevice dev = await _adapter.ConnectToKnownDeviceAsync(id, connectParameters);
			Write("Waiting 5 secs");
			await Task.Delay(5000);

			if (dev is { State: DeviceState.Connected })
			{
				Write("connect success");
				Write("Disconnecting");
				await _adapter.DisconnectDeviceAsync(dev);
				if (dev.State != DeviceState.Connected)
					Write("disconnect success");

				dev.Dispose();
			}
			else
				Write("fail");

			Write("Test_Connect_Disconnect done");
		}

		public async Task GetSelectedStatus()
		{
			if (_selectedDevice == null)
			{
				Write("Request requires a Selected Device");
				return;
			}

			if (_selectedDevice.NativeDevice is null)
			{
				Write("Underlying device is null, Device Unselected");
				_selectedDevice.Dispose();
				_selectedDevice = null;
				return;
			}

			Write($"{nameof(_selectedDevice.Id)}: {_selectedDevice.Id}");
			Write($"{nameof(_selectedDevice.Name)}: {_selectedDevice.Name}");
			Write($"{nameof(_selectedDevice.State)}: {_selectedDevice.State}");
			Write($"{nameof(_selectedDevice.SupportsIsConnectable)}: {_selectedDevice.SupportsIsConnectable}");
			Write($"{nameof(_selectedDevice.IsConnectable)}: {_selectedDevice.IsConnectable}");
			Write($"{nameof(_selectedDevice.BondState)}: {_selectedDevice.BondState}");
			Write($"{nameof(_selectedDevice.Rssi)}: {_selectedDevice.Rssi}");
			Write($"{nameof(_selectedDevice.AdvertisementRecords)}: {_selectedDevice.AdvertisementRecords.Count}");
			Write($"Bonded: {_adapter.BondedDevices.Contains(_selectedDevice)}");
		}

		public async Task ConnectSelected()
		{
			if (_selectedDevice == null)
			{
				Write("Request requires a Selected Device");
				return;
			}

			if (_selectedDevice.State is DeviceState.Connected or DeviceState.Connecting)
			{
				Write($"Device State: {_selectedDevice.State}. Request requires a Selected Disconnected Device");
				return;
			}

			var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
			try
			{
				await _adapter.ConnectToDeviceAsync(_selectedDevice, new(connectionParameterSet: ConnectionParameterSet.PowerOptimized), cts.Token);
			}
			finally
			{
				Write($"Request {_selectedDevice.State}");
			}
		}

		public async Task DisconnectSelected()
		{
			if (_selectedDevice == null)
			{
				Write("Request requires a Selected Device");
				return;
			}

			if (_selectedDevice.State is DeviceState.Disconnected or DeviceState.Connecting)
			{
				Write($"Device State: {_selectedDevice.State}. Request requires a Selected Connected Device");
				return;
			}

			var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
			try
			{
				await _adapter.DisconnectDeviceAsync(_selectedDevice, cts.Token);
			}
			finally
			{
				Write($"Request result {_selectedDevice.State}");
			}
		}

		public async Task ShowBondState()
		{
			var id = BleAddressSelector.GetBleAddress().ToBleDeviceGuid();
			IDevice dev = await _adapter.ConnectToKnownDeviceAsync(id);
			Write("BondState: " + dev.BondState);
			dev.Dispose();
		}

		public async Task Connect_Read_Services_Disconnect_Loop()
		{
			var id = BleAddressSelector.GetBleAddress().ToBleDeviceGuid();
			var connectParameters = new ConnectParameters(connectionParameterSet: ConnectionParameterSet.Balanced);
			new Task(ConsoleKeyReader).Start();
			using (IDevice dev = await _adapter.ConnectToKnownDeviceAsync(id, connectParameters))
			{
				int count = 1;
				while (true)
				{
					await Task.Delay(100);
					Write($"---------------- {count++} ------- (Esc to stop) ------");
					if (dev.State != DeviceState.Connected)
					{
						Write("Connecting");
						await _adapter.ConnectToDeviceAsync(dev);
					}
					Write("Reading services");

					var services = await dev.GetServicesAsync();
					List<ICharacteristic> charList = [];
					foreach (var service in services)
					{
						var characteristics = await service.GetCharacteristicsAsync();
						charList.AddRange(characteristics);
					}

					foreach (var service in services)
					{
						service.Dispose();
					}
					charList.Clear();
					Write("Waiting 3 secs");
					await Task.Delay(3000);
					Write("Disconnecting");
					await _adapter.DisconnectDeviceAsync(dev);
					Write("Test_Connect_Disconnect done");
					if (_consoleKey == ConsoleKey.Escape)
						break;
				}
			}
		}

		public async Task Connect_Read_Services_Dispose_Loop()
		{
			string bleAddress = BleAddressSelector.GetBleAddress();
			var id = bleAddress.ToBleDeviceGuid();
			var connectParameters = new ConnectParameters(connectionParameterSet: ConnectionParameterSet.Balanced);
			new Task(ConsoleKeyReader).Start();
			int count = 1;
			while (true)
			{
				await Task.Delay(100);
				Write($"---------------- {count++} ------- (Esc to stop) ------");
				IDevice dev = await _adapter.ConnectToKnownDeviceAsync(id, connectParameters);
				Write("Reading services");
				var services = await dev.GetServicesAsync();
				List<ICharacteristic> charList = [];
				foreach (var service in services)
				{
					var characteristics = await service.GetCharacteristicsAsync();
					charList.AddRange(characteristics);
					foreach (Characteristic characteristic in characteristics)
					{
						if (characteristic.Properties.HasFlag(CharacteristicPropertyType.Indicate) || characteristic.Properties.HasFlag(CharacteristicPropertyType.Notify))
						{
							Write($"Characteristic.Properties: {characteristic.Properties}");
							try
							{
								await characteristic.StartUpdatesAsync();
							}
							catch
							{
								// ignored
							}
						}
					}
				}

				foreach (var service in services)
				{
					service.Dispose();
				}
				foreach (Characteristic characteristic in charList)
				{
					try
					{
						await characteristic.StopUpdatesAsync();
					}
					catch
					{
						// ignored
					}
				}
				charList.Clear();
				Write("Waiting 3 secs");
				await Task.Delay(3000);
				await _adapter.DisconnectDeviceAsync(dev);
				Write("Disposing");
				dev.Dispose();
			}
		}

		private void ConsoleKeyReader()
		{
			while (_consoleKey != ConsoleKey.Escape)
			{
				_consoleKey = Console.ReadKey().Key;
			}
			Write("Escape key pressed - stopping...");
			_escKeyCancellationTokenSource.Cancel();
		}

		private async Task ConnectWorker(Guid id)
		{
			while (_consoleKey != ConsoleKey.Escape)
			{
				try
				{
					Write("Trying to connect to device (Escape key to abort)");
					_reconnectDevice = await _adapter.ConnectToKnownDeviceAsync(id, cancellationToken: _escKeyCancellationTokenSource.Token);
					Write("Reading all services and characteristics");
					var services = await _reconnectDevice.GetServicesAsync();
					List<ICharacteristic> characteristics = [];
					foreach (var service in services)
					{
						var newCharacteristics = await service.GetCharacteristicsAsync();
						characteristics.AddRange(newCharacteristics);
					}
					await Task.Delay(1000);
					Write(new('-', 80));
					Write("Connected successfully!");
					Write("To test connection lost: Move the device out of range / power off the device");
					Write(new('-', 80));
					break;
				}
				catch
				{
					// ignored
				}
			}
		}

		public async Task Connect_ConnectionLost_Reconnect()
		{
			var id = BleAddressSelector.GetBleAddress().ToBleDeviceGuid();
			var consoleReaderTask = new Task(ConsoleKeyReader);
			consoleReaderTask.Start();
			await ConnectWorker(id);
			consoleReaderTask.Wait();
		}

		private async void Adapter_DeviceConnectionLost(object? sender, DeviceErrorEventArgs e)
		{
			Write($"Adapter_DeviceConnectionLost {e.Device.Id.ToHexBleAddress()} with name: {e.Device.Name}");
			if (_reconnectDevice is not null && _reconnectDevice.Id == e.Device.Id)
			{
				_reconnectDevice.Dispose();
				_reconnectDevice = null;
				await Task.Delay(1000);
				Write(new('-', 80));
				Write("Lost connection!");
				Write("To test reconnect: Move the device back in range / power on the device");
				Write(new('-', 80));
				_ = ConnectWorker(e.Device.Id);
			}
		}

		public async Task Connect_Change_Parameters_Disconnect()
		{
			var id = BleAddressSelector.GetBleAddress().ToBleDeviceGuid();
			var connectParameters = new ConnectParameters(connectionParameterSet: ConnectionParameterSet.Balanced);
			IDevice dev = await _adapter.ConnectToKnownDeviceAsync(id, connectParameters);
			Write("Waiting 5 secs");
			await Task.Delay(5000);
			connectParameters = new(connectionParameterSet: ConnectionParameterSet.ThroughputOptimized);
			dev.UpdateConnectionParameters(connectParameters);
			Write("Waiting 5 secs");
			await Task.Delay(5000);
			connectParameters = new(connectionParameterSet: ConnectionParameterSet.Balanced);
			dev.UpdateConnectionParameters(connectParameters);
			Write("Waiting 5 secs");
			await Task.Delay(5000);
			Write("Disconnecting");
			await _adapter.DisconnectDeviceAsync(dev);
			dev.Dispose();
			Write("Test_Connect_Disconnect done");
		}

		public async Task BondAsync()
		{
			string bleaddress = BleAddressSelector.GetBleAddress();
			var id = bleaddress.ToBleDeviceGuid();
			IDevice dev = await _adapter.ConnectToKnownDeviceAsync(id);
			await _adapter.BondAsync(dev);
		}

		 #region New Bonding

		public async Task PairNone() => await Bond(PairModes.None);
		public async Task PairConsent() => await Bond(PairModes.Consent);
		public async Task PairDisplayPin() => await Bond(PairModes.DisplayPin);
		public async Task PairProvidePin() => await Bond(PairModes.ProvidePin);
		public async Task PairConfirmPinMatch() => await Bond(PairModes.ConfirmPinMatch);
		public async Task PairProvidePasswordCredential() => await Bond(PairModes.ProvidePasswordCredential);
		public async Task PairAny() => await Bond(PairModes.Any);

		public async Task SetPairingRequestProtectionLevel()
		{
			_protectionLevel = (_protectionLevel) switch
			{
				ProtectionLevel.Unused => ProtectionLevel.None,
				ProtectionLevel.None => ProtectionLevel.Encryption,
				ProtectionLevel.Encryption => ProtectionLevel.EncryptionAndAuthentication,
				ProtectionLevel.EncryptionAndAuthentication => ProtectionLevel.Unused,
				_ => throw new ArgumentOutOfRangeException()
			};

			Write($"Use {nameof(ProtectionLevel)}: {_protectionLevel} for Pair requests");
			await Task.CompletedTask;
		}

		private async Task Bond(PairModes pairModes)
		{
			if (_selectedDevice == null)
			{
				Write($"no device selected");
				return;
			}

			var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
			if (_adapter is IBondable bondable)
			{
				var pairProcess = _adapter as IPairProcess;
				if (pairProcess != null)
					pairProcess.PairResponded += OnPairResponded;

				try
				{
					var bondResult = await bondable.BondAsync(_selectedDevice, new(pairModes, _protectionLevel), cts.Token);
					Write($"{nameof(bondResult.Status)}: {bondResult.Status}, {nameof(bondResult.Detail)}: {bondResult.Detail}, {nameof(bondResult.ProtectionUsed)}: {bondResult.ProtectionUsed}");
				}
				catch (Exception ex)
				{
					Write(ex.Message);
				}
				finally
				{
					if (pairProcess != null)
						pairProcess.PairResponded -= OnPairResponded;
				}

				void OnPairResponded(object? _, IPairProcess.PairRespondedEventArgs pairRespondedEventArgs)
				{
					switch (pairRespondedEventArgs.SelectedMode)
					{
						case PairModes.None: break;
						case PairModes.Consent:
							Write("Confirm Pair request? 'Y'");
							if (CancelableTaskSync.AwaitInput(Console.ReadLine, cts.Token)?.Contains('Y', StringComparison.InvariantCultureIgnoreCase) == true)
								pairRespondedEventArgs.ApprovePairingResponse(new IPairProcess.ConfirmPairResponse());
							break;
						case PairModes.ConfirmPinMatch:
							Write($"Accept PIN? 'Y': {pairRespondedEventArgs.Pin}");
							if (CancelableTaskSync.AwaitInput(Console.ReadLine, cts.Token)?.Contains('Y', StringComparison.InvariantCultureIgnoreCase) == true)
								pairRespondedEventArgs.ApprovePairingResponse(new IPairProcess.ConfirmPairResponse());
							break;
						case PairModes.DisplayPin:
							Write($"PIN: {pairRespondedEventArgs.Pin}");
							break;
						case PairModes.ProvidePin:
							Write("Enter pairing PIN");
							pairRespondedEventArgs.ApprovePairingResponse(new IPairProcess.PinPairResponse(CancelableTaskSync.AwaitInput(Console.ReadLine, cts.Token)));
							break;
						case PairModes.ProvidePasswordCredential:
							Write($"Enter User Name");
							var userName = CancelableTaskSync.AwaitInput(Console.ReadLine, cts.Token);
							Write($"Enter Password");
							var password = CancelableTaskSync.AwaitInput(Console.ReadLine, cts.Token);

							if (!string.IsNullOrWhiteSpace(userName) && !string.IsNullOrWhiteSpace(password))
								pairRespondedEventArgs.ApprovePairingResponse(new IPairProcess.CredentialsPairResponse(userName, password));
							break;
					}
				}
			}
		}

		#endregion New Bonding

		public Task GetBondedDevices()
		{
			int idx = 0;
			foreach (var dev in _adapter.BondedDevices)
			{
				Write($"{idx++} Bonded device: {dev.Name} : {dev.Id}");
			}
			return Task.FromResult(true);
		}

		public async Task Pair_Connect_Disconnect()
		{
			var id = BleAddressSelector.GetBleAddress().ToBleDeviceGuid();
			ulong bleAddressUlong = id.ToBleAddress();
			DeviceInformation? deviceInformation;
			using (BluetoothLEDevice nativeDevice = await BluetoothLEDevice.FromBluetoothAddressAsync(bleAddressUlong))
			{
				deviceInformation = await DeviceInformation.CreateFromIdAsync(nativeDevice.DeviceId);
			}

			if (!deviceInformation.Pairing.IsPaired && deviceInformation.Pairing.CanPair)
			{
				Write("Starting custom pairing...");
				deviceInformation.Pairing.Custom.PairingRequested += Custom_PairingRequested;
				DevicePairingResult result = await deviceInformation.Pairing.Custom.PairAsync(DevicePairingKinds.ConfirmOnly, DevicePairingProtectionLevel.Encryption);
				Write("Pairing result: " + result.Status);
			}
			else
			{
				Write("Already paired");
			}
			Write("Calling Adapter.ConnectToKnownDeviceAsync");
			IDevice dev = await _adapter.ConnectToKnownDeviceAsync(id);
			Write($"Calling Adapter.ConnectToKnownDeviceAsync done with {dev.Name}");
			await Task.Delay(1000);
			await dev.RequestMtuAsync(517);
			Write("Waiting 3 secs");
			await Task.Delay(3000);
			Write("Disconnecting");
			await _adapter.DisconnectDeviceAsync(dev);
			dev.Dispose();
			Write("Custom_Pair_Connect_Disconnect done");
		}

		private void Custom_PairingRequested(DeviceInformationCustomPairing sender, DevicePairingRequestedEventArgs args)
		{
			Write("Custom_PairingRequested -> Accept");
			//args.Accept();
		}

		public async Task UnPairSelectedDevice()
		{
			if (_selectedDevice?.Id == null)
				throw new();

			try
			{
				var collection = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelector());
				var deviceInformation = collection.Single(deviceInfo => ExtractIdFromDeviceInfo(deviceInfo.Id) == ExtractIdFromIDevice(_selectedDevice.Id));
				DeviceUnpairingResult result = await deviceInformation.Pairing.UnpairAsync();
				Write($"Unpairing {deviceInformation.Name ?? deviceInformation.Id}: {result.Status}");
			}
			catch (Exception ex)
			{
				Write($"Exception when unpairing device id: {_selectedDevice?.Name ?? _selectedDevice?.Id.ToString()}: {ex.Message}");
			}

			static string ExtractIdFromDeviceInfo(string input)
			{
				Match match = Regex.Match(input, @"([0-9A-Fa-f]{2}[:-]){5}([0-9A-Fa-f]{2})$");
				return match.Success ? match.Value.Replace(":", "") : "No MAC address found.";
			}

			static string ExtractIdFromIDevice(Guid input) => input.ToString().Split('-')[^1];
		}

		private async Task DoTheScanning(ScanMode scanMode = ScanMode.LowPower, int timeMs = 3000)
		{
			if (!_bluetoothLe.IsOn)
			{
				Write("Bluetooth is not On - it is {0}", _bluetoothLe.State);
				return;
			}
			Write("Bluetooth is on");
			Write("Scanning now for " + timeMs + " ms...");
			var cancellationTokenSource = new CancellationTokenSource(timeMs);
			_discoveredDevices.Clear();

			int index = 1;

			_adapter.DeviceDiscovered += OnDeviceDiscovered;
			_adapter.ScanMode = scanMode;
			_isScanning = true;
			await _adapter.StartScanningForDevicesAsync(cancellationToken: cancellationTokenSource.Token);
			_adapter.DeviceDiscovered -= OnDeviceDiscovered;
			_isScanning = false;

			void OnDeviceDiscovered(object? _, DeviceEventArgs a)
			{
				var dev = a.Device;
				Write($"{index++}: DeviceDiscovered: {0} with Name = 1", dev.Id.ToHexBleAddress(), dev.Name);
				if (!_discoveredDevices.Contains(a.Device))
					_discoveredDevices.Add(a.Device);
			}
		}

		internal async Task DiscoverAndSelect()
		{
			if (!_bluetoothLe.IsOn)
			{
				Console.WriteLine("Bluetooth is off - cannot discover");
				return;
			}
			await DoTheScanning();
			int index = 1;
			await Task.Delay(200);
			Console.WriteLine();
			foreach (var dev in _discoveredDevices)
			{
				Console.WriteLine($"{index++}: {dev.Id.ToHexBleAddress()} with Name = {dev.Name}");
			}
			if (_discoveredDevices.Count == 0)
			{
				Console.Write("NO BLE Devices discovered");
				return;
			}
			Console.WriteLine();
			Console.Write($"Select BLE address index with value {1} to {_discoveredDevices.Count}: ");
			if (int.TryParse(Console.ReadLine(), out var selectedIndex) && selectedIndex > 0 && selectedIndex < _discoveredDevices.Count)
			{
				_selectedDevice = _discoveredDevices[selectedIndex - 1];
				Console.WriteLine($"Selected {selectedIndex}: {_selectedDevice.Id.ToHexBleAddress()} with Name = {_selectedDevice.Name}");
				BleAddressSelector.SetBleAddress(_selectedDevice.Id.ToHexBleAddress());
			}

			_discoveredDevices.Clear();
		}

		private void WriteAdvertisementRecords(IDevice device)
		{
			if (device.AdvertisementRecords is null)
			{
				Write("{0} {1} has no AdvertisementRecords...", device.Name, device.State);
				return;
			}
			Write("{0} {1} with {2} AdvertisementRecords", device.Name, device.State, device.AdvertisementRecords.Count);
			foreach (var ar in device.AdvertisementRecords)
			{
				switch (ar.Type)
				{
					case AdvertisementRecordType.CompleteLocalName: Write($"{ar} = {Encoding.UTF8.GetString(ar.Data)}"); break;
					default: Write(ar.ToString()); break;
				}
			}
		}

		/// <summary>
		/// Connect to a device with a specific name
		/// Assumes that DoTheScanning has been called and that the device is advertising
		/// </summary>
		/// <param name="name"></param>
		/// <returns></returns>
		public async Task<IDevice?> ConnectTest(string name)
		{
			if (!_isScanning)
			{
				Write("ConnectTest({0}) Failed - Call the DoTheScanning() method first!");
				return null;
			}

			Thread.Sleep(10);
			foreach (IDevice device in _discoveredDevices.Where(device => device.Name.Contains(name)))
			{
				await _adapter.ConnectToDeviceAsync(device);
				return device;
			}
			return null;
		}

		public Task RunGetSystemConnectedOrPairedDevices()
		{
			IReadOnlyList<IDevice> devs = _adapter.GetSystemConnectedOrPairedDevices();
			Task.Delay(200);
			Write($"GetSystemConnectedOrPairedDevices found {devs.Count} devices:");
			foreach (var dev in devs)
			{
				Write("{0}: {1}", dev.Id.ToHexBleAddress(), dev.Name);
			}
			return Task.CompletedTask;
		}

		/// <summary>
		/// This demonstrates a bug where the known services is not cleared at disconnect (2023-11-03)
		/// </summary>
		public async Task ShowNumberOfServices()
		{
			string bleAddress = BleAddressSelector.GetBleAddress();
			Write("Connecting to device with address = {0}", bleAddress);
			IDevice dev = await _adapter.ConnectToKnownDeviceAsync(bleAddress.ToBleDeviceGuid()) ?? throw new("null");
			string name = dev.Name;
			Write("Connected to {0} {1} {2}", name, dev.Id.ToHexBleAddress(), dev.State);
			Write("Calling dev.GetServicesAsync()...");
			var services = await dev.GetServicesAsync();
			Write("Found {0} services", services.Count);
			Thread.Sleep(1000);
			Write("Disconnecting from {0} {1}", name, dev.Id.ToHexBleAddress());
			await _adapter.DisconnectDeviceAsync(dev);
			Thread.Sleep(1000);
			Write("ReConnecting to device {0} {1}...", name, dev.Id.ToHexBleAddress());
			await _adapter.ConnectToDeviceAsync(dev);
			Write("Connect Done.");
			Thread.Sleep(1000);
			Write("Calling dev.GetServicesAsync()...");
			services = await dev.GetServicesAsync();
			Write("Found {0} services", services.Count);
			await _adapter.DisconnectDeviceAsync(dev);
			Thread.Sleep(1000);
		}

		internal Task Disconnect(IDevice dev) => _adapter.DisconnectDeviceAsync(dev);
	}
}
