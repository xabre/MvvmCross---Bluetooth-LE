using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreBluetooth;
using Foundation;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.Utils;

namespace Plugin.BLE.iOS
{
    public class Device : DeviceBase<CBPeripheral>
    {
        private readonly IBleCentralManagerDelegate _bleCentralManagerDelegate;

        public Device(Adapter adapter, CBPeripheral nativeDevice, IBleCentralManagerDelegate bleCentralManagerDelegate)
            : this(adapter, nativeDevice, bleCentralManagerDelegate, nativeDevice.Name, nativeDevice.RSSI?.Int32Value ?? 0,
                new List<AdvertisementRecord>(), true)
        {
        }

        public Device(Adapter adapter, CBPeripheral nativeDevice, IBleCentralManagerDelegate bleCentralManagerDelegate, string name, int rssi, List<AdvertisementRecord> advertisementRecords, bool isConnectable = true)
            : base(adapter, nativeDevice)
        {
            _bleCentralManagerDelegate = bleCentralManagerDelegate;

            Id = Guid.ParseExact(NativeDevice.Identifier.AsString(), "d");
            Name = name;

            Rssi = rssi;
            AdvertisementRecords = advertisementRecords;
            IsConnectable = isConnectable;

            // TODO figure out if this is in any way required,
            // https://github.com/xabre/xamarin-bluetooth-le/issues/81
            //_nativeDevice.UpdatedName += OnNameUpdated;
        }

        private void OnNameUpdated(object sender, EventArgs e)
        {
            Name = ((CBPeripheral)sender).Name;
            Trace.Message("Device changed name: {0}", Name);
        }

        protected override Task<IReadOnlyList<IService>> GetServicesNativeAsync(CancellationToken cancellationToken)
        {
            return GetServicesInternal(null, cancellationToken);
        }

        protected override async Task<IService> GetServiceNativeAsync(Guid id, CancellationToken cancellationToken)
        {
            var cbuuid = CBUUID.FromString(id.ToString());
            var nativeService = NativeDevice.Services?.FirstOrDefault(service => service.UUID.Equals(cbuuid));
            if (nativeService != null)
            {
                return new Service(nativeService, this, _bleCentralManagerDelegate);
            }

            var services = await GetServicesInternal(cbuuid, cancellationToken);
            return services?.FirstOrDefault();
        }

        private Task<IReadOnlyList<IService>> GetServicesInternal(CBUUID id, CancellationToken cancellationToken)
        {
            var exception = new Exception($"Device {Name} disconnected while fetching services.");

            return TaskBuilder.FromEvent<IReadOnlyList<IService>, EventHandler<NSErrorEventArgs>, EventHandler<CBPeripheralErrorEventArgs>>(
                    execute: () =>
                    {
                        if (NativeDevice.State != CBPeripheralState.Connected)
                            throw exception;

                        if (id != null)
                        {
                            NativeDevice.DiscoverServices(new[] { id });
                        }
                        else
                        {
                            NativeDevice.DiscoverServices();
                        }
                    },
                    getCompleteHandler: (complete, reject) => (sender, args) =>
                    {
                        // If args.Error was not null then the Service might be null
                        if (args.Error != null)
                        {
                            reject(new Exception($"Error while discovering services {args.Error.LocalizedDescription}"));
                        }
                        else if (NativeDevice.Services == null)
                        {
                            // No service discovered. 
                            reject(new Exception($"Error while discovering services: returned list is null"));
                        }
                        else
                        {
                            var services = NativeDevice.Services
                                .Select(nativeService => new Service(nativeService, this, _bleCentralManagerDelegate))
                                .Cast<IService>().ToList();
                            complete(services);
                        }
                    },
                    subscribeComplete: handler => NativeDevice.DiscoveredService += handler,
                    unsubscribeComplete: handler => NativeDevice.DiscoveredService -= handler,
                    getRejectHandler: reject => ((sender, args) =>
                    {
                        if (args.Peripheral.Identifier == NativeDevice.Identifier)
                            reject(exception);
                    }),
                    subscribeReject: handler => _bleCentralManagerDelegate.DisconnectedPeripheral += handler,
                    unsubscribeReject: handler => _bleCentralManagerDelegate.DisconnectedPeripheral -= handler,
					token: cancellationToken);
        }

        public override Task<bool> UpdateRssiAsync(CancellationToken cancellationToken)
        {
            return TaskBuilder.FromEvent<bool, EventHandler<CBRssiEventArgs>, EventHandler<CBPeripheralErrorEventArgs>>(
                execute: () => NativeDevice.ReadRSSI(),
                getCompleteHandler: (complete, reject) => (sender, args) =>
                {
                    if (args.Error != null)
                    {
                        reject(new Exception($"Error while reading rssi services {args.Error.LocalizedDescription}"));
                    }
                    else
                    {
                        Rssi = args.Rssi?.Int32Value ?? 0;
                        complete(true);
                    }
                },
                subscribeComplete: handler => NativeDevice.RssiRead += handler,
                unsubscribeComplete: handler => NativeDevice.RssiRead -= handler,
                getRejectHandler: reject => ((sender, args) =>
                {
                    if (args.Peripheral.Identifier == NativeDevice.Identifier)
                        reject(new Exception($"Device {Name} disconnected while reading RSSI."));
                }),
                subscribeReject: handler => _bleCentralManagerDelegate.DisconnectedPeripheral += handler,
                unsubscribeReject: handler => _bleCentralManagerDelegate.DisconnectedPeripheral -= handler,
                token: cancellationToken);
        }

        protected override DeviceState GetState()
        {
            switch (NativeDevice.State)
            {
                case CBPeripheralState.Connected:
                    return DeviceState.Connected;
                case CBPeripheralState.Connecting:
                    return DeviceState.Connecting;
                case CBPeripheralState.Disconnected:
                    return DeviceState.Disconnected;
                case CBPeripheralState.Disconnecting:
                    return DeviceState.Disconnected;
                default:
                    return DeviceState.Disconnected;
            }
        }

        public void Update(CBPeripheral nativeDevice)
        {
            Rssi = nativeDevice.RSSI?.Int32Value ?? 0;
            Name = nativeDevice.Name;
        }

        protected override async Task<int> RequestMtuNativeAsync(int requestValue, CancellationToken cancellationToken)
        {
            Trace.Message($"Request MTU is not supported on iOS.");
            return await Task.FromResult((int)NativeDevice.GetMaximumWriteValueLength(CBCharacteristicWriteType.WithoutResponse));
        }

        protected override bool UpdateConnectionIntervalNative(ConnectionInterval interval)
        {
            Trace.Message("Cannot update connection inteval on iOS.");
            return false;
        }
        
        public override bool IsConnectable { get; protected set; }

        public override bool SupportsIsConnectable { get => true; }
        
        protected override DeviceBondState GetBondState()
        {
            return DeviceBondState.NotSupported;
        }

        public override bool UpdateConnectionParameters(ConnectParameters connectParameters = default)
        {
            throw new NotImplementedException();
        }
    }
}
